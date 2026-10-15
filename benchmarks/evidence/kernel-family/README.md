# V1-04c (#29): is there kernel headroom on the dev partition?

`dev-headroom.json` is one card, on one task, on one machine.

**Yes: 6.461x median** on `softmax` at shape `[4096, 1024]`, comparing a hand-written fused CUDA
`RawKernel` (one block per row) against the initial multi-pass CuPy program. 7 samples, ratios
**5.481-6.695**, seed 3000, 40 repeats per request, on an NVIDIA GeForce GTX 1660 Ti (driver 616.56)
inside image `sha256:7f096bcd...`, harness commit `509ee0b`.

Timed kernel-only, with CUDA events around the kernel call, each reading checked against the host's
independently measured wall time. That matters because of what the card records as **superseded**: an
earlier end-to-end figure of **1.219x** mostly timed input generation and digests rather than the
kernel, and is replaced rather than quietly dropped.

Both programs are pinned by content (`candidate_sha256`, `initial_sha256`), so the pair that produced
the number can be identified.

The card's own reading is the limit of the claim: headroom exists **on dev data**, and there is no
claim beyond this card. The gate campaign (#122) is what would turn it into one.
