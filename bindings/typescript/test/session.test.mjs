import assert from 'node:assert/strict';
import test from 'node:test';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { EvolutionError, evolve, openSession } from '../dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const FAKE = join(here, 'fake-host.mjs');

const CONFIG = {
  parameters: [{ name: 'x', min: 0, max: 1 }],
  descriptors: [{ name: 'd', min: 0, max: 1 }],
  hostPath: process.execPath,
};

const withMode = (mode, extra = {}) => ({ ...CONFIG, hostArgs: [FAKE, mode], ...extra });

/** Asks the OS, rather than trusting a flag we set ourselves. */
const alive = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    // EPERM means it exists but belongs to somebody else, which still counts as alive.
    return error.code === 'EPERM';
  }
};

const waitForExit = async (pid) => {
  for (let attempt = 0; attempt < 100 && alive(pid); attempt += 1) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
};


test('a run drains its candidates and ends with an empty batch', async () => {
  const session = await openSession(withMode('ok'));
  try {
    const first = await session.ask(4);
    assert.equal(first.length, 4);
    assert.equal(await session.tell(first.map((c) => ({ evaluationId: c.evaluationId, quality: 1 }))), 4);

    const second = await session.ask(4);
    assert.equal(second.length, 2, 'the fake host has six candidates in total');
    assert.equal((await session.ask(4)).length, 0, 'an empty batch is the completion signal');
  } finally {
    await session.close();
  }
});

test('close returns the best genome and the stop reason', async () => {
  const session = await openSession(withMode('ok'));
  const summary = await session.close();
  assert.deepEqual(summary.best, { evaluationId: 1, parameters: { x: 0 }, quality: 1 });
  assert.equal(summary.stopReason, 'Fake');
});

test('close is idempotent', async () => {
  const session = await openSession(withMode('ok'));
  assert.equal((await session.close()).stopReason, 'Fake');
  // The second call must neither throw nor wait for a response that is never coming.
  assert.deepEqual(await session.close(), { best: null, stopReason: null });
});

test('a host that will not start reports its exit code and stderr', async () => {
  await assert.rejects(() => openSession(withMode('die-on-start')), (error) => {
    assert.ok(error instanceof EvolutionError, `expected an EvolutionError, got ${error}`);
    assert.match(error.message, /exited unexpectedly/);
    assert.match(error.message, /code 3/);
    assert.match(error.message, /refusing to start/, 'stderr is what says WHY it exited');
    return true;
  });
});

test('a host refusing to open surfaces its own error', async () => {
  await assert.rejects(
    () => openSession(withMode('refuse-open')),
    /the fake host refuses to open/
  );
});

