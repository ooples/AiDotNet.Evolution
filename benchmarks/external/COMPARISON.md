# US-02 comparison adapters

## Numeric comparisons

`run_numeric_comparison.py` consumes a frozen US-01 numeric request and runs the
repository's random search, hill climbing, MAP-Elites and diagonal-CMA controls
alongside SciPy differential evolution and upstream pyribs CMA-ME. Both invoke the **same C# evaluator**:
there is no Python reimplementation of the objective. Initial genome identities,
raw quality/constraints, independent evaluator receipts and task work units are
reconciled. Partial final generations stop before an over-budget dispatch.

The `controlled` track uses the same eight starting genomes. The optional
`native-sized` track uses 120 Latin-hypercube points with its first eight replaced
by those shared starts. That initialization is explicitly **not untouched SciPy
defaults**. Tracks remain separate. No tuning trials are performed. DiagonalCma
is the repository's diagonal control; the separate pyribs 0.12.0 baseline provides
upstream CMA-ME (release commit `105cf16f27288c189dbf12c7bacdc40448ab1928`). It uses
the upstream improvement emitter, full covariance CMA-ES, and a matching 10x10
coordinate archive. Infeasible samples never enter its archive; a partial last
batch counts toward results/work but is not used for an incomplete CMA update.
This entry point refuses registered selection/final requests because it does
not own their seed custody. Contract-smoke partitions are not untouched holdouts.

```powershell
python benchmarks/external/run_numeric_comparison.py --request request.json --evaluator benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll --output new-results --include-native-sized
```

## Program comparisons

`run_program_comparison.py` runs six separate configurations: controlled AiDotNet,
controlled OpenEvolve, one-shot, single-parent, native-bounded AiDotNet and
native-bounded OpenEvolve. Each receives the same initial source, task, provider
instance, evaluator callback and independently declared model/evaluator caps.
The first controlled prompts must match exactly; later parents depend on search.
The one-shot baseline deliberately uses one call, retaining its unused allowance.

The AiDotNet companion executable uses its real program task and LLM variation
integration. The OpenEvolve adapter invokes the unmodified controller at
[`411fb59c886c18704caaffb611e17cf9e7d824d2`](https://github.com/algorithmicsuperintelligence/openevolve/tree/411fb59c886c18704caaffb611e17cf9e7d824d2)
and verifies both the checkout and installed import location. Its module-level
`init_client` callback passes through upstream's process workers to the same
loopback broker. Native settings are bounded explicitly: full rewrites, no
retries, no LLM judge/cascade, and sequential evaluation. Prompt histories and
all applied settings remain evidence; native-bounded is not untouched defaults.

Install into an isolated environment, checking out the exact revision above:

```powershell
python -m pip install -r benchmarks/external/requirements-program.txt
python -m pip install --no-deps -e C:/path/to/pinned-openevolve
python benchmarks/external/run_program_comparison.py --aidotnet C:/path/to/EvolutionComparison.dll --upstream C:/path/to/pinned-openevolve --output new-program-fixture
```

The CLI is deliberately a **nonexecuting contract fixture** with synthetic model
responses and constant scores. It proves orchestration/accounting only. Real
development experiments call `run_campaign` with a declared provider and trusted
isolated evaluator. The evaluator must own its candidate sandbox, allowed
libraries, hardware, correctness checks and bounded execution. Never execute
generated code in the broker or pass its capability/provider credentials to a
candidate. The controller itself is not a candidate sandbox.

## Subscription transport and evidence limits

`CodexTransport` is opt-in, pinned to `codex-cli 0.154.0`, and requires an explicit
model name and a fresh evidence directory. It verifies ChatGPT login, removes
API-key environment overrides and enforces `forced_login_method="chatgpt"`.
It never falls back to API-key authentication. Calls use read-only, ephemeral
sessions with user configuration and model-side execution/integration features
disabled. Unexpected tool events, missing usage or failed generations fail
closed. Prompts, JSONL events, actual usage and failures are retained. A failed
generation closes subsequent admission; no automatic retries spend allowance.

OpenAI documents [saved authentication for noninteractive runs](https://learn.chatgpt.com/docs/non-interactive-mode)
and [ChatGPT versus API-key authentication](https://learn.chatgpt.com/docs/auth).
The installed CLI reported `Logged in using ChatGPT` during implementation.
That is not a live-generation test or a guarantee of remaining subscription
allowance. The provider's resolved model snapshot is unknown when the CLI does
not report it; requested model names must not be presented as verified snapshots.
Token usage is not converted to invented monetary costs.

Wall-clock limits are admission bounds plus bounded child-process termination,
not equal algorithmic CPU time. Task work units are not FLOPs or money. Failed,
unstarted and unknown-work rows remain in reports. No published winner or
statistical generalization claim follows from adapter contract tests.

## Remaining verification / scope

Implementation is not yet verified. Final gates must include all three .NET
targets, raw numeric reconciliation, the actual pinned OpenEvolve process path,
the companion AiDotNet executable, prompt matching, and adverse protocol tests.
No paid model calls have been made. Registered competitor
holdout integration, real isolated task experiments, and equally budgeted
tuning/selection evidence are not delivered by these fixtures.
