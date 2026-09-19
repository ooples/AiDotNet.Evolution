# Release publishing and recovery

Evolution uses the same release model as AiDotNet: a reviewable release-please PR,
then a GitHub tag/release, verified packages, NuGet trusted publishing, and GitHub
release assets. No NuGet PAT is used.

All five NuGet libraries are released together: `AiDotNet.Evolution`, `.Programs`,
`.CSharp`, `.Deployment`, and `.Surrogates`. Native/host executables are not NuGet
packages. Each library retains its existing license and target frameworks.

`eng/Pack-Release.ps1` builds all targets, stamps both Version and PackageVersion
through project references, rejects stale output, checks package inventory,
internal dependency versions, assemblies, and licenses. Adding a packable source
project without updating this set fails the rehearsal. Release-please updates all
five project versions. CI uses the same packer and package-only consumer checks;
six negative fixtures verify that malformed packages are rejected.

The release workflow builds tagged product source. Packaging helpers and consumer
fixtures come from the immutable workflow revision in a separate checkout, so a
tag predating those helpers can still be packaged. This does not move or rewrite
the tag. Very old tags lacking the five projects or consumer APIs fail validation
rather than substituting current product code.

Build/pack jobs have no publishing OIDC permission. Provenance remains in the
separate attestation workflow; only the publishing job authenticates to NuGet.
The NuGet policy must authorize repository `ooples/AiDotNet.Evolution`, workflow
`automated-release.yml`, and the intended package IDs for account `ooples`.
Policy existence/coverage cannot be proved by local packing tests.

## Failure diagnosis

Run 35438753952 attempt 1 failed inside release-please with `Bad credentials` on
2026-09-19, before the publishing job. The `AUTOFIX_PAT` secret was subsequently
updated at 12:33:07Z. That old run does not establish whether the replacement
credential succeeds. `AUTOFIX_PAT` authenticates GitHub release automation, not
NuGet; a nonempty invalid PAT does not trigger the `GITHUB_TOKEN` fallback.
The new preflight reports repository-access failures without printing credentials;
it cannot prove every write permission or distinguish all authentication causes.

An independent CI defect also existed: Test-Package required the manifest's tag
even for PR validation. Main's manifest was already `0.1.0` while the GitHub tag
API returned 404 for `v0.1.0`. PR validation now permits that pre-tag state;
the publication workflow explicitly uses `-RequireReleaseTag` and still refuses
to publish without the tag. The adversarial rehearsal tests both outcomes.

Merge the reviewed workflow fix before retrying a release. A retry can create a
public release and publish packages; it is not a read-only authentication test.
For an existing tag whose publication failed, a maintainer can dispatch Automated
Release Pipeline from the fixed main workflow with that tag. Do not create or move
tags merely to satisfy a packaging test. Existing packages are skipped on recovery;
verify NuGet versions and GitHub assets before declaring publication complete.

Local rehearsal (no publishing credentials):

```powershell
./eng/Pack-Release.ps1 -Version 0.1.0-localcheck.1 -OutputDirectory .local/release-check
./eng/Test-ReleasePackageFailures.ps1 -PackageDirectory .local/release-check -Version 0.1.0-localcheck.1
./eng/Test-ReleaseConsumers.ps1 -Version 0.1.0-localcheck.1 -PackageDirectory .local/release-check
./eng/Test-ReleaseWorkflow.ps1
```

Use a fresh output directory. Consumer caches and negative-fixture directories
are uniquely named under the temporary directory and retained for diagnosis.

References: [release-please authentication](https://github.com/googleapis/release-please-action),
[NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
