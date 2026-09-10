/**
 * Quality-diversity evolution for TypeScript, over a NativeAOT child process.
 *
 * WHY A PROCESS AND NOT AN FFI BINDING. The engine's surface is ask/tell, which is
 * coarse-grained: one call per BATCH of candidates, not per evaluation. At that
 * granularity a process hop costs almost nothing, and paying it removes an ABI to keep in
 * sync with the C# side, a C++ toolchain in CI, a prebuild matrix over Node ABI versions,
 * and the possibility that a fault in native code takes the Node process down with it.
 *
 * THE HOST NEVER CALLS BACK. Evolution normally drives the loop and calls your evaluator;
 * here you drive it. That inversion is what makes this transport possible at all -- with
 * callbacks, the child would have to reach back into the Node event loop.
 */

import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface, type Interface } from 'node:readline';
import { resolveHostBinary } from './binary.js';

/** One knob the search varies. */
export interface ParameterSpec {
  readonly name: string;
  readonly min: number;
  readonly max: number;
  /**
   * Resolution. Defaults to a hundredth of the range.
   *
   * Values are snapped to this, which is what stops two genomes differing in the
   * seventeenth decimal from counting as distinct candidates and filling the archive with
   * float noise.
   */
  readonly step?: number;
  /** True when only whole numbers are meaningful, such as a count. */
  readonly integral?: boolean;
}

/**
 * A behaviour dimension the archive is organised by.
 *
 * Quality-diversity keeps the best candidate found in each REGION of this space, not just
 * the single best overall -- so the descriptors decide what "different kinds of good
 * answer" means for your problem.
 */
export interface DescriptorSpec {
  readonly name: string;
  readonly min: number;
  readonly max: number;
  /** Cells along this dimension. Defaults to 10. */
  readonly bins?: number;
}

export interface RunConfig {
  readonly parameters: readonly ParameterSpec[];
  readonly descriptors: readonly DescriptorSpec[];
  /** Starting points. Defaults to one genome at the midpoint of every range. */
  readonly seeds?: readonly Record<string, number>[];
  /**
   * Anything derived from this is reproducible; the same seed replays the same search.
   *
   * A NON-NEGATIVE SAFE INTEGER. The host holds it as a 64-bit unsigned value, but it
   * arrives through `JSON.stringify`, and a JavaScript number above
   * `Number.MAX_SAFE_INTEGER` is rounded on the way -- 9007199254740993 is sent as
   * 9007199254740992. Two seeds a caller believes are different would then produce the
   * same run, so anything unrepresentable is rejected rather than quietly rounded.
   */
  readonly seed?: number;
  readonly maxProposals?: number;
  /**
   * Evaluation budget, a SEPARATE cap from proposals.
   *
   * Defaults to `maxProposals`. Left unset on the engine it silently caps a run at 100 and
   * reports a stop reason naming no setting you could raise.
   */
  readonly maxEvaluations?: number;
  readonly maxGenerations?: number;
  readonly batchSize?: number;
  readonly direction?: 'maximize' | 'minimize';
  /**
   * How long any single request may take before the session is considered wedged.
   *
   * Defaults to two minutes. A timeout is FATAL to the session, not to the one request:
   * a host that stopped answering has no reason to start again, and leaving it alive
   * would leak a process nobody holds a reference to.
   */
  readonly requestTimeoutMs?: number;
  /** Path to the host binary, when not using the bundled one. */
  readonly hostPath?: string;
  /**
   * Arguments for `hostPath`.
   *
   * For running the host through something else -- `dotnet path/to/host.dll` while
   * developing, or a wrapper that sets up an environment. Ignored without `hostPath`,
   * since the published binary takes none.
   */
  readonly hostArgs?: readonly string[];
}

/** One candidate to score. */
export interface Candidate {
  readonly evaluationId: number;
  readonly parameters: Record<string, number>;
  readonly quality?: number;
}

