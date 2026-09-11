import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { resolveHostBinary } from './binary.js';

/** Exact decimal strings, never JavaScript floating-point cost estimates or receipts. */
export type ResourceAmounts = Readonly<Record<string, string>>;
export interface DurableWorkIdentity {
  readonly runId: string;
  readonly evaluationId: string;
  readonly attempt: number;
  readonly leaseId: string;
}
export interface DurableWorkConfig {
  readonly directory: string;
  readonly runId: string;
  readonly compatibilityHash: string;
  readonly limits: ResourceAmounts;
  readonly maximumWorkItems?: number;
  readonly maximumDeliveriesPerWork?: number;
  readonly maximumStateBytes?: number;
  readonly maximumPayloadBytes?: number;
  readonly maximumWorkers?: number;
  readonly leaseDurationMs?: number;
  readonly hostPath?: string;
  /** Prefix arguments, for example the managed host DLL. --durable is appended automatically. */
  readonly hostArgs?: readonly string[];
  readonly requestTimeoutMs?: number;
}
export interface DurableJob {
  readonly evaluationId: string;
  readonly attempt: number;
  readonly canonicalGenomeId: string;
  readonly payload: string;
  readonly tags?: readonly string[];
  readonly minimumResources?: ResourceAmounts;
  readonly estimated: ResourceAmounts;
  readonly maximum: ResourceAmounts;
}
export interface DurableWorker {
  readonly workerId: string;
  readonly compatibilityHash: string;
  readonly tags?: readonly string[];
  readonly capacity?: ResourceAmounts;
  readonly maximumConcurrentWork?: number;
}
export interface DurableLease {
  readonly identity: DurableWorkIdentity;
  readonly workerId: string;
  readonly canonicalGenomeId: string;
  readonly payload: string;
  readonly deliveryNumber: number;
  readonly expiresAt: string;
}
export type ReceiptOutcome = 'completed' | 'failed' | 'rejected' | 'canceled';
export type CommitDisposition = 'accepted' | 'duplicate' | 'stale' | 'duplicate-stale' | 'unknown-lease' | 'budget-violation';
export type HeartbeatStatus = 'renewed' | 'expired' | 'canceled' | 'completed' | 'unknown-lease';
export interface DurableReceipt {
  readonly identity: DurableWorkIdentity;
  readonly workerId: string;
  readonly payload: string;
  readonly provenance: string;
  readonly actual: ResourceAmounts;
  readonly outcome: ReceiptOutcome;
}
export interface DurableResult extends Omit<DurableReceipt, 'workerId'> { readonly accepted: boolean; }
export interface DurableStatus {
  readonly runId: string;
  readonly compatibilityHash: string;
  readonly wasRecovered: boolean;
  readonly sourceSessionId: string | null;
  readonly supportsExactSearchContinuation: false;
  readonly searchContinuationGuarantee: string;
  readonly searchState: 'not-owned';
  readonly spent: ResourceAmounts;
  readonly reserved: ResourceAmounts;
  readonly admitted: string;
  readonly settled: string;
  readonly denied: string;
  readonly maximumViolated: boolean;
}
export interface DurableEvaluationPayload {
  readonly schema: 1;
  readonly genomePayload: string;
  readonly canonicalGenomeId: string;
  readonly evaluationId: string;
  readonly attempt: number;
  readonly rootSeed: string;
  readonly seedStream: string;
}

