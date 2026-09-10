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
  /** Anything derived from this is reproducible; the same seed replays the same search. */
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
const REQUEST_TIMEOUT_MS = 120_000;

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

  private constructor(child: ChildProcessWithoutNullStreams) {
    this.#child = child;
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

    // A CHILD THAT DIES MUST FAIL EVERY WAITER, or `await ask()` never settles and the
    // caller's loop hangs on a process that no longer exists.
    const die = (reason: string): void => {
      this.#exitError ??= new EvolutionError(reason);
      for (const [, waiter] of this.#pending) {
        clearTimeout(waiter.timer);
        waiter.reject(this.#exitError);
      }
      this.#pending.clear();
    };

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
      die(
        `the evolution host exited ${how} (code ${code ?? 'null'}, signal ${signal ?? 'none'})` +
          (tail ? `: ${tail}` : '')
      );
    });
    child.on('error', (error) => die(`the evolution host failed to start: ${error.message}`));
  }

  /** The host process id, or undefined if it never started. For diagnostics and tests. */
  get pid(): number | undefined {
    return this.#child.pid;
  }

  /** Starts the host process and opens a run. */
  static async open(config: RunConfig): Promise<EvolutionSession> {
    const binary = config.hostPath ?? resolveHostBinary();
    const args = config.hostPath ? [...(config.hostArgs ?? [])] : [];
    const child = spawn(binary, args, { stdio: ['pipe', 'pipe', 'pipe'] });
    const session = new EvolutionSession(child);

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
      session.#child.kill();
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
   * the result comes from. Safe to call more than once.
   */
  async close(): Promise<RunSummary> {
    if (this.#closed) return { best: null, stopReason: null };
    this.#closed = true;

    let summary: RunSummary = { best: null, stopReason: null };
    try {
      const response = await this.#send('close', {});
      summary = { best: response.best ?? null, stopReason: response.stopReason ?? null };
    } catch {
      // Already gone. There is nothing to report and nothing to fix.
    }

    this.#child.stdin.end();
    this.#lines.close();
    // The host exits on `close`; this is the backstop for one that does not.
    if (this.#child.exitCode === null) this.#child.kill();
    return summary;
  }

  #send(op: string, extra: Record<string, unknown>): Promise<Response> {
    if (this.#exitError) return Promise.reject(this.#exitError);

    const id = this.#nextId++;
    return new Promise<Response>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new EvolutionError(`the evolution host did not answer '${op}' within ${REQUEST_TIMEOUT_MS}ms`));
      }, REQUEST_TIMEOUT_MS);
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
  } finally {
    // close() is idempotent, and this is what guarantees the child dies if `evaluate`
    // threw partway through.
    await session.close();
  }
}

export {
  HOST_PATH_ENV,
  SUPPORTED_PLATFORMS,
  currentPlatform,
  resolveHostBinary,
  type SupportedPlatform,
} from './binary.js';
