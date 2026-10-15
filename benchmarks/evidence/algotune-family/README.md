# V1-04b (#112): the AlgoTune oracle, measured against seeded wrong answers

Two cards, both produced under the `strict-upstream-v1` contract against `oripress/AlgoTune` at
`dff9914c`.

## `mutants.json` - can a wrong answer be accepted?

**0 false accepts out of 168 mutants** across **11 tasks**: `base64_encoding`, `convolve_1d`,
`correlate_1d`, `count_connected_components`, `matrix_multiplication`, `minimum_spanning_tree`,
`outer_product`, `sha256_hashing`, `shortest_path_dijkstra`, `stable_matching` and
`unit_simplex_projection`. `per_task` holds the per-task counts and the `kinds` breakdown each one
came from.

Three tasks also reject a perturbation *within* their stated tolerance - `minimum_spanning_tree`
(weights copied from the input must match exactly), `shortest_path_dijkstra` (the distance diagonal
must be exactly 0, which upstream logs) and `unit_simplex_projection` (not yet attributed). Those are
rejections of near-correct output, which is the safe direction and is recorded in
`within_tolerance_note` so it is not mistaken for a false accept.

## `self-speedup-noise.json` - what can the speedup gate resolve?

The pinned reference timed against itself in the real warm Docker sandbox, 5 interleaved samples per
task, where the true speedup is 1.0 by construction. It measures **0.956** on
`count_connected_components` and **1.061** on `matrix_multiplication` as 5-sample medians, with single
ratios spanning **0.79-1.10**.

So a `>= 1.10x` geomean gate is inside this instrument's own noise at 5 samples. The sample count has
to come from V1-07's power analysis (#116, #121) rather than being chosen; that is the card's own
reading, and it is why no speedup claim is made here.
