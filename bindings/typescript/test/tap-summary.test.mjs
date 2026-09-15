import assert from 'node:assert/strict';
import test from 'node:test';
import { requireExecutedTests } from '../scripts/check-test-output.mjs';

const passed = '# tests 2\n# pass 2\n# fail 0\n# cancelled 0\n# skipped 0\n# todo 0\n';

test('the CI guard accepts a complete, unskipped TAP summary with either newline style', () => {
  assert.doesNotThrow(() => requireExecutedTests(passed));
  assert.doesNotThrow(() => requireExecutedTests(passed.replaceAll('\n', '\r\n')));
});

for (const [name, output] of [
  ['no TAP', 'all tests passed'],
  ['missing skip count', passed.replace('# skipped 0\n', '')],
  ['missing pass count', passed.replace('# pass 2\n', '')],
  ['incomplete pass count', passed.replace('# pass 2', '# pass 1')],
  ['duplicate summaries', passed + passed],
  ['empty suite', passed.replace('# tests 2', '# tests 0')],
  ['unsafe count', passed.replace('# tests 2', '# tests 9007199254740992')],
  ['malformed count', passed.replace('# skipped 0', '# skipped nope')],
  ['failed test', passed.replace('# fail 0', '# fail 1')],
  ['cancelled test', passed.replace('# cancelled 0', '# cancelled 1')],
  ['skipped test', passed.replace('# skipped 0', '# skipped 1')],
  ['todo test', passed.replace('# todo 0', '# todo 1')],
]) {
  test(`the CI guard rejects ${name}`, () => assert.throws(() => requireExecutedTests(output)));
}
