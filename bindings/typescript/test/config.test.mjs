import assert from 'node:assert/strict';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { EvolutionError, openSession } from '../dist/index.js';

const config = {
  parameters: [{ name: 'x', min: 0, max: 1 }],
  descriptors: [{ name: 'd', min: 0, max: 1 }],
  hostPath: process.execPath,
  hostArgs: [fileURLToPath(new URL('fake-host.mjs', import.meta.url)), 'refuse-open'],
};

for (const [field, minimum, maximum] of [
  ['seed', 0, Number.MAX_SAFE_INTEGER],
  ['maxProposals', 1, 2_147_483_647],
  ['maxEvaluations', 0, 2_147_483_647],
  ['maxGenerations', 0, 2_147_483_647],
  ['batchSize', 1, 2_147_483_647],
  ['requestTimeoutMs', 1, 2_147_483_647],
]) {
  for (const value of [minimum - 1, maximum + 1, 1.5, NaN, Infinity]) {
    test(`${field} rejects ${value} before launching a host`, async () => {
      // An invalid config must fail with its field, never the fake host's refusal.
      await assert.rejects(() => openSession({ ...config, [field]: value }), (error) => {
        assert.ok(error instanceof EvolutionError);
        assert.match(error.message, new RegExp(`^${field} must be `));
        return true;
      });
    });
  }
}

test('zero evaluations and seed-only generations survive validation', async () => {
  await assert.rejects(() => openSession({
    ...config, seed: 0, maxEvaluations: 0, maxGenerations: 0,
  }), /the fake host refuses to open/);
});

test('the maximum representable timeout is usable, not clamped to one millisecond', async () => {
  await assert.rejects(() => openSession({
    ...config, requestTimeoutMs: 2_147_483_647,
  }), /the fake host refuses to open/);
});
