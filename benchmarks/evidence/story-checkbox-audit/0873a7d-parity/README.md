# V1-01 (#108): the parity, performance and robustness stories, audited for the first time

Measured by [`eng/Measure-StoryCheckboxAudit.ps1`](../../../../eng/Measure-StoryCheckboxAudit.ps1) at main
`0873a7d`, over `-Issues 163..185` rather than the twelve US-stories #108 names. `audit.json` is the
script's output, unedited.

**176 unchecked, 46 checked, and 21 of the 23 stories are CLOSED with unticked acceptance criteria.**

That is the one state this script was written to flag: *"an issue that is CLOSED while its acceptance
criteria are still unticked, which is what makes the epic's progress untruthful."* The twelve
US-stories have been at `closed-with-unticked: 0` since #238. Nobody had pointed the same instrument
at #163-#185.

## The shape of it

| state | stories |
| --- | --- |
| closed, **zero** boxes ticked | #164, #165, #168, #171, #172, #173, #175, #176, #177, #178, #179, #180, #181, #182, #183, #184, #185 |
| closed, some ticked | #163 (1 left), #166 (1), #167 (3), #169 (2) |
| open | #170 (9), #174 (2) |

Fifteen of the seventeen in the first row are the performance and robustness stories #175-#185, each
closed by a merged PR and most with committed evidence under `benchmarks/evidence/` - the work
shipped, and the record of *which criterion each piece of it satisfied* was never made.

## Why this is not a bookkeeping complaint

Three consequences, each checkable:

1. **The epic reads as finished work it cannot show.** #125's checklist was reconciled earlier today:
   fourteen stories ticked, each with its closing PR. A closing PR is evidence the story shipped; it
   is not evidence that a named criterion was met. Both statements are now true at once - the epic
   says done, and 21 of those issues say nothing.
2. **#124 cannot tick its release precondition**, which asks that every one of these stories "is
   closed with its metrics table filled". Beyond the two still open, **17 of the 23 tables have no
   `measured` column at all** - they are `metric | baseline | target | measured by`, so there is
   nowhere for the value to live. Only #163, #166, #167, #169, #170 and #174 carry one, and all six
   are filled.
3. **A closed story with no ticked criteria is where a regression hides.** #119 closed earlier today
   with one of its two declared targets missed by 22-55x; that was visible only because the metric
   had a `measured` column to write the miss into.

## What this evidence is not

It does not tick a box, and it does not propose closing or reopening anything: the script reports and
never edits an issue, and ticking is a judgement about evidence that belongs to whoever makes it.
Nor does it claim the work is absent - for most of these stories the evidence directory exists and the
closing PR is merged. The claim is narrower and entirely mechanical: **the issues do not say which
criterion that evidence satisfies**, and 21 of them are closed anyway.
