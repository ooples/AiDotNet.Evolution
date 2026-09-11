import { createInterface } from 'node:readline';
const mode = process.argv[2];
const status = { runId: 'run', compatibilityHash: 'compat', wasRecovered: false, sourceSessionId: null,
  supportsExactSearchContinuation: false, searchContinuationGuarantee: 'delivery-only; explicit fork required', searchState: 'not-owned',
  spent: {}, reserved: {}, admitted: '0', settled: '0', denied: '0', maximumViolated: false };
createInterface({ input: process.stdin }).on('line', line => {
  const request = JSON.parse(line);
  const base = { id: request.id, protocol: mode === 'downgrade' ? 0 : 1, ok: true };
  if (request.op === 'open') { process.stdout.write(JSON.stringify({ ...base, ...status }) + '\n'); return; }
  if (mode === 'timeout') return;
  if (mode === 'oversized') { process.stdout.write('x'.repeat(16 * 1024 * 1024 + 1)); return; }
  if (mode === 'uncorrelated') base.id++;
  if (request.op === 'claim') process.stdout.write(JSON.stringify({ ...base, available: false }) + '\n');
  else process.stdout.write(JSON.stringify({ ...base, closed: true }) + '\n');
});
