// Refuses a commit message that would make release-please publish a closure it has not earned.
//
// THE DEFECT THAT PUT THIS HERE. The 0.6.0 changelog said the broker-call metering fix
// "closes #122" - the decisive v1 gate campaign, which has not run, let alone passed. Nobody wrote
// that. The commit footer said `Refs #122`, and release-please's changelog template renders every
// reference it captured under the word "closes" whatever action word actually preceded it. So the
// repository's convention - `Closes #N` for what a change finishes, `Refs #N` for what it advances
// - is invisible to the published artifact, and the artifact asserts the stronger claim.
//
// MEASURED WITH RELEASE-PLEASE'S OWN PARSER, not with a belief about it. Feeding the four 0.6.0
// commits to `parseConventionalCommits` from the pinned release-please gives:
//
//   68334010 (#316 feat)  references -> (none)        changelog: no "closes"
//   be9d2528 (#330 fix)   references -> (none)        changelog: no "closes"
//   42b456e8 (#333 fix)   references -> Refs:#122     changelog: "closes #122"
//   38458752 (#332 fix)   references -> Refs:#183     changelog: "closes #183"
//
// The first row is the one that disproves the obvious theory. #316's body DOES contain `Refs #124`
// and `Refs #125` - but in the middle of a squashed multi-commit body, and the parser captures
// references only from a commit's trailing footer block. A regex over the whole message therefore
// flags prose that release-please ignores, which is why this uses the parser instead: the rule has
// to match what actually gets published, not what looks like it might.
//
// WHAT TO WRITE INSTEAD. A reference that does not close its issue must not use `#` in the footer,
// because `#` is what the parser looks for. Use the full issue URL, which it does not capture:
//
//   Refs https://github.com/ooples/AiDotNet.Evolution/issues/122
//
// Run `--self-test` to exercise the rule against known answers; a rule with no failing case is not
// a rule, so the self-test refuses to pass if none of its cases reject.
'use strict';

const { execFileSync } = require('node:child_process');
const path = require('node:path');

// conventional-commits-parser's closing actions: the words that make a reference a deliberate
// closure. A captured reference with any other action - including none - is this defect.
const CLOSING = new Set(['close', 'closes', 'closed', 'fix', 'fixes', 'fixed',
                         'resolve', 'resolves', 'resolved']);

function loadParser(toolsDirectory) {
  const manifest = require.resolve('release-please/package.json', { paths: [toolsDirectory] });
  const directory = path.dirname(manifest);
  return require(path.join(directory, 'build/src/commit.js')).parseConventionalCommits;
}

/** The references release-please would publish as closures, with the word that was actually used. */
function unearnedClosures(parse, sha, message) {
  const parsed = parse([{ sha, message, files: [] }]);
  const found = [];
  for (const commit of parsed) {
    for (const reference of commit.references || []) {
      const action = (reference.action || '').toLowerCase();
      if (!CLOSING.has(action)) {
        found.push({ action: reference.action || '(none)', issue: reference.prefix + reference.issue });
      }
    }
  }
  return found;
}