/** What you report back for one candidate. */
export interface Evaluation {
  readonly evaluationId: number;
  /**
   * Higher is better under `maximize`.
   *
   * Omit it, or pass a non-finite number, to report a FAILED evaluation -- which is not the
   * same as scoring zero. A zero is a real result that competes in the archive; a failure
   * is recorded as one and competes with nothing.
   */
  readonly quality?: number;
  readonly descriptors?: Record<string, number>;
  /** Why the evaluation failed, when it did. */
  readonly reason?: string;
}

export interface RunSummary {
  readonly best: Candidate | null;
  readonly stopReason: string | null;
}

interface Response {
  id: number;
  ok: boolean;
  error?: string;
  candidates?: Candidate[];
  accepted?: number;
  complete?: boolean;
  best?: Candidate | null;
  stopReason?: string | null;
  version?: string;
}

/** How long any single request may take before the session is considered wedged. */
const DEFAULT_REQUEST_TIMEOUT_MS = 120_000;

/**
 * Rejects configuration that cannot survive the wire.
 *
 * BEFORE THE PROCESS IS SPAWNED, so a bad value costs nothing and the message names the
 * field rather than surfacing as a host-side parse error with no context.
 */
function validate(config: RunConfig): void {
  const counts: readonly (readonly [string, number | undefined])[] = [
    ['seed', config.seed],
    ['maxProposals', config.maxProposals],
    ['maxEvaluations', config.maxEvaluations],
    ['maxGenerations', config.maxGenerations],
    ['batchSize', config.batchSize],
    ['requestTimeoutMs', config.requestTimeoutMs],
  ];
  for (const [name, value] of counts) {
    if (value === undefined) continue;
    if (!Number.isSafeInteger(value) || value < 0) {
      // Number.isSafeInteger is the exact test: it is false for a non-integer, for a
      // non-finite value, and for anything JSON.stringify would round on the way out.
      throw new EvolutionError(
        `${name} must be a non-negative integer no larger than ${Number.MAX_SAFE_INTEGER}, ` +
          `not ${value}. Larger values are rounded by JSON, so two seeds you believe are ` +
          `different would produce the same run.`
      );
    }
  }
  if (config.parameters.length === 0) {
    throw new EvolutionError('config.parameters must declare at least one parameter');
  }
  if (config.descriptors.length === 0) {
    throw new EvolutionError('config.descriptors must declare at least one descriptor');
  }
}

export class EvolutionError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'EvolutionError';
  }
}

/**
 * A running search. Ask for candidates, score them, tell the results.
 *
 * Create one with {@link openSession} and always `close()` it -- the child process outlives
 * a dropped reference otherwise, and `close()` is also what returns the best genome found.
 */
export class EvolutionSession {
  readonly #child: ChildProcessWithoutNullStreams;
  readonly #lines: Interface;
  readonly #pending = new Map<
    number,
    { resolve: (value: Response) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }
  >();

  #nextId = 1;
  #closed = false;
  #exitError: Error | null = null;
  #stderr = '';
  readonly #timeoutMs: number;

