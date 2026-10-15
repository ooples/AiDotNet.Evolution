# V1-02 (#109): what the Claude Code transport costs, and whether every call is receipted

`bbbaf5e/summary.json` with `raw.json.gz` beside it. **21 calls** through Claude Code CLI 2.1.280,
model `haiku` resolving to `claude-haiku-4-5-20251001`.

| target | measured |
| --- | --- |
| `overhead_fraction_p95 < 0.01` | **0.00827** (p50 0.00658; p95 **20.9 ms** against a p50 call latency of **2.495 s**) |
| `receipt_coverage = 1.0` | **1.0**, `missing_receipts` empty |
| `canary_delta_max = 0` | **0** |
| - | `api_key_calls` **0**: every call went through the CLI, none to the API directly |

`passed: true`, and each target is recorded in the card's own `targets` field rather than being
asserted here.

The canary is what makes the overhead number trustworthy: a fixed prompt read **448** on the warmup
and **448** on all twenty subsequent calls, so `canary_delta_max` is 0. A transport that silently
rewrote, truncated or re-routed the prompt would move that reading, and the overhead figure would be
measuring something other than the same request.
