# Deployment relocation verification

Release solution build: zero warnings/errors. Tests: 41 deployment/AutoML on each
of net10.0/net8.0, 203 program/compiler on each, and four changed workflow contracts
on net10.0: **492 passing executions, no skips**. Core behavior is unchanged from
the already-green PR88; this is not a claim of a fresh full-core test run.

The polynomial regression fixture used six trials: initial validation RMSE
`0.24701108593355436`, evolved RMSE `5.404403298774802E-15`, discovering the correct
quadratic degree. Repeated seeded runs matched archive states. This is an authored
functional regression fixture, not a competitor comparison or general speed claim.
The separate deployment fixture promoted and reloaded actual MultipleRegression
weights and predicted the independently specified held-out value within `1e-7`.

`verification.zip` retains final TRX/build/test/package logs. The package-only
consumer uses unique prerelease versions, so an old locally cached preview cannot
masquerade as this implementation. Reproduce with `eng/Test-DeploymentPackage.ps1`.
No packages were published and no provider/model API calls were made.

Original PR2202 docs/evidence are retained separately in
`docs/migration/aidotnet-pr-2202-original.zip`; all four contained files match their
original Git blobs at `d4535f7376888a7c2d35d7e6229494c4c60c0ac6`.

## Clean-checkout packaging correction

Hosted consumer run35162459264 failed with NU5026: the net471 core DLL was missing.
Core sets `GeneratePackageOnBuild=true`, which causes Pack to skip its normal build;
earlier local outputs masked that assumption. `Test-DeploymentPackage.ps1` now
sets `GeneratePackageOnBuild=false` explicitly for each pack invocation.
A fresh detached worktree reproduced the original failure and passed with this
single script change, building all target frameworks and running the package-only
consumer. Before/after logs are retained in `clean-package-verification.zip`.
