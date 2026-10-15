# V1-25/V1-56: an unmodified OpenEvolve evaluator, served through the durable worker

Two runs of the same check on different platforms, schema
`aidotnet-upstream-evaluator-through-worker-v1`, against OpenEvolve `411fb59c`:

| directory | platform | python |
| --- | --- | --- |
| `3f73e03-ci/` | `Linux-6.17.0-1022-azure-x86_64-with-glibc2.39` | 3.12.3 |
| `8e2135c/` | `Windows-11-10.0.26200-SP0` | 3.13.3 |

Both feed `examples/function_minimization`'s **unmodified** evaluator and initial program - pinned by
blob SHA in `unmodified_blobs`, so "unmodified" is checkable and not a claim - two programs each: the
upstream seed and a deliberately broken one. **Both dispositions `accepted`** in both runs, with
`host_status` showing `admitted 2`, `denied 0`, `settled 2`, `maximumViolated false`.

The `receipts` carry what the upstream evaluator actually returned, including its artifacts, which is
how the Linux and Windows runs can be compared: `average_distance_to_global` **0.1656** against
**0.1691**, different because the example is stochastic, not because the platforms disagree.

## What the card says it does not cover

Quoted from its own `limitations`, because each one bounds the claim:

- the example's **stochastic metrics vary run to run**, so the receipt values are not a baseline;
- the upstream evaluator **scores a broken program 0 with an error artifact instead of raising**, so
  that receipt is `completed` - matching OpenEvolve, and the reason a completed receipt here is not
  evidence the program worked;
- **only `evaluate(program_path)` is served**; cascade stages are not invoked;
- the home directory is redacted to `~` in tracebacks.