const MAX_FRAME_BYTES = 16 * 1024 * 1024;
const MAX_INT32 = 2_147_483_647;
const INT64_MAX = 9223372036854775807n;
const UINT64_MAX = 18446744073709551615n;
type ObjectValue = Record<string, unknown>;
function record(value: unknown): value is ObjectValue { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function integer(value: unknown, max: number = MAX_INT32): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0 && value <= max;
}
function unsigned(value: unknown, max: bigint = INT64_MAX): value is string {
  return typeof value === 'string' && /^(0|[1-9][0-9]{0,19})$/.test(value) && BigInt(value) <= max;
}
function amounts(value: unknown): value is ResourceAmounts {
  return record(value) && Object.keys(value).length <= 32 && Object.entries(value).every(([key, amount]) =>
    key.trim().length > 0 && key.length <= 64 && !/[\u0000-\u001f\u007f-\u009f]/.test(key) &&
    typeof amount === 'string' && /^(0|[1-9][0-9]{0,28})(\.[0-9]{0,27}[1-9])?$/.test(amount));
}
function identity(value: unknown): value is DurableWorkIdentity {
  return record(value) && typeof value.runId === 'string' && value.runId.length > 0 && value.runId.length <= 256 &&
    unsigned(value.evaluationId) && integer(value.attempt) && typeof value.leaseId === 'string' && /^[0-9a-f]{32}$/.test(value.leaseId);
}
function lease(value: unknown): value is DurableLease {
  return record(value) && identity(value.identity) && typeof value.workerId === 'string' && value.workerId.length > 0 &&
    typeof value.canonicalGenomeId === 'string' && typeof value.payload === 'string' && integer(value.deliveryNumber, 16) &&
    typeof value.expiresAt === 'string' && Number.isFinite(Date.parse(value.expiresAt));
}
const outcomes = ['completed', 'failed', 'rejected', 'canceled'] as const;
const dispositions = ['accepted', 'duplicate', 'stale', 'duplicate-stale', 'unknown-lease', 'budget-violation'] as const;
const heartbeats = ['renewed', 'expired', 'canceled', 'completed', 'unknown-lease'] as const;
function member<T extends string>(value: unknown, choices: readonly T[]): value is T {
  return typeof value === 'string' && choices.includes(value as T);
}
function result(value: unknown): value is DurableResult {
  return record(value) && identity(value.identity) && typeof value.payload === 'string' && typeof value.provenance === 'string' &&
    amounts(value.actual) && member(value.outcome, outcomes) && typeof value.accepted === 'boolean';
}
function status(value: ObjectValue): DurableStatus {
  if (typeof value.runId !== 'string' || typeof value.compatibilityHash !== 'string' || typeof value.wasRecovered !== 'boolean' ||
      !(value.sourceSessionId === null || (typeof value.sourceSessionId === 'string' && /^[0-9a-f]{32}$/.test(value.sourceSessionId))) ||
      value.supportsExactSearchContinuation !== false || value.searchState !== 'not-owned' ||
      typeof value.searchContinuationGuarantee !== 'string' || !value.searchContinuationGuarantee.includes('fork') ||
      !amounts(value.spent) || !amounts(value.reserved) || !unsigned(value.admitted) || !unsigned(value.settled) ||
      !unsigned(value.denied) || typeof value.maximumViolated !== 'boolean') throw new DurableWorkError('Invalid durable status response.');
  return value as unknown as DurableStatus;
}
function boolean(value: unknown): boolean {
  if (typeof value !== 'boolean') throw new DurableWorkError('Missing boolean response field.');
  return value;
}
function receipt(value: unknown): DurableResult | null {
  if (value === null || result(value)) return value;
  throw new DurableWorkError('Invalid durable receipt response.');
}
function ticket(value: DurableWorkIdentity): void {
  if (!identity(value)) throw new DurableWorkError('The full original durable identity is required; evaluationId is a decimal string.');
}
function cost(value: ResourceAmounts): void {
  if (!amounts(value)) throw new DurableWorkError('Resources require bounded canonical decimal-string amounts.');
}

/** Parses the engine bridge envelope without losing 64-bit seeds or evaluation identities. */
export function parseDurableEvaluationPayload(json: string): DurableEvaluationPayload {
  if (Buffer.byteLength(json, 'utf8') > 1024 * 1024) throw new DurableWorkError('Evaluation envelope exceeds one MiB.');
  const value: unknown = JSON.parse(json);
  if (!record(value) || value.schema !== 1 || typeof value.genomePayload !== 'string' ||
      typeof value.canonicalGenomeId !== 'string' || !value.canonicalGenomeId.trim() || !unsigned(value.evaluationId) ||
      !integer(value.attempt) || !unsigned(value.rootSeed, UINT64_MAX) || !unsigned(value.seedStream, UINT64_MAX)) {
    throw new DurableWorkError('Invalid versioned evaluation envelope.');
  }
  return value as unknown as DurableEvaluationPayload;
}
export class DurableWorkError extends Error {
  constructor(message: string) { super(message); this.name = 'DurableWorkError'; }
}

