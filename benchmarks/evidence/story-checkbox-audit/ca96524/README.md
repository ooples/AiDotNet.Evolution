# V1-01 (#108): acceptance-checkbox state across the roadmap stories

Measured by [`eng/Measure-StoryCheckboxAudit.ps1`](../../../../eng/Measure-StoryCheckboxAudit.ps1)
at main `ca96524`. `audit.json` beside this file is the script's output, unedited.

The script is the instrument #108's metrics table names ("audit script over issue bodies"), so the
baseline is reproducible rather than a number someone counted once. It reports and never edits an
issue: ticking a box is a judgement about evidence and stays with the person doing the audit.

## Baseline

| issue | state | unchecked | checked |
|---|---|---:|---:|
| #21 US-03 | open | 6 | 5 |
| #22 US-04 | open | 5 | 9 |
| #24 US-06 | open | 4 | 9 |
| #29 US-11 | open | 12 | 3 |
| #30 US-12 | open | 12 | 2 |
| #35 US-17 | **closed** | 6 | 7 |
| #36 US-18 | **closed** | 10 | 3 |
| #37 US-19 | **closed** | 10 | 3 |
| #38 US-20 | **closed** | 3 | 6 |
| #41 US-23 | open | 8 | 4 |
| #42 US-24 | open | 6 | 8 |
| #43 US-25 | open | 8 | 6 |
| **total** | | **90** | **65** |

## What the baseline shows

**Four of the twelve are closed while 29 of their acceptance boxes are unticked** (#35, #36, #37,
#38). That is the single state the audit exists to remove: the epic reads those stories as done and
their own criteria say otherwise. Each needs the ticks with evidence or a reopen.

**#108's third acceptance criterion rests on a stale premise.** It says US-03 (#21) has "core
criteria unticked although #75 merged". On the body at this revision all three core criteria are
already ticked — a fast-but-wrong candidate cannot become a deployable winner, finalists are checked
by an independent evaluator on held-out/boundary/adversarial/randomized cases, and generated code
runs under enforceable process isolation. #21's six remaining boxes are process/meta boxes (story
PR, CI green, CodeRabbit confirmed, metrics table, retained experiments, dependency readiness), not
capability claims.

Those three ticks were verified rather than trusted: the suites that claim them —
`ExecutionAdversarialTests`, `JudgeAdversarialTests`, `ModelRuntimeAdversarialTests`,
`NoveltyAdversarialTests`, `ProgramFoundationAdversarialTests`, `ScriptEvidenceAdversarialTests`,
the whole `.Execution.` isolation set and `ProgramDescriptorPromotionTests` — ran **244 passed, 0
failed** on this revision (net10.0, Release). No criterion needs reopening as a gap.

## What this is not

The remaining eleven issues' 84 boxes are **not** audited here. This commit delivers the
instrument, the measured baseline and the two findings that change what the epic reports; the
per-criterion pass over #22, #24, #29, #30, #35–#38, #41–#43 is still open work, and
closed-with-unticked (#35–#38) is where it should start.
