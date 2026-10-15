# V1-02 (#109): what the Claude Code transport costs, and whether every call is receipted

`bbbaf5e/summary.json` with `raw.json.gz` beside it. **21 calls** through Claude Code CLI 2.1.280,
model `haiku` resolving to `claude-haiku-4-5-20251001`.

Rendered from the card's own `targets` field by
`./eng/Render-CardTargets.ps1 -CardPath bbbaf5e/summary.json`, not typed:

<!-- BEGIN GENERATED TABLES -->
| field | target | measured | met |
| --- | --- | ---: | --- |
| `canary_delta_max` | 0 | 0 | yes |
| `receipt_coverage` | 1 | 1 | yes |
| `overhead_fraction_p95` | < 0.01 | 0.008273710378452355 | yes |
<!-- END GENERATED TABLES -->

`passed: true`. Beside the three declared targets: `missing_receipts` is empty, the p95 overhead is
**20.9 ms** against a p50 call latency of **2.495 s**, and `api_key_calls` is **0** - every call went
through the CLI, none to the API directly.

The canary is what makes the overhead number trustworthy: a fixed prompt read **448** on the warmup
and **448** on all twenty subsequent calls, so `canary_delta_max` is 0. A transport that silently
rewrote, truncated or re-routed the prompt would move that reading, and the overhead figure would be
measuring something other than the same request.
