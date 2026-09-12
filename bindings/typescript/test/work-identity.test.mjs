import assert from 'node:assert/strict';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { evolve, openSession, resolveHostBinary } from '../dist/index.js';

const identity = { taskId: 'test', taskVersionHash: 'v1', evaluatorVersionHash: 'eval-v1' };
const config = {
  parameters: [{ name: 'x', min: -1, max: 1, step: .5 }],
  descriptors: [{ name: 'x', min: -1, max: 1, bins: 4 }],
  taskIdentity: identity, maxProposals: 1, maxEvaluations: 1, maxGenerations: 0,
};
const fake = (mode) => ({ ...config, hostPath: process.execPath,
  hostArgs: [fileURLToPath(new URL('fake-host.mjs', import.meta.url)), mode] });
const score = (candidate) => ({ evaluationId: candidate.evaluationId,
  workIdentity: candidate.workIdentity, quality: 1, descriptors: { x: 0 } });

test('strict mode rejects a host that silently ignores fencing', async () => {
  await assert.rejects(() => openSession(fake('ok')), /refusing a protocol downgrade/);
});

test('strict mode refuses missing ask tickets and tears down the session', async () => {
  const session = await openSession(fake('strict-missing-ticket'));
  await assert.rejects(() => session.ask(), /without workIdentity/);
  await assert.rejects(() => session.close(), /without workIdentity/);
});

test('strict binding rejects invalid tell envelopes without consuming valid work', async () => {
  const session = await openSession(fake('strict'));
  try {
    assert.equal(session.compatibilityHash, 'a'.repeat(64));
    const [candidate] = await session.ask(1);
    for (const workIdentity of [undefined, null, {}, { ...candidate.workIdentity, attempt: 0 },
      { ...candidate.workIdentity, runId: ' ' }, { ...candidate.workIdentity, leaseId: 'x'.repeat(32) },
      { ...candidate.workIdentity, evaluationId: candidate.evaluationId + 1 }]) {
      await assert.rejects(() => session.tell([{ ...score(candidate), workIdentity }]), /original candidate workIdentity/);
    }
    assert.equal(await session.tell([score(candidate)]), 1);
  } finally { await session.close(); }
});

test('evolve preserves evaluator-supplied tickets end to end', async () => {
  const summary = await evolve(fake('strict'), candidates => candidates.map(score));
  assert.equal(summary.stopReason, 'Fake');
});

for (const taskIdentity of [null, {}, { ...identity, taskId: ' ' },
  { ...identity, evaluatorVersionHash: 'x'.repeat(1025) }]) {
  test(`reject malformed identity before spawning: ${JSON.stringify(taskIdentity).slice(0, 90)}`, async () => {
    await assert.rejects(() => openSession({ ...fake('refuse-open'), taskIdentity }), /taskIdentity requires/);
  });
}
test('retries cannot opt into an unfenced legacy session', async () => {
  await assert.rejects(() => openSession({ ...fake('refuse-open'), taskIdentity: undefined, maxRetries: 1 }), /maxRetries requires/);
});

let host;
try { host = resolveHostBinary(); } catch { /* CI separately rejects skipped real-host tests. */ }
const skip = host ? false : 'no host binary; run npm run host first';

test('real strict host fences cross-session results and echoes fingerprints', { skip }, async () => {
  const a = await openSession(config);
  const b = await openSession(config);
  try {
    assert.match(a.compatibilityHash, /^[0-9a-f]{64}$/);
    assert.equal(a.compatibilityHash, b.compatibilityHash);
    const [first] = await a.ask(1); const [second] = await b.ask(1);
    assert.notEqual(first.workIdentity.leaseId, second.workIdentity.leaseId);
    assert.equal(await a.tell([score(second)]), 0);
    assert.equal(await a.tell([score(first)]), 1);
    assert.equal(await a.tell([score(first)]), 0);
    assert.equal(await b.tell([score(second)]), 1);
    assert.deepEqual(await a.ask(1), []);
    assert.equal((await a.close()).best.quality, 1);
  } finally { await Promise.all([a.close(), b.close()]); }
});

test('real strict host rejects an expired ticket while accepting its retry', { skip }, async () => {
  const session = await openSession({ ...config, maxEvaluations: 2, maxRetries: 1, evaluationTimeoutMs: 2000 });
  try {
    const [first] = await session.ask(1);
    const [retry] = await session.ask(1);
    assert.equal(first.evaluationId, retry.evaluationId);
    assert.equal(retry.workIdentity.attempt, 2);
    assert.equal(await session.tell([score(first)]), 0);
    assert.equal(await session.tell([score(retry)]), 1);
    assert.deepEqual(await session.ask(1), []);
    assert.equal((await session.close()).best.quality, 1);
  } finally { await session.close(); }
});