/** Trusted local durable-control/worker endpoint. Always close; never automatically rerun unacknowledged physical work. */
export class DurableWorkClient {
  readonly #child: ChildProcessWithoutNullStreams;
  readonly #timeout: number;
  readonly #runId: string;
  readonly #pending = new Map<number, { accept: (value: ObjectValue) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }>();
  #nextId = 1;
  #buffer = '';
  #stderr = '';
  #fatal: Error | undefined;
  #closing: Promise<void> | undefined;

  private constructor(child: ChildProcessWithoutNullStreams, timeout: number, runId: string) {
    this.#child = child; this.#timeout = timeout; this.#runId = runId;
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk: string) => { this.#stderr = (this.#stderr + chunk).slice(-4096); });
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      if (this.#fatal) return;
      this.#buffer += chunk;
      for (;;) {
        const index = this.#buffer.indexOf('\n');
        const frame = index < 0 ? this.#buffer : this.#buffer.slice(0, index);
        if (Buffer.byteLength(frame, 'utf8') > MAX_FRAME_BYTES) { this.#fail('Oversized durable response; reconcile the original store.'); return; }
        if (index < 0) return;
        this.#buffer = this.#buffer.slice(index + 1);
        try {
          const response: unknown = JSON.parse(frame);
          if (!record(response) || !integer(response.id, Number.MAX_SAFE_INTEGER) || response.protocol !== 1 || typeof response.ok !== 'boolean') {
            throw new DurableWorkError('Invalid durable response or protocol downgrade.');
          }
          const waiter = this.#pending.get(response.id);
          if (!waiter) throw new DurableWorkError('Uncorrelated durable response.');
          this.#pending.delete(response.id); clearTimeout(waiter.timer); waiter.accept(response);
        } catch (error) { this.#fail(String(error)); return; }
      }
    });
    child.on('error', (error) => this.#fail(`Durable host failed to start: ${error.message}`));
    child.stdin.on('error', (error) => this.#fail(`Durable pipe failed: ${error.message}; reconcile the original store.`));
    child.on('close', (code, signal) => this.#fail(`Durable host closed (${code ?? signal}); reconcile unacknowledged work. ${this.#stderr}`));
  }
  get pid(): number | undefined { return this.#child.pid; }

  static async open(config: DurableWorkConfig): Promise<DurableWorkClient> {
    const { hostPath, hostArgs, requestTimeoutMs = 120_000, ...wire } = config;
    if (!integer(requestTimeoutMs)) throw new DurableWorkError('requestTimeoutMs must fit a positive Int32 timer.');
    cost(config.limits);
    const child = spawn(hostPath ?? resolveHostBinary(), [...(hostArgs ?? []), '--durable'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    const client = new DurableWorkClient(child, requestTimeoutMs, config.runId);
    try {
      const opened = await client.#call('open', { config: wire }, status);
      if (opened.runId !== config.runId || opened.compatibilityHash !== config.compatibilityHash) throw new DurableWorkError('Durable host changed the requested run or compatibility.');
      return client;
    } catch (error) { client.#fail(String(error)); throw error; }
  }
  async enqueue(job: DurableJob): Promise<boolean> {
    if (!unsigned(job.evaluationId) || !integer(job.attempt)) throw new DurableWorkError('Invalid logical work identity.');
    cost(job.estimated); cost(job.maximum); if (job.minimumResources !== undefined) cost(job.minimumResources);
    return this.#call('enqueue', { job }, value => boolean(value.enqueued));
  }
  /** Null means no matching work now; it never signals completion or permits a budget reset. */
  async claim(worker: DurableWorker): Promise<DurableLease | null> {
    if (worker.capacity !== undefined) cost(worker.capacity);
    return this.#call('claim', { worker }, value => {
      if (value.available === false && value.lease === null) return null;
      if (value.available === true && lease(value.lease) && value.lease.workerId === worker.workerId && value.lease.identity.runId === this.#runId) return value.lease;
      throw new DurableWorkError('Invalid claim response.');
    });
  }
  async heartbeat(identity: DurableWorkIdentity, workerId: string): Promise<HeartbeatStatus> {
    ticket(identity);
    return this.#call('heartbeat', { identity, workerId }, value => {
      if (!member(value.status, heartbeats)) throw new DurableWorkError('Invalid heartbeat response.'); return value.status;
    });
  }
  async cancel(evaluationId: string, attempt: number): Promise<boolean> {
    if (!unsigned(evaluationId) || !integer(attempt)) throw new DurableWorkError('Invalid logical work identity.');
    return this.#call('cancel', { evaluationId, attempt }, value => boolean(value.canceled));
  }
  async commit(receipt: DurableReceipt): Promise<CommitDisposition> {
    ticket(receipt.identity); cost(receipt.actual);
    if (!member(receipt.outcome, outcomes)) throw new DurableWorkError('An explicit actual receipt outcome is required.');
    return this.#call('commit', { ...receipt }, value => {
      if (!member(value.disposition, dispositions)) throw new DurableWorkError('Invalid commit response.'); return value.disposition;
    });
  }
  async result(evaluationId: string, attempt: number): Promise<DurableResult | null> {
    if (!unsigned(evaluationId) || !integer(attempt)) throw new DurableWorkError('Invalid logical work identity.');
    return this.#call('result', { evaluationId, attempt }, value => {
      const found = receipt(value.result);
      if (found && (found.identity.runId !== this.#runId || found.identity.evaluationId !== evaluationId || found.identity.attempt !== attempt)) {
        throw new DurableWorkError('Result belongs to different logical work.');
      }
      return found;
    });
  }
  async delivery(identity: DurableWorkIdentity, workerId: string): Promise<DurableResult | null> {
    ticket(identity); return this.#call('delivery', { identity, workerId }, value => {
      const found = receipt(value.result);
      if (found && Object.entries(identity).some(([key, field]) => found.identity[key as keyof DurableWorkIdentity] !== field)) {
        throw new DurableWorkError('Receipt belongs to a different delivery.');
      }
      return found;
    });
  }
  /** Single-lease keyset page for reconciliation only, never permission to repeat physical execution. */
  async unsettled(workerId: string, afterLeaseId?: string): Promise<{ lease: DurableLease | null; nextAfterLeaseId: string | null }> {
    return this.#call('unsettled', { workerId, afterLeaseId }, value => {
      if (value.reconciliationOnly !== true || !(value.lease === null || (lease(value.lease) && value.lease.workerId === workerId && value.lease.identity.runId === this.#runId)) ||
          value.nextAfterLeaseId !== (value.lease?.identity.leaseId ?? null)) throw new DurableWorkError('Invalid reconciliation response.');
      return { lease: value.lease, nextAfterLeaseId: value.nextAfterLeaseId as string | null };
    });
  }
  async status(): Promise<DurableStatus> { return this.#call('status', {}, status); }
  close(): Promise<void> {
    this.#closing ??= this.#call('close', {}, value => { if (value.closed !== true) throw new DurableWorkError('Close was not confirmed.'); })
      .finally(() => { this.#child.stdin.end(); this.#child.kill(); });
    return this.#closing;
  }
  #fail(message: string): void {
    this.#fatal ??= new DurableWorkError(message);
    for (const waiter of this.#pending.values()) { clearTimeout(waiter.timer); waiter.reject(this.#fatal); }
    this.#pending.clear(); this.#buffer = ''; this.#child.kill();
  }
  #call<T>(op: string, fields: ObjectValue, parse: (value: ObjectValue) => T): Promise<T> {
    if (this.#fatal) return Promise.reject(this.#fatal);
    if (this.#closing || this.#pending.size >= 32) return Promise.reject(new DurableWorkError('Endpoint closing or 32 pending requests reached.'));
    const id = this.#nextId++;
    if (!Number.isSafeInteger(id)) return Promise.reject(new DurableWorkError('Request identity space exhausted.'));
    let frame: string;
    try { frame = JSON.stringify({ ...fields, id, protocol: 1, op }); }
    catch (error) { return Promise.reject(error); }
    if (Buffer.byteLength(frame, 'utf8') > MAX_FRAME_BYTES) return Promise.reject(new DurableWorkError('Request exceeds frame byte bound.'));
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => this.#fail(`Durable '${op}' timed out; reconcile the original store before retrying.`), this.#timeout);
      timer.unref();
      this.#pending.set(id, { timer, reject, accept: value => {
        if (value.ok === false) { reject(new DurableWorkError(typeof value.error === 'string' ? value.error : 'Durable operation refused.')); return; }
        try { resolve(parse(value)); }
        catch (error) { reject(error); this.#fail('Invalid durable operation response; reconcile the original store.'); }
      } });
      this.#child.stdin.write(frame + '\n', error => { if (error) this.#fail(`Durable write failed: ${error.message}; reconcile the original store.`); });
    });
  }
}
