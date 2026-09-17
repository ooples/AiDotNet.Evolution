/**
 * A stand-in for the NativeAOT host, so the client's protocol handling can be tested
 * without a .NET toolchain and without the real engine's timing.
 *
 * The real binary cannot produce these situations on demand -- it will not crash mid-ask
 * or emit a malformed line to order -- and those are exactly the paths where a client bug
 * shows up as a hang rather than a failure.
 *
 * Usage: node fake-host.mjs <mode>
 */

import { createInterface } from 'node:readline';

const mode = process.argv[2] ?? 'ok';
const invalidPayload = process.argv[3] ? JSON.parse(process.argv[3]) : {};

/**
 * Exits only once the write has reached the pipe.
 *
 * `process.exit()` does not wait. A write to a pipe is asynchronous, so exiting straight
 * after one can discard it -- and the tests that read these diagnostics, or the close
 * response, would then fail intermittently for a reason unrelated to what they test.
 */
const writeThenExit = (stream, text, code) => {
  stream.write(text, () => process.exit(code));
};

if (mode === 'die-on-start') {
  // AND NOTHING ELSE IS SET UP. `writeThenExit` exits from the write callback, so the
  // process is briefly alive afterwards -- long enough to install a readline handler
  // and answer an `open` that had already arrived. The fixture would then sometimes
  // start successfully, which is the one thing it exists not to do.
  writeThenExit(process.stderr, 'fake host refusing to start\n', 3);
} else {
  serve();
}

function serve() {

const say = (value) => process.stdout.write(`${JSON.stringify(value)}\n`);
let nextEvaluationId = 1;
let remaining = 6;

createInterface({ input: process.stdin }).on('line', (line) => {
  if (!line) return;
  const request = JSON.parse(line);

  switch (request.op) {
    case 'open':
      if (mode === 'invalid-payload-before-open') {
        say({ id: request.id, ok: true, ...invalidPayload });
        say({ id: request.id, ok: false, error: 'valid rejection after malformed open' });
        return;
      }
      if (mode === 'refuse-open') {
        say({ id: request.id, ok: false, error: 'the fake host refuses to open' });
        return;
      }
      say({ id: request.id, ok: true, version: 'fake' });
      return;

    case 'ask': {
      if (mode === 'die-on-ask') {
        writeThenExit(process.stderr, 'fake host died during ask\n', 4);
        return;
      }
      if (mode === 'garbage-before-ask') {
        // Not JSON at all. A client that lets this reach JSON.parse unguarded throws
        // inside a stream handler, which is an unhandled rejection rather than a
        // failed request.
        process.stdout.write('this is not json\n');
      }
      if (mode === 'non-object-before-ask') {
        // Valid JSON is not necessarily a protocol response. None of these frames
        // may throw in the parent process's stdout event handler.
        process.stdout.write('null\n17\ntrue\n"not a response"\n[]\n');
      }
      if (mode === 'invalid-response-before-ask') {
        say({ id: request.id, ok: 'false', candidates: [] });
      }
      if (mode === 'invalid-payload-before-ask' || mode === 'invalid-payload-only-ask') {
        say({ id: request.id, ok: true, ...invalidPayload });
        if (mode === 'invalid-payload-only-ask') return;
      }
      if (mode === 'silent-on-ask') return;

      const count = Math.min(request.max ?? 1, remaining);
      remaining -= count;
      const candidates = [];
      for (let index = 0; index < count; index += 1) {
        candidates.push({ evaluationId: nextEvaluationId++, parameters: { x: index } });
      }
      say({ id: request.id, ok: true, candidates, complete: candidates.length === 0 });
      return;
    }

    case 'tell':
      if (mode === 'invalid-payload-before-tell') {
        say({ id: request.id, ok: true, ...invalidPayload });
      }
      say({ id: request.id, ok: true, accepted: request.results.length });
      return;

    case 'close':
      if (mode === 'invalid-payload-before-close') {
        say({ id: request.id, ok: true, ...invalidPayload });
      }
      if (mode === 'refuse-close') {
        // Answers, and says no. Distinct from dying: the transport is fine and
        // the host is telling the client it could not stop the run.
        say({ id: request.id, ok: false, error: 'the run could not be stopped' });
        return;
      }
      if (mode === 'die-on-close') {
        // Exits WITHOUT replying. The client cannot tell "stopped cleanly with no
        // result" from "never answered" unless close reports the difference.
        writeThenExit(process.stderr, 'fake host died during close\n', 5);
        return;
      }
      say({
        id: request.id,
        ok: true,
        best: { evaluationId: 1, parameters: { x: 0 }, quality: 1 },
        stopReason: 'Fake',
      });
      // The close RESPONSE is the one write the client cannot do without, so the exit
      // waits behind an empty write queued after it.
      process.stdout.write('', () => process.exit(0));
      return;

    default:
      say({ id: request.id, ok: false, error: `unknown op '${request.op}'` });
  }
});
}
