# Standalone script evaluation and metric scoring

This cleanup increment extends Evolution PR92 with the actual script evaluator,
typed metrics and scoring policies from AiDotNet PR2148/2168. All implementation is
in `AiDotNet.Evolution.Programs`; no AiDotNet dependency or forwarding API is added.

## Source disposition

The [original archive](aidotnet-script-metrics-original.zip) retains 13 selected Git
blobs from `9cd7d5d6c366a483874024650d02901f69a1829c`, verified byte-for-byte:

- `ScriptProgramFitnessEvaluator`, `ScriptProgramEvaluationOptions` and metric options.
- Four metric types and three metric enums.
- Original aggregator, script scalarization and script evaluator test suites.

Implementations live under `src/AiDotNet.Evolution.Programs/Execution` and `Metrics`;
tests live under `tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics`. The existing
execution fixture and caller-owned execution contract from PR92 are reused.
Original licensing remains in `src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt`.

## Behavior preserved and hardened

Scripts receive the exact candidate source on standard input and return a JSON object.
An explicit finite numeric `quality` takes precedence; otherwise an explicitly supplied
aggregator can derive quality from top-level fields or a `metrics` object. Numeric
reporting metrics now survive in `EvolutionTaskResult.Metrics`, independently of the
scalar score. Descriptors and objectives remain available.

Policies include combined-score-or-mean, mean, weighted mean and Tchebycheff distance.
The legacy `UpstreamFitnessScore` helper is retained as a compatibility API, **not a
claim of parity with any current competitor version**. Script scoring refuses a
fallback with no contributing measurements or a boolean used as the selected score.
An aggregator fixes the direction for both explicit and derived scores; Tchebycheff
uses minimization. Conflicting explicitly supplied script options are rejected before
execution. Without an aggregator, script options fix the direction. A single evaluator
never mixes maximization and minimization between candidate responses. The evaluator
author must also keep explicit and derived scores on the same measurement scale.

Adversarial corrections:

- Invalid explicit quality cannot fall back to a favorable unrelated metric.
- Duplicate, ambiguous, overlong or control-character field names are rejected.
- Response size, JSON depth, collection sizes and retained diagnostics are bounded.
- Missing required weighted metrics produce a failed result with the dispatched-call
  receipt, not an escaping ordinary exception or a silently substituted score.
- Pre-dispatch cancellation costs zero; cancellation after dispatch costs one call.
  Fatal runtime exceptions propagate so the enclosing ledger can preserve unknown work.
- Runner identity is pinned and included in cache identity, together with exact script
  text and all options. CRLF/LF scripts no longer share an identity merely by normalization.
- Metric configuration uses canonical JSON rather than delimiter concatenation; one
  excluded field named `a,b` is distinct from two fields named `a` and `b`.
- Inconsistent success receipts, compile-only responses and incomplete output fail closed.
- Parser and runner failures do not export raw private error payloads. Artifact names
  and text are withheld by default. `RetainArtifactText=true` explicitly restores bounded
  text retention, marks it **not redacted**, and changes cache identity.

## Package-only usage

```csharp
using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Metrics;

var fitness = new ScriptProgramFitnessEvaluator(
    callerProvisionedRunner,
    evaluatorSource,
    new ScriptProgramEvaluationOptions { MaxResponseChars = 65536 },
    metricAggregator: new ProgramMetricAggregator());
```

Scripts and candidates still require the caller-provisioned execution boundary described
in [execution migration](EXECUTION_RUNTIME_MIGRATION.md). A child process alone does not
isolate filesystem or network access. The entry-point marker is a configuration check,
not proof that a script is safe or implements a correct oracle. Reported metrics remain
caller-trusted measurements, not independent performance attestation.

## Verification and remaining cleanup

See [retained verification](../evidence/script-metrics-relocation/README.md). The fresh
package consumer executes the scoring script in a real child process and verifies
both the derived score and retained numeric metrics. No paid/model calls are used.

This increment does **not** close AiDotNet PR2148/2168. Remaining work includes LLM and
novelty evaluators, artifact/output integration, the standalone run facade and then
CLI/config/preflight/control/evidence. Only complete verified replacements justify
closing those source PRs; the separate clean-breaking removal follows. US-11/PTX stays
after repository consolidation.
