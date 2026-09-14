# Representative benchmark suite (US-01)

This suite adds nine independently seeded numeric definitions and a predeclared **11-task AlgoTune subset**. It retains the existing four-objective development harness and the fast library regression tests. Its purpose is to make comparisons reproducible and auditable; it does not itself establish competitive superiority.

## Panels and partitions

The exact catalogue is [catalog-v1.json](catalog-v1.json). Each semantic family belongs to one partition only; merely relabeling another instance of a development task as an unseen family is rejected.

| Partition | Numeric families | AlgoTune application tasks |
| --- | --- | --- |
| Development | Deceptive binary blocks, periodic multimodal landscapes, constrained capacity selection | `base64_encoding`, `sha256_hashing`, `count_connected_components`, `minimum_spanning_tree`, `shortest_path_dijkstra` |
| Configuration selection | Noisy regression, expensive time-stepped diffusion, nonsmooth coupled loss | `convolve_1d`, `correlate_1d`, `stable_matching` |
| Final reporting | Signed interaction graphs, stochastic inventory control, nonlinear robust constraints | `matrix_multiplication`, `outer_product`, `unit_simplex_projection` |

Each partition has a fresh random 256-bit generation root. The public plan commits to its hash before execution; private roots are stored separately. Instance seeds and search seeds use distinct derivations. Numeric objectives have seeded coefficients and, where applicable, fresh reproducible evaluation noise. Program instances come from the pinned upstream generators, not downloaded public datasets. These are independent generated instances under the declared generator, not proof of statistical independence or immunity to public task exposure. The task definitions remain public.

Numeric methods receive the same eight initial genomes for each task/search seed, including two fixed feasible anchors for constrained tasks. Two coordinate descriptors are independent inputs, not an encoding of objective quality. All six existing methods are available: random search, hill climbing, fixed MAP-Elites, adaptive MAP-Elites, its uniform-allocation control and diagonal CMA. Evaluation caching is disabled for suite tasks, including noisy cases. Constraints are explicitly recorded and infeasible evaluations cannot improve the reported best feasible loss. This is an instance/task suite, not a claim that selecting the minimum noisy observation estimates true expected fitness; replication/confirmation protocols remain essential for that claim.

## Application starting implementations

