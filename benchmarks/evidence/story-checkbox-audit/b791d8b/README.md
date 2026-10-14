# V1-01 (#108): acceptance-checkbox state, re-measured

Measured by [`eng/Measure-StoryCheckboxAudit.ps1`](../../../../eng/Measure-StoryCheckboxAudit.ps1) at
main `b791d8b`. `audit.json` beside this file is the script output, unedited. The earlier run at
`ca96524` is kept beside it, so the delta is a comparison of two instrument readings rather than a
recollection.

## What changed since `ca96524`

| issue | story | state then | state now | unchecked then | unchecked now |
|---|---|---|---|---:|---:|
| #21 | [US-03] Prevent invalid improvemen | OPEN | OPEN | 6 | 4 |
| #22 | [US-04] Turn traces into trustwort | OPEN | OPEN | 5 | 5 |
| #24 | [US-06] Handle noisy measurements  | OPEN | OPEN | 4 | 4 |
| #29 | [US-11] Demonstrate value in consu | OPEN | OPEN | 12 | 12 |
| #30 | [US-12] Gate releases on demonstra | OPEN | OPEN | 12 | 12 |
| #35 | [US-17] Build a compiler-guided pr | **CLOSED** | **OPEN** | 6 | 6 |
| #36 | [US-18] Reuse experience and detec | **CLOSED** | **OPEN** | 10 | 6 |
| #37 | [US-19] Route models and prompts b | **CLOSED** | **OPEN** | 10 | 3 |
| #38 | [US-20] Overlap proposal generatio | **CLOSED** | **OPEN** | 3 | 2 |
| #41 | [US-23] Warm-start compatible sear | OPEN | OPEN | 8 | 8 |
| #42 | [US-24] Provide a practical run an | OPEN | OPEN | 6 | 6 |
| #43 | [US-25] Promote validated results  | OPEN | OPEN | 8 | 8 |
| **total** | | | | **90.0** | **76.0** |

## The one state the script says cannot be left alone is now clear

The baseline flagged **four issues closed while 29 of their acceptance boxes were unticked** (#35, #36,
#37, #38) - the condition that makes the epic's progress untruthful, because a closed issue reads as
finished work. All four are **open again**, and `ClosedWithUntickedCount` is **0**.

## What the number is not

The metric's target is zero unchecked boxes, and this is **76**, so #108 is not met. Nor is the fall from
90 to 76 all audit: some boxes were ticked by work merging in between, and four issues reopening moved
29 boxes out of the "closed with unticked" bucket without ticking any of them. The script reports state;
attributing a change to a cause needs the issue timeline, which it deliberately does not read.

## Reproducing

```powershell
./eng/Measure-StoryCheckboxAudit.ps1 -Repository ooples/AiDotNet.Evolution -OutputFile audit.json
```

It reports and never edits an issue: ticking a box is a judgement about evidence and stays with the
person doing the audit.
