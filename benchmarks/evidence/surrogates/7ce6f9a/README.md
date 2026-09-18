# US-14 current-stack cost-matched verification

Runtime source: `7ce6f9a`. This is a CPU-only development study, not an OpenEvolve
comparison or a release/default-promotion gate. Configuration matches the prior
`c770896` pilot: 2 tasks × 4 methods × 10 seeds × 3 evaluator tariffs = 240 runs.
No post-result retuning. Caps are 6.4, 64 and 640 synthetic work units for evaluator
tariffs 0.1, 1 and 10 respectively; compare methods within each tariff only.

All 240 runs completed without invalid accounting. Primary: 13,624 true objective
calls. Exact full replay: another 13,624, totaling **27,248 pilot calls**. Initial
populations match within each task/seed/tariff. Setup, rejected proposals, training,
calibration, validation, inference and true evaluations are charged. Synthetic work
tariffs are not measured wall time or dollars. No model/API calls were made.

Raw SHA256: `33c2663c1f91fc7b52b86f63a2378c7b87241ffdb247f57809e5e4fd3fc38d95`.
`raw.json.gz` retains all measurements, decisions and receipts; `summary.json`
binds its compressed and decompressed hashes; `analysis.json` binds the exact
summary bytes. Hash-chain tests cover this and the unchanged historical archive.

## Mean final loss: before, after, and current ordinary control

Lower is better. Previous/current columns refer to the validation-guarded backend.

| Task | Evaluator tariff | Previous backend | Current backend | Ordinary search |
|---|---:|---:|---:|---:|
| Shifted quadratic | 0.1 | 0.012068 | 0.010809 | 0.003157 |
| Shifted quadratic | 1 | 0.002560 | 0.002819 | 0.003157 |
| Shifted quadratic | 10 | 0.002317 | 0.002547 | 0.003157 |
| Rippled quadratic | 0.1 | 0.257182 | 0.291890 | 0.272316 |
| Rippled quadratic | 1 | 0.246864 | 0.286598 | 0.262746 |
| Rippled quadratic | 10 | 0.246864 | 0.286566 | 0.262746 |

The current backend does not demonstrate a net improvement. At the cheap shifted
quadratic tariff its paired mean loss difference versus ordinary search is
**+0.007652**, with unadjusted 95% seed-bootstrap interval **[+0.000764,+0.015517]**.
The other five ordinary-search contrasts include zero. All 18 comparator contrasts,
tail losses and failures are retained in the analysis; intervals are retrospective,
unadjusted across comparisons and not independent confirmation.

US-13 changed real-domain schema identities. Because this backend partitions by
genome hash, partition membership changes even for unchanged coordinates; these
before/after differences must not be attributed solely to the endpoint bug fix.
There were 1,607 accepted fits, 480 insufficient-genome fallbacks and 143 coverage
failures. Validation acceptance is not evidence of optimization benefit.

Decision: retain opt-in/default-off behavior. Correctness fixes and packaging are
delivered; competitive superiority or a generally beneficial acquisition policy
is **not** established. Do not tune on these results and then reuse them as held-out
proof. Representative expensive/noisy workloads require a separately frozen plan.
