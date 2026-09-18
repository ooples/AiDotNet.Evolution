# Real program pilot analysis

Claim: none. One independent search run per task/method; confidence intervals and sample-size planning withheld.

| Task | Mode / method | Status | Before ms | Deployed ms | Tokens |
| --- | --- | --- | ---: | ---: | ---: |
| sha256_hashing | controlled / aidotnet | validated | 217.167 | 207.351 | 36905 |
| sha256_hashing | controlled / openevolve | validated | 211.364 | 224.632 | 39535 |
| sha256_hashing | controlled / one-shot | validated | 207.416 | 194.542 | 9919 |
| sha256_hashing | controlled / single-parent | validated | 215.406 | 211.816 | 36767 |
| sha256_hashing | native-bounded / aidotnet | validated | 216.183 | 203.999 | 39780 |
| sha256_hashing | native-bounded / openevolve | validated | 230.312 | 200.900 | 44774 |
| count_connected_components | controlled / aidotnet | validated | 322.460 | 189.965 | 40367 |
| count_connected_components | controlled / openevolve | validated | 363.548 | 192.973 | 41121 |
| count_connected_components | controlled / one-shot | validated | 349.453 | 175.467 | 10263 |
| count_connected_components | controlled / single-parent | validated | 363.919 | 176.903 | 39971 |
| count_connected_components | native-bounded / aidotnet | validated | 340.767 | 184.264 | 42022 |
| count_connected_components | native-bounded / openevolve | validated | 394.337 | 188.487 | 44285 |
| base64_encoding | controlled / aidotnet | validated | 286.138 | 206.143 | 37029 |
| base64_encoding | controlled / openevolve | validated | 319.380 | 208.768 | 40058 |
| base64_encoding | controlled / one-shot | validated | 312.799 | 199.473 | 10033 |
| base64_encoding | controlled / single-parent | validated | 321.037 | 220.877 | 37167 |
| base64_encoding | native-bounded / aidotnet | validated | 319.664 | 203.888 | 40317 |
| base64_encoding | native-bounded / openevolve | validated | 297.223 | 211.596 | 45280 |

## Descriptive comparisons, not demonstrated superiority

Ratio is comparator runtime / Evolution runtime, geometrically averaged with equal task weights. Above 1 favors Evolution; actual costs differ.

| Mode | Comparator | Runtime ratio | Inference |
| --- | --- | ---: | --- |
| controlled | openevolve | 1.036799 | insufficient-independent-search-runs |
| controlled | one-shot | 0.943007 | insufficient-independent-search-runs |
| controlled | single-parent | 1.006388 | insufficient-independent-search-runs |
| native-bounded | openevolve | 1.014930 | insufficient-independent-search-runs |

Known model-token subtotal: 635593; unknown-cost rows: 0.
Known search-evaluator seconds: 256.987; unknown-work rows: 0.
Progress curves are unavailable from these summary inputs; no missing trajectory is reconstructed.

- Fresh-process batch latency includes startup/imports/serialization, not kernel latency.
- Timing repeats and different tasks cannot replace independent paired search runs.
- Ratios compare selected/deployed runtimes directly, not ratios with different noisy baseline denominators.
- Known cost subtotals are not complete totals when any cost is unknown; equal caps do not imply equal cost.
- Independent counters take precedence over retained trace summaries, even when outcomes are missing; legacy summary-only costs do not establish accounting completeness.
- No numeric-protocol conversion, final registration, power guarantee or exact provider snapshot is invented.
- Hash consistency is not authentication; source evidence and externally retained hashes require trusted custody.
