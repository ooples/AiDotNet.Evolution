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

test('integral parameters remain integral with fractional bounds', { skip }, async () => {
  const session = await openSession({
    parameters: [{ name: 'x', min: 0.1, max: 2.4, step: 1, integral: true }],
    descriptors: [{ name: 'x', min: 0, max: 3, bins: 3 }],
    seeds: [{ x: 0.1 }],
    maxProposals: 1,
    maxEvaluations: 1,
  });
  try {
    const batch = await session.ask(1);
    assert.equal(batch.length, 1);
    const value = batch[0].parameters.x;
    assert.ok(Number.isInteger(value), `integral parameter returned ${value}`);
    assert.ok(value >= 1 && value <= 2, 'the candidate must be an integer inside the declared bounds');
  } finally {
    await session.close();
  }
});

test('a binary integral search can leave its seed with the default step', { skip }, async () => {
  const seen = [];
  await evolve({
    parameters: [{ name: 'x', min: 0, max: 1, integral: true }],
    descriptors: [{ name: 'x', min: 0, max: 1, bins: 2 }],
    seeds: [{ x: 1 }],
    seed: 1234,
    maxProposals: 10,
    maxEvaluations: 10,
    batchSize: 1,
  }, (candidates) => candidates.map((candidate) => {
    seen.push(candidate.parameters.x);
    return {
      evaluationId: candidate.evaluationId,
      quality: candidate.parameters.x,
      descriptors: candidate.parameters,
    };
  }));
  assert.deepEqual([...new Set(seen)].sort((a, b) => a - b), [0, 1],
    'a representable alternative must be evaluated, not lost as repeated parent proposals');
});

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

test('a pending real-host ask cannot block the tell needed to produce its batch', { skip }, async () => {
  const session = await openSession({ ...CONFIG, batchSize: 1, requestTimeoutMs: 3000 });
  try {
    const first = await session.ask(1);
    assert.equal(first.length, 1);
    // Register both outcomes immediately: the old host deadlocked here, so the
    // timeout must remain an asserted failure, not an unhandled rejection.
    const next = session.ask(1).then((batch) => ({ batch }), (error) => ({ error }));
    assert.equal(await session.tell(first.map((candidate) => ({
      evaluationId: candidate.evaluationId,
      quality: quality(candidate.parameters),
      descriptors: candidate.parameters,
    }))), 1);
    const result = await next;
    assert.ok('batch' in result, `pending ask failed: ${result.error}`);
    assert.equal(result.batch.length, 1);
    assert.notEqual(result.batch[0].evaluationId, first[0].evaluationId);
  } finally {
    await session.close().catch(() => undefined);
  }
});

test('closing the real host cancels a pending ask without reporting completion', { skip }, async () => {
  const session = await openSession({ ...CONFIG, batchSize: 1, requestTimeoutMs: 3000 });
  try {
    assert.equal((await session.ask(1)).length, 1);
    const next = session.ask(1).then((batch) => ({ batch }), (error) => ({ error }));
    const summary = await session.close();
    assert.ok(summary.stopReason, 'the host must confirm that the run stopped');
    const result = await next;
    assert.ok('error' in result, 'a canceled ask must not be an empty successful batch');
    assert.match(result.error.message, /request was canceled/);
  } finally {
    await session.close().catch(() => undefined);
  }
});

test('the same seed replays the same search', { skip }, async () => {
  // THE WHOLE TRAJECTORY, not just where it ended up. Two searches that visit entirely
  // different candidates can still converge on the same optimum of a smooth function --
  // so comparing `best` alone passes against a regression that changed which candidates
  // were proposed, which is exactly what a seed is supposed to pin.
  const run = async () => {
    const seen = [];
    const summary = await evolve(
      { ...CONFIG, maxProposals: 80, maxEvaluations: 80 },
      (candidates) => {
        seen.push(candidates.map((c) => ({ id: c.evaluationId, at: c.parameters })));
        return candidates.map((candidate) => ({
          evaluationId: candidate.evaluationId,
          quality: quality(candidate.parameters),
          descriptors: candidate.parameters,
        }));
      }
    );
    return { seen, summary };
  };

  const first = await run();
  const second = await run();

  assert.ok(first.seen.length > 1, 'a replay test needs more than one batch to compare');
  assert.deepEqual(second.seen, first.seen, 'the candidate sequence diverged');
  assert.deepEqual(second.summary.best?.parameters, first.summary.best?.parameters);
  assert.equal(second.summary.best?.quality, first.summary.best?.quality);
});

test('a different seed produces a different search', { skip }, async () => {
  // The control for the test above: if every seed produced the same sequence, comparing
  // two runs of one seed would prove nothing at all.
  const run = async (seed) => {
    const seen = [];
    await evolve({ ...CONFIG, seed, maxProposals: 80, maxEvaluations: 80 }, (candidates) => {
      seen.push(candidates.map((c) => c.parameters));
      return candidates.map((candidate) => ({
        evaluationId: candidate.evaluationId,
        quality: quality(candidate.parameters),
        descriptors: candidate.parameters,
      }));
    });
    return seen;
  };

  assert.notDeepEqual(await run(20260910), await run(20260911));
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
