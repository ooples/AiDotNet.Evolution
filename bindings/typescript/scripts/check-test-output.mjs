import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

/** Fails closed when TAP is absent, ambiguous, empty, or hides unexecuted tests. */
export function requireExecutedTests(output) {
  const count = (name) => {
    const matches = [...output.matchAll(new RegExp(`^# ${name} (\\d+)\\r?$`, 'gm'))];
    if (matches.length !== 1) throw new Error(`Expected exactly one TAP '${name}' summary.`);
    const value = Number(matches[0][1]);
    if (!Number.isSafeInteger(value)) throw new Error(`Invalid TAP '${name}' count.`);
    return value;
  };
  const tests = count('tests');
  if (tests === 0) throw new Error('No tests ran.');
  if (count('pass') !== tests) throw new Error('The TAP pass count must equal the executed test count.');
  for (const name of ['fail', 'cancelled', 'skipped', 'todo']) {
    if (count(name) !== 0) throw new Error(`TAP reports ${name} tests; every CI test must execute and pass.`);
  }
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  if (!process.argv[2]) throw new Error('Usage: node scripts/check-test-output.mjs <TAP output>');
  requireExecutedTests(readFileSync(process.argv[2], 'utf8'));
}
