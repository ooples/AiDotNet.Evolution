# US-16 current-stack allocation/restart comparison

Runtime: `cfebe5891ac2911b1e5e71cfc2b70d2439d6f87d`.
Frozen design: 30 paired seeds × two authored tasks × four policies, 128 actual objective calls per run.
All 240 primary runs, 240 replay comparisons and eight aligned checkpoint cases passed.
30,720 primary +30,720 replay +1,024 checkpoint +256 warmup calls = **62,720 local objective calls**.
No model/API calls. Raw evidence and hashes are in [summary.json](summary.json); lossless measurements,
receipts, policy states, quality/time curves and decisions in [raw.json.gz](raw.json.gz).
[analysis.json](analysis.json) reports all methods and contrasts. Nothing failed or was omitted.

## Before/after at the same evaluation budget

"Before" means the uniform/no-restart ablation, not a different repository revision.
Quality values reproduce the historical authored pilot; integration is not a new quality algorithm improvement.
Success is measured quality >=0.95, not independent deployment qualification.

| Task | Allocation / restart | Mean quality | Worst quality | Success | Calls/run | Median elapsed ms |
|---|---|---:|---:|---:|---:|---:|
| Shifted quadratic | Uniform / off | 0.999999334 | 0.999991742 | 30/30 | 128 | 32.190 |
| Shifted quadratic | Adaptive / off | 0.999971340 | 0.999147594 | 30/30 | 128 | 32.232 |
| Shifted quadratic | Uniform / on | 0.999999276 | 0.999991742 | 30/30 | 128 | 30.390 |
| Shifted quadratic | Adaptive / on | 0.999971283 | 0.999147594 | 30/30 | 128 | 31.933 |
| Separated basins | Uniform / off | 0.500000000 | 0.500000000 | 0/30 | 128 | 29.921 |
| Separated basins | Adaptive / off | 0.500000000 | 0.500000000 | 0/30 | 128 | 29.057 |
| Separated basins | Uniform / on | 0.999828253 | 0.995134189 | 30/30 | 128 | 29.225 |
| Separated basins | Adaptive / on | 0.999978027 | 0.999769869 | 30/30 | 128 | 29.190 |

Restart escape is supported on the intentionally separated-basin task. Adaptive allocation superiority is **not**:
with restarts its mean gain over uniform is +0.000149774, diagnostic 95% interval [-0.000025666,+0.000480474].
On the quadratic adaptive minus uniform is -0.000027995, interval [-0.000085158,+0.000000893], with a worse tail.
The small negative quadratic adaptive-restart contrast (-5.6561e-8, interval [-1.2418e-7,-1.3654e-8]) is retained.
Intervals use 10,000 paired seed-bootstrap draws, seed42, unadjusted and retrospective. Defaults remain unchanged.

## Time and cost interpretation

Each call is one declared cost unit; proposal/setup/checkpoint CPU is not priced in those units. Equal evaluation
budgets do not imply equal total compute. Elapsed time includes the in-process run, setup and final checkpoint,
excludes process startup and artifact export, and is captured separately from deterministic state. Methods rotate
by seed after eight 32-call warmups. Timings are single-host descriptive observations, not a speedup claim.

Environment: .NET10.0.12, Windows26200, process-local processor count2, workstation GC, heap limit0x30000000.
Other host load, CPU frequency and power policy were not controlled. No competing verification jobs were launched
by this task during the campaign. Authored scalar tasks are not representative workloads or an OpenEvolve comparison.

## Integrity and replay

The analyzer verifies compressed/raw hashes, reconciles summaries with raw rows, recomputes objective values and
running bests, checks ordered unique measurement identities, exact per-call receipts, reservation clearance and timing.
Engine/policy state and ledger snapshots match on replay/resume; nondeterministic elapsed time is not compared.
Recovery is at an aligned quiescent engine/ledger boundary, not arbitrary in-flight crash recovery.

Reproduce using the pinned runtime and fresh output paths:

```powershell
dotnet run --project examples/AdaptiveIslandSearch -c Release -- 30 128 TestResults/new-us16.json
./eng/Export-IslandEvidence.ps1 -InputFile TestResults/new-us16.json -Revision cfebe5891ac2911b1e5e71cfc2b70d2439d6f87d -OutputDirectory TestResults/new-us16-evidence
python benchmarks/analysis/analyze_islands.py TestResults/new-us16-evidence/summary.json
```

Use the process-local environment settings above for timing comparability. Outputs refuse overwrites.
