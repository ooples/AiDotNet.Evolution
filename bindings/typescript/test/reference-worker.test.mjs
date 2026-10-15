// The TypeScript reference worker, held to the same behaviour as the Python one
// (bindings/python/tests/test_reference_worker.py): every lease committed once, evaluator failures are failed
// receipts, both OpenEvolve result shapes keep their metrics and artifacts, and engine-bridge envelopes are unwrapped.
// Each test claims a behaviour id from conformance/reference-worker-v1/behaviours.json, and the last
// test fails when a declared behaviour has no test here. The Python suite claims from the same file,
// so a behaviour added to one binding cannot be quietly missing from the other. The protocol itself
// is pinned separately, by conformance/durable-v1/scenarios.json, which both clients and the C# host
// run; these are the behaviours above the protocol, which each binding implements in its own
// language and so cannot share a test for.
import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DurableWorkClient, canonicalJson, engineProgramSource, loadEvaluatorModule, normalizeEvaluation,
  programFileEvaluator, rawProgramSource, serve,
} from '../dist/index.js';

const root = fileURLToPath(new URL('../../../', import.meta.url));
// Same host selection as conformance.test.mjs; never a skip (check-test-output.mjs fails a run that skips).
const dll = process.env.AIDOTNET_DURABLE_HOST_DLL
  ?? join(root, 'src', 'AiDotNet.Evolution.Host', 'bin', 'Release', 'net10.0', 'aidotnet-evolution-host.dll');
const behaviours = JSON.parse(readFileSync(join(root, 'conformance', 'reference-worker-v1', 'behaviours.json'), 'utf8'));
const declared = new Set(behaviours.behaviours.map((entry) => entry.id));
const covered = new Set();

/** Registers a test and claims the manifest behaviour it covers. An unknown id fails here, not silently. */
function covering(behaviour, title, fn) {
  if (!declared.has(behaviour)) throw new Error(`No such reference-worker behaviour: ${behaviour}`);
  covered.add(behaviour);
  test(title, fn);
}

const host = process.env.AIDOTNET_DURABLE_HOST_PATH;
const transport = host ? { hostPath: host } : existsSync(dll) ? { hostPath: 'dotnet', hostArgs: [dll] } : {};

// Both OpenEvolve return shapes. The dict evaluator imports the candidate it is given, as OpenEvolve's do.
const DICT_EVALUATOR = `
import { pathToFileURL } from 'node:url';
export async function evaluate(programPath) {
  const { run } = await import(pathToFileURL(programPath).href);
  const value = run();
  if (value < 0) throw new RangeError('negative');
  return { combined_score: value, path_is_file: programPath.endsWith('.mjs') };
}
`;
const RESULT_EVALUATOR = `
import { readFileSync } from 'node:fs';
class EvaluationResult { constructor(metrics, artifacts) { this.metrics = metrics; this.artifacts = artifacts; } }
export function evaluate(programPath) {
  const source = readFileSync(programPath, 'utf8');
  return new EvaluationResult({ combined_score: source.length, inf: Infinity }, { stderr: 'none' });
}
`;

const envelope = (evaluationId, source) => JSON.stringify({ schema: 1, genomePayload: source,
  canonicalGenomeId: `program:${evaluationId}`, evaluationId, attempt: 1, rootSeed: '7', seedStream: '0' });
const job = (evaluationId, payload) => ({ evaluationId, attempt: 1, canonicalGenomeId: `program:${evaluationId}`,
  payload, estimated: { cost: '1' }, maximum: { cost: '1' } });
const worker = { workerId: 'reference-1', compatibilityHash: 'compat' };

async function withHost(body) {
  const directory = await mkdtemp(join(tmpdir(), 'reference-worker-'));
  let client;
  try {
    client = await DurableWorkClient.open({ directory: join(directory, 'work'), runId: 'run', compatibilityHash: 'compat',
      limits: { cost: '100' }, ...transport });
    await body(client, directory);
  } finally {
    if (client !== undefined) await client.close().catch(() => undefined);
    await rm(directory, { recursive: true, force: true });
  }
}
async function evaluatorModule(directory, text) {
  const path = join(directory, 'evaluator.mjs');
  await writeFile(path, text, 'utf8');
  return loadEvaluatorModule(path, '.mjs');
}

covering('engine-envelope-yields-genome-source', 'the engine envelope yields the genome source and raw payloads pass through', () => {
  assert.equal(engineProgramSource(envelope('1', 'def run():\n    return 1\n')), 'def run():\n    return 1\n');
  assert.equal(rawProgramSource('x = 1\n'), 'x = 1\n');
  assert.throws(() => engineProgramSource('x = 1\n'));
});

covering('non-scalar-and-non-finite-metrics-are-text', 'non-scalar and non-finite metrics are recorded as text, not turned into failures', () => {
  const result = normalizeEvaluation(new Map([['combined_score', 2.5], ['flag', true], ['curve', [1, 2]],
    ['nan', NaN], ['low', -Infinity], ['big', 2n ** 70n], ['missing', undefined]]));
  assert.deepEqual(result, { metrics: { combined_score: 2.5, flag: true, curve: '[1,2]', nan: 'nan', low: '-inf',
    big: '1180591620717411303424', missing: null }, artifacts: {} });
  // A plain object is the metrics themselves, even when it has a "metrics" key -- as a Python Mapping is.
  assert.deepEqual(normalizeEvaluation({ metrics: 1 }), { metrics: { metrics: 1 }, artifacts: {} });
  assert.throws(() => normalizeEvaluation(7), TypeError);
  assert.throws(() => normalizeEvaluation(null), TypeError);
  assert.throws(() => normalizeEvaluation({ get metrics() { return 1; }, __proto__: { kind: 'result' } }), TypeError);
});

