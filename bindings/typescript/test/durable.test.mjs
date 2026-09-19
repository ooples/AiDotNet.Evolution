import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { DurableWorkClient, parseDurableEvaluationPayload } from '../dist/index.js';

const transport = process.env.AIDOTNET_DURABLE_HOST_DLL
  ? { hostPath: 'dotnet', hostArgs: [process.env.AIDOTNET_DURABLE_HOST_DLL] } : {};
const config = directory => ({ directory, runId: 'run', compatibilityHash: 'compat', limits: { cost: '100' }, ...transport });
const job = { evaluationId: '9223372036854775807', attempt: 1, canonicalGenomeId: 'integer:7', payload: '7', estimated: { cost: '1' }, maximum: { cost: '5' } };
const worker = workerId => ({ workerId, compatibilityHash: 'compat' });
const receipt = lease => ({ identity: lease.identity, workerId: lease.workerId, payload: '49', provenance: 'local-square-v1', actual: { cost: '0.1234567890123456789012345678' }, outcome: 'completed' });
const fake = mode => ({ ...config('unused'), hostPath: process.execPath,
  hostArgs: [fileURLToPath(new URL('fake-durable-host.mjs', import.meta.url)), mode], requestTimeoutMs: 1500 });

test('durable live host preserves Int64 identities and exact decimal receipts across reopen', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'evolution-ts-durable-')); let client;
  try {
    client = await DurableWorkClient.open(config(directory));
    assert.equal(await client.enqueue(job), true); assert.equal(await client.enqueue(job), false);
    const lease = await client.claim(worker('one')); assert.equal(lease.identity.evaluationId, job.evaluationId);
    assert.equal(await client.claim(worker('two')), null);
    assert.equal(await client.heartbeat(lease.identity, 'one'), 'renewed');
    await client.close();
    client = await DurableWorkClient.open(config(directory));
    assert.equal((await client.status()).wasRecovered, true);
    const recovered = await client.unsettled('one'); assert.deepEqual(recovered.lease.identity, lease.identity);
    assert.equal((await client.unsettled('one', recovered.nextAfterLeaseId)).lease, null);
    assert.equal(await client.commit(receipt(lease)), 'accepted'); assert.equal(await client.commit(receipt(lease)), 'duplicate');
    assert.equal((await client.delivery(lease.identity, 'one')).actual.cost, '0.1234567890123456789012345678');
    await client.close();
    client = await DurableWorkClient.open(config(directory));
    assert.equal((await client.result(job.evaluationId, 1)).payload, '49');
    assert.equal((await client.status()).settled, '1'); assert.equal((await client.status()).reserved.cost, '0');
  } finally { await client?.close().catch(() => {}); await rm(directory, { recursive: true, force: true }); }
});

test('real coordinator process killed after dispatch retains the original reservation', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'evolution-ts-kill-')); let client;
  try {
    client = await DurableWorkClient.open(config(directory)); await client.enqueue(job);
    const lease = await client.claim(worker('worker')); process.kill(client.pid, 'SIGKILL');
    await assert.rejects(() => client.status()); await client.close().catch(() => {}); await delay(100);
    client = await DurableWorkClient.open(config(directory));
    assert.equal((await client.status()).reserved.cost, '5');
    assert.deepEqual((await client.unsettled('worker')).lease.identity, lease.identity);
    assert.equal(await client.claim(worker('worker')), null);
    assert.equal(await client.commit(receipt(lease)), 'accepted');
    assert.equal((await client.status()).settled, '1');
  } finally { await client?.close().catch(() => {}); await rm(directory, { recursive: true, force: true }); }
});

test('hardware matching and cancellation preserve unresolved physical cost', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'evolution-ts-cancel-')); let client;
  try {
    client = await DurableWorkClient.open(config(directory));
    await client.enqueue({ ...job, tags: ['gpu'], minimumResources: { gpu_slots: '1' } });
    assert.equal(await client.claim(worker('cpu')), null);
    const lease = await client.claim({ ...worker('gpu'), tags: ['gpu'], capacity: { gpu_slots: '1' } });
    assert.equal(await client.cancel(job.evaluationId, 1), true);
    assert.equal(await client.heartbeat(lease.identity, 'gpu'), 'canceled');
    assert.equal((await client.status()).reserved.cost, '5');
    assert.equal(await client.commit(receipt(lease)), 'stale'); assert.equal(await client.commit(receipt(lease)), 'duplicate-stale');
    assert.equal(await client.result(job.evaluationId, 1), null);
  } finally { await client?.close().catch(() => {}); await rm(directory, { recursive: true, force: true }); }
});

test('evaluation context retains every seed bit and refuses lossy numeric seeds', () => {
  const payload = { schema: 1, genomePayload: '7', canonicalGenomeId: 'integer:7', evaluationId: job.evaluationId,
    attempt: 2, rootSeed: '18446744073709551615', seedStream: '18446744073709551614' };
  assert.deepEqual(parseDurableEvaluationPayload(JSON.stringify(payload)), payload);
  for (const mutation of [{ rootSeed: 18446744073709551615 }, { rootSeed: '01' }, { rootSeed: '18446744073709551616' },
    { evaluationId: 9223372036854775807 }, { schema: 2 }, { attempt: 0 }]) {
    assert.throws(() => parseDurableEvaluationPayload(JSON.stringify({ ...payload, ...mutation })));
  }
});

test('durable handshake rejects a downgraded host', async () => {
  await assert.rejects(() => DurableWorkClient.open(fake('downgrade')), /downgrade/);
});
for (const [mode, message] of [['invalid-claim', /Invalid claim/], ['uncorrelated', /Uncorrelated/], ['oversized', /Oversized/], ['timeout', /timed out/]]) {
  test(`durable transport fails closed on ${mode}`, async () => {
    const client = await DurableWorkClient.open(fake(mode));
    await assert.rejects(() => client.claim(worker('one')), message);
    await assert.rejects(() => client.close());
  });
}
test('resource numbers and overflowing timers are rejected before process creation', async () => {
  await assert.rejects(() => DurableWorkClient.open({ ...fake('timeout'), limits: { cost: 100 } }), /decimal-string/);
  await assert.rejects(() => DurableWorkClient.open({ ...fake('timeout'), requestTimeoutMs: 2147483648 }), /Int32/);
});