Every AlgoTune entry pins its task class and Git blob at upstream revision [`dff9914c10800c7a031c9e8c3d4d1c8cd1b38906`](https://github.com/oripress/AlgoTune/tree/dff9914c10800c7a031c9e8c3d4d1c8cd1b38906/AlgoTuneTasks). The valid starting implementation is that class's unchanged `solve` method; its original source remains in the upstream checkout with its license. Execution verifies the exact checkout revision and each task's normalized Git blob, and records a SHA256 of the actual normalized source. The path/class/method is retained in each result, so a later program-evolution adapter has a concrete starting program, not just a task name.

The worker runs the upstream generator, solver and `is_solution` method. It supplies an explicit minimal constructor/registration bridge because importing the full upstream Task base also imports configuration, dataset-download and agent infrastructure. The eleven selected implementations do not call those base services. This is **not** the full AlgoTuner harness, its dataset calibration or its public leaderboard protocol. No agents, model APIs or dataset downloads run. The worker additionally rejects nonfinite solution values, including NaN values that can otherwise evade some tolerance comparisons; `None` remains the upstream disconnected-path sentinel.

Pinned upstream semantics are preserved, including MST's exact reference edge-list comparison and possible spanning-forest outputs on disconnected generated graphs. These are not silently replaced with more permissive textbook validators. Upstream self-validation is a reproducibility oracle, not an independent mathematical proof; cross-oracle tests belong in further evaluator hardening.

Each reference instance includes one charged warm-up and three measured solver repetitions, each validated. Input/output hashes, exact seeds, scale, dependency versions, hardware/runtime information, source/evaluator hashes, solver/validator call counts and phase/total wall times are retained. There is no evolved-program comparison in this story and **no speedup is inferred from timing the reference alone**.

## Run a contract smoke panel

Prerequisites: .NET 10 SDK, Python 3.11+ compatible with the pinned requirements, Git, and a checkout of the exact upstream revision. Use an isolated virtual environment; do not install AlgoTune's complete agent dependency stack.

```powershell
python -m venv .local/benchmark-env
.local/benchmark-env/Scripts/python.exe -m pip install -r benchmarks/suite/requirements.txt
git clone --filter=blob:none --sparse https://github.com/oripress/AlgoTune.git .local/algotune-upstream
git -C .local/algotune-upstream sparse-checkout set AlgoTuneTasks
git -C .local/algotune-upstream checkout --detach dff9914c10800c7a031c9e8c3d4d1c8cd1b38906
./eng/Test-RepresentativeSuite.ps1 -Python .local/benchmark-env/Scripts/python.exe -Upstream .local/algotune-upstream -SourceRevision <40-character-source-commit>
```

Use new paths if these already exist. Linux uses `.local/benchmark-env/bin/python`. The script builds once (or accepts `-NoBuild`), runs protocol tests, and executes **all three partitions explicitly as `contract-smoke`**. It also exercises a separately named registered-lifecycle fixture: real selection output, freezing an explicitly illustrative configuration, execution of exactly those final methods, and refusal of repeat final access. Neither fixture is advertised as a competitive final result or evidence of policy improvement. CI performs this same protocol and uploads failed/successful results.

## Registered evaluation with separate selection and final stages

1. Run `run_suite.py prepare <new-plan-directory> --source-revision <commit>` without `--contract-smoke`. Choose instance/replicate/evaluation budgets before running it.
2. Keep `final-private.json` under benchmark-custodian control, outside development/model/configuration-selection inputs. File ownership and access control are your responsibility; the CLI is not a security sandbox.
3. Run `run_suite.py run <plan> development --numeric-dll <built-quality.dll> --upstream <pinned-checkout> --output <new-output>`, then the separate `selection` partition.
4. Write a configuration containing exactly the schema below. Choose registered numeric methods using only development/selection results, retaining random search and hill climbing as fixed controls. Unknown/ignored knobs are rejected.
5. Run `run_suite.py freeze <plan> <configuration.json> <selection-report.json>`. This requires a completed selection report from that exact plan and freezes the actual method list, program reference role, source/runtime artifacts and selection-report hash.
6. Run the `final` partition. The actual frozen method list is executed. The final-consumed receipt is exclusively created **before** seeds are exposed or evaluation begins; failures do not reset it. Runtime/artifact changes after selection are rejected. Repeated final access requires a new plan and fresh instances, not deleting a receipt and claiming the old result is untouched.

```json
{"schema":"aidotnet-suite-configuration-v1","numeric_methods":["RandomSearch","HillClimb","FixedMapElites"],"program_solver":"upstream-reference"}
```

The lower-level `.NET --suite` entry point accepts explicit frozen requests for orchestration/replay; it does not enforce private-root custody itself. A filesystem owner can bypass or forge local workflow files. Neither that entry point nor the Python controller makes malicious code safe. This story executes only registered trusted numeric implementations and the pinned upstream references; arbitrary generated code belongs in the isolated program-execution story.

## Budgets and evidence interpretation

Numeric reports preserve each run, full terminal-evaluation records, actual evaluator/proposal counts, feasible best-loss traces and resource receipts. Work units are task-defined (for example diffusion steps), **not inferred FLOPs, money, CPU time or comparable cross-task prices**. Equal per-task evaluation/proposal limits and shared information make within-task method comparisons meaningful; aggregate cross-family claims need a preregistered normalization/analysis protocol.

The controller bounds one numeric child to 180 seconds, each reference child to 30 seconds and the campaign to 300 seconds. Trusted child timeouts are terminated and recorded with unknown consumption, not zero cost; a reference timeout stops further dispatch. The program subset's fixed small scales validate integration and starting implementations, not realistic workload throughput. Frozen manifests, private-root commitments and raw reports are written exclusively and never overwritten. Runtime JSON includes dependency manifests/hashes and available hardware identifiers; unavailable hardware fields are explicitly labeled rather than invented.

No API spending, package publication, production promotion or merger is part of these commands. References: [AlgoTune repository and offline evaluation documentation](https://github.com/oripress/AlgoTune), [NetworkX package metadata](https://pypi.org/project/networkx/3.6.1/), [cryptography package metadata](https://pypi.org/project/cryptography/50.0.1/).
