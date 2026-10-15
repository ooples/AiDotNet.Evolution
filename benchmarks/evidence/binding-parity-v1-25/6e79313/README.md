# V1-25 (#119): the Python worker and the TypeScript binding, measured against one another

Three things this story asks for, and what was run for each. Raw output beside this file:
`summary.json`, `python-conformance.txt`, `python-reference-worker.txt`, `typescript-suites.txt`,
`upstream-evaluator-worker.json`, and the two ratchet probes.

## The protocol: one file, three implementations

`conformance/durable-v1/scenarios.json` holds **six** scenarios as declarative step lists -
`lifecycle`, `conflicting-work-is-rejected`, `canceled-work-is-not-claimable`,
`a-foreign-worker-cannot-commit`, `unavailable-is-never-completion`,
`reopen-recovers-and-a-mismatch-is-refused`. The C# endpoint (`DurableConformanceTests`), the Python
client (`bindings/python/tests/test_conformance.py`) and the TypeScript client
(`bindings/typescript/test/conformance.test.mjs`) all run it, so a scenario added there is
immediately a requirement for all three.

Measured here: Python **OK**, driving the real host rather than a fake; TypeScript **17/17** across
its conformance and reference-worker suites.

## The reference worker: a ratchet, not a count

The behaviours above the protocol cannot share a step list - Python normalises numpy scalars,
TypeScript normalises BigInt and `undefined` - so `conformance/reference-worker-v1/behaviours.json`
declares the **nine** behaviours and why each matters, every test claims the id it covers, and each
suite fails when a declared behaviour has no test of its own.

Python **13/13** with the host built.

**The ratchet was checked by breaking it**, which is the only way to know it is load-bearing: a tenth
behaviour added to the manifest and covered by nobody fails Python
(`declared reference-worker behaviours with no Python test: ['probe-uncovered-behaviour']`) **and**
TypeScript (`not ok 11`), 10 of 11 passing. Removing it returns both to green. Before this, parity
was whatever someone last compared by hand; #283 closed a gap found that way (TypeScript had nine
reference-worker cases, Python six) and nothing stopped the next one opening.

## A competitor's evaluator, unmodified

`benchmarks/external/run_upstream_evaluator_through_worker.py` runs OpenEvolve's own
`function_minimization` evaluator through the Python reference worker against the real durable host.
Both files are checked **blob-for-blob** against the pinned OpenEvolve commit
`411fb59c886c18704caaffb611e17cf9e7d824d2` before anything runs -
`evaluator.py` `f16318125b56073905d10e24143c17286c0963b9`, `initial_program.py`
`670c02cc459be42f66a2a92fd0e6a33037220671` - so "unmodified" is a measurement, not a claim.

Two leases, both `accepted`, both receipts `completed`; the host reports 2 admitted, 2 settled, 0
denied. The second program is deliberately broken (`def search_algorithm(:`): upstream's evaluator
scores it 0 with an error artifact rather than raising, so that receipt is `completed` too - which is
upstream's behaviour, not ours, and is why the worker must commit it rather than treat it as a crash.

## What this run is not

- **Not CI.** Windows 11, Python 3.13.14, Node v25.6.0, measured locally. The same suites and the
  same upstream-evaluator run execute in CI (`typescript-binding.yml` for both bindings,
  `build.yml` for the upstream evaluator and the C# conformance cases); this bundle is the local
  record, and the merged head's CI is the hosted one.
- **Not the whole TypeScript suite.** `bindings/typescript/test/durable.test.mjs` needs a published
  per-RID host binary (`bindings/typescript/bin/win-x64/...`) that CI builds with `npm run host` and
  this worktree does not have, so three of its cases fail here for want of that binary. They are
  untouched by this story's change.
- **Home directory redacted.** The two probe logs had `C:\Users\<user>` replaced with `~`, the same
  redaction `run_upstream_evaluator_through_worker.py` already applies to its own tracebacks.
  Nothing else in any file was edited.
- **Not a performance claim.** Nothing here measures throughput or overhead; `benchmarks/evidence/engine-overhead`
  and `benchmarks/evidence/distributed-v1-61` are where those live.

## Reproducing it

```text
dotnet build src/AiDotNet.Evolution.Host/AiDotNet.Evolution.Host.csproj -c Release -f net10.0
cd bindings/python     && python -m unittest discover -s tests -v
cd bindings/typescript && npm install && npm run build && node --test --test-reporter=tap test/*.test.mjs
python benchmarks/external/run_upstream_evaluator_through_worker.py <openevolve-checkout> \
    function_minimization src/AiDotNet.Evolution.Host/bin/Release/net10.0/aidotnet-evolution-host.dll out.json
```

The upstream run needs `openevolve` importable (the evaluators import `openevolve.evaluation_result`,
which needs `dacite`), and the checkout at exactly the pinned commit - the script refuses anything else.
