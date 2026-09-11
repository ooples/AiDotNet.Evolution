# User-story delivery index

The [research roadmap](COMPETITIVE_ANALYSIS_AND_ROADMAP.md) has **26 open issues and 26 separate dependent PRs**:
US-08 / #51 and US-09 / #52 are ready for review; US-10 / #53 now has completed local performance acceptance evidence.
The remaining stories still require their own implementation/acceptance verification.
Each issue includes the original Given/When/Then criteria, dependency checklist, partial evidence and remaining work.
All initial story PR commits were **tracking-only checklists**. US-08, US-09 and US-10 subsequently added tested implementation;
US-13 is actively completing its preset/domain audit. See each branch's evidence instead of treating creation as completion.
Creating a PR or inheriting a foundation does not complete a story.

## Agreed delivery structure

Per the user's September 11 decision, retain [core #15](https://github.com/ooples/AiDotNet.Evolution/pull/15),
[AiDotNet #2148](https://github.com/ooples/AiDotNet/pull/2148) and
[Tensors #1024](https://github.com/ooples/AiDotNet.Tensors/pull/1024) as shared foundations.
Story PRs target the corresponding foundation branch. All issues are centralized in Evolution;
primary story PR ownership is 19 Evolution, six AiDotNet and one Tensors. Add companion PRs where needed.

Mark a completed, verified PR scope ready for review; keep unfinished implementation and tracking-only PRs in draft.
Review readiness is not merge approval, dependency completion or completion of the containing user story.
Do not merge tracking-only PRs into the shared feature branches just to clear the queue.
After a foundation merges, retarget dependents to the repository's default branch and verify ancestry,
the resulting diff, package/source compatibility, tests, coverage and current-head reviews.
Cross-repository prerequisites are explicit links, not dependencies enforced by GitHub's base branch.
Retargeting after squash/rebase merging needs special care to avoid reintroducing foundation changes.

| Story | Issue | Implementation status | Dependent PR |
| --- | --- | --- | --- |
| US-01: Establish a representative benchmark suite | [#19](https://github.com/ooples/AiDotNet.Evolution/issues/19) | Partial | [AiDotNet.Evolution#45](https://github.com/ooples/AiDotNet.Evolution/pull/45) |
| US-02: Make fair comparisons against competitors | [#20](https://github.com/ooples/AiDotNet.Evolution/issues/20) | Partial | [AiDotNet.Evolution#46](https://github.com/ooples/AiDotNet.Evolution/pull/46) |
| US-03: Prevent invalid improvements from winning | [#21](https://github.com/ooples/AiDotNet.Evolution/issues/21) | Partial, companion | [AiDotNet#2163](https://github.com/ooples/AiDotNet/pull/2163) |
| US-04: Turn traces into trustworthy experiment reports | [#22](https://github.com/ooples/AiDotNet.Evolution/issues/22) | Partial | [AiDotNet.Evolution#47](https://github.com/ooples/AiDotNet.Evolution/pull/47) |
| US-05: Account for and enforce the real search budget | [#23](https://github.com/ooples/AiDotNet.Evolution/issues/23) | Partial | [AiDotNet.Evolution#48](https://github.com/ooples/AiDotNet.Evolution/pull/48) |
| US-06: Handle noisy measurements and expensive evaluation | [#24](https://github.com/ooples/AiDotNet.Evolution/issues/24) | Partial | [AiDotNet.Evolution#49](https://github.com/ooples/AiDotNet.Evolution/pull/49) |
| US-07: Identify which existing features improve search | [#25](https://github.com/ooples/AiDotNet.Evolution/issues/25) | Partial | [AiDotNet.Evolution#50](https://github.com/ooples/AiDotNet.Evolution/pull/50) |
| US-08: Adapt proposal strategies using measured outcomes | [#26](https://github.com/ooples/AiDotNet.Evolution/issues/26) | Partial story; completed fix ready for review | [AiDotNet.Evolution#51](https://github.com/ooples/AiDotNet.Evolution/pull/51) |
| US-09: Preserve useful tradeoffs between objectives | [#27](https://github.com/ooples/AiDotNet.Evolution/issues/27) | Implemented; review/dependencies open | [AiDotNet.Evolution#52](https://github.com/ooples/AiDotNet.Evolution/pull/52) |
| US-10: Measure engine overhead and scaling | [#28](https://github.com/ooples/AiDotNet.Evolution/issues/28) | Local acceptance verified; review/dependencies open | [AiDotNet.Evolution#53](https://github.com/ooples/AiDotNet.Evolution/pull/53) |
| US-11: Demonstrate value in consumer workloads | [#29](https://github.com/ooples/AiDotNet.Evolution/issues/29) | Partial, companion | [AiDotNet#2164](https://github.com/ooples/AiDotNet/pull/2164) |
| US-12: Gate releases on demonstrated improvement | [#30](https://github.com/ooples/AiDotNet.Evolution/issues/30) | Partial | [AiDotNet.Evolution#54](https://github.com/ooples/AiDotNet.Evolution/pull/54) |
| US-13: Supply typed search spaces and useful operators | [#31](https://github.com/ooples/AiDotNet.Evolution/issues/31) | Partial; reused-learning regression open | [AiDotNet.Evolution#55](https://github.com/ooples/AiDotNet.Evolution/pull/55) |
| US-14: Rank proposals using a surrogate model | [#32](https://github.com/ooples/AiDotNet.Evolution/issues/32) | Partial | [AiDotNet.Evolution#56](https://github.com/ooples/AiDotNet.Evolution/pull/56) |
| US-15: Allocate evaluation resources dynamically | [#33](https://github.com/ooples/AiDotNet.Evolution/issues/33) | Partial | [AiDotNet.Evolution#57](https://github.com/ooples/AiDotNet.Evolution/pull/57) |
| US-16: Adapt islands and restart stalled searches | [#34](https://github.com/ooples/AiDotNet.Evolution/issues/34) | Not implemented | [AiDotNet.Evolution#58](https://github.com/ooples/AiDotNet.Evolution/pull/58) |
| US-17: Build a compiler-guided program improvement loop | [#35](https://github.com/ooples/AiDotNet.Evolution/issues/35) | Partial, companion | [AiDotNet#2165](https://github.com/ooples/AiDotNet/pull/2165) |
| US-18: Reuse experience and detect meaningful novelty | [#36](https://github.com/ooples/AiDotNet.Evolution/issues/36) | Not implemented | [AiDotNet#2166](https://github.com/ooples/AiDotNet/pull/2166) |
| US-19: Route models and prompts by measured return | [#37](https://github.com/ooples/AiDotNet.Evolution/issues/37) | Not implemented | [AiDotNet#2167](https://github.com/ooples/AiDotNet/pull/2167) |
| US-20: Overlap proposal generation and evaluation | [#38](https://github.com/ooples/AiDotNet.Evolution/issues/38) | Not implemented | [AiDotNet.Evolution#59](https://github.com/ooples/AiDotNet.Evolution/pull/59) |
| US-21: Make external and distributed evaluation durable | [#39](https://github.com/ooples/AiDotNet.Evolution/issues/39) | Pending API integration | [AiDotNet.Evolution#60](https://github.com/ooples/AiDotNet.Evolution/pull/60) |
| US-22: Bound diversity search with many descriptors | [#40](https://github.com/ooples/AiDotNet.Evolution/issues/40) | Implemented and locally verified; CI/review/dependency gates remain; no default promotion | [AiDotNet.Evolution#61](https://github.com/ooples/AiDotNet.Evolution/pull/61) |
| US-23: Warm-start compatible searches and reuse evaluations | [#41](https://github.com/ooples/AiDotNet.Evolution/issues/41) | Partial | [AiDotNet.Evolution#62](https://github.com/ooples/AiDotNet.Evolution/pull/62) |
| US-24: Provide a practical run and inspection experience | [#42](https://github.com/ooples/AiDotNet.Evolution/issues/42) | Partial | [AiDotNet#2168](https://github.com/ooples/AiDotNet/pull/2168) |
| US-25: Promote validated results and retune when conditions change | [#43](https://github.com/ooples/AiDotNet.Evolution/issues/43) | Partial, companion | [AiDotNet.Tensors#1030](https://github.com/ooples/AiDotNet.Tensors/pull/1030) |
| US-26: Experiment with evolution of search policies themselves | [#44](https://github.com/ooples/AiDotNet.Evolution/issues/44) | Not implemented | [AiDotNet.Evolution#63](https://github.com/ooples/AiDotNet.Evolution/pull/63) |

## Review-readiness audit

The user requested that completed work leave draft status while continuing to build on the combined foundations.

- **Evolution #51: ready for review.** The bounded reuse-learning fix is implemented; at code/integration head
  `c412cc1`, all 651 tests passed on each target framework and all hosted gates passed. Full US-08 remains open.
- **Tensors #1024: ready for review.** The bounded quarantine/validated-rollback slice at `665cb3c8` has passing
  build, GPU-parity and coverage checks. Independent approval and the broader US-25 work remain outstanding.
- **Evolution #15: draft.** Retained as the shared foundation; the known reuse-learning correction is in #51,
  not this branch. Passing CI does not remove that integration requirement.
- **AiDotNet #2148: draft.** At `367fce237`, focused compiler checks pass but normal build/validation,
  documentation/sample and model-fixture workflows report failures. Compatible published-package integration
  also remains unresolved. An older approval is not approval of this head.

No issues were closed and no PRs merged. The full roadmap is unfinished.

## Current adversarial finding

A new 25-case local regression suite reproduced 15 failures where producer-declared reused measurements
are treated as fresh by operator credit, surrogate observation intake and diagonal-CMA distribution learning.
No-origin and fresh-measurement controls pass. The fix is implemented and locally verified in
[US-08 / #51](https://github.com/ooples/AiDotNet.Evolution/pull/51), with relevant acceptance checks in
[US-13 / #55](https://github.com/ooples/AiDotNet.Evolution/pull/55),
[US-14 / #56](https://github.com/ooples/AiDotNet.Evolution/pull/56) and
[US-23 / #62](https://github.com/ooples/AiDotNet.Evolution/pull/62).
The expanded 53-case regression suite passes. With four shared workflow-trigger tests integrated, all 651
tests pass on net10.0, net8.0 and net471 at `c412cc1`; hosted checks also passed on that revision.
The fix remains on #51, not the shared foundation; US-13, US-14 and US-23 require dependency integration.
All affected stories remain partial: this does not establish their full acceptance criteria.

## Tracking verification

At creation, GitHub returned 26 distinct open draft PRs with the expected issue URL, foundation base,
story branch, original Given/When/Then criteria and exactly one story checklist file per initial diff.
The issue bodies link back to their respective PRs. This verifies tracking structure only:
the initial documentation commits claimed no new production tests, current-head review approval or competitive superiority.
US-08's subsequent code evidence is recorded separately; neither review approval nor competitive superiority is claimed.

See [implementation evidence](IMPLEMENTATION_STATUS.md) for verified work already in the shared foundations.
