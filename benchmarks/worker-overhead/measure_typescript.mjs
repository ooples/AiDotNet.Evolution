// Measures the TypeScript reference worker's overhead per evaluation against the real durable host (V1-25, #119).
// The counterpart of measure_python.py, with the same definition and report schema: each lease is served by one
// serve(..., { maxLeases: 1 }) call and the evaluator's own time, measured inside it, is subtracted from that call's
// wall time. What remains is the claim round trip, envelope unwrapping, receipt encoding and the commit round trip
// (which includes the host's durable journal write).
//
// usage: node measure_typescript.mjs [--host <executable-or-dotnet>] [--host-arg <dll>] [--evaluations N]
//                                    [--warmup W] [--gate-p95-ms MS] [--output <new.json>]
// Run `npm run build` in bindings/typescript first. Without --host the binding's bundled binary is used.
import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises';
import { tmpdir, platform, release, arch } from 'node:os';
import { dirname, join } from 'node:path';
import { parseArgs } from 'node:util';
import { DurableWorkClient, serve } from '../../bindings/typescript/dist/index.js';

const SCHEMA = 'aidotnet-worker-overhead-v1';
const MAXIMUM_WORK_ITEMS = 1024; // EvolutionWorkCoordinatorOptions' default retained work-item limit

/** Nearest-rank percentile, identical to measure_python.py's. */
export function percentile(sorted, fraction) {
  if (sorted.length === 0) throw new Error('no samples');
  return sorted[Math.max(1, Math.ceil(sorted.length * fraction)) - 1];
}
function summarize(samplesNs) {
  const ordered = samplesNs.map(value => Number(value) / 1e6).sort((a, b) => a - b);
  return { count: ordered.length, min_ms: ordered[0], p50_ms: percentile(ordered, 0.5), p95_ms: percentile(ordered, 0.95),
    p99_ms: percentile(ordered, 0.99), max_ms: ordered[ordered.length - 1],
    mean_ms: ordered.reduce((sum, value) => sum + value, 0) / ordered.length };
}
const envelope = (evaluationId, source) => JSON.stringify({ schema: 1, genomePayload: source,
  canonicalGenomeId: `program:${evaluationId}`, evaluationId, attempt: 1, rootSeed: '7', seedStream: '0' });

async function measure(transport, evaluations, warmup) {
  const total = evaluations + warmup;
  const evaluatorNs = [];
  const evaluate = source => {
    const started = process.hrtime.bigint();
    const result = { metrics: { combined_score: source.length }, artifacts: {} };
    evaluatorNs.push(process.hrtime.bigint() - started);
    return result;
  };
  const overheadNs = [];
  const roundTripNs = [];
  const scratch = await mkdtemp(join(tmpdir(), 'worker-overhead-'));
  let work;
  try {
    work = await DurableWorkClient.open({ directory: join(scratch, 'work'), runId: 'overhead', compatibilityHash: 'overhead-v1',
      limits: { evaluations: String(total) }, ...transport });
    const worker = { workerId: 'overhead-1', compatibilityHash: 'overhead-v1' };
    const options = { provenance: 'overhead-v1', actual: { evaluations: '1' } };
    for (let index = 1; index <= total; index++) {
      const enqueued = await work.enqueue({ evaluationId: String(index), attempt: 1, canonicalGenomeId: `program:${index}`,
        payload: envelope(String(index), `def run():\n    return ${index}\n`), estimated: { evaluations: '1' }, maximum: { evaluations: '1' } });
      if (!enqueued) throw new Error(`job ${index} was not enqueued`);
    }
    for (let index = 0; index < total; index++) {
      const before = evaluatorNs.length;
      let started = process.hrtime.bigint();
      const dispositions = await serve(work, worker, evaluate, { ...options, maxLeases: 1 });
      const elapsed = process.hrtime.bigint() - started;
      if (dispositions.length !== 1 || dispositions[0] !== 'accepted' || evaluatorNs.length !== before + 1) {
        throw new Error(`lease ${index + 1} was not evaluated and accepted exactly once: ${dispositions}`);
      }
      // A read-only request: the IPC round trip alone, without the durable state write a claim or commit makes.
      started = process.hrtime.bigint();
      await work.status();
      const roundTrip = process.hrtime.bigint() - started;
      if (index >= warmup) { overheadNs.push(elapsed - evaluatorNs[evaluatorNs.length - 1]); roundTripNs.push(roundTrip); }
    }
    if ((await serve(work, worker, evaluate, options)).length !== 0) throw new Error('work remained after every enqueued lease was served');
    const status = await work.status();
    if (status.settled !== String(total)) throw new Error(`the host settled ${status.settled} of ${total} leases`);
  } finally {
    await work?.close().catch(() => undefined);
    await rm(scratch, { recursive: true, force: true });
  }
  const tenth = Math.max(1, Math.floor(overheadNs.length / 10));
  return { overhead: summarize(overheadNs), evaluator: summarize(evaluatorNs.slice(warmup)), ipc_round_trip: summarize(roundTripNs),
    // The host rewrites its whole durable state on every claim and commit, so overhead grows with retained work.
    overhead_first_tenth: summarize(overheadNs.slice(0, tenth)), overhead_last_tenth: summarize(overheadNs.slice(-tenth)),
    settled: total };
}

const { values: args } = parseArgs({ options: {
  host: { type: 'string' }, 'host-arg': { type: 'string', multiple: true, default: [] },
  evaluations: { type: 'string', default: '950' }, warmup: { type: 'string', default: '50' },
  'gate-p95-ms': { type: 'string' }, output: { type: 'string' },
} });
const evaluations = Number(args.evaluations);
const warmup = Number(args.warmup);
const gate = args['gate-p95-ms'] === undefined ? null : Number(args['gate-p95-ms']);
if (!Number.isSafeInteger(evaluations) || evaluations < 20 || !Number.isSafeInteger(warmup) || warmup < 0) {
  throw new Error('--evaluations must be an integer of at least 20 (a p95 needs samples) and --warmup a non-negative integer');
}
if (evaluations + warmup > MAXIMUM_WORK_ITEMS) throw new Error(`the host retains at most ${MAXIMUM_WORK_ITEMS} work items by default`);
if (gate !== null && !(gate > 0)) throw new Error('--gate-p95-ms must be a positive number');
const transport = args.host === undefined ? {} : { hostPath: args.host, hostArgs: args['host-arg'] };
const measured = await measure(transport, evaluations, warmup);
const sortKeys = value => (value !== null && typeof value === 'object' && !Array.isArray(value)
  ? Object.fromEntries(Object.keys(value).sort().map(key => [key, sortKeys(value[key])])) : value);
const report = sortKeys({ schema: SCHEMA, worker: 'typescript', definition: "serve(maxLeases: 1) wall time minus the evaluator's own time",
  evaluations, warmup, gate_p95_ms: gate, node: process.versions.node, platform: `${platform()}-${release()}`, machine: arch(), ...measured });
const text = `${JSON.stringify(report, null, 2)}\n`;
if (args.output !== undefined) {
  await mkdir(dirname(args.output), { recursive: true });
  await writeFile(args.output, text, { encoding: 'utf8', flag: 'wx' });
}
process.stdout.write(text);
if (gate !== null && !(measured.overhead.p95_ms < gate)) {
  process.stderr.write(`worker overhead p95 ${measured.overhead.p95_ms.toFixed(3)} ms is not below ${gate} ms\n`);
  process.exitCode = 1;
}
