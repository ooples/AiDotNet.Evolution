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
