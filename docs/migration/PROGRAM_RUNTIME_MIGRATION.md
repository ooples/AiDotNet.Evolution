# Standalone program runtime migration

As an Evolution consumer, I want program search and evidence storage shipped from
AiDotNet.Evolution so I do not need AiDotNet's model-builder assembly to evolve programs.

## Acceptance and adversarial checks

- Given an authored program and independently versioned correctness and fitness evaluators,
  when the standalone engine evaluates it, then correctness gates fitness and both costs
  are charged to the shared ledger, including rejected programs.
- Given a persistent fitness hit, when a new engine evaluates the same program,
  then current correctness executes again, the original sample identities remain intact,
  and only current work is charged. Engine-wide memoization is rejected, even if the
  persistent evaluator is hidden inside a delegate.
- Given mismatched proposal/evaluation ledgers or cost-unit versions, or a different
  persistent-fitness ledger, when creating an engine, then it fails before dispatch.
- Given automatic resume or checkpoint requests, when using this entry point, then it
  fails rather than publishing an engine-only checkpoint that omits ledger state.
  Explicit coordinated persistence remains a lower-level engine workflow.
- Given corrupt, fabricated, ambiguous, unavailable or oversized raw samples, when the
  evidence store verifies them, then reuse fails closed. Existing capacity, contention,
  cancellation and statistical reconstruction tests run against the actual package.
- Given compiler diagnostics and bounded proposals, when using the existing US-17
  compiler-guided implementation, then only validated artifacts can be promoted.

## Source disposition

| Source | Destination / disposition |
|---|---|
| Evolution PR73, `6527d27` | Existing Programs/CSharp projects, compiler-guided example and tests incorporated into this dependency stack; not a newly invented second engine |
| AiDotNet PR2182, `de4bc695667bb7765a618a7991b6b2a13657c0de` | Raw scalar observation model/statistics, directory evidence store and regression tests ported; original evidence archive and verifier retained byte-for-byte |
| AiDotNet PR2212, `66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d` | Program genome/codec, evaluator contracts, correctness and version gates, persistent reuse, noise session and metered portfolio runtime imported; corresponding standalone tests ported |

PR2182's eight changed files are represented by the two runtime files, one test
file, updated usage documentation and four unchanged historical evidence files.
Namespace imports and guards now resolve within the standalone package. Source
provenance remains in file headers; copied AiDotNet code retains its BSL license.
The package also includes the Apache license for pre-existing Evolution source.
This does not relicense AiDotNet code as Apache.

The historical archive verifies its original source/runtime hashes and old test
counts. Those counts are **not** current migration results. Current tests are in
`tests/AiDotNet.Evolution.CSharp.Tests`, with local TRX receipts under
`TestResults/program-migration-final`. The optional projects support net8.0 and
net10.0; the core retains net471. No forwarding APIs are supplied.

Final local Release verification: solution build has zero warnings/errors;
program/compiler tests pass 141/141 on each modern framework; core tests pass
797/797 on net10.0 and 720/720 on each of net8.0 and net471; analysis tests pass
110/110. Historical evidence verification passes, and all four retained evidence
files match their original Git blob IDs. No model calls were made.

## Remaining cleanup is not complete

PR88 subsequently ports and verifies the PR2210 model benchmark and PR2212
compiler-arm/provider adapter; those two source PRs are closed as relocated.
See [the complete consumer mapping](PROGRAM_CONSUMER_MIGRATION.md).
PR89 subsequently relocates PR2202 deployment/model retuning and MAP-Elites search;
that source PR is closed too. The complete PR2148 foundation, PR2168 CLI and PR2203
comparison host remain open. The AiDotNet breaking-removal PR follows the verified
ports; do not delete still-needed implementations first.

No competitor performance claim, model invocation or head-to-head study is part
of this repository relocation.
