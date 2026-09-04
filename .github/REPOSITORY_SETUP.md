# Repository setup

The repository workflows are checked in and validated, but GitHub-side configuration must be completed after the
repository is created.

## Required secrets

| Secret | Purpose |
| --- | --- |
| `AUTOFIX_PAT` | Lets release-please open and update a release PR whose checks run normally. Use a fine-grained token limited to this repository with Contents and Pull requests read/write access. |
| `NUGET_API_KEY` | Publishes `AiDotNet.Evolution` and its symbol package to nuget.org. Scope the key to this package ID. |
| `SONAR_TOKEN` | Authenticates the `ooples_AiDotNet.Evolution` SonarCloud project. |

`GITHUB_TOKEN` is supplied automatically. `CODECOV_TOKEN` is optional for a public repository because coverage upload
is non-blocking. `CODE_SIGNING_CERT_BASE64` and `CODE_SIGNING_CERT_PASSWORD` are optional, but must be configured
together; when present, release packages are signed and verified before publication.

## Repository services

1. Create the SonarCloud project with key `ooples_AiDotNet.Evolution`, then add its token as `SONAR_TOKEN`.
2. Install/enable CodeRabbit for the repository.
3. Enable Dependabot alerts, security updates, private vulnerability reporting, and GitHub Actions provenance.
4. Allow GitHub Actions to create and approve pull requests so release-please can maintain its release PR.

## Main-branch rules

Require pull requests, dismiss stale approvals, require conversation resolution, and block force pushes and deletion.
Require the following checks after their first successful run establishes the status names:

- `CI Gate`
- `CodeQL Analysis`
- `Dependency Review`
- `SonarCloud Analysis`
- `Validate PR title`

Use squash merges so the validated Conventional Commit PR title becomes the commit on `main`. Direct pushes to
`main` should be restricted to administrators for recovery only.

## Release flow

Merges to `main` update one release-please PR. Merging that PR creates the version tag and GitHub release, then calls
the reusable automated-release workflow directly. The publish workflow validates the tag, builds and tests all three
target frameworks, validates the package boundary, optionally signs packages, creates a provenance attestation,
publishes to NuGet, and attaches both package files to the GitHub release.

The direct reusable-workflow call is intentional: it does not rely on a second GitHub event being emitted by a bot
token, so the release still publishes when release-please has to fall back to `GITHUB_TOKEN`. The manual dispatch path
accepts only an already-existing `v<semver>` tag and runs the identical pipeline.
