# AiDotNet.Evolution.Ptx

Evolve CUDA PTX kernel source with verified correctness and timing. A model proposes rewrites or line patches of a
kernel; every candidate is JIT-loaded by the CUDA driver in an isolated worker process, checked against a reference on
seeded and shape-fuzzed inputs, and only then timed against the incumbent with a sealed paired replay. Winners are
exported as versioned, content-addressed artifacts that AiDotNet.Tensors' kernel-tuning registry can consume.

The package builds and verifies candidates; it never decides to run a campaign. Running one (and paying for the model
calls) is a separate, explicitly budgeted step.

## One contract type for every operator

There is no per-operator code. A `PtxKernelContract` describes a kernel entirely as data, and serializes to canonical
JSON (`ToJson` / `FromJson`, identified by `Fingerprint`):

| Part | What it states |
| --- | --- |
| `EntryPoint`, `Parameters` | The `.entry` name and every argument in `.param` order: guarded buffers (`Input`, `Output`, `InPlace`, with element type, length and seeded distribution) and scalars (an extent such as `N`, or a constant such as an epsilon). |
| `Symbols` | Shape dimensions, each with a fuzzing range and the tile sizes whose boundaries must be probed. |
| `Launch` | Grid extents (such as `ceil(N / blockX)`), block dimensions, dynamic shared memory. Candidates may change only the block. |
| `Tolerance` | `abs(candidate - reference) <= absolute + relative * abs(reference)`, overridable per output. |
| `Target` | The SM (`sm_75` for the GTX 1660 Ti development GPU) and its register, shared-memory, thread and spill limits. |
| `TimingShape`, `ValidationShapes`, `FuzzCases`, `Seed` | What is timed, what must always pass, and how many fuzzed shapes are added. |

Extents (`PtxExtent`) compute every size from the shape symbols, so one contract covers a convolution, a LayerNorm, a
GEMM or a fused optimizer step at any shape. The reserved symbols `blockX`, `blockY`, `blockZ` let grid sizes follow a
candidate's block change.

## The pipeline

1. **`PtxProgramCompiler`** - static checks without a GPU (`.version`, `.target` no newer than the contract's SM,
   `.address_size 64`, the entry signature against the contract), then `cuModuleLoadDataEx` with info and error logs in
   the worker, whose ptxas messages become line-numbered `CompilationDiagnostic`s, then resource limits: registers,
   static plus dynamic shared memory, local (spill) memory, launchable threads per block, and the device architecture.
   It is also an `IProgramCompiler`, so it plugs into `ProgramImprovement` (catalog = the entry body's lines).
2. **`PtxCorrectnessEvaluator`** - runs the candidate on the fixed shapes, the timing shape and fuzzed shapes
   (degenerate, every tile boundary `t - 1, t, t + 1, 2t + 1`, the maximum, ragged values), smallest first. Each case runs
   twice, onto outputs pre-filled with `0xFF` and with `0x00`, so an element the kernel never writes cannot pass, and
   every buffer sits between 4 KiB guard regions, so an out-of-bounds write is reported even when the outputs are right.
   The reference is a CPU implementation (`IPtxKernelReference`) or, without one, the incumbent kernel on the same
   inputs. Any failing case rejects the candidate; nothing is timed until all pass.
3. **`PtxTimingEvaluator`** - both kernels in one worker and context, CUDA events, warm-up, then incumbent/incumbent
   control pairs (the calibrated noise floor) and candidate/incumbent pairs with alternating order. The evidence
   (`PtxPairedTimingEvidence`: median, IQR, P95, median speedup, lower 5% bound, noise ratio) and the promotion gate
   mirror AiDotNet.Tensors' `KernelTuningPairedEvidence` exactly. Timing holds the machine-wide mutex
   `Global\AiDotNetBenchLock`, so no two measurements on the machine overlap.
4. **`PtxKernelFitnessEvaluator`** - an `IProgramFitnessEvaluator` combining the two: quality is the median speedup
   over the incumbent; descriptors carry resources, errors, noise and the gate result.
5. **`PtxProgramVariation`** - a metered proposal arm over any `IProgramChatClient`. The prompt carries the contract
   (signature, launch, shapes and layouts, tolerance, limits), the incumbent's measured profile and the parent's
   measured descriptors; the reply is a whole-kernel rewrite or line patches against a hashed catalog, optionally with a
   block change. Every proposal is JIT-checked; failures are fed back for bounded repairs, and all work is charged.
6. **`PtxKernelArtifact`** - the winner: PTX, SM target, launch, resources, correctness summary and raw paired samples,
   stored as canonical JSON named by its SHA-256. `Load` re-derives every statistic and refuses tampering.
   `ToConfiguration` with `PtxKernelConfigurationCodec` is the `TConfiguration` and codec for Tensors'
   `KernelTuningArtifactRegistry<TConfiguration>`; registering never promotes - Tensors' own replay and policy decide.

## Isolation

Every driver call runs in `AiDotNet.Evolution.Ptx.Worker`, a small process embedded in this assembly and started on
the installed `dotnet` host. A kernel that spins, faults or corrupts the CUDA context ends only that worker; the watchdog
kills it at `PtxIsolationOptions.Timeout`, and its output is bounded. PTX cannot touch files or the network, so the
worker is a failure boundary, not a security sandbox. On a machine without a driver or device, every component returns
a clear `cuda-unavailable` / `no-device` diagnostic instead of throwing (`PtxDeviceProbe.Run` reports which).

## Example

```csharp
var contract = new PtxKernelContract("axpy-f32", "axpy", "out[i] = a * x[i] + y[i]", PtxTargetLimits.ForSm(75),
    new[]
    {
        PtxKernelParameter.Input("x", PtxElementType.Float32, PtxExtent.Of("N")),
        PtxKernelParameter.Input("y", PtxElementType.Float32, PtxExtent.Of("N")),
        PtxKernelParameter.Output("out", PtxElementType.Float32, PtxExtent.Of("N")),
        PtxKernelParameter.ScalarConstant("a", PtxElementType.Float32, 2.0),
        PtxKernelParameter.Scalar("n", PtxElementType.Int32, PtxExtent.Of("N"))
    },
    new[] { new PtxShapeSymbol("N", 1, 1 << 16, new long[] { 256 }) },
    new PtxLaunchConfiguration(PtxExtent.Of("N").CeilDiv(PtxExtent.BlockX), null, null, 256, 1, 1, null),
    PtxTolerance.Exact, new Dictionary<string, long> { ["N"] = 1 << 22 }, null, fuzzCases: 12, seed: 7);

var compiler = new PtxProgramCompiler(contract);
var correctness = new PtxCorrectnessEvaluator(compiler, reference, incumbentSource: null);
var timing = new PtxTimingEvaluator(compiler);
PtxCorrectnessReport check = correctness.Evaluate(candidatePtx);
if (check.Passed)
{
    PtxTimingReport measured = timing.Measure(candidatePtx, incumbentPtx);
    if (measured.QualifiesForPromotion)
        PtxKernelArtifact.Create(compiler, check, measured, incumbentPtx, timing.Identity).Save(artifactDirectory);
}
```

## Tests

`tests/AiDotNet.Evolution.Ptx.Tests` runs offline: the evaluators are exercised against an in-process stand-in for the
worker that regenerates the exact seeded inputs, and the watchdog, refusal and probe tests start the real worker
process. Tests that need a real sm_75 device are `[CudaFact]` and skip themselves where none answers.