/**
 * A reference durable worker, the TypeScript counterpart of the Python package's `reference_worker`.
 *
 * `serve` claims leases, evaluates each leased program and commits one receipt per lease, exactly as the Python
 * `serve` does: same receipt payload (sorted-key JSON of the evaluator's result), same `failed` receipt carrying only
 * the error's type name, same refusal to re-run an evaluator to retry a commit. `programFileEvaluator` and
 * `loadEvaluatorModule` give a JavaScript evaluator the OpenEvolve contract the Python worker serves: each candidate
 * is written to its own temporary file and `evaluate(programPath)` receives that path.
 */
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  parseDurableEvaluationPayload,
  type CommitDisposition, type DurableWorkClient, type DurableWorker, type ResourceAmounts,
} from './durable.js';

/** A JSON value a receipt can carry: numbers are finite, everything else has already been turned into text. */
export type EvaluationValue = string | number | boolean | null;
export interface NormalizedEvaluation {
  readonly metrics: Readonly<Record<string, EvaluationValue>>;
  readonly artifacts: Readonly<Record<string, EvaluationValue>>;
}
/** Turns one candidate's program source into the receipt payload object; may be async. */
export type Evaluate = (source: string) => unknown;
/** An OpenEvolve-contract evaluator: receives the path of a file holding the candidate. */
export type ProgramPathEvaluator = (programPath: string) => unknown;

export interface ServeOptions {
  /** Recorded on every receipt; identifies the evaluator and its version. */
  readonly provenance: string;
  /** The resources each evaluation actually used, as exact decimal strings. */
  readonly actual: ResourceAmounts;
  /** Stop after this many leases (at least 1); by default serve until no lease is available. */
  readonly maxLeases?: number;
  /** Lease payload to program source; defaults to {@link engineProgramSource}. */
  readonly sourceFrom?: (payload: string) => string;
}

function plain(value: unknown): value is Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return false;
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

/** Python's spelling of a non-finite float, so a receipt reads the same whichever worker wrote it. */
function nonFinite(value: number): string {
  return Number.isNaN(value) ? 'nan' : value > 0 ? 'inf' : '-inf';
}

/** Keeps finite numbers, booleans, text and null exactly; records anything else as text rather than failing. */
function jsonValue(value: unknown): EvaluationValue {
  if (value === null || value === undefined) return null;
  if (typeof value === 'number') return Number.isFinite(value) ? value : nonFinite(value);
  if (typeof value === 'string' || typeof value === 'boolean') return value;
  if (typeof value === 'bigint') return value.toString();
  try {
    const text = JSON.stringify(value);
    if (typeof text === 'string') return text;
  } catch { /* cyclic or otherwise unserializable: fall through to its string form */ }
  return String(value);
}

function entries(value: unknown, what: string): [string, unknown][] {
  if (value instanceof Map) return [...value.entries()].map(([key, item]) => [String(key), item]);
  if (plain(value)) return Object.entries(value);
  throw new TypeError(`the evaluator's ${what} must be a plain object or a Map`);
}

/**
 * Normalizes both OpenEvolve result shapes. A plain object (or Map) is the metrics themselves; any other object with
 * a `metrics` property is an `EvaluationResult` whose `artifacts` are kept too. Non-finite numbers become text.
 */
export function normalizeEvaluation(result: unknown): NormalizedEvaluation {
  let metrics: unknown;
  let artifacts: unknown = {};
  if (result instanceof Map || plain(result)) {
    metrics = result;
  } else if (result !== null && typeof result === 'object' && 'metrics' in result) {
    metrics = (result as { metrics: unknown }).metrics;
    artifacts = (result as { artifacts?: unknown }).artifacts ?? {};
  } else {
    const kind = result === null ? 'null' : typeof result === 'object' ? (result.constructor?.name ?? 'object') : typeof result;
    throw new TypeError(`evaluate() must return a plain object or an object with a 'metrics' mapping, not ${kind}`);
  }
  const convert = (value: unknown, what: string) =>
    Object.fromEntries(entries(value, what).map(([key, item]) => [key, jsonValue(item)]));
  return { metrics: convert(metrics, 'metrics'), artifacts: convert(artifacts, 'artifacts') };
}

/**
 * Serializes like Python's `json.dumps(value, sort_keys=True, allow_nan=False)` (compact separators aside): keys are
 * sorted at every depth and a non-finite number is refused, so the lease becomes a failed receipt, not a lossy one.
 */
