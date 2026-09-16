# US-02: selection-policy efficacy experiment

As a maintainer, I want evidence that a search-policy change improves generated programs against OpenEvolve, so that competitive claims depend on measured outcomes rather than feature counts.

Given the frozen US-04 results do not establish superiority, when we investigate them, then original losses remain in the record. Structural AST identity is a diagnostic, not proof of equivalent performance. The two-proposal budget provides little evidence about sustained population search.

Given the existing uniform policy and built-in elite selection, when this development experiment runs, then both use four proposals, the same evaluator and declared caps, and all six comparator tracks. Controlled mode changes parent selection only; native mode also changes the built-in inspiration policy. No production default changes.

Given two paired seeds (201, 203), when profiles run in balanced order (uniform/best, best/uniform), then every profile uses the same three development families and paired search, diagnostic and correctness-audit instances. The frozen four-block schedule permits at most 252 subscription model attempts, with no paid API fallback or optional extension. Failed and missing cells remain visible; unknown work stops further block dispatch.

Given completed measurements, when reporting, then show per-task deployed fresh-process batch timings, comparisons to OpenEvolve, original-to-deployed speedups, and actual model/evaluator costs. Startup, imports and serialization are included: these are not kernel-only timings. Equal caps are not equal actual spend. Every development call counts as tuning; OpenEvolve is not represented as exhaustively tuned.

Given these previously inspected families and only two paired seeds, when interpreting results, then no significance, unseen-family generalization, or superiority claim is permitted. Model snapshot identity remains unreported by the subscription transport. A future confirmatory protocol must be frozen separately after development, with an adequately powered and affordable design.

## Adversarial review

- Reject changing all defaults based on retrospective losses: keep the original default and isolate an opt-in policy.
- Reject comparing the new four-proposal runs only to old two-proposal results: rerun uniform and competitors at four proposals.
- Reject attributing native effects solely to parent selection: inspiration selection changes too.
- Reject treating repeated timing samples as independent searches: only two search seeds are scheduled.
- Reject relabeling inspected families as a holdout or erasing failed work: retain the complete schedule and independent accounting.
- Reject claiming strict wins on primitive-dominated tasks without evidence; equal structures and process overhead may limit measurable gains.

Implementation: `benchmarks/analysis/selection_study.py`; retrospective diagnostic: `benchmarks/analysis/program_diagnostics.py`. These are development tools, not proof that US-02 is complete.

## Completed experiment: do not promote elite selection

The registered four-block experiment completed at implementation `88f328d`.
All 72 scheduled rows were validated, all 72 selected programs passed the
post-selection audit (3,600 output checks), and no fallback/missing rows occurred.
There were 252 subscription model attempts, 2,532,697 reported input-plus-output
tokens, 1,350 container attempts, and zero unknown model/container attempts.
Known supervisor time was 1,683.713 seconds; this is not CPU time or kernel time.
Peak memory and monetary allocation remain unmeasured, not zero. No API-key calls
were used.

**Decision: retain uniform as the default.** Elite selection did not deliver
consistent improvement or beat OpenEvolve across all task/mode pairs. This rejects
promotion on this evidence; it does not prove elite selection is always worse.