test('a host that dies mid-ask fails the pending request rather than hanging', async () => {
  const session = await openSession(withMode('die-on-ask'));
  // Without the exit handler failing every waiter, this await never settles: the promise
  // is pending on a process that no longer exists.
  await assert.rejects(() => session.ask(4), /exited unexpectedly \(code 4/);
  // And closing a session whose host is already gone REPORTS that rather than returning
  // an empty summary, which would read as "the run finished and found nothing".
  await assert.rejects(() => session.close(), /exited unexpectedly \(code 4/);
});

test('an unparseable line is ignored and the real response still arrives', async () => {
  const session = await openSession(withMode('garbage-before-ask'));
  try {
    const batch = await session.ask(2);
    assert.equal(batch.length, 2);
  } finally {
    await session.close();
  }
});

test('non-object JSON cannot crash the client before the real response', { timeout: 5000 }, async () => {
  const session = await openSession(withMode('non-object-before-ask', { requestTimeoutMs: 1000 }));
  try {
    const batch = await session.ask(2);
    assert.equal(batch.length, 2);
    assert.deepEqual(batch.map((candidate) => candidate.evaluationId), [1, 2]);
  } finally {
    await session.close();
  }
});

test('an invalid response envelope cannot complete a pending request', async () => {
  const session = await openSession(withMode('invalid-response-before-ask', { requestTimeoutMs: 1000 }));
  try {
    const batch = await session.ask(2);
    assert.equal(batch.length, 2, 'a string ok value must not turn an invalid frame into end-of-run');
  } finally {
    await session.close();
  }
});

test('every request after the host dies fails immediately', async () => {
  const session = await openSession(withMode('die-on-ask'));
  await assert.rejects(() => session.ask(1));
  // The second call must not spawn a fresh 120s timeout on a dead process.
  const started = Date.now();
  await assert.rejects(() => session.ask(1), /exited unexpectedly/);
  assert.ok(Date.now() - started < 1000, 'a known-dead host is failed without waiting');
  await assert.rejects(() => session.close(), /exited unexpectedly/);
});

const withInvalidPayload = (op, payload, mode = 'before') => withMode('unused', {
  hostArgs: [FAKE, `invalid-payload-${mode}-${op}`, JSON.stringify(payload)],
  requestTimeoutMs: 1000,
});

for (const [name, payload] of [
  ['missing candidates', { complete: true }],
  ['null candidates', { candidates: null, complete: true }],
  ['non-array candidates', { candidates: {}, complete: false }],
  ['null candidate', { candidates: [null], complete: false }],
  ['unsafe evaluation id', { candidates: [{ evaluationId: 9007199254740992, parameters: { x: 0 } }], complete: false }],
  ['invalid parameter map', { candidates: [{ evaluationId: 1, parameters: [] }], complete: false }],
  ['invalid parameter value', { candidates: [{ evaluationId: 1, parameters: { x: null } }], complete: false }],
  ['invalid quality', { candidates: [{ evaluationId: 1, parameters: { x: 0 }, quality: 'good' }], complete: false }],
  ['missing completion flag', { candidates: [] }],
  ['false completion flag', { candidates: [], complete: false }],
]) {
  test(`ask ignores ${name} without losing its real response`, async () => {
    const session = await openSession(withInvalidPayload('ask', payload));
    try {
      assert.deepEqual(await session.ask(2), [
        { evaluationId: 1, parameters: { x: 0 } },
        { evaluationId: 2, parameters: { x: 1 } },
      ]);
    } finally {
      await session.close();
    }
  });
}

for (const [name, payload] of [
  ['missing count', {}],
  ['negative count', { accepted: -1 }],
  ['fractional count', { accepted: 0.5 }],
  ['non-numeric count', { accepted: '2' }],
]) {
  test(`tell ignores a ${name} without losing its real response`, async () => {
    const session = await openSession(withInvalidPayload('tell', payload));
    try {
      const batch = await session.ask(2);
      assert.equal(await session.tell(batch.map((c) => ({ evaluationId: c.evaluationId, quality: 1 }))), 2);
    } finally {
      await session.close();
    }
  });
}

for (const [name, payload] of [
  ['invalid best', { best: 1, stopReason: 'Fake' }],
  ['invalid best quality', { best: { evaluationId: 1, parameters: { x: 0 }, quality: 'good' }, stopReason: 'Fake' }],
  ['invalid stop reason', { stopReason: 1 }],
  ['missing stop reason', {}],
]) {
  test(`close ignores a payload with ${name} without reporting a false result`, async () => {
    const session = await openSession(withInvalidPayload('close', payload));
    assert.deepEqual(await session.close(), {
      best: { evaluationId: 1, parameters: { x: 0 }, quality: 1 },
      stopReason: 'Fake',
    });
  });
}

for (const version of [undefined, null, 1]) {
  test(`open cannot accept an invalid version (${version})`, async () => {
    let session;
    try {
      await assert.rejects(async () => {
        session = await openSession(withInvalidPayload('open', { version }));
      }, /valid rejection after malformed open/);
    } finally {
      await session?.close();
    }
  });
}

test('a malformed-only ask times out and terminates its host instead of completing empty', async () => {
  const session = await openSession(withInvalidPayload('ask', {}, 'only'));
  try {
    await assert.rejects(() => session.ask(1), /did not answer 'ask'/);
  } finally {
    await session.close().catch(() => undefined);
  }
  await waitForExit(session.pid);
  assert.equal(alive(session.pid), false);
});

test('evolve drives the loop and returns the summary', async () => {
  const seen = [];
  const summary = await evolve(withMode('ok', { batchSize: 3 }), (candidates) => {
    seen.push(candidates.length);
    return candidates.map((c) => ({ evaluationId: c.evaluationId, quality: c.parameters.x }));
  });
  assert.deepEqual(seen, [3, 3], 'six candidates in batches of three');
  assert.equal(summary.stopReason, 'Fake');
});

test('evolve closes the host when the evaluator throws', async () => {
  await assert.rejects(
    () =>
      evolve(withMode('ok'), () => {
        throw new Error('the evaluator exploded');
      }),
    /the evaluator exploded/
  );
  // The fake host in this mode waits on stdin forever, so a leaked child would keep the
  // test runner's event loop alive and hang the whole suite at exit. That is the proof,
  // and it is why the failure mode of removing evolve's finally is a hang, not a red test.
});

test('closing a session actually terminates the host process', async () => {
  const session = await openSession(withMode('ok'));
  const { pid } = session;
  assert.ok(pid, 'the host must have started');
  assert.ok(alive(pid), 'and be running before close');

  await session.close();
  await waitForExit(pid);
  assert.equal(alive(pid), false, 'the child outlives close() otherwise');
});

test('a host that dies before answering close is reported, not reported as empty', async () => {
  const session = await openSession(withMode('ok'));
  // Kill the host out from under the session, so close() can never be answered.
  process.kill(session.pid, 'SIGKILL');
  await waitForExit(session.pid);

  // The failure mode this guards: returning { best: null, stopReason: null } here says
  // "the run finished and found nothing", which is a different and much worse claim than
  // "nobody answered".
  await assert.rejects(() => session.close(), (error) => {
    assert.ok(error instanceof EvolutionError);
    assert.match(error.message, /exited/);
    return true;
  });
});

test('evolve rejects rather than resolving empty when close is never answered', async () => {
  // THE EXACT SHAPE OF THE BUG: every ask and tell succeeds, the run reaches its end
  // normally, and only the close goes unanswered. Swallowing that made evolve resolve
  // with { best: null, stopReason: null } -- a completed search that found nothing.
  await assert.rejects(
    () =>
      evolve(withMode('die-on-close'), (candidates) =>
        candidates.map((c) => ({ evaluationId: c.evaluationId, quality: 1 }))
      ),
    /exited while closing \(code 5/
  );
});

test('an evaluator error survives a failing close', async () => {
  // BOTH MUST ACTUALLY FAIL, or this proves nothing. Against a host that closes
  // cleanly there is only one error to report and any implementation passes; the
  // question is which error survives when the cleanup fails too, so the host is one
  // that dies on close.
  await assert.rejects(
    () =>
      evolve(withMode('die-on-close'), () => {
        throw new Error('the evaluator exploded');
      }),
    /the evaluator exploded/
  );
});

test('a request that is never answered kills the host instead of leaking it', async () => {
  // silent-on-ask never replies, so only the timeout can end this.
  const session = await openSession(withMode('silent-on-ask', { requestTimeoutMs: 300 }));
  const { pid } = session;

  await assert.rejects(() => session.ask(1), /did not answer 'ask' within 300ms/);

  // Without the kill the child stays alive with nobody holding a reference to it, and
  // repeated failed opens leak one host process each.
  await waitForExit(pid);
  assert.equal(alive(pid), false, 'the host outlives a timeout otherwise');
});

test('an unsuccessful close response is an error, not an empty summary', async () => {
  // The transport is fine here and the host answered -- it answered `ok: false`.
  // Reading that as `{best: null, stopReason: null}` makes the same wrong claim a
  // swallowed transport failure did, arriving through the other door.
  const session = await openSession(withMode('refuse-close'));

  await assert.rejects(() => session.close(), (error) => {
    assert.ok(error instanceof EvolutionError);
    assert.match(error.message, /the run could not be stopped/);
    return true;
  });

  // ...and the host is still torn down, because a caller who cannot stop the run
  // cleanly must not be left holding the process either.
  await waitForExit(session.pid);
  assert.equal(alive(session.pid), false);
});