covering('receipt-payload-is-canonical-json', 'receipt payloads are sorted-key JSON and a non-finite number is refused, as Python json.dumps does', () => {
  assert.equal(canonicalJson({ b: 1, a: { d: [3, { z: 1, y: 2 }], c: 'x' } }), '{"a":{"c":"x","d":[3,{"y":2,"z":1}]},"b":1}');
  assert.equal(canonicalJson(new Map([['b', 1], ['a', 2]])), '{"a":2,"b":1}');
  assert.throws(() => canonicalJson({ score: NaN }), RangeError);
  assert.throws(() => canonicalJson({ score: 1n }), TypeError);
  assert.throws(() => canonicalJson(undefined), TypeError);
});

covering('one-commit-per-lease-and-failures-are-receipts', 'every lease is committed once and failures are receipts, not crashes', () => withHost(async (work, directory) => {
  const evaluate = await evaluatorModule(directory, DICT_EVALUATOR);
  for (const [index, value] of [3, 5, -1].entries()) {
    assert.equal(await work.enqueue(job(String(index + 1), `export const run = () => ${value};\n`)), true);
  }
  const options = { provenance: 'dict-evaluator-v1', actual: { cost: '1' }, sourceFrom: rawProgramSource };
  assert.deepEqual(await serve(work, worker, evaluate, options), ['accepted', 'accepted', 'accepted']);
  assert.deepEqual(await serve(work, worker, evaluate, options), []);
  const first = await work.result('1', 1);
  assert.equal(first.outcome, 'completed');
  assert.deepEqual(JSON.parse(first.payload).metrics, { combined_score: 3, path_is_file: true });
  const failed = await work.result('3', 1);
  assert.equal(failed.outcome, 'failed');
  assert.deepEqual(JSON.parse(failed.payload), { error: 'RangeError' });
}));

covering('result-objects-keep-metrics-and-artifacts', 'evaluation result objects keep metrics and artifacts and maxLeases bounds the loop', () => withHost(async (work, directory) => {
  const evaluate = await evaluatorModule(directory, RESULT_EVALUATOR);
  await work.enqueue(job('1', 'x = 1\n'));
  await work.enqueue(job('2', 'y = 22\n'));
  assert.deepEqual(await serve(work, worker, evaluate,
    { provenance: 'result-v1', actual: { cost: '1' }, maxLeases: 1, sourceFrom: rawProgramSource }), ['accepted']);
  const receipt = JSON.parse((await work.result('1', 1)).payload);
  assert.deepEqual(receipt.metrics, { combined_score: 6, inf: 'inf' }); // non-finite numbers survive as text
  assert.deepEqual(receipt.artifacts, { stderr: 'none' });
  assert.equal(await work.result('2', 1), null);
  for (const maxLeases of [0, -1, 1.5]) {
    await assert.rejects(serve(work, worker, evaluate, { provenance: 'result-v1', actual: { cost: '1' }, maxLeases }), RangeError);
  }
  assert.equal(await work.result('2', 1), null); // a refused call claims nothing
}));

covering('evaluator-without-evaluate-is-refused-at-load', 'an evaluator module without evaluate is refused at load', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'reference-worker-'));
  try {
    const path = join(directory, 'evaluator.mjs');
    await writeFile(path, 'export function score(path) { return {}; }\n', 'utf8');
    await assert.rejects(loadEvaluatorModule(path), TypeError);
    assert.throws(() => programFileEvaluator('not a function'), TypeError);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

covering('engine-bridge-lease-is-evaluated-as-its-genome-source', 'leases enqueued by the engine bridge are evaluated as their genome source', () => withHost(async (work, directory) => {
  const evaluate = await evaluatorModule(directory, DICT_EVALUATOR);
  assert.equal(await work.enqueue(job('1', envelope('1', 'export const run = () => 4;\n'))), true);
  assert.deepEqual(await serve(work, worker, evaluate, { provenance: 'engine-v1', actual: { cost: '1' } }), ['accepted']);
  assert.equal(JSON.parse((await work.result('1', 1)).payload).metrics.combined_score, 4);
}));

covering('unparseable-envelope-and-empty-result-are-failed-receipts', 'an unparseable envelope and an evaluator returning no JSON become failed receipts', () => withHost(async (work) => {
  await work.enqueue(job('1', 'not an envelope'));
  await work.enqueue(job('2', envelope('2', 'ignored')));
  const options = { provenance: 'failures-v1', actual: { cost: '1' } };
  assert.deepEqual(await serve(work, worker, () => undefined, options), ['accepted', 'accepted']);
  assert.equal((await work.result('1', 1)).outcome, 'failed');
  assert.deepEqual(JSON.parse((await work.result('2', 1)).payload), { error: 'TypeError' });
}));

covering('commit-failure-is-propagated-not-retried', 'a commit that fails is propagated, never retried by re-running the evaluator', () => withHost(async (work) => {
  await work.enqueue(job('1', envelope('1', 'source')));
  let calls = 0;
  const evaluate = async () => { calls += 1; await work.close(); return { score: 1 }; };
  await assert.rejects(serve(work, worker, evaluate, { provenance: 'closed-v1', actual: { cost: '1' } }));
  assert.equal(calls, 1);
}));

test('a claim on an undeclared behaviour is refused', () => {
  // Otherwise a typo in an id would read as coverage of something nobody declared.
  assert.throws(() => covering('no-such-behaviour', 'never registered', () => {}), /No such reference-worker behaviour/);
});

test('every declared reference-worker behaviour is covered by a test here', () => {
  const missing = [...declared].filter((behaviour) => !covered.has(behaviour)).sort();
  assert.deepEqual(missing, [], `declared reference-worker behaviours with no TypeScript test: ${missing}`);
});
