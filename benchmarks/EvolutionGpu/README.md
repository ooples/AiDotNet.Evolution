# US-11: measured GPU tactic selection

This is a real CUDA consumer of the Evolution engine, not a synthetic fitness
function. It compares **two existing Tensors execution tactics** for
`GELU(input * weight + bias)`: the public fused path and cuBLAS GEMM followed by
resident bias and GELU operations. It does not generate or tune PTX instructions,
prove which internal dispatch ran, change production defaults, or compare OpenEvolve.

The fused path is the original fallback. Both candidates must pass an independent
FP64 scalar oracle (absolute tolerance `1e-4` plus relative tolerance `1e-3`) before
timing and again after timing. Nonfinite output, invalid timing, missing CUDA, or
incomplete screening fails the process rather than fabricating a score.

Evolution evaluates both categorical seeds under a two-evaluation ledger per
shape. This bounded exhaustive screen demonstrates adapter integration, not an
advantage of evolutionary search over enumeration. Three shapes cover small
decode and larger batched linear work, not the complete kernel/input space.

The selected tactic is checked against the original on five new input seeds with
alternating execution order. A changed tactic is eligible only if all five paired
median speedups exceed 1.05. This conservative guard is **not** a confidence
interval or a generalization claim. Each process reports its decision; nothing
installs a dispatch change. Cross-process disagreement must retain the fallback.

## Run

```powershell
# CPU-only acceptance checks and build, suitable for hosted CI:
./eng/Test-GpuConsumer.ps1 -ContractsOnly -OutputDirectory .local/gpu-contracts

# Three independent GPU processes using the published Tensors 0.130.3:
./eng/Test-GpuConsumer.ps1 -OutputDirectory .local/gpu-package-proof

# Or build a clean, pinned Tensors source checkout (used for the recorded proof):
./eng/Test-GpuConsumer.ps1 -TensorsSourceDirectory F:/path/to/clean-tensors -OutputDirectory .local/gpu-source-proof
```

CUDA and the native libraries required by Tensors must be available. The runner
builds only net10.0. A full cross-target local project-reference restore currently
hits Tensors' System.Text.Json 10.0.11 versus Evolution's net471 10.0.12 downgrade;
the single-target consumer neither suppresses that error nor claims compatibility
for the untested targets. Tensors 0.131.0 was not available on NuGet when verified;
the package-mode contract test passed with 0.130.3. Source-mode and package-mode
results must not be mixed: their kernel implementations may differ.

Existing output directories are refused. Reports contain every timing sample,
numerical error, assembly SHA256/MVID, shape, decision, throughput, and operation
count. The wrapper records the source revision, GPU identity/driver/activity and
separate process logs. Failed runs retain logs and any prior completed reports.
No API/model spending or production deployment occurs.

## Measurement boundaries

- Timings are synchronized host elapsed time per operation, including dispatch
  and synchronization overhead. They are **not CUDA-event/device-only timings**.
- Inputs, weights, bias and outputs are resident/preallocated. Allocation, input
  transfers, oracle downloads and ten warmups are outside timing.
- Screening uses 15 samples, confirmation 31; each sample batches 16 operations.
  All three shapes total 6 screening evaluations, 30 confirmation measurements,
  and 16,716 operations per process including correctness/warmup calls. Ledger
  cost units account for screening evaluations only; raw operation counts include
  confirmation. They are not monetary or GPU-energy accounting.
- ResidentBufferBytes counts these four explicit buffers, not total device peak
  memory, backend scratch or cuBLAS workspace.
- A desktop RTX 3080 under WDDM is not exclusive GPU access; reports cannot rule
  out interference. Three fresh processes do not establish cross-device results.

See [recorded evidence](../../docs/evidence/us11-gpu/README.md).
Timing rationale: [NVIDIA CPU/GPU synchronization guidance](https://developer.nvidia.com/blog/how-implement-performance-metrics-cuda-cc/).
