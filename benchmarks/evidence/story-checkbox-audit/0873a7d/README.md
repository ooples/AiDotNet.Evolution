# V1-01 (#108): the audit at `0873a7d`, 68 to 66

Measured by [`eng/Measure-StoryCheckboxAudit.ps1`](../../../../eng/Measure-StoryCheckboxAudit.ps1) at main
`0873a7d`. `audit.json` is the script's output, unedited. **66 unchecked, 89 checked, 0 issues closed with
unticked boxes.**

The previous reading, at `e083c3b`, was 68 / 87. **Two boxes moved, and both moved because a test was
re-executed on current `main` rather than because the reading was re-taken:**

| issue | box | what earned it |
| --- | --- | --- |
| #21 | the V1-01 audit box | The audit's own filter (`Admission|Correctness|Promotion|Feasib`, net10.0) now runs **64 tests, all passing** - 23 in `AiDotNet.Evolution.CSharp.Tests` and 41 in `AiDotNet.Evolution.Tests`. The V1-01 audit recorded **39** at `a8a20f0`, so the suite grew by 25 cases and none of it regressed. The story's other two criteria keep their current-main execution in CI: `test_program_correctness.py` through `run_required_tests.py benchmarks/external 'test_program*.py'` (`build.yml:373`), and the isolation suites against the real sandbox image (`build.yml:419`) |
| #37 | the V1-01 audit box | `EvolutionRoutingTests` re-executed on current `main`: **9/9** |

## What the two boxes do *not* mean

Neither story is finished. #21 is waiting on a merged PR with a counted CodeRabbit review and attached
current-revision evidence; #37 is waiting on its metrics table, which needs the paired pilot (#116).
The box that moved is the one that asks whether the criteria below it are ticked *with evidence* - and
for both stories they now are, against this commit rather than a six-week-old one.

## The shape of what is left, counted

66 unchecked boxes across the twelve stories, and they are not 66 pieces of work:

| what the box waits on | issues | roughly |
| --- | --- | ---: |
| a paid paired pilot, or the decisive gate campaign after it | #29, #30, #35, #36, #37, #41, #43 | ~34 |
| a dependency box pointing at a sibling story that is itself open | #22, #24, #35, #36, #42, #43 | ~8 |
| a merged PR with a counted review, or attached current-revision evidence | all twelve | ~24 |

**The review half of that last group is not always obtainable.** CodeRabbit's free tier has twice now
passed a PR's checks while posting no review at all - #238 (recorded on #174) and #293 - so a box
asking for `gh pr view N --json reviews` to be non-empty can stay unticked on work that is otherwise
complete. Counting checks instead would tick it falsely.

## What this evidence is not

It does not tick a box: the script reports and never edits an issue, because ticking is a judgement
about evidence and belongs to whoever makes it. The two ticks above were made by hand on #21 and #37,
each annotated with the measurement that earned it, and this reading is what those edits look like
from the instrument's side.

It is also not a measure of remaining effort. Two of the stories whose boxes are counted here hold
metrics that are **measured and unmet** (#38's pipeline target names real models; #22's evidence
regeneration is 4 of 51), so a ticked box can record a miss as faithfully as a win.