export function canonicalJson(value: unknown): string {
  const sorted = (pairs: [string, unknown][]) => Object.fromEntries(pairs.sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
  const text: string | undefined = JSON.stringify(value, (_key, item: unknown) => {
    if (typeof item === 'number' && !Number.isFinite(item)) throw new RangeError('Out of range float values are not JSON compliant');
    if (typeof item === 'bigint') throw new TypeError('BigInt values are not JSON serializable; convert them to strings first');
    if (item instanceof Map) return sorted([...item.entries()].map(([key, entry]) => [String(key), entry]));
    if (plain(item)) return sorted(Object.entries(item));
    return item;
  });
  if (text === undefined) throw new TypeError('the value has no JSON form');
  return text;
}

/** The program source in a lease the engine bridge enqueued: its envelope's encoded genome. */
export function engineProgramSource(payload: string): string {
  return parseDurableEvaluationPayload(payload).genomePayload;
}

/** For work enqueued directly with the program source as the lease payload. */
export function rawProgramSource(payload: string): string {
  return payload;
}

/**
 * Adapts an `evaluate(programPath)` evaluator: each candidate is written, UTF-8, to its own temporary file (removed
 * afterwards) and the evaluator's result is normalized. `suffix` is the candidate file's extension.
 */
export function programFileEvaluator(evaluate: ProgramPathEvaluator, suffix = '.py'): Evaluate {
  if (typeof evaluate !== 'function') throw new TypeError('evaluate must be a function of the program path');
  return async (source: string) => {
    const scratch = await mkdtemp(join(tmpdir(), 'aidotnet-candidate-'));
    try {
      const program = join(scratch, `program${suffix}`);
      await writeFile(program, source, 'utf8');
      return normalizeEvaluation(await evaluate(program));
    } finally {
      await rm(scratch, { recursive: true, force: true });
    }
  };
}

/**
 * Loads `evaluate(programPath)` from a JavaScript evaluator module, unmodified, as the Python worker loads an OpenEvolve
 * `evaluator.py`. The module's own relative imports resolve from its location.
 */
export async function loadEvaluatorModule(path: string, suffix = '.js'): Promise<Evaluate> {
  const file = resolve(path);
  const module: Record<string, unknown> = await import(pathToFileURL(file).href);
  const evaluate = module.evaluate ?? (module.default as Record<string, unknown> | undefined)?.evaluate;
  if (typeof evaluate !== 'function') throw new TypeError(`${file} defines no evaluate(programPath) function`);
  return programFileEvaluator(evaluate as ProgramPathEvaluator, suffix);
}

function errorType(error: unknown): string {
  if (error instanceof Error) return error.name || error.constructor.name || 'Error';
  return error === null ? 'null' : typeof error;
}

/**
 * Claims and evaluates leases until none is available (or `maxLeases`), returning each commit disposition.
 *
 * A null claim means no work is available now, not that the search is complete; the caller decides whether to poll
 * again. A failure turning the payload into source, in the evaluator, or in serializing its result commits a `failed`
 * receipt carrying only the error's type name. A commit that throws is propagated, never retried by re-running the
 * evaluator: the durable host settles lost replies.
 */
export async function serve(client: DurableWorkClient, worker: DurableWorker, evaluate: Evaluate,
  options: ServeOptions): Promise<CommitDisposition[]> {
  const { provenance, actual, maxLeases, sourceFrom = engineProgramSource } = options;
  if (maxLeases !== undefined && (!Number.isSafeInteger(maxLeases) || maxLeases < 1)) {
    throw new RangeError('maxLeases must be an integer of at least 1');
  }
  const dispositions: CommitDisposition[] = [];
  while (maxLeases === undefined || dispositions.length < maxLeases) {
    const lease = await client.claim(worker);
    if (lease === null) break;
    let payload: string;
    let outcome: 'completed' | 'failed';
    try {
      payload = canonicalJson(await evaluate(sourceFrom(lease.payload)));
      outcome = 'completed';
    } catch (error) { // the evaluator is caller code; any failure is that lease's outcome
      payload = canonicalJson({ error: errorType(error) });
      outcome = 'failed';
    }
    dispositions.push(await client.commit({
      identity: lease.identity, workerId: lease.workerId, payload, provenance, actual: { ...actual }, outcome,
    }));
  }
  return dispositions;
}
