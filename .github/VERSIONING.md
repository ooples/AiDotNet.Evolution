# Versioning

AiDotNet.Evolution uses release-please and Conventional Commit PR titles. While the package is in preview, generated
versions retain the `preview` prerelease label. A breaking-change marker produces the corresponding semantic-version
bump; `feat` produces a feature release; fixes and the other visible changelog categories produce patch releases.

Do not edit the version by hand. Release-please updates all three version authorities together:

- `.release-please-manifest.json`
- `src/AiDotNet.Evolution/AiDotNet.Evolution.csproj`
- `CHANGELOG.md`

Release tags use `v<version>`, for example `v0.1.0-preview.2`. NuGet publication always derives `PackageVersion` from
the validated tag rather than from ambient branch state.
