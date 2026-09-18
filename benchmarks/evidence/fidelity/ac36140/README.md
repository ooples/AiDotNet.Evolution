# US-15 multi-fidelity evidence

Production revision: `ac36140667911b47a955616b95ba00569041bc2a`. September 11, 2026, local Windows CPU/.NET 10; no paid API or rented compute. These are retrospective, authored fixtures, not representative competitor benchmarks.

Both complete 128-run campaigns reproduced **byte-for-byte** with the same pinned binary: 256 primary runs, 256 replay runs, 18,432 evaluator calls. No failed/invalid run was omitted; all 256 primary rows passed the accounting/confirmation validator. Raw artifacts include every batch, individual sample, origin, charge and resource receipt. Trained raw additionally records actual epochs, data identity, held-out error and final weight hashes.

## Actual model training

Four-feature linear regression, full-batch gradient descent on 128 generated training rows and 64 held-out rows. Eight fixed learning-rate/warmup genomes, two replicates, epochs 4/16/64, 32 paired seeds. Continuation carries actual weights and resumes the same data stream; confirmation uses fresh training/held-out data and initialization, retraining all 64 epochs.

| Method | Mean confirmed quality | Worst quality | Work units/run | Actual training epochs/run |
| --- | ---: | ---: | ---: | ---: |
| FullCohort | 0.9991393703 | 0.9990050368 | 2064.08 | 2048 |
| GreedyHalving | 0.9991393703 | 0.9990050368 | 616.08 | 608 |
| ExploratoryHalving | 0.9991393293 | 0.9990050368 | 616.08 | 608 |
| RestartOnlyHalving | 0.9991393703 | 0.9990050368 | 712.08 | 704 |

Quality is `1 / (1 + held-out MSE)`. The declared tariff is one work unit per actual epoch, 0.25 per data-regeneration/scoring/token operation, and 0.08 initial cohort setup. These are work assumptions, not measured wall-clock time, CPU utilization or dollars.

Greedy matches full-cohort and restart-only quality exactly on all 32 seeds; token continuation saves 96 actual epochs against restart-only. This observation on an easy authored task is not a statistical equivalence or generalization claim. All methods have the same 2100 cap, but finite brackets **spend different amounts**; FullCohort also confirms eight models instead of two.

Exploration minus greedy quality is **-4.0968994e-8**, unadjusted paired 95% percentile interval **[-6.1084792e-8, -2.2306908e-8]**, 14 losses and 18 ties. This tiny negative result is retained. No default change is justified by this fixture.

## Misleading early-score control

The separate synthetic curves deliberately create an aligned ranking and a late-improver family. Greedy/exploratory methods share eight initial genomes, epochs/steps 1/3/9, 32 seeds and a 100-unit cap; each spends 92.08 units. These curves test the mechanism, not actual learning.

Exploratory minus greedy:

- Aligned: mean **-0.00125310**, interval **[-0.00326099, 0.00078651]**, 13 wins/19 losses.
- LateImprover: mean **+0.08533854**, interval **[0.05892505, 0.10903252]**, 28 wins/4 losses.

Exploration helps some late improvers reach full evaluation; it does not guarantee rescue or universally improve quality. Intervals use 10,000 paired-seed bootstrap draws, seed 42, no task resampling and no multiplicity adjustment. Failed/incomplete/invalid rows receive worst-support quality zero rather than disappearing; malformed schedules are rejected.

## Separate-process recovery

`recovery.zip` retains baseline/start/resume JSON from three distinct processes using the same pinned binary. The start checkpoint pauses after nine settled batches: 18 calls/88 training epochs. Resume executes 14 new calls/520 epochs. Together they exactly match the uninterrupted 32 calls/608 epochs, every measurement/weight hash, final report and ledger. This adds 64 evaluator calls outside the 18,432-call campaign/replay count.

This is trusted, coordinated settled pause/restart. It does not recover unjournaled in-flight work after arbitrary process death; that requires US-21 reconciliation. Checkpoint I/O, process startup and rehydration CPU are not priced automatically. The retained tokens contain only this authored fixture's weights, not credentials or user data.

## Artifact integrity

| Artifact | SHA-256 |
| --- | --- |
| Trained uncompressed raw (17,899,433 bytes) | `bfce562f5a16a43fbe383f025505f29bbf42a4f55ec02966f64a4406f677b8ac` |
| [Trained raw.json.gz](trained/raw.json.gz) | `187ebeee1a2c211c172a45ec0d64ccbbf58b9d3d898057adb71ceced75b78ed0` |
| Curves uncompressed raw (10,902,432 bytes) | `033e7c94e853559dd1859f005f018f9e56ffae011edaf772dffa180cb750d287` |
| [Curves raw.json.gz](curves/raw.json.gz) | `66c734061f266d18892d49f41cf58de4e04df10df3d7bab1970260bdf440d68f` |
| [Recovery archive](recovery.zip) | `d72bfdf4e2ef5312f4609493dec508417b3640102e44e3054e0ac56da6814512` |

[Trained analysis](trained/analysis.json) and [curve analysis](curves/analysis.json) retain all normalized rows, paired contrasts, mean/median/worst outcomes and analysis-program hash. CI rehashes compressed/uncompressed evidence, recomputes both analyses, checks recovery equivalence, and rejects corrupt trained error/work/confirmation records. Source code and evidence are separated: documentation/test-only follow-up commits do not relabel the production binary.

## Reproduction

Use a clean checkout of the full production revision and new output paths. Build with .NET 10; the full core suite also supports .NET 8 and .NET Framework 4.7.1.

```powershell
dotnet build examples/MultiFidelitySearch -c Release
dotnet run --project examples/MultiFidelitySearch -c Release --no-build -- --regression 32 trained.json
dotnet run --project examples/MultiFidelitySearch -c Release --no-build -- 32 curves.json
python benchmarks/analysis/analyze_fidelity.py trained.json new-evidence/trained
python benchmarks/analysis/analyze_fidelity.py curves.json new-evidence/curves
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Test-FidelityRecovery.ps1
```

Repeat the two runs into new paths and compare complete file hashes. Binary hashes/JSON can differ on another build/runtime; preserve its provenance rather than relabeling it as this exact run. `python -m unittest discover -s benchmarks/analysis -v` validates the retained evidence at the delivery revision.
