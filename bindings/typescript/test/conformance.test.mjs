// The shared durable-worker conformance scenarios (conformance/durable-v1/scenarios.json), run through the
// TypeScript client against the real host. The C# endpoint and the Python client run the same file.
import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DurableWorkClient } from '../dist/index.js';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const suite = JSON.parse(readFileSync(join(root, 'conformance', 'durable-v1', 'scenarios.json'), 'utf8'));
// An explicit host wins; then a local Release build of the managed host; otherwise the client's own bundled
// binary (CI publishes it to bin/<rid>). Never a skip: check-test-output.mjs fails a run that skips.
const dll = process.env.AIDOTNET_DURABLE_HOST_DLL
  ?? join(root, 'src', 'AiDotNet.Evolution.Host', 'bin', 'Release', 'net10.0', 'aidotnet-evolution-host.dll');
const host = process.env.AIDOTNET_DURABLE_HOST_PATH;
const transport = host ? { hostPath: host } : existsSync(dll) ? { hostPath: 'dotnet', hostArgs: [dll] } : {};

const clone = value => structuredClone(value);
function substitute(value, directory, lease) {
  if (value === '$dir') return directory;
  if (value === '$config') return substitute(clone(suite.config), directory, lease);
  if (value === '$job') return clone(suite.job);
  if (typeof value === 'string' && value.startsWith('$lease')) {
    let node = lease;
    for (const part of value.slice('$lease'.length).split('.').filter(Boolean)) node = node[part];
    return clone(node);
  }
  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, substitute(v, directory, lease)]));
  }
  return value;
}

const lookup = (reply, path) => path.split('.').reduce(
  (node, part) => (node !== null && typeof node === 'object' ? node[part] : undefined), reply);

// The client's typed return, re-expressed as the protocol reply fields the scenarios assert on.
async function call(client, op, args) {
  switch (op) {
    case 'enqueue': return { enqueued: await client.enqueue(args.job) };
    case 'claim': { const lease = await client.claim(args.worker); return { available: lease !== null, lease }; }
    case 'heartbeat': return { status: await client.heartbeat(args.identity, args.workerId) };
    case 'cancel': return { canceled: await client.cancel(args.evaluationId, args.attempt) };
    case 'commit': return { disposition: await client.commit(args) };
    case 'result': return { result: await client.result(args.evaluationId, args.attempt) };
    case 'delivery': return { result: await client.delivery(args.identity, args.workerId) };
    case 'close': await client.close(); return { closed: true };
    default: throw new Error(`unmapped op ${op}`);
  }
}

for (const scenario of suite.scenarios) {
  test(`conformance: ${scenario.name}`, async () => {
    const directory = await mkdtemp(join(tmpdir(), 'durable-conformance-'));
    let client; let lease;
    try {
      for (const [position, step] of scenario.steps.entries()) {
        const where = `${scenario.name} step ${position + 1} (${step.op})`;
        const args = Object.fromEntries(Object.entries(step.args).map(([k, v]) => [k, substitute(v, directory, lease)]));
        const expect = step.expect;
        let reply;
        try {
          if (step.op === 'open') {
            client = await DurableWorkClient.open({ ...args.config, ...transport });
            reply = await client.status();
          } else {
            reply = await call(client, step.op, args);
          }
        } catch (error) {
          assert.ok('$error' in expect, `${where} failed: ${error}`);
          // A refusal must be the host's ok:false protocol error, not a dead or crashed host.
          assert.match(error.message, /^\w+Exception: /, `${where}: not a protocol refusal: ${error}`);
          continue;
        }
        assert.ok(!('$error' in expect), `${where} should have been refused`);
        if (step.op === 'claim' && reply.lease !== null) lease = reply.lease;
        for (const [path, expected] of Object.entries(expect)) {
          assert.deepEqual(lookup(reply, path), expected, `${where}: ${path}`);
        }
      }
    } finally {
      if (client !== undefined) await client.close().catch(() => undefined);
      await rm(directory, { recursive: true, force: true });
    }
  });
}
