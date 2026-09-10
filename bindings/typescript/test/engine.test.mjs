import assert from 'node:assert/strict';
import test from 'node:test';

import { evolve, openSession, resolveHostBinary } from '../dist/index.js';

/**
 * These run the REAL NativeAOT host. Without a binary there is nothing to test, so they
 * skip rather than fail -- but CI builds the host first, so a skip there is a CI bug.
 */
let host = null;
try {
  host = resolveHostBinary();
} catch {
  host = null;
}

const skip = host ? false : 'no host binary; run `npm run host` first';

/** A quadratic with its optimum at (3, -1), which no seed sits on. */
const quality = ({ x, y }) => -((x - 3) ** 2 + (y + 1) ** 2);

const CONFIG = {
  parameters: [
    { name: 'x', min: -10, max: 10, step: 0.1 },
    { name: 'y', min: -10, max: 10, step: 0.1 },
  ],
  descriptors: [
    { name: 'x', min: -10, max: 10, bins: 20 },
    { name: 'y', min: -10, max: 10, bins: 20 },
  ],
  seed: 20260910,
  maxProposals: 400,
  maxEvaluations: 400,
  batchSize: 8,
};

test('a real run converges on the optimum of a quadratic', { skip }, async () => {
  const summary = await evolve(CONFIG, (candidates) =>
    candidates.map((candidate) => ({
      evaluationId: candidate.evaluationId,
      quality: quality(candidate.parameters),
      descriptors: candidate.parameters,
    }))
  );

  assert.ok(summary.best, 'a run of 400 evaluations must archive something');
  const { x, y } = summary.best.parameters;
  // Half a unit on a 20-wide range: comfortably better than the seed and far outside what
  // a search that was not actually optimising could reach by luck.
  assert.ok(Math.abs(x - 3) < 0.5, `x converged to ${x}, expected near 3`);
  assert.ok(Math.abs(y + 1) < 0.5, `y converged to ${y}, expected near -1`);
  assert.ok(summary.best.quality > -0.5, `quality ${summary.best.quality} is too poor`);
});

test('the same seed replays the same search', { skip }, async () => {
  const run = () =>
    evolve({ ...CONFIG, maxProposals: 80, maxEvaluations: 80 }, (candidates) =>
      candidates.map((candidate) => ({
        evaluationId: candidate.evaluationId,
        quality: quality(candidate.parameters),
        descriptors: candidate.parameters,
      }))
    );

  const first = await run();
  const second = await run();
  assert.deepEqual(second.best?.parameters, first.best?.parameters);
  assert.equal(second.best?.quality, first.best?.quality);
});

test('a failed evaluation is not scored zero', { skip }, async () => {
  // Everything fails, so nothing can enter the archive. Were a failure treated as a
  // quality of zero, every candidate would archive and `best` would be non-null -- which
  // is exactly the bug this asserts against.
  const summary = await evolve({ ...CONFIG, maxProposals: 40, maxEvaluations: 40 }, (candidates) =>
    candidates.map((candidate) => ({
      evaluationId: candidate.evaluationId,
      reason: 'the build did not compile',
    }))
  );
  assert.equal(summary.best, null);
});

test('minimize inverts which candidate wins', { skip }, async () => {
  const summary = await evolve(
    { ...CONFIG, direction: 'minimize', maxProposals: 200, maxEvaluations: 200 },
    (candidates) =>
      candidates.map((candidate) => ({
        evaluationId: candidate.evaluationId,
        // Minimising this is the same problem as maximising the quadratic above.
        quality: -quality(candidate.parameters),
        descriptors: candidate.parameters,
      }))
  );
  assert.ok(summary.best, 'a minimising run archives too');
  assert.ok(Math.abs(summary.best.parameters.x - 3) < 0.5);
  assert.ok(Math.abs(summary.best.parameters.y + 1) < 0.5);
});

test('the run honours its evaluation budget', { skip }, async () => {
  let evaluated = 0;
  const session = await openSession({ ...CONFIG, maxProposals: 1000, maxEvaluations: 48 });
  try {
    for (;;) {
      const batch = await session.ask(8);
      if (batch.length === 0) break;
      evaluated += batch.length;
      await session.tell(
        batch.map((candidate) => ({
          evaluationId: candidate.evaluationId,
          quality: quality(candidate.parameters),
          descriptors: candidate.parameters,
        }))
      );
    }
  } finally {
    await session.close();
  }
  assert.ok(evaluated > 0, 'the run must actually produce candidates');
  assert.ok(
    evaluated <= 48,
    `evaluated ${evaluated} with a budget of 48; the cap is not being applied`
  );
});
