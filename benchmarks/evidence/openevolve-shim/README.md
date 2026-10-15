# V1-03 (#110): the OpenEvolve shim, run live and timed

`b8d5f9a/`, three cards against one **contract fixture - explicitly not a family oracle**.

## It runs

`live-smoke.json`: both broker configurations exit 0 under real OpenEvolve, `passed: true`, with the
stderr tail retained so the island log can be read rather than taken on trust.
`live-matched-broker-rows.json` and `live-recommended-broker-rows.json` hold the 5 brokered rows each
config produced - the `evaluate` request, its `status`, and the result with its `candidate_hash` - so
the two configurations can be compared row by row.

## It is not slow, and nothing goes unrecorded

`shim-latency.json`, **600 samples** at a 16,140-byte request over a persistent HTTP/1.1 keep-alive
connection (connect counted once per broker):

Rendered from the card's own `targets` field by
`./eng/Render-CardTargets.ps1 -CardPath b8d5f9a/shim-latency.json`, not typed:

<!-- BEGIN GENERATED TABLES -->
| field | target | measured | met |
| --- | --- | ---: | --- |
| `shim_ms_p95` | < 5 | 0.818000000435859 | yes |
| `unrecorded_prompts` | 0 | 0 | yes |
<!-- END GENERATED TABLES -->

`passed: true`, with p50 **0.484 ms**, max **21.81 ms**, and 600 chats received against 600 recorded.
The raw samples are in `shim-latency-raw.json.gz`, so the percentiles can be recomputed rather than
trusted; the max being 27x the p95 is why the gate is stated on p95 and the raw file is kept.

The `unrecorded_prompts` count is the load-bearing one. A shim fast because it dropped work would
report a good p95; 600 received against 600 recorded is what rules that out.
