# Fresh replication and independent confirmation

US-06 foundation: `EvolutionReplicateRunner<TGenome>` executes independently requested scalar measurements with
bounded sample counts, uncertainty reporting and per-measurement resource receipts. It calls the supplied evaluator
directly, with no engine evaluation cache, and never inserts a result into an archive or authorizes deployment.

```csharp
var plan = new EvolutionReplicationPlan(
    minimumSamples: 8, maximumSamples: 64,
    minimumQuality: 0, maximumQuality: 1,
    maximumCostPerSample: 1, confidence: 0.95, normalizedWidthTarget: 0.25);
var runner = new EvolutionReplicateRunner<MyGenome>(evaluatorFingerprint, plan, ledger, MeasureFreshAsync);
var evidence = await runner.RunAsync(canonicalCandidate, evaluationContext, "batch-1");
// Confirmation uses new identities/streams and the Confirmation resource stage, never cache credit:
var confirmation = await runner.RunAsync(canonicalCandidate, evaluationContext, "batch-1",
    EvolutionReplicationPurpose.Confirmation);
```

The evaluator must actually perform fresh, independent, identically distributed measurements at the same fidelity.
It receives an immutable sample identity and a new deterministic stream while preserving the original evaluation ID,
root seed and attempt. Separate pseudorandom streams do not prove physical independence. Reset training state and
account for warm-up/cache/environment effects when measuring real programs or models. Version changes must cover
data, evaluator, fidelity and environment. This API cannot discover an evaluator's hidden internal cache.

## Statistics and stopping

Declare finite support bounds **before sampling**; never infer them from the observed extrema. The mean is kept in
original units. Scaled Welford updates avoid variance overflow and preserve small measurable differences despite wide
declared bounds. Standard error is descriptive; zero observed variance does not imply certainty. Normalized variance
can underflow for extremely tiny ratios even when the separately computed standard error remains representable.

The mean interval uses the bounded-independent-variable [Hoeffding inequality](https://www.tandfonline.com/doi/abs/10.1080/01621459.1963.10500830).
For support span R, n samples and batch error alpha, the radius is `R * sqrt(log(2*L/alpha)/(2*n))`, clipped to support.
With precision stopping, `L = maximumSamples - minimumSamples + 1` accounts for all possible decision looks; a zero
width target uses the fixed maximum sample count and L=1. Bounds round outward to avoid false zero-width intervals
at floating-point resolution limits. These are conservative intervals for **one batch under the stated assumptions**,
not global guarantees across adaptively selected candidates, a power calculation or a general confidence-sequence API.

The runnable [example](../examples/ReplicatedEvaluation/Program.cs) uses one predeclared synthetic pair, search and
fresh confirmation batches, and alpha/2 for each of the two confirmation intervals. Real promotion additionally needs
trusted correctness, applicability checks and a prospectively declared comparison/multiplicity policy.

## Accounting and failure behavior

Given a repeat measurement, when it is dispatched, then its maximum is reserved and its returned cost is charged even
if the score fails its status, direction, feasibility or support checks. Each identity can be charged only once;
accidentally repeating a used batch is rejected before more work. Changing batch identity requests new resource charges,
not statistical independence by itself. Bounds must be enforced by the producer's own timeout/isolation mechanism.

Given failure, missing receipts or in-flight cancellation, when exact consumption is unavailable, then the reservation
maximum remains charged as unknown. Unrepresentable returned costs retain their original double value in the sample
evidence; they are not silently clipped or rounded to free work. If a producer violates its declared maximum, that
maximum is no longer a trustworthy upper bound: stop and investigate, rather than treating unknown charges as proven
actual consumption. Representable overruns are charged in full and stop subsequent ledger admission.

Failed, canceled or budget-short batches retain every dispatched sample but expose no successful mean or confidence
interval. Pre-cancellation dispatches nothing. The runner retains compact measurement evidence, not raw artifacts or
diagnostic messages; it does not pool objectives or behavior descriptors. Meter the same cost only once—do not wrap
an already cost-metered evaluator in this runner.

```powershell
powershell -ExecutionPolicy Bypass -File eng/Test-Replication.ps1
dotnet run --project examples/ReplicatedEvaluation -c Release -- 32
```

Remaining US-06 work includes archive-incumbent resampling policy, cascade-rejection audits and representative noisy
quality comparisons. Purpose separation is a logical identity/accounting boundary, **not a security sandbox**: callers
must keep hidden test data and confirmation feedback outside the proposing process. The runner is not a transparent
task decorator and does not silently choose aggregation rules for task metrics, artifacts or noisy descriptors.
