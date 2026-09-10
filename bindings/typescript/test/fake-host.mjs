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

if (mode === 'die-on-start') {
  process.stderr.write('fake host refusing to start\n');
  process.exit(3);
}

const say = (value) => process.stdout.write(`${JSON.stringify(value)}\n`);
let nextEvaluationId = 1;
let remaining = 6;

createInterface({ input: process.stdin }).on('line', (line) => {
  if (!line) return;
  const request = JSON.parse(line);

  switch (request.op) {
    case 'open':
      if (mode === 'refuse-open') {
        say({ id: request.id, ok: false, error: 'the fake host refuses to open' });
        return;
      }
      say({ id: request.id, ok: true, version: 'fake' });
      return;

    case 'ask': {
      if (mode === 'die-on-ask') {
        process.stderr.write('fake host died during ask\n');
        process.exit(4);
      }
      if (mode === 'garbage-before-ask') {
        // Not JSON at all. A client that lets this reach JSON.parse unguarded throws
        // inside a stream handler, which is an unhandled rejection rather than a
        // failed request.
        process.stdout.write('this is not json\n');
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
      say({ id: request.id, ok: true, accepted: request.results.length });
      return;

    case 'close':
      say({
        id: request.id,
        ok: true,
        best: { evaluationId: 1, parameters: { x: 0 }, quality: 1 },
        stopReason: 'Fake',
      });
      process.exit(0);
      return;

    default:
      say({ id: request.id, ok: false, error: `unknown op '${request.op}'` });
  }
});
