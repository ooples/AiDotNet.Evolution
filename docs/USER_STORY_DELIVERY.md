# User-story delivery index

The [research roadmap](COMPETITIVE_ANALYSIS_AND_ROADMAP.md) has one GitHub issue per user story.
All issues remain open. Partial implementation and a tracking PR do not satisfy the story's acceptance criteria.
Each issue includes the original Given/When/Then criteria, dependency checklist, current evidence and remaining work.

The existing shared drafts are [core #15](https://github.com/ooples/AiDotNet.Evolution/pull/15),
[AiDotNet #2148](https://github.com/ooples/AiDotNet/pull/2148) and
[Tensors #1024](https://github.com/ooples/AiDotNet.Tensors/pull/1024). The user requested separate story PRs;
choosing whether to retain those shared foundations or replace them with a fully split series is still pending.
No separate story PR is claimed in the table until an actual PR exists.

| Story | Issue | Implementation status | Separate story PR |
| --- | --- | --- | --- |
| US-01: Establish a representative benchmark suite | [#19](https://github.com/ooples/AiDotNet.Evolution/issues/19) | Partial | Pending split layout |
| US-02: Make fair comparisons against competitors | [#20](https://github.com/ooples/AiDotNet.Evolution/issues/20) | Partial | Pending split layout |
| US-03: Prevent invalid improvements from winning | [#21](https://github.com/ooples/AiDotNet.Evolution/issues/21) | Partial, companion | Pending split layout |
| US-04: Turn traces into trustworthy experiment reports | [#22](https://github.com/ooples/AiDotNet.Evolution/issues/22) | Partial | Pending split layout |
| US-05: Account for and enforce the real search budget | [#23](https://github.com/ooples/AiDotNet.Evolution/issues/23) | Partial | Pending split layout |
| US-06: Handle noisy measurements and expensive evaluation | [#24](https://github.com/ooples/AiDotNet.Evolution/issues/24) | Partial | Pending split layout |
| US-07: Identify which existing features improve search | [#25](https://github.com/ooples/AiDotNet.Evolution/issues/25) | Partial | Pending split layout |
| US-08: Adapt proposal strategies using measured outcomes | [#26](https://github.com/ooples/AiDotNet.Evolution/issues/26) | Partial | Pending split layout |
| US-09: Preserve useful tradeoffs between objectives | [#27](https://github.com/ooples/AiDotNet.Evolution/issues/27) | Not implemented | Pending split layout |
| US-10: Measure engine overhead and scaling | [#28](https://github.com/ooples/AiDotNet.Evolution/issues/28) | Partial | Pending split layout |
| US-11: Demonstrate value in consumer workloads | [#29](https://github.com/ooples/AiDotNet.Evolution/issues/29) | Partial, companion | Pending split layout |
| US-12: Gate releases on demonstrated improvement | [#30](https://github.com/ooples/AiDotNet.Evolution/issues/30) | Partial | Pending split layout |
| US-13: Supply typed search spaces and useful operators | [#31](https://github.com/ooples/AiDotNet.Evolution/issues/31) | Implemented | Pending split layout |
| US-14: Rank proposals using a surrogate model | [#32](https://github.com/ooples/AiDotNet.Evolution/issues/32) | Partial | Pending split layout |
| US-15: Allocate evaluation resources dynamically | [#33](https://github.com/ooples/AiDotNet.Evolution/issues/33) | Partial | Pending split layout |
| US-16: Adapt islands and restart stalled searches | [#34](https://github.com/ooples/AiDotNet.Evolution/issues/34) | Not implemented | Pending split layout |
| US-17: Build a compiler-guided program improvement loop | [#35](https://github.com/ooples/AiDotNet.Evolution/issues/35) | Partial, companion | Pending split layout |
| US-18: Reuse experience and detect meaningful novelty | [#36](https://github.com/ooples/AiDotNet.Evolution/issues/36) | Not implemented | Pending split layout |
| US-19: Route models and prompts by measured return | [#37](https://github.com/ooples/AiDotNet.Evolution/issues/37) | Not implemented | Pending split layout |
| US-20: Overlap proposal generation and evaluation | [#38](https://github.com/ooples/AiDotNet.Evolution/issues/38) | Not implemented | Pending split layout |
| US-21: Make external and distributed evaluation durable | [#39](https://github.com/ooples/AiDotNet.Evolution/issues/39) | Pending API integration | Pending split layout |
| US-22: Bound diversity search with many descriptors | [#40](https://github.com/ooples/AiDotNet.Evolution/issues/40) | Partial | Pending split layout |
| US-23: Warm-start compatible searches and reuse evaluations | [#41](https://github.com/ooples/AiDotNet.Evolution/issues/41) | Partial | Pending split layout |
| US-24: Provide a practical run and inspection experience | [#42](https://github.com/ooples/AiDotNet.Evolution/issues/42) | Partial | Pending split layout |
| US-25: Promote validated results and retune when conditions change | [#43](https://github.com/ooples/AiDotNet.Evolution/issues/43) | Partial, companion | Pending split layout |
| US-26: Experiment with evolution of search policies themselves | [#44](https://github.com/ooples/AiDotNet.Evolution/issues/44) | Not implemented | Pending split layout |

## Current adversarial finding

A new 25-case local regression suite reproduced 15 failures where producer-declared reused measurements
are treated as fresh by operator credit, surrogate observation intake and diagonal-CMA distribution learning.
No-origin and fresh-measurement controls pass. The production fix is pending and is recorded in
[US-08](https://github.com/ooples/AiDotNet.Evolution/issues/26),
[US-13](https://github.com/ooples/AiDotNet.Evolution/issues/31),
[US-14](https://github.com/ooples/AiDotNet.Evolution/issues/32) and
[US-23](https://github.com/ooples/AiDotNet.Evolution/issues/41).

See [implementation evidence](IMPLEMENTATION_STATUS.md) for the verified work already in the shared drafts.

