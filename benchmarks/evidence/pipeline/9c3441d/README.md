# US-20: bounded proposal pipeline evidence

Pipeline/harness source: `9c3441d9723ed333644d991e2df9e658e4e25a74`. Default-profile group-control refinement: `e62b314d005faf41e7101735363b3c49af55081a`. The latter changes profiling/analysis only, not the pipeline implementation. No external model API, paid compute, publication or competitor run was used.

## Authored overlap and replay pilot

`pilot/raw.json.gz` retains **576 live executions**, their **576 offline recorded-response replays**, and four warmups. Eight search seeds × three nested timing repetitions × two numeric tasks × three artificial-delay profiles × four dispatchers. All 576 live/replay pairs validated; every replay executed zero physical proposal/evaluator calls. Primary physical work: **13,824 proposal calls + 18,432 evaluations**; four warmups add 96 + 128 calls. Every primary live run spent **38.08 declared work units** under the common 40-unit cap (32 proposals/evaluations, eight matched starting points). Replay receipts simulate original accounting and are not new physical spending.

Raw JSON: **80,833,777 bytes**, SHA-256 `b46ebbed7f0959393b6dca2f939d57667abd110238c28ab8086f736e75e38921`.
Compressed: **8,862,461 bytes**, SHA-256 `195c9bfbd095fe7699fba4311d1d05f0eaa4195e609914bc34946fcddefe6b45`.
Exact core/example assembly identities and hashes, UTC interval, runtime/OS, every response, receipt, schedule, queue metric, measured CPU/allocation and timing are in the raw artifact. This pilot ran with `DOTNET_PROCESSOR_COUNT=1`, workstation GC and a 768-MiB managed-heap cap on a shared Windows host; artificial async delays can overlap without additional CPU parallelism.

| Profile | PipelineConcurrent / Batch elapsed speed ratio | Adjusted bootstrap interval | Paired final quality difference |
| --- | ---: | --- | ---: |
| ZeroLatency | 0.4682 | [0.3820, 0.5728] | 0 |
| ProposalBound | 2.9473 | [2.8975, 2.9846] | 0 |
| Mixed | 2.9428 | [2.9156, 2.9824] | 0 |

A speed ratio above one favors PipelineConcurrent. **The zero-latency downside is retained**: median 3.537 ms versus Batch's 1.937 ms, with median measured allocations 2,521,560 versus 1,156,224 bytes. Delayed-profile medians are roughly 167 ms versus 492 ms. Windows timer granularity and shared-host scheduling strongly influence these artificial profiles; these are not production speedup estimates.

PipelineSerial and Batch give identical matched final quality. Against Continuous, PipelineConcurrent's mean quality difference is −0.0002952 with adjusted interval [−0.0254226, 0.0501502]; no quality advantage is established. The analyzer pairs before averaging tasks/timing repetitions inside each of **eight seed clusters**, uses 10,000 bootstrap draws (seed 20) and adjusts 18 endpoints. Completed-only latency tails and all unfavorable seed effects remain in `pilot/analysis.json`. This small fixed authored suite is retrospective, not representative or confirmatory. Pipeline remains off by default.

## Default-mode regression and rejected hardware comparison

`default-profiles.zip` retains all **396 fresh-process measurements**, complete plans, per-case inputs, outputs and logs:

- `us20-defaults-before`: 132 attempts on pre-pipeline source `255feb24369702a32ea9db7a3f8a0b7a847d2762`, Windows processor group 0.
- `us20-defaults-after`: 132 attempts on pipeline source `9c3441d9723ed333644d991e2df9e658e4e25a74`, group 1. Each campaign passed independently and logical results matched, but the cross-build comparison **was rejected**: mask `F` in different processor groups denotes different CPUs. These measurements were not discarded or presented as controlled timing evidence.
- `us20-defaults-after-group0`: 132 attempts on `e62b314d005faf41e7101735363b3c49af55081a`, explicitly selecting group 0 with `AIDOTNET_PROFILE_PROCESSOR_GROUP=0`.

The accepted comparison uses the first and third campaigns. All 132 pairs have identical state hashes, best quality, evaluation calls, operations and occupied cells; full case/runtime/group/affinity identities match. Each source ran 44 predeclared cases × three repetitions. The existing engine cases evaluate eight seeds and then invoke variation; they are not seed-only tests. CPU mask `F`, four effective processors, tiered compilation off, workstation GC and Windows all-group/assignment controls off are recorded per measurement.

For cheap single-worker, no-checkpoint cases, Batch's median managed allocations increase by **72,776 bytes (+0.194%)** and Continuous by **72,776 bytes (+0.191%)**. Their descriptive elapsed medians are 40.495 → 40.254 ms and 42.335 → 41.963 ms. **Do not call this zero overhead or a speedup**: allocations increased, and source groups ran sequentially on a shared host. `default-comparison.json` retains every case, including checkpoint/archive/dimension/island factors and unfavorable outcomes. Its hashes link both original profile reports and the complete ZIP.

## Reproduction and verification

The [pipeline contract](../../../../docs/PROPOSAL_PIPELINE.md) specifies caps, timing boundaries, reservation conservatism and replay limitations. Build the listed clean source commits; informational versions on dirty worktrees are not evidence of source cleanliness. Python analysis was generated with 3.14.6; verification permits only tiny cross-platform floating-point roundoff, never changed counts or identities.

```powershell
python benchmarks/analysis/analyze_pipeline.py --verify-evidence benchmarks/evidence/pipeline/9c3441d/pilot
python benchmarks/analysis/compare_pipeline_defaults.py --archive benchmarks/evidence/pipeline/9c3441d/default-profiles.zip --verify-comparison benchmarks/evidence/pipeline/9c3441d/default-comparison.json
```

Both verifiers recompute the analysis from the retained raw data and validate the hash chain. A successful local check does not imply hosted CI, current-head review, foundation merge or representative competitor superiority.
