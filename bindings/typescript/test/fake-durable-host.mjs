import { createInterface } from 'node:readline';
const mode = process.argv[2];
const status = { runId: 'run', compatibilityHash: 'compat', wasRecovered: false, sourceSessionId: null,
  supportsExactSearchContinuation: false, searchContinuationGuarantee: 'delivery-only; explicit fork required', searchState: 'not-owned',
  spent: {}, reserved: {}, admitted: '0', settled: '0', denied: '0', maximumViolated: false };
const held = [];
createInterface({ input: process.stdin }).on('line', line => {
  const request = JSON.parse(line);
  const base = { id: request.id, protocol: mode === 'downgrade' ? 0 : 1, ok: true };
  if (mode === 'split-utf8' && request.op === 'open') {
    // The handshake echoes the requested run id, written in pieces that cut its three- and four-byte characters apart;
    // the client compares that id with its own, so any mis-decoded byte fails the open.
    const bytes = Buffer.from(JSON.stringify({ ...base, ...status, runId: request.config.runId }) + '\n');
    const euro = bytes.indexOf(0xe2), emoji = bytes.indexOf(0xf0);
    const cuts = [0, euro + 1, euro + 2, emoji + 1, emoji + 3, bytes.length];
    cuts.slice(1).forEach((end, i) => setTimeout(() => process.stdout.write(bytes.subarray(cuts[i], end)), 15 * i));
    return;
  }
  if (request.op === 'open') { process.stdout.write(JSON.stringify({ ...base, ...status }) + '\n'); return; }
  if (mode === 'timeout') return;
  if (mode === 'oversized') { process.stdout.write('x'.repeat(16 * 1024 * 1024 + 1)); return; }
  // 6 Mi three-byte characters: under the limit in UTF-16 code units, 18 MiB as UTF-8, so only the byte check can catch it.
  if (mode === 'oversized-bytes') { process.stdout.write('\u20ac'.repeat(6 * 1024 * 1024) + '\n'); return; }
  if (mode === 'two-frames' && request.op === 'claim') {
    // Both claims answered in one write, so one chunk carries two complete frames.
    held.push(JSON.stringify({ ...base, available: false, lease: null }) + '\n');
    if (held.length === 2) process.stdout.write(held.splice(0).join(''));
    return;
  }
  if (mode === 'uncorrelated') base.id++;
  if (request.op === 'claim') process.stdout.write(JSON.stringify({ ...base, available: false }) + '\n');
  else process.stdout.write(JSON.stringify({ ...base, closed: true }) + '\n');
});
