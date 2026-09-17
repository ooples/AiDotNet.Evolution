// Offline policy proof using the same release-please version pinned by release-please-action.
// Install tools into an isolated directory, then pass that directory as the only argument.
// Historical proof for this promotion, not a permanent gate freezing the released manifest.
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

assert.equal(process.argv.length, 3, 'Pass the directory containing the isolated node_modules.');
const toolsDirectory = path.resolve(process.argv[2]);
const packageJsonPath = require.resolve('release-please/package.json', { paths: [toolsDirectory] });
const packageDirectory = path.dirname(packageJsonPath);
const releasePlease = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
assert.equal(releasePlease.version, '17.6.0', 'Keep this proof aligned with the pinned action lockfile.');

const { Version } = require(path.join(packageDirectory, 'build/src/version.js'));
const { parseConventionalCommits } = require(path.join(packageDirectory, 'build/src/commit.js'));
const { buildVersioningStrategy } = require(
  path.join(packageDirectory, 'build/src/factories/versioning-strategy-factory.js'));

const repositoryRoot = path.resolve(__dirname, '..');
const config = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'release-please-config.json'), 'utf8'));
const effective = { ...config, ...config.packages['.'] };
assert.equal(effective.prerelease, false);
assert.equal(effective.versioning, 'prerelease');
assert.equal(effective['release-as'], undefined, 'A persistent release-as override freezes future versions.');

function bump(policy, version, commits) {
  const strategy = buildVersioningStrategy({
    type: policy.versioning,
    prerelease: policy.prerelease,
    prereleaseType: policy['prerelease-type'],
    bumpMinorPreMajor: policy['bump-minor-pre-major'],
    bumpPatchForMinorPreMajor: policy['bump-patch-for-minor-pre-major'],
  });
  return strategy.bump(Version.parse(version), parseConventionalCommits(commits)).toString();
}

function git(...args) {
  return execFileSync('git', args, { cwd: repositoryRoot, encoding: 'utf8' }).trim();
}

// Exact pre-policy main and last-published release: no GitHub writes or synthetic release tags.
const baselineCommit = 'b7fbc301259994b46c79e4b57e376366f8af8b86';
const publishedVersion = '0.1.0-preview.1';
const commits = git('rev-list', '--max-count=101', `v${publishedVersion}..${baselineCommit}`)
  .split(/\r?\n/).filter(Boolean).map(sha => ({ sha, message: git('show', '-s', '--format=%B', sha) }));
assert.ok(commits.length > 0 && commits.length <= 100, 'Unexpected promotion audit range.');

const fixture = message => [{ sha: 'policy-regression', message }];
const cases = [
  ['before: exact pending commits', { ...effective, prerelease: true }, publishedVersion, commits, '0.1.0-preview.2'],
  ['after: exact pending commits', effective, publishedVersion, commits, '0.1.0'],
  ['after: pending commits plus policy fix', effective, publishedVersion,
    [...commits, ...fixture('fix(release): enable stable publishing')], '0.1.0'],
  ['control: default changes minor and retains preview', { ...effective, versioning: 'default' }, publishedVersion, commits, '0.2.0-preview.1'],
  ['after: pending preview.2 also graduates', effective, '0.1.0-preview.2', commits, '0.1.0'],
  ['after: preview.2 published first, only policy fix remains', effective, '0.1.0-preview.2',
    fixture('fix(release): enable stable publishing'), '0.1.0'],
  ['next stable fix', effective, '0.1.0', fixture('fix: correct an edge case'), '0.1.1'],
  ['next stable feature', effective, '0.1.0', fixture('feat: add a typed capability'), '0.2.0'],
  ['next breaking release', effective, '0.1.0', fixture('feat!: change a public contract'), '1.0.0'],
];
const results = cases.map(([name, policy, from, inputs, expected]) => {
  const actual = bump(policy, from, inputs);
  assert.equal(actual, expected, name);
  return { name, from, to: actual };
});

// A promotion request must not claim that its candidate is already published.
for (const file of ['.release-please-manifest.json', 'src/AiDotNet.Evolution/AiDotNet.Evolution.csproj']) {
  assert.equal(fs.readFileSync(path.join(repositoryRoot, file), 'utf8').replace(/^\uFEFF/, '').replace(/\r\n/g, '\n').trim(),
    git('show', `${baselineCommit}:${file}`).replace(/^\uFEFF/, '').replace(/\r\n/g, '\n').trim(), `${file} changed`);
}

console.log(JSON.stringify({ releasePlease: releasePlease.version, baselineCommit, commitCount: commits.length, results }, null, 2));
