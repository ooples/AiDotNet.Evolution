# V1-01 (#108): the audit after the merges, 72 to 68

Measured by [`eng/Measure-StoryCheckboxAudit.ps1`](../../../../eng/Measure-StoryCheckboxAudit.ps1) at main
`e083c3b`. `audit.json` is the script's output, unedited. **68 unchecked, 87 checked, 0 issues closed with
unticked boxes.**

The previous reading, at `86d56c9`, was 72 / 83. Four boxes moved, two on each of two stories, and each
was ticked because work merged - not because the reading was re-taken.

| issue | box | what earned it |
| --- | --- | --- |
| #22 | metrics table filled | **0 bytes on 4 of 51 evidence READMEs**, measured by `eng/Measure-EvidenceRegeneration.ps1`, raw census committed. The target is *not* met; the box records that the value is measured |
| #22 | merged PR, CI green, CodeRabbit review | [#275](https://github.com/ooples/AiDotNet.Evolution/pull/275) and [#277](https://github.com/ooples/AiDotNet.Evolution/pull/277) merged; `gh pr view 275 --json reviews` lists **4 `coderabbitai` reviews**, and its three threads were on this work |
| #42 | metrics table filled | both rows measured and met: **12.155 s median of three** from a clean container to a first HTML report against 600 s, and report-opens-offline **yes** |
| #42 | merged PR, CI green, CodeRabbit review | #275 merged with 17/17 checks green and the same 4 counted reviews |

## Why the count is not a measure of remaining effort

Two of the four boxes above belong to a metric that is **measured and unmet** - 4 of 51 READMEs
regenerate. Ticking "metrics table filled" says a number exists and is linked, not that it passed, and
#22's target row still reads 0 bytes against a partial surface. The audit counts boxes; only the metrics
tables say whether the targets were reached.

What is left is still mostly one of four things: a paid paired pilot (#35, #36, #37, #41), the pilot
campaign (#116, and #121's variance input), the decisive gate campaign (#29's first metric, #30, #122,
#123), and the dependency boxes that wait on a sibling story rather than on code.

## What this evidence is not

It does not tick a box - the script reports and never edits an issue, and ticking is a judgement about
evidence that stays with the person doing the audit. It is also a **dated reading**: it describes the
issues on the day it ran, not the commit it is keyed by. The directory is keyed by the `main` SHA because
the claims are checked against that tree, and a previous cycle demonstrated the difference - two runs two
hours apart against the same commit read 75 and then 72, because three boxes were ticked in between.
