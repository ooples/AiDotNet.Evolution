# US-06 dependency release: foundation integration

The user authorized dependency review, merge and release on September 15, 2026. US-06 remains the active story.
This prepares foundation [PR #15](https://github.com/ooples/AiDotNet.Evolution/pull/15); it does not claim a release occurred.

## Integration

Foundation `10aa358ddc806cd5fc26afedfdeed91864fcf85d` was integrated with current main
`483b822` at `31ba96956a5e32470b643ab5afdf0d6fbb0f207a` in an isolated detached worktree.
Main's external session API, NativeAOT host, TypeScript binding, stable-release policy and dependency updates remain
intact. The sole merge conflict in `build.yml` retained BOTH the foundation's quality/accounting checks and main's
generated-code coverage explanation/filter. No check or protection was removed.

## Final local validation

- Release solution build: zero warnings/errors, 25.10 seconds.
- All .NET tests passed: **825 net10 / 825 net8 / 633 net471**, zero failures/skips.
- Coverage **92.23% lines / 78.77% branches**, above unchanged minima88.80%/73.51%.
- Final solution formatting verification passed. Initial local mixed line endings in `HostSession.cs` were normalized
  by the formatter; Git's normalized source content is unchanged. Initial failed-format log is retained.
- NativeAOT Windows host published, and **108 TypeScript tests passed with zero skips**, using the real host.
  Publish emitted trimming/AOT warnings from library paths; these are retained and not represented as a warning-free
  proof for every optional library API. The exercised binding contract passed.
- Package-content gate passed for the local build's `0.1.0-preview.2` package. This local artifact was NOT published
  over the already existing version. A new version must come from the release workflow after the dependency chain merges.
- Release-workflow security tests are included in the full .NET suite. `Test-StableReleasePolicy.cjs` was mistakenly
  invoked without its isolated-tool argument and refused execution; inspection showed it is a historical promotion
  proof tied to an older manifest, not the current release gate. No dependency installation or false pass was claimed.

[Raw logs/TRX/Cobertura/TypeScript output](foundation-validation.zip), 886,859 bytes.
SHA-256 `02e6bb56d382ff7c2d8a8f0b057bfa84dfd11d1d5fd98b026a563acf5994d4a5`.

## Remaining merge/release control

The active Main ruleset requires **one independent approving review and resolved review threads**; ordinary branch
protection also requires current CI. Only `ooples` is listed as collaborator, and that account authors this PR.
Chat authorization to do the work does not itself create a GitHub approval. No admin bypass, ruleset change or
fabricated self-approval is performed. Obtain a legitimate independent approval and current checks before merge.

The repository permits squash merges only and does not enable auto-merge. For each dependent PR after a squash,
merge the new main into its branch and verify the resulting base diff before retargeting; do not assume the old
foundation head became an ancestor of main. Preserve #45→#46→#47→#48→#49 and consumer #2148→#2203→#2210 dependencies.
Release PR #64 must incorporate the completed core chain before it is used to publish the package needed by US-06.
No next story, foundation merge, tag or package publication is claimed by this evidence.
