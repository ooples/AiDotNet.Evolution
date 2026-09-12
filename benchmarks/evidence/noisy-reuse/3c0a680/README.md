# US-23 equivalent-prior noisy measurement reuse

Runtime/source pin: `3c0a6803d02bd0b6848a02a47137dec6db703ac4`.
Audit hardening: `1f41623e0e44f6abf580d112e20b1bdce9fe486f`; no acquisition, budget,
task, seed, comparison or primary endpoint changed. Full protocol and reproduction
commands: [NOISY_REUSE_CAMPAIGN.md](../../../../docs/NOISY_REUSE_CAMPAIGN.md).

The fixed campaign passed **1,152 runs**, including priors, across 32 seeds, two
authored objectives, four phases, two configurations and physically repeated replay.

| Acquired scalar observations | Primary | Replay |
|---|---:|---:|
| Search | 161,280 | 161,280 |
| Prior construction | 5,120 | 5,120 |
| Fresh winner confirmation | 12,800 | 12,800 |
| Total | 179,200 | 179,200 |

All **358,400 physical sample IDs are distinct**. Cache references retain their
original sample IDs, mean, standard error, acquisition cost and observation time;
they add no independent samples. All scheduled outputs are retained, with no failed
or omitted primary cases. Quality/accounting replay matches after excluding the
intentionally different acquisition identities.

Warm reuse saved **40 scalar observations per paired run**, with paired-seed bootstrap
95% interval **[40, 40]**. Every seed has the same effect because eight imported seeds
have five reusable observations each. Both controls receive identical prior bytes and
the full **80-observation prior-cost attribution**; that prior is physically acquired
once per task/seed/execution. This does not show net first-run savings against a cold
no-prior search, faster wall time, greater independent sample power or better search
quality. It compares two configurations of this engine, not OpenEvolve or another
competitor. Procedural scalar noise is not production timing noise. The 25 separately
charged post-selection observations per winner are fresh-stream confirmation, not
held-out representative task evidence, and are never returned to search.

Verification: **661/661 core tests on each of net10.0, net8.0 and net471**, zero skips;
line/branch coverage **91.97% / 77.96%**, unchanged ratchet; **25 Python analysis tests**;
formatting passed. The original one-argument deterministic example still passes
8/0/8 evaluator calls for cold/warm/force-fresh, with current validation on each phase.
The unpublished package passes the dependency/content contract, and every packed DLL
matches its target's built bytes. The campaign's core hash equals the tested net10 DLL.

Adversarial tests exposed six missing rejections in the first audit: duplicate or
dropped confirmation receipts, maximum violations, exceeded receipts and hidden
charged resources. The stricter audit reproduces the original summary byte-for-data
without reacquiring or selecting results. Both red/green test logs and both summaries
are retained. Earlier pre-v3 development smokes remain in the local TestResults history;
they are not substituted for this fixed v3 campaign. The included v3 smoke independently
retains 72 runs and 22,400 physical samples with matching replay.

`verification.zip.001` through `.004` are consecutive byte parts of one unchanged
ZIP (140,227,474 bytes; SHA-256 `2bbf8ee6a658aa00806de9c09bcf32b03d690b360fdc549469b2983756666214`).
Each part is at most 40 MiB to keep individual Git blobs below hosting limits.
The archive contains all primary/replay outputs (including raw observations,
cache records, repertoires and receipt traces), v3 smoke outputs, logs, TRX, coverage
and package proof. `integrity.json` binds each part and the combined archive; its internal
manifest hashes every file. The verifier assembles the ZIP in memory and reads it directly,
without extracting or
executing candidate code or starting any evaluator:

```powershell
python benchmarks/evidence/noisy-reuse/3c0a680/verify.py
```

Production scalar evidence storage and its cross-target receipts are in
[AiDotNet PR #2182](https://github.com/ooples/AiDotNet/pull/2182). Hosted CI, review,
dependency integration, CLI consumer wiring, representative benchmark evidence and
normal published-package compatibility remain distinct merge/release or cross-story
gates. No issue closure, deployment, package publication or full-plan completion is implied.