/** The commit messages between two refs, each as one string. */
function messages(base, head) {
  // git's own %x00 emits the NUL into the OUTPUT; a NUL inside the argument itself is rejected by
  // node's execFileSync - which a self-test cannot catch, because it never shells out.
  const raw = execFileSync('git', ['log', '--format=%H %B%x00', base + '..' + head],
                           { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
  return raw.split(String.fromCharCode(0))
    .map(part => part.trim())
    .filter(part => part.length > 0)
    .map(part => ({ sha: part.slice(0, 40), message: part.slice(41) }));
}

// Known answers, every one measured against the pinned parser rather than assumed. The forms
// matter in ways no regex would guess: a single `Refs #122` footer is captured and becomes a
// published closure, while `Refs #121, #122` on one line is captured as nothing at all, and a
// footer line followed by ordinary prose voids the whole block. Across the eight commits behind
// 0.6.0, exactly two produced a visible false claim for that reason.
//
// Each case is a list of lines, joined below, so the fixtures carry no escape sequences.
const CASES = [
  { name: 'a single Refs footer is an unearned closure', expected: 1,
    lines: ['fix(bench): compare served model calls (#333)','','Body.','','Refs #122','','Co-authored-by: Claude Opus 5 <noreply@anthropic.com>'] },
  { name: 'two Refs footer lines are both captured', expected: 2,
    lines: ['fix(x): y (#10)','','Body.','','Refs #1','Refs #2'] },
  { name: 'Closes beside Refs reports only the Refs', expected: 1,
    lines: ['fix(x): y (#10)','','Body.','','Closes #1','Refs #2'] },
  { name: 'two issues on one Refs line are captured as nothing', expected: 0,
    lines: ['fix(x): y (#10)','','Body.','','Refs #1, #2'] },
  { name: 'a prose line after a footer voids the block', expected: 0,
    lines: ['fix(x): y (#10)','','Body.','','Refs #1','See also #2'] },
  { name: 'Closes is a deliberate closure', expected: 0,
    lines: ['fix(x): y (#10)','','Body.','','Closes #327'] },
  { name: 'Fixes and Resolves are deliberate closures', expected: 0,
    lines: ['fix(x): y (#10)','','Body.','','Fixes #1','Resolves #2'] },
  { name: 'the full issue URL is the non-closing form', expected: 0,
    lines: ['fix(x): y (#10)','','Body.','','Refs https://github.com/ooples/AiDotNet.Evolution/issues/122'] },
  { name: 'the subject reference is the squash PR number, not a closure', expected: 0,
    lines: ['fix(x): y (#336)','','Body with no footer reference.'] },
  { name: 'a mid-body mention in a squashed body is not captured (#316)', expected: 0,
    lines: ['feat(bench): publish where v1 stands (#316)','','First body.','','Refs #124','Refs #125','','Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>','','---------','','Co-authored-by: Claude Opus 5 <noreply@anthropic.com>'] },
];

const NEWLINE = String.fromCharCode(10);

function selfTest(parse) {
  let failed = 0;
  for (const item of CASES) {
    const actual = unearnedClosures(parse, '0'.repeat(40), item.lines.join(NEWLINE)).length;
    const ok = actual === item.expected;
    if (!ok) failed += 1;
    console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${item.name} (expected ${item.expected}, got ${actual})`);
  }
  const rejecting = CASES.filter(item => item.expected > 0).length;
  if (rejecting === 0) {
    console.log('  FAIL the self-test has no rejecting case, so it proves nothing');
    failed += 1;
  }
  console.log(failed === 0
    ? `commit issue references: ${CASES.length} case(s) pass, ${rejecting} of them rejecting`
    : `commit issue references: ${failed} case(s) failed`);
  return failed === 0 ? 0 : 1;
}

function main() {
  const args = process.argv.slice(2);
  if (args.length < 2) {
    console.log('usage: node eng/Test-CommitIssueReferences.cjs <tools-dir> <base-ref> <head-ref>');
    console.log('       node eng/Test-CommitIssueReferences.cjs <tools-dir> --self-test');
    console.log('<tools-dir> holds a node_modules with the pinned release-please installed.');
    process.exit(2);
  }
  const parse = loadParser(path.resolve(args[0]));
  if (args[1] === '--self-test') {
    process.exit(selfTest(parse));
  }
  if (args.length !== 3) {
    console.log('Pass a base and a head ref, or --self-test.');
    process.exit(2);
  }
  const commits = messages(args[1], args[2]);
  // Nothing checked must not read as a pass.
  if (commits.length === 0) {
    console.log(`No commits between ${args[1]} and ${args[2]}; nothing was checked.`);
    process.exit(1);
  }
  let offending = 0;
  for (const commit of commits) {
    const found = unearnedClosures(parse, commit.sha, commit.message);
    if (found.length === 0) continue;
    offending += found.length;
    console.log(`\n  ${commit.sha.slice(0, 8)} ${commit.message.split('\n')[0]}`);
    for (const item of found) {
      console.log(`    "${item.action} ${item.issue}" will be published as "closes ${item.issue}".`);
    }
  }
  if (offending === 0) {
    console.log(`${commits.length} commit message(s) checked; no reference would publish as a closure.`);
    process.exit(0);
  }
  console.log(
    `\n${offending} reference(s) in ${commits.length} commit message(s) would appear in CHANGELOG.md\n` +
    `as "closes #N". release-please renders every captured reference under that word whatever action\n` +
    `word precedes it, which is how "Refs #122" became "closes #122" in 0.6.0.\n\n` +
    `If the change closes the issue, say so: "Closes #N", "Fixes #N" or "Resolves #N".\n` +
    `If it only advances the issue, drop the "#" and use the full URL, which is not captured:\n` +
    `  Refs https://github.com/ooples/AiDotNet.Evolution/issues/N\n`);
  process.exit(1);
}

main();
