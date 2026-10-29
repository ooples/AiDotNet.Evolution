# Repository setup

The repository workflows are checked in and validated, but GitHub-side configuration must be completed after the
repository is created.

## Required secrets

| Secret | Purpose |
| --- | --- |
| `AUTOFIX_PAT` | Lets release-please open and update a release PR whose checks run normally. Use a fine-grained token limited to this repository with Contents, Pull requests, and Issues read/write access. |
| `SONAR_TOKEN` | Authenticates the `ooples_AiDotNet.Evolution` SonarCloud project. |

`GITHUB_TOKEN` is supplied automatically. `CODECOV_TOKEN` is optional for a public repository because coverage upload
is non-blocking. `CODE_SIGNING_CERT_BASE64` and `CODE_SIGNING_CERT_PASSWORD` are optional, but must be configured
together; when present, release packages are signed and verified before publication.

## NuGet trusted publishing

NuGet publication uses GitHub OIDC and does not require a stored API key. Configure a trusted-publishing policy for
the `ooples` NuGet account with these values:

| Setting | Value |
| --- | --- |
| Repository owner | `ooples` |
| Repository | `AiDotNet.Evolution` |
| Workflow file | `automated-release.yml` |
| Environment | Empty |
| Package glob | `AiDotNet.Evolution` |
| Scopes | Push new packages and package versions |

Enter only the workflow filename in NuGet, not the `.github/workflows/` path. The publish job requests a single-use
OIDC token immediately before publication and exchanges it for a short-lived NuGet API key through `NuGet/login`.
The workflow never stores a long-lived NuGet credential. Package provenance runs through the separately named
`attest-release.yml` reusable workflow. NuGet validates GitHub's `job_workflow_ref` claim, so that workflow's OIDC
token cannot satisfy the trusted-publishing policy for `automated-release.yml`; only the publish job in the trusted
workflow can obtain a NuGet credential. Verified package artifacts are immutable and retained for 30 days so a
publication outage can be retried without rebuilding different package bytes.

## npm and PyPI trusted publishing (E-5)

`automated-release.yml` also publishes `@aidotnet/evolution`, its three platform hosts
(`@aidotnet/evolution-host-win-x64`, `-linux-x64`, `-osx-arm64`) and `aidotnet-evolution-worker`, at the same
version as the NuGet packages. Both registries use OIDC; no token is stored. Configure once, then turn it on:

1. **npm.** For each of the four package names, add a trusted publisher on npmjs.com (package settings, *Trusted
   publishing*, GitHub Actions): organization or user `ooples`, repository `AiDotNet.Evolution`, workflow filename
   `automated-release.yml`, no environment. A package that does not exist yet needs its first version published
   by hand or the name reserved, since npm attaches trusted publishers to existing packages; the `@aidotnet` scope
   must exist and allow public packages.
2. **PyPI.** Add a *pending* trusted publisher at pypi.org (Account, *Publishing*): project
   `aidotnet-evolution-worker`, owner `ooples`, repository `AiDotNet.Evolution`, workflow `automated-release.yml`,
   environment `pypi`. Create the `pypi` environment in this repository (Settings, Environments); adding a required
   reviewer there makes each PyPI upload wait for approval.
3. Set the repository variable `PUBLISH_REGISTRIES` to `true` (Settings, Secrets and variables, Actions, Variables).
   Until it is set, every npm and PyPI job in the release workflow is skipped and only NuGet publishes.

The registry jobs build the hosts on Windows, Linux and macOS, pack the npm tarballs and the Python wheel and sdist,
check that every version equals the tag, and install them into fresh consumers (`eng/Test-RegistryConsumers.ps1`)
before anything is published. The two publish jobs only download those files and upload them; they never check out
the repository, because any job of this workflow holding an OIDC token could also obtain a NuGet key
(`ReleaseWorkflowSecurityContractTests` enforces this). After publishing, the same consumers install from npmjs.org
and pypi.org on all three operating systems.
## Repository services

1. Create the SonarCloud project with key `ooples_AiDotNet.Evolution`, then add its token as `SONAR_TOKEN`.
2. Install/enable CodeRabbit for the repository.
3. Enable Dependabot alerts, security updates, private vulnerability reporting, and GitHub Actions provenance.
4. Allow GitHub Actions to create pull requests so release-please can maintain its release PR. Do not allow workflow
   tokens to approve pull requests; require an independent human approval through the effective branch-protection rules.

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

The initial release is bootstrapped with an empty `.release-please-manifest.json` and the package-level
`initial-version` in `release-please-config.json`. Manifest values describe versions that have already been released;
the package validation fails if a populated manifest has no matching Git tag, preventing downstream projects from
pinning a version the release workflow will skip.

The direct reusable-workflow call is intentional: it does not rely on a second GitHub event being emitted by a bot
token, so the release still publishes when release-please has to fall back to `GITHUB_TOKEN`. The manual dispatch path
accepts only an already-existing `v<semver>` tag and runs the identical pipeline. Because NuGet validates the workflow
that contains the trusted-publishing login step, its policy must name the reusable `automated-release.yml` workflow,
not the calling `release-please.yml` workflow.
