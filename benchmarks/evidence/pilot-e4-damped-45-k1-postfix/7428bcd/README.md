# E-4 (#347): the post-fix pilot, on main after #359

The third pilot ([`../../pilot-e4-damped-45-k1-artifacts/b99bb35/`](../../pilot-e4-damped-45-k1-artifacts/b99bb35/)) sized the gate
before #359 merged its three engine fixes:

- descriptor binning;
- artifact escaping;
- Previous Attempts that recount evaluated children.

This pilot repeats that design on 7428bcd (main after #359 and #361), so the gate is sized from the engine it would run.

- **Design:** the same 18-cell design as the third pilot.
  - 6 paired seeds x 3 arms (ours, OpenEvolve `recommended`, OpenEvolve `matched`), Williams balanced.
  - `damped_oscillator` dev partition, 45 iterations, a 63-call broker cap per cell, held-out seed 20261017.
  - 600/200 selection rows; our champion rule at k = 1.
- **Seeds:** drawn fresh at registration (`registration.json`).
- **CLI:** run from a frozen copy built at 7428bcd (`F:\evo-pilot\cli-7428bcd`).
- **Model:** Claude Code 2.1.316, sonnet (`claude-sonnet-5-5`).

## Three launches refused before the run

`LAUNCH-NOTES.txt` has the timeline. No cell ran in any of the three.

1. Claude Code had self-updated to 2.1.316, and the transport's version pin refused the first call.
2. **The re-recorded baseline was wrong, and the pilot refused twice.**
   - The baseline was re-recorded from a shell that exported `DO_NOT_TRACK=1`. That variable removes 32 tokens from the CLI's own prompt, and the transport did not scrub it, so the recording read 626/605/601.
   - The pilot, launched without the variable, read sonnet at 637 on both its warm-up and its check, and refused both times.
   - The variable was isolated by unsetting candidates one group at a time.
3. **Fix in b65c4c2:** the transport scrubbed `DO_NOT_TRACK` and `CLAUDE_EFFORT`.
   - The canary, re-recorded under that fix, read 658/637/633, the same as 2.1.314.
   - The fourth launch passed, and this pilot ran under that transport.
4. **After the pilot, in review:** stripping `DO_NOT_TRACK` also discarded a caller's telemetry opt-out. The transport now launches every call with `DO_NOT_TRACK=1` whatever the caller set.
   - The baselines are 626/605/601 from bash and from PowerShell alike.

## Result

All 18 of 18 cells completed: 830 model calls and 2,881,802 tokens in the cells, 5,808 s wall.

Held-out NMSE per cell (`rows.json`); 0 is the exact formula, `exp(-0.3 t) sin(2 t)`:

| Seed | Ours | OpenEvolve recommended | OpenEvolve matched |
|---|---|---|---|
| 2039418671 | 0 | 8.1e-33 | 0 |
| 2677011789 | 0 | 0 | 0 |
| 1650068384 | 0 | 0 | 0 |
| 2362860140 | 0 | 8.1e-33 | 0 |
| 2770936532 | 0 | 0 | 0 |
| 4230622915 | 0 | 0 | 0 |

- **18 of 18 cells reached the exact formula** (16 at 0, plus two at 8.1e-33). In the third pilot, 14 of 18 did.
  - Held-out NMSE alone shows agreement on the held-out rows, not identity. So every returned program (`programs/`) was also evaluated on a dense grid, `t = linspace(0, 6, 100001)`, against `exp(-0.3 t) sin(2 t)` (`identity-check.json`).
  - 16 programs agree exactly. The two `recommended` cells at 8.1e-33 differ by at most 2.22e-16, one floating-point ulp.
  - 16 programs return `exp(-0.3 t) sin(2 t)` written out, or with named constants (`decay`, `omega`, `envelope`).
  - The two `recommended` cells at 8.1e-33 return `A exp(-k t) cos(w t + phi)` with A = 1, k = 0.3, w = 2 and phi = -pi/2. That is the same formula, since cos(2 t - pi/2) = sin(2 t), and the 2.22e-16 is the rounding of `-math.pi / 2`.
- **Our champion beat the seed in 6 of 6 cells.**
- **Model calls per arm:** ours 290, `recommended` 270, `matched` 270. Tokens: ours 1,073,323, `recommended` 1,120,663, `matched` 687,816.

Endpoints (`MeanDifference` is theirs minus ours in held-out NMSE, so positive favours ours; paired bootstrap;
`summary.json`):

| Comparison | Mean difference | 95% interval | One-sided p | Holm p | Superior |
|---|---|---|---|---|---|
| vs recommended | +2.7e-33 | [0, 5.4e-33] | 0.095 | 0.190 | No |
| vs matched | 0 | [0, 0] | 1 | 1 | No |

- **Cells lost:** ours lost no cell on either endpoint (`ranked-losses.json`).
- **The vs-recommended difference is floating-point residue.** It comes entirely from `recommended`'s two 8.1e-33 cells.
- **Power analysis:** `power-analysis.json` reports the required N as 2 for each endpoint at the frozen 0.1 half-width. The `matched` endpoint is flagged `Degenerate`, because its variance is 0.

## Spend

The account is a subscription, so the money charged is $0. The CLI's list-price estimates (`receipts-compact.json`):

| Run | Calls | Failed | Canaries | List price |
|---|---|---|---|---|
| Launch 1 (version pin) | 0 | 0 | 0 | $0.00 |
| Launch 2 (canary refused) | 2 | 0 | 2 | $0.0027 |
| Launch 3 (canary refused) | 2 | 0 | 2 | $0.0002 |
| Pilot | 832 | 0 | 2 | $13.63 |
| **Total** | 836 | 0 | 6 | **$13.63** |

**Calls with no receipt:** the canary recordings and the environment probes (about 40 one-line canary calls of about 650 input tokens each) went through `record_claude_canary.py` and ad-hoc probes, so they have no receipt here.

**Against the cap:** the maintainer approved this pilot with a $50 cap.