Registration SHA-256:
`75b0a5f213a150489a7218cd4d51427fb447e8855984bbb823a3b426696e3d01`.
It was [published before dispatch](https://github.com/ooples/AiDotNet.Evolution/issues/20#issuecomment-5689307701).
Actual repeated execution was also refused with `FileExistsError` on
`consumed.json`, before any additional dispatch.

### Measured policy comparison

Values are geometric means over **two search seeds**, in milliseconds of deployed
fresh-process batch latency. Lower is better. The OpenEvolve column uses the
matched elite-profile blocks. Ratios above 1 favor Evolution. These are descriptive
observations without powered inference; timing repeats are not extra searches.

| Task | Mode | Uniform Evolution ms | Elite Evolution ms | Matched OpenEvolve ms | OpenEvolve / elite |
| --- | --- | ---: | ---: | ---: | ---: |
| Base64 | Controlled | 213.36 | 215.03 | 217.89 | 1.0133 |
| SHA-256 | Controlled | 194.92 | 213.94 | 229.71 | 1.0737 |
| Graph components | Controlled | 194.01 | 207.96 | 203.91 | 0.9805 |
| Base64 | Native-bounded | 211.21 | 212.34 | 216.70 | 1.0205 |
| SHA-256 | Native-bounded | 206.64 | 205.90 | 209.37 | 1.0169 |
| Graph components | Native-bounded | 191.79 | 221.27 | 188.81 | 0.8533 |

The native graph result reverses by seed: uniform→elite is 182.311→265.701 ms
at seed 201, but 201.760→184.264 ms at seed 203. That instability is a reason
not to turn the aggregate into a causal/general superiority claim. Balanced order
does not eliminate host/provider drift or stochastic candidate differences.

### Original-to-deployed results

The original programs and deployed candidates are separately measured on matched
diagnostic inputs. These speedups include import removal and startup effects.

| Task | Controlled uniform: original→deployed ms | Speedup | Controlled elite: original→deployed ms | Speedup |
| --- | ---: | ---: | ---: | ---: |
| Base64 | 304.89→213.36 | 1.429× | 299.41→215.03 | 1.392× |
| SHA-256 | 213.93→194.92 | 1.098× | 242.81→213.94 | 1.135× |
| Graph components | 389.44→194.01 | 2.007× | 349.98→207.96 | 1.683× |

### Costs and cheaper controls

Each column sums six task/seed cells. Tokens are reported input plus output, not
currency; evaluator seconds include supervision. Independent counters, not retained
receipt counts, supply the search-work totals.

| Track | Uniform-block tokens | Elite-block tokens | Uniform evaluator seconds | Elite evaluator seconds |
| --- | ---: | ---: | ---: | ---: |
| Evolution controlled | 229,024 | 227,477 | 71.535 | 69.087 |
| OpenEvolve controlled | 241,061 | 240,411 | 106.194 | 110.544 |
| One-shot controlled | 60,110 | 60,201 | 44.458 | 47.079 |
| Single-parent controlled | 226,942 | 226,654 | 110.082 | 114.057 |
| Evolution native | 242,114 | 243,369 | 74.575 | 74.677 |
| OpenEvolve native | 267,662 | 267,672 | 110.834 | 116.268 |

The one-shot control also matters: in the elite blocks its descriptive geometric
latency was about 4.4% lower than Evolution's across the six controlled cells,
using 60,201 versus 227,477 tokens. This experiment does **not** establish that
population search adds value over cheaper controls. Native presets were not
equally tuned; every call here is disclosed development/tuning effort.

## Verification and remaining acceptance

One final Release solution build and format check passed. Core tests:
654 net10 / 608 net8 / 608 net471; analysis 91; external/integration 39.
All 2,000 passed without skips, including actual elite-profile controller execution
and 11 Docker tests. No heavy tests ran beside live timing. Adversarial CI review
also found that the new profile integration needed explicit binary/upstream paths;
the workflow now supplies them instead of silently skipping that test.

US-02 remains open for the [broader empirical acceptance](../../benchmarks/US02_EMPIRICAL_ACCEPTANCE.md):
informative work-dominated endpoints, equally budgeted native tuning, independently
frozen held-out evaluation, adequate uncertainty/regression analysis, and ablations
for other claimed features. The three inspected families and two paired seeds here
cannot satisfy those requirements. Do not extend this consumed registration or
silently relabel development data as confirmation.

## Evidence and reproduction

- [Lossless raw archive](selection-study.tar.xz): registration, all attempts,
  candidate sources, prompts/responses, container receipts, audits and result rows.
- Per-block JSON/Markdown/HTML: [uniform-201](selection-analysis/uniform-201/report.md),
  [best-201](selection-analysis/best-201/report.md),
  [best-203](selection-analysis/best-203/report.md),
  [uniform-203](selection-analysis/uniform-203/report.md).
- All 72 complete, reconciled search trajectories are retained in archived
  `study.json` under `blocks[].progress`. They describe search utility, not held-out
  performance; the per-block summary reports do not reconstruct them from endpoints.
- [Retrospective US-04 diagnostics](us04-loss-diagnostics.json) retain the earlier
  findings separately; no old scores or intervals were changed.

Archive SHA-256:
`9dfafbb8dc6de7f1f1380fe1f41bd15eac3ff6416d2ddb6adbbb6687e6330123`.
Archive size: 19,588,440 bytes; 10,849 file mappings and 3,675 distinct blobs.
The archive was actually verified and restored (1,599,428,984 bytes). All twelve
regenerated JSON/Markdown/HTML files were byte-identical to the local originals.
Git normalizes checked-in text to LF; this does not assert raw Windows/Linux
byte identity. Hash consistency is not authentication; retain the expected hash
independently.

```powershell
python benchmarks/analysis/program_evidence.py --archive docs/evidence/us02/selection-study.tar.xz --sha256 9dfafbb8dc6de7f1f1380fe1f41bd15eac3ff6416d2ddb6adbbb6687e6330123 --output <new-restored-directory>
python benchmarks/analysis/program_report.py --pilot <new-restored-directory>/uniform-201 --audit <new-restored-directory>/uniform-201-audit --output <new-report-directory>
```

Repeat reporting for the other three block IDs. These commands only restore/read
evidence, with no model calls or candidate execution. Geometric summaries above
use `exp(mean(log(seconds)))` across the two seeds for each task/mode/profile;
cost columns sum those six task/seed rows per track.

For a separately planned development pilot, `run_program_pilot.py` accepts
`--evolution-profile best --iterations 4` in addition to its existing required
paths/image arguments. Defaults remain `uniform` and two proposals. Do not reuse
the consumed study registration to launch another experiment.
