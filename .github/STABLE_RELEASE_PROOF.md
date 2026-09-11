# Stable Evolution dependency: local promotion proof

Recorded 2026-09-11 against main `b7fbc301259994b46c79e4b57e376366f8af8b86`.
This is candidate evidence, **not a published release**. No release, tag, PR, remote
policy, or global NuGet package was created or replaced during this verification.

## Change and before/after evidence

The only production change is `prerelease: true` to `false` in
`release-please-config.json`. Keep `versioning: prerelease`: that strategy supports
graduating a preview to its corresponding stable version. Do not add a persistent
`release-as` override, which would also force subsequent releases to the same version.

The checked-in [offline proof](../eng/Test-StableReleasePolicy.cjs) executes the real
release-please **17.6.0** strategy and commit parser, using the exact pending commit
between `v0.1.0-preview.1` and the main SHA above. That version is verified against
the [pinned action's lockfile](https://github.com/googleapis/release-please-action/blob/45996ed1f6d02564a971a2fa1b5860e934307cf7/package-lock.json).
The [upstream strategy](https://github.com/googleapis/release-please/blob/v17.6.0/src/versioning-strategies/prerelease.ts)
and [configuration documentation](https://github.com/googleapis/release-please/blob/v17.6.0/docs/manifest-releaser.md)
describe this graduation behavior.

| Executed case | Result |
| --- | --- |
| Old policy, exact pending commit | `0.1.0-preview.2` |
| Changed policy, same commit | `0.1.0` |
| Same pending commit plus this policy fix | `0.1.0` |
| Adversarial control: switch to `default` instead | `0.2.0-preview.1` — still unsuitable |
| Changed policy starting at preview.2 | `0.1.0` |
| Preview.2 already published; only the policy fix remains | `0.1.0` |
| Subsequent stable fix | `0.1.1` |
| Subsequent stable feature | `0.2.0` |
| Subsequent breaking change | `1.0.0` |

All nine cases passed. The proof also checks that the last-published manifest and
project version remain unchanged; both still describe `0.1.0-preview.1`.
This is a historical reproduction for the promotion PR, not a permanent CI gate
requiring future legitimate releases to retain that old manifest.

## Build, API tests, and candidate package

On .NET SDK **10.0.401**, the ordinary all-target build and a second all-target build
with **both** `Version=0.1.0` and `PackageVersion=0.1.0` succeeded with zero warnings
and errors. Formatting verification and all four release-security contract tests passed.

The candidate binaries passed **285/285 tests on each of net10.0, net8.0, and net471**,
with no skipped tests. The ordinary-version binaries separately passed the same
285 tests on each framework. These include the public `EvolutionEngineOptions.Copy`
API and option-copy regression tests. Candidate TRX reports are under
`artifacts/stable-promotion/test-results/candidate-*.trx`.

The actual candidate is
`artifacts/stable-promotion/candidate/AiDotNet.Evolution.0.1.0.nupkg`.
Its SHA-256 for this run is
`b8d1375b02dd649d85302430d8b19e5b5820c0d2367f28f06692da708a96c316`.

The unchanged `eng/Test-Package.ps1` fully accepts the ordinary preview package
against the existing preview manifest/tag. Against the stable candidate, it completes
all package-content checks (three framework assets, XML documentation, package
identity/version/license, and forbidden dependency/assembly boundaries), then
**correctly rejects** the candidate at the last-published-manifest check. The exact
expected rejection was asserted; any other error, or unexpected acceptance, fails
the audit. The tag-existence safeguard was not changed and no synthetic stable tag
was created. The complete published-release validator can pass for 0.1.0 only after
the release PR creates the truthful matching manifest and tag.

## Reproduce without publishing

Run in this worktree with its full Git history and existing preview tag. Node 25.6.0
and npm 11.6.2 were used. Tool installation disables lifecycle scripts and puts npm
and NuGet package artifacts under this worktree, not the global package cache.

```powershell
npm install --prefix artifacts/stable-promotion/tools --cache artifacts/stable-promotion/npm-cache --ignore-scripts --no-package-lock --no-audit --no-fund release-please@17.6.0
node eng/Test-StableReleasePolicy.cjs artifacts/stable-promotion/tools
dotnet restore AiDotNet.Evolution.slnx --packages artifacts/stable-promotion/nuget-packages --disable-parallel
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
dotnet build AiDotNet.Evolution.slnx -c Release --no-restore -m:1
./eng/Test-ReleaseWorkflow.ps1 -NoRestore
./eng/Test-Package.ps1 -PackagePath src/AiDotNet.Evolution/bin/Release/AiDotNet.Evolution.0.1.0-preview.1.nupkg -ExpectedVersion 0.1.0-preview.1
dotnet build AiDotNet.Evolution.slnx -c Release --no-restore -m:1 -p:Version=0.1.0 -p:PackageVersion=0.1.0
foreach ($targetFramework in @('net10.0', 'net8.0', 'net471')) {
    dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f $targetFramework --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Candidate tests failed on $targetFramework." }
}
dotnet pack src/AiDotNet.Evolution/AiDotNet.Evolution.csproj -c Release --no-build --no-restore --output artifacts/stable-promotion/candidate -p:Version=0.1.0 -p:PackageVersion=0.1.0
$candidatePackage = 'artifacts/stable-promotion/candidate/AiDotNet.Evolution.0.1.0.nupkg'
$expectedGuard = "Released manifest version '0.1.0-preview.1' does not match package version '0.1.0'."
try {
    ./eng/Test-Package.ps1 -PackagePath $candidatePackage -ExpectedVersion 0.1.0
    throw 'Unpublished candidate was incorrectly accepted as published.'
} catch {
    if ($_.Exception.Message -cne $expectedGuard) { throw }
}
```

Check every native command's exit code when reproducing these commands manually.
The archive hash identifies this retained artifact; it is not a claim that a build
from a different checkout path or toolchain must produce the same archive bytes.

## Remaining release order

1. Review this policy change and its checks. After it is merged by an authorized
   maintainer, release-please should regenerate the existing release PR as **0.1.0**;
   verify its actual version, manifest, project XML, changelog, and reviews.
2. The authorized release-PR merge creates the real tag and runs the existing full
   validation, trusted publishing, and attestation pipeline. Confirm 0.1.0 on the
   official NuGet feed; this local audit did not execute publication.
3. Pin that published stable dependency in Tensors #1029 and AiDotNet #2092 and run
   their ordinary stable pack and integration checks. Do not suppress NU5104.
4. Merge the existing #2092 extraction through master before #2088 consumes the new
   Tensors package. #2092 removes the duplicate embedded engine; project aliases
   are not a replacement for that ownership fix. No extraction work is duplicated here.