  private constructor(child: ChildProcessWithoutNullStreams, timeoutMs: number) {
    this.#child = child;
    this.#timeoutMs = timeoutMs;
    this.#lines = createInterface({ input: child.stdout });

    // STDERR MUST BE DRAINED even though the protocol never uses it. A piped stream nobody
    // reads fills its buffer and then blocks the child's next write -- so a host that
    // logged a warning would deadlock rather than answer. Keeping the tail also turns "the
    // host exited" into an error that says why.
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk: string) => {
      this.#stderr = (this.#stderr + chunk).slice(-4096);
    });

    this.#lines.on('line', (line: string) => {
      if (!line) return;
      let response: Response;
      try {
        response = JSON.parse(line) as Response;
      } catch {
        // A line we cannot parse is the host misbehaving; there is no id to fail, so the
        // request will time out rather than hang forever.
        return;
      }
      const waiter = this.#pending.get(response.id);
      if (!waiter) return;
      this.#pending.delete(response.id);
      clearTimeout(waiter.timer);
      waiter.resolve(response);
    });

    // AN EXIT ALWAYS FAILS THE WAITERS, including during close. Skipping them while closing
    // looks reasonable and is not: a host that crashes while answering `close` leaves that
    // request pending, and the caller then blocks for the full request timeout on a process
    // that is already gone. Only the WORDING depends on whether the exit was expected.
    //
    // 'close', NOT 'exit'. 'exit' fires when the process ends, which can be BEFORE its
    // stdout has been drained -- so failing the waiters there would discard a `close`
    // response that had already been written and was still in the pipe. 'close' is the
    // event that waits for the stdio streams.
    child.on('close', (code, signal) => {
      const tail = this.#stderr.trim();
      const how = this.#closed ? 'while closing' : 'unexpectedly';
      this.#die(
        `the evolution host exited ${how} (code ${code ?? 'null'}, signal ${signal ?? 'none'})` +
          (tail ? `: ${tail}` : '')
      );
    });
    child.on('error', (error) =>
      this.#die(`the evolution host failed to start: ${error.message}`)
    );
  }

  /**
   * Puts the session permanently into a failed state and fails every waiter.
   *
   * A CHILD THAT IS GONE MUST FAIL EVERY WAITER, or `await ask()` never settles and the
   * caller's loop hangs on a process that no longer exists. The first reason wins: a
   * timeout that then kills the child should be reported as the timeout, not as the exit
   * it caused.
   */
  #die(reason: string): void {
    this.#exitError ??= new EvolutionError(reason);
    for (const [, waiter] of this.#pending) {
      clearTimeout(waiter.timer);
      waiter.reject(this.#exitError);
    }
    this.#pending.clear();
  }

  /** Tears down the child and the reader. Safe to call more than once. */
  #teardown(): void {
    this.#child.stdin.end();
    this.#lines.close();
    if (this.#child.exitCode === null && this.#child.signalCode === null) this.#child.kill();
  }

  /** The host process id, or undefined if it never started. For diagnostics and tests. */
  get pid(): number | undefined {
    return this.#child.pid;
  }

  /** Starts the host process and opens a run. */
  static async open(config: RunConfig): Promise<EvolutionSession> {
    validate(config);
    const binary = config.hostPath ?? resolveHostBinary();
    const args = config.hostPath ? [...(config.hostArgs ?? [])] : [];
    const child = spawn(binary, args, { stdio: ['pipe', 'pipe', 'pipe'] });
    const session = new EvolutionSession(
      child,
      config.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS
    );

    const response = await session.#send('open', {
      config: {
        parameters: config.parameters,
        descriptors: config.descriptors,
        seeds: config.seeds,
        seed: config.seed ?? 1234,
        maxProposals: config.maxProposals ?? 200,
        maxEvaluations: config.maxEvaluations,
        maxGenerations: config.maxGenerations ?? 1000,
        batchSize: config.batchSize ?? 8,
        direction: config.direction ?? 'maximize',
      },
    });
    if (!response.ok) {
      // Marked closed as well as torn down: a caller who catches this and calls `close()`
      // anyway must not then wait out a request timeout on a process already gone.
      session.#closed = true;
      session.#teardown();
      throw new EvolutionError(response.error ?? 'the host refused to open the run');
    }
    return session;
  }

  /**
   * The next candidates needing evaluation.
   *
   * An EMPTY array means the run is over. That is the completion signal: there is no
   * separate "done" event to miss.
   */
  async ask(max = 8): Promise<Candidate[]> {
    const response = await this.#send('ask', { max });
    if (!response.ok) throw new EvolutionError(response.error ?? 'ask failed');
    return response.candidates ?? [];
  }

  /** Reports scores. Returns how many were actually outstanding. */
  async tell(results: readonly Evaluation[]): Promise<number> {
    if (results.length === 0) return 0;
    const response = await this.#send('tell', { results });
    if (!response.ok) throw new EvolutionError(response.error ?? 'tell failed');
    return response.accepted ?? 0;
  }

  /**
   * Stops the run, returning the best genome found.
   *
   * GRACEFUL: the current batch commits and the archive is kept, which is why this is where
   * the result comes from. Safe to call more than once; the second call is a no-op.
   *
   * THROWS when the host cannot confirm the stop -- because it died, or never answered.
   * Returning an empty summary there would say "the run finished and found nothing",
   * which is a different and much worse claim. Call it in a `catch` rather than a
   * `finally` if you have your own error to preserve; {@link evolve} does exactly that.
   *
   * @throws {EvolutionError} The host did not answer the stop request.
   */
  async close(): Promise<RunSummary> {
    if (this.#closed) return { best: null, stopReason: null };
    this.#closed = true;

    let response: Response;
    try {
      response = await this.#send('close', {});
    } catch (error) {
      // A FAILED CLOSE IS NOT AN EMPTY RESULT. Swallowing it here returned
      // `{best: null, stopReason: null}` from a host that died before answering, so
      // `evolve()` resolved successfully with no confirmed result -- a search reported as
      // having found nothing when in truth nobody ever asked. The child is still torn
      // down; the caller still hears about it.
      this.#teardown();
      throw error;
    }

    this.#teardown();
    // `ok: false` IS NOT A SUMMARY. FinishAsync throwing on the host produces a
    // response with an error and no best, and reading it as `{best: null}` says "the
    // run finished and found nothing" -- the same wrong claim a swallowed transport
    // failure used to make, arriving through the other door.
    if (!response.ok) {
      throw new EvolutionError(response.error ?? 'the host could not stop the run');
    }
    return { best: response.best ?? null, stopReason: response.stopReason ?? null };
  }

  #send(op: string, extra: Record<string, unknown>): Promise<Response> {
    if (this.#exitError) return Promise.reject(this.#exitError);

    const id = this.#nextId++;
    return new Promise<Response>((resolve, reject) => {
      const timer = setTimeout(() => {
        // A TIMEOUT KILLS THE SESSION, it does not just fail one request. Removing the
        // waiter alone leaves the child alive with nobody holding a reference that could
        // close it -- and a timed-out `open` rejects before the session is ever returned,
        // so repeated failed opens leak one host process each.
        this.#die(
          `the evolution host did not answer '${op}' within ${this.#timeoutMs}ms`
        );
        this.#teardown();
      }, this.#timeoutMs);
      // Unref'd so a pending request cannot by itself keep the process alive.
      timer.unref?.();

      this.#pending.set(id, { resolve, reject, timer });
      this.#child.stdin.write(`${JSON.stringify({ op, id, ...extra })}\n`, (error) => {
        if (!error) return;
        this.#pending.delete(id);
        clearTimeout(timer);
        reject(new EvolutionError(`could not write to the evolution host: ${error.message}`));
      });
    });
  }
}

