# US-22: fixed-capacity archives under paired resource budgets

Source/runtime/analysis pin: `f990516a31c5e6c9f2e7f7110373581a9d460cfc`.
The optional archive and remap/report contracts are implemented. This authored development
experiment does **not** establish a quality advantage, equivalence, or a default-promotion case.

## Fixed plan and result

32 seeds × two objectives (Quadratic/Rippled) × 12/20 descriptors × two methods,
then an additional physical replay: **512 fresh processes, 131,072 evaluations**.
Both methods receive identical initial genomes, 32 elite slots, 256 evaluation units,
1,024 proposal-call units and a 256 MiB observed process-lifetime peak RSS budget.
AB/BA ordering alternates by seed and reverses for replay. All four task/dimension
contexts are averaged within seed before a single predeclared paired-seed bootstrap.

- All cases completed; no unreported physical work in the successful campaign.
- 65,536 primary evaluations plus 65,536 replay evaluations; quality replay is exact.
- Centroid-minus-grid common-reference utility: **-0.0005897766136612284**.
- Paired-seed bootstrap 95% interval: **[-0.0050483404561363545, 0.004198490537998666]**.
- All archive capacities, common-reference projection counts, resource receipts and
  trace physical-attempt totals validate. Resource/context summaries remain descriptive.

| Context | Grid median peak MB | Centroid median peak MB | Grid median wall ms | Centroid median wall ms |
| --- | ---: | ---: | ---: | ---: |
| Quadratic, 12D | 44.95 | 44.80 | 574.9 | 571.6 |
| Quadratic, 20D | 46.06 | 45.30 | 651.8 | 659.4 |
| Rippled, 12D | 45.06 | 44.94 | 616.4 | 612.5 |
| Rippled, 20D | 46.09 | 45.37 | 689.3 | 687.6 |

MB here is decimal; the budget uses binary MiB. These are process measurements including
runtime/startup/shared pages/instrumentation, not retained archive bytes. The Windows x64
.NET 10.0.12 host was not exclusive. `DOTNET_PROCESSOR_COUNT=1`, workstation GC,
`DOTNET_GCHeapHardLimit=0x30000000`. No controlled latency speedup is claimed.
Frozen uniform Voronoi sites are not fitted CVT sites. There is no task-population or
competitor generalization from these four authored contexts.

## Retained instrumentation failure

The original `b834c96` campaign was interrupted after 20 case records: 18 valid reports,
two timed-out workers with partial JSON, and one additional interrupted worker without
a record. Work for those three unreported cases remains **unknown**, not zero. Its full
plan, raw/partial outputs and interruption manifest remain in `interrupted/`.

Per-engine-event `Process.Refresh`/peak-memory polling dominated these cheap objectives:
100 diagnostic observations took 3.98 seconds, and workers hit the 60-second limit.
The corrected protocol observes at construction, before search, every 16 `Evaluated`
events and after common-reference projection. The OS retains the lifetime peak between
observations; admission can continue between checks. This is an observed-budget gate,
not an OS-enforced allocation ceiling. Final over-budget cases receive zero utility
without erasing actual charges. The correction also preserves timeout and report-parse
failures separately. Tasks, seeds, budgets, timeout, analysis and replay schedule are
unchanged; the offline verifier checks both plans. No quality result selected the correction.

## Verification and artifacts

`verification.zip`: 14,403,210 bytes; SHA256
`706cfa1d33b79c823b0f96930cb066bc34393818e3778b7d7c7f92bae146d546`.
`integrity.json` and the internal manifest bind every raw file. This is integrity and
reproducibility evidence, not authentication of the machine or evaluator.

```powershell
python benchmarks/evidence/archive-resources/f990516/verify.py
```

The verifier bounds extraction into a fresh private temporary directory, verifies all
file digests, recomputes the primary/smoke summaries from complete raw schedules,
checks unchanged experiment parameters across the instrumentation correction, and
validates test and package receipts. It makes no model calls and never changes live stores.
To inspect all quality/diversity/resource curves, use the raw `primary/*.json` reports,
`primary/summary.json` and their independent embedded `.record.json` copies.

Source-pinned local verification: **664/664 tests on net10.0, net8.0 and net471**, zero skips;
coverage **91.99% line / 77.96% branch**, above unchanged ratchets. 27 Python analysis
tests and formatting passed. All three package DLL hashes equal their explicit builds;
the net10 core hash also equals every campaign worker's core hash. No package was published.
The subsequent CI-only follow-up makes the archive job part of the aggregate CI gate
and adds a regression test (28 analysis tests at PR head); the pinned experiment and
its retained artifacts are unchanged.
Separate smoke evidence retains 512+512 evaluations, both 1 MiB pre-admission rejections,
64D grid configuration rejection (zero calls), and 64D centroid completion (eight calls).
Smoke intervals are not inferential evidence; these additional calls are not hidden inside
the primary budget. Earlier development runs outside this artifact are not included in a
claimed all-history cost total.

US-04/US-10 dependency readiness, hosted CI and review remain merge gates. Representative
held-out comparisons and any default promotion belong to the broader benchmark roadmap.
