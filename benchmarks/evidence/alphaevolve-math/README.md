# V1-04a (#111): AlphaEvolve's published mathematical results, transcribed and self-checked

`manifest.json` transcribes **36 problems** from DeepMind's `alphaevolve_results`
`mathematical_results.ipynb`, pinned by content: the notebook's SHA-256 is recorded as
`2cce2543e48c89aa3e91614272a698a0147dd2548ea11cf92f1292b7435d38ff`, so the transcription can be
re-checked against the exact source it was taken from.

**36 of 36 verified, 0 pending.** Each row carries its `section`, `kind` (`analytic` or `tensor`),
optimisation `direction`, `published_value` with the `published_decimals` it was published to, whether
it is `proven_optimal`, its `citation`, the `verifier` that checks it, what it is `derived_from`, and a
`self_test_value` with `self_test_matches` - the verifier run against the published value itself, so a
verifier that cannot reproduce a known answer is visible before it is ever pointed at a candidate.

This is a target list, not a result. Nothing here claims AiDotNet.Evolution reaches any of these
values; the manifest exists so that a future attempt is scored against the published figure rather
than against a number chosen afterwards.
