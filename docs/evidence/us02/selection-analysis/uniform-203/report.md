# Real program pilot analysis

Claim: none. One independent search run per task/method; confidence intervals and sample-size planning withheld.

| Task | Mode / method | Status | Before ms | Deployed ms | Tokens |
| --- | --- | --- | ---: | ---: | ---: |
| sha256_hashing | controlled / aidotnet | validated | 213.967 | 198.461 | 37903 |
| sha256_hashing | controlled / openevolve | validated | 224.487 | 208.713 | 39873 |
| sha256_hashing | controlled / one-shot | validated | 221.351 | 195.958 | 9947 |
| sha256_hashing | controlled / single-parent | validated | 224.189 | 214.465 | 36986 |
| sha256_hashing | native-bounded / aidotnet | validated | 215.280 | 205.254 | 40102 |
| sha256_hashing | native-bounded / openevolve | validated | 226.405 | 204.199 | 44830 |
| count_connected_components | controlled / aidotnet | validated | 421.889 | 218.988 | 40081 |
| count_connected_components | controlled / openevolve | validated | 355.072 | 204.997 | 41393 |
| count_connected_components | controlled / one-shot | validated | 373.149 | 202.811 | 10291 |
| count_connected_components | controlled / single-parent | validated | 435.029 | 226.742 | 39862 |
| count_connected_components | native-bounded / aidotnet | validated | 381.318 | 201.760 | 42028 |
| count_connected_components | native-bounded / openevolve | validated | 362.123 | 200.618 | 44379 |
| base64_encoding | controlled / aidotnet | validated | 320.383 | 223.145 | 37876 |
| base64_encoding | controlled / openevolve | validated | 299.531 | 217.154 | 39920 |
| base64_encoding | controlled / one-shot | validated | 319.734 | 210.928 | 9977 |
| base64_encoding | controlled / single-parent | validated | 303.988 | 212.937 | 36960 |
| base64_encoding | native-bounded / aidotnet | validated | 320.129 | 211.693 | 40205 |
| base64_encoding | native-bounded / openevolve | validated | 321.350 | 218.267 | 45126 |

## Descriptive comparisons, not demonstrated superiority

Ratio is comparator runtime / Evolution runtime, geometrically averaged with equal task weights. Above 1 favors Evolution; actual costs differ.

| Mode | Comparator | Runtime ratio | Inference |
| --- | --- | ---: | --- |
| controlled | openevolve | 0.985812 | insufficient-independent-search-runs |
| controlled | one-shot | 0.952581 | insufficient-independent-search-runs |
| controlled | single-parent | 1.022082 | insufficient-independent-search-runs |
| native-bounded | openevolve | 1.006606 | insufficient-independent-search-runs |

Known model-token subtotal: 637739; unknown-cost rows: 0.
Known search-evaluator seconds: 280.464; unknown-work rows: 0.
Progress curves are unavailable from these summary inputs; no missing trajectory is reconstructed.

- Fresh-process batch latency includes startup/imports/serialization, not kernel latency.
- Timing repeats and different tasks cannot replace independent paired search runs.
- Ratios compare selected/deployed runtimes directly, not ratios with different noisy baseline denominators.
- Known cost subtotals are not complete totals when any cost is unknown; equal caps do not imply equal cost.
- Independent counters take precedence over retained trace summaries, even when outcomes are missing; legacy summary-only costs do not establish accounting completeness.
- No numeric-protocol conversion, final registration, power guarantee or exact provider snapshot is invented.
- Hash consistency is not authentication; source evidence and externally retained hashes require trusted custody.