/** Starts a run. Remember to `close()` it. */
export const openSession = (config: RunConfig): Promise<EvolutionSession> =>
  EvolutionSession.open(config);

/**
 * Runs a whole search, calling `evaluate` for each batch.
 *
 * The convenience wrapper over ask/tell for the common case where you simply want the loop
 * driven. Use the session directly when you need to interleave evolution with other work.
 */
export async function evolve(
  config: RunConfig,
  evaluate: (candidates: readonly Candidate[]) => Promise<readonly Evaluation[]> | readonly Evaluation[]
): Promise<RunSummary> {
  const session = await openSession(config);
  try {
    for (;;) {
      const batch = await session.ask(config.batchSize ?? 8);
      if (batch.length === 0) break;
      await session.tell(await evaluate(batch));
    }
    return await session.close();
  } catch (error) {
    // THE FIRST ERROR WINS. `close()` now propagates its own failures, so closing in a
    // `finally` would let a teardown failure replace the evaluator error that caused it
    // -- and the evaluator error is the one that says what actually went wrong. The
    // close still happens, so the child cannot outlive a thrown evaluator; its failure
    // is simply not allowed to speak over the first one.
    //
    // close() is idempotent, so this is a no-op when the throw came from close() itself.
    await session.close().catch(() => undefined);
    throw error;
  }
}

export {
  HOST_PATH_ENV,
  SUPPORTED_PLATFORMS,
  currentPlatform,
  resolveHostBinary,
  type SupportedPlatform,
} from './binary.js';
