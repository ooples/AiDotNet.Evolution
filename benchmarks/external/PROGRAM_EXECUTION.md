# US-02 real program execution and development pilot

All new implementation is in **AiDotNet.Evolution**. `benchmarks/EvolutionComparison` references only the generic core;
the program CI no longer checks out or builds AiDotNet. Existing consumer PRs/files are preserved for separate removal.
The benchmark host evolves immutable Python source strings through the actual Evolution engine. This is an explicitly
configured benchmark adapter, not a claim that the old AiModelBuilder/native prompt configuration has been migrated.

## What is now executable

- Both real controllers (Evolution and pinned OpenEvolve), one-shot and iterative single-parent baselines share one
  evaluator and provider broker. Controlled and native-bounded tracks remain separate. Native presets are disclosed,
  not asserted to be equally tuned. Model-side tools are disabled by the existing opt-in subscription transport.
- Starting modules come unchanged from the existing pinned AlgoTune task blobs. The first panel uses only development
  tasks: base64 encoding, SHA-256 and connected components. No public final partition is consumed or renamed.
- Fresh bounded input batches include empty/boundary data, bytes up to 512 KiB, graphs up to 512 nodes, isolated nodes,
  repeated edges and self-loops. These are declared custom instances, not upstream's full generator distribution or
  an AlgoTune leaderboard run. Broader workload/device coverage remains required for broader claims.
- Correctness is checked outside the container: a bit-level base64 encoder, host SHA-256 with known-answer controls
  (shared cryptographic backend lineage remains possible), and union-find independent of the original NetworkX solver.
- Actual controller-selected source must match a valid recorded search evaluation. Selection is saved before fresh
  diagnostic evaluation. Failures keep the exact original program; unchanged fallbacks have reported speedup exactly 1,
  not a fictitious gain from comparing two noisy timings of the same source.

## Isolation and measurement boundary

Build `benchmarks/external/sandbox/Dockerfile` from the pinned base-image digest and pinned dependencies. Resolve the
resulting local **immutable image ID**; the runner rejects mutable tags. Different builds can produce different image
IDs; compare methods under one recorded ID, never silently substitute another during a campaign.

Only a trusted local Linux Docker daemon is accepted. Before starting each candidate, inspect and verify nonroot UID,
network none, readonly root and input mount, all capabilities dropped, no-new-privileges, seccomp, one CPU pinned to
CPU0, 512 MiB memory/no additional swap, 32 PIDs and bounded scratch. A host supervisor enforces time and output limits.
No API credentials, Docker socket, home directory or expected answers are mounted. Candidate code is never imported
by the host. Only the task's original trusted source is verified/read there. Kernel/daemon/image trust remains an
assumption; containers are not a defense against unknown host-kernel vulnerabilities or malicious administrators.

Candidate output is untrusted bounded JSON with duplicate/nonfinite fields rejected. Output cannot set fitness or
timing. Duration comes from the Docker daemon's start/finish state, outside candidate control. The metric is **complete
fresh-process batch latency**, including Python startup, imports, execution and output serialization—not isolated
algorithm-kernel latency. This prevents forged candidate clocks but can make startup dominate; do not extrapolate its
results to long-lived in-process services. Keep host background activity quiet during the pilot; no machine-wide power
or affinity settings are altered. The initial local baseline was built before the measurement batch, not concurrently.

Every created candidate container is removed by its exact ID after owner-label verification; source, request, inspected
configuration, terminal state, bounded stdout/stderr, daemon time and supervisor wall time remain in evidence. No global
pruning or deletion of existing containers/images occurs. Cleanup failure aborts and preserves a failing receipt.

## Reproduce

```powershell
docker build -t evolution-program-sandbox:local -f benchmarks/external/sandbox/Dockerfile .
$image = docker image inspect evolution-program-sandbox:local --format '{{.Id}}'
$env:EVOLUTION_SANDBOX_IMAGE = $image
python -m unittest discover -s benchmarks/external -p test_docker_sandbox.py -v
dotnet build benchmarks/EvolutionComparison -c Release
python benchmarks/external/run_program_pilot.py --image $image --output <new-directory> --upstream <pinned-AlgoTune-checkout> --openevolve <pinned-OpenEvolve-checkout> --dll benchmarks/EvolutionComparison/bin/Release/net10.0/EvolutionComparison.dll
```

Use the pinned program virtual environment from `requirements-program.txt`. The command above is **scripted plumbing
validation**, not LLM optimization. Add `--codex <pinned-codex.exe>` only for the expressly authorized subscription-backed
development pilot. No API-key fallback is available. The fixed pilot allows at most **33 model calls across three tasks**
(two calls per track except one-shot), one search seed, three timing samples per evaluation, six tracks per task and
100,000 reported tokens per track. It uses the previously selected requested model; resolved snapshot remains
`unreported` if the transport cannot supply it. Token overrun detection is post-receipt, not a guaranteed preemptive cap.

`plan.json` precedes model dispatch. Every raw model receipt remains in the transport evidence; every evaluator attempt
remains in sandbox evidence. Tuning is zero and actual tokens, setup/oracle work and evaluator wall time are separately
reported; **equal per-track caps are not equal reconciled total search cost**. No dollar conversion is invented.
Repeated diagnostic timings do not increase the count of independent search runs.

## What remains before empirical story completion

This pilot must not be relabeled as a sealed final campaign. Its purpose is to discover integration failures, resource
requirements and measurement variance before registering the final study. It always declares `claim: none`.

Remaining: pilot-based sample/budget design; independently reviewed frozen protocol; equal-budget tuning where claimed;
appropriate provider identity limitations; fresh held-out execution with independent custody; complete task/seed grids,
paired uncertainty and regression reporting; and reconciliation with US-12's exact endpoint/cost requirements. Unknown
work, failed controls, insufficient statistical power or nonmatching costs cannot be converted into a competitive win.
US-02 remains empirically incomplete until that work is delivered. User handles reviews and merging; no release or
superiority claim is authorized by this runner.
