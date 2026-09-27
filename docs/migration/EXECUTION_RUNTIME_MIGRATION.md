# Standalone execution runtime relocation

This increment ports the actual process runner and input/output evaluation layer
from AiDotNet PR2148/2168 into `AiDotNet.Evolution.Programs`. It depends on Evolution
PR91 (all hosted checks passed), not on the AiDotNet package. It does not yet replace
the builder, CLI, artifact/output observers or novelty orchestration.

## Ownership and disposition

| Original at `9cd7d5d6c366a483874024650d02901f69a1829c` | Evolution destination |
|---|---|
| `src/ProgramSynthesis/Execution/ProcessProgramExecutionEngine.cs`, bounded reader, Windows job object | `src/AiDotNet.Evolution.Programs/Execution/` |
| Execution request/response/error and compilation diagnostics; execution/telemetry interfaces | Same directory, `AiDotNet.Evolution.Programs` namespace |
| Sandbox/interpreter/limit configuration, mode/comparison enums, input/output examples | Same directory; independent owned types, no AiDotNet forwarding facade |
| Input/output, sandboxed and all-pass fitness evaluators | Same directory; reuse existing exact-source genomes and correctness metadata copying |
| Four original test suites, shell fixture and execution doubles | `tests/AiDotNet.Evolution.CSharp.Tests/Execution/` |

[Original archive](aidotnet-execution-runtime-original.zip) retains all 25 selected
source blobs, verified against Git object hashes. It includes the complete original
test-doubles file; only its execution doubles are needed here. Existing model doubles
and newer task/correctness implementations are not overwritten. Imported files retain
the original license under `src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt`.

The original generic ProgramSynthesis DTO/interfaces can still serve unrelated
AiDotNet synthesis code. Their presence there is not permission to keep Evolution
orchestration there. The separate removal audit must distinguish generic callers
from the misplaced Evolution process/runtime callers before deleting anything.

## Adversarial changes

- Snapshot every request field before queue admission: changing input or compile mode
  while queued cannot bypass validated limits or change which operation was requested.
- Disposal refuses new calls but lets admitted calls finish and release their permits.
- Invalid concrete-language allowlists fail closed, rather than becoming unrestricted.
- Output is complete only after observed EOF. Canceled/faulted/undrained streams are
  marked truncated; the synchronous adapter refuses truncated output too.
- Sandbox fitness refuses inconsistent success evidence: wrong language, nonzero exit,
  an error code, compile-only output or null output. Caller cancellation remains canceled
  with its dispatched-call receipt, not a completed bad candidate.
- Evaluator identity includes the execution boundary identity and checks for drift,
  including when a provider changes identity and throws. Configuration and caller-supplied
  runtime version invalidate cached fitness. Unversioned local runners get per-instance
  identities, preventing accidental cache/checkpoint reuse across unknown runtimes.
- Workspace preparation failures return structured errors; configured paths are resolved
  absolutely. Template substitution cannot recursively expand placeholder text in a path.
- The inherited process-tree test previously waited four seconds for a marker scheduled
  twenty seconds later. Its descendant now schedules the marker after three seconds,
  so the four-second observation can detect failure to terminate it. Shell/descendant
  prerequisites now fail tests rather than silently skipping proof.

## Usage and security boundary

```csharp
var options = new ProgramSandboxOptions { RuntimeVersion = "my-provisioned-image-sha256" };
options.SetInterpreter(ProgramLanguage.Python,
    new ProgramInterpreterSpecification("/usr/bin/python3", "{source}", "-m py_compile {source}"));
using var runner = new ProcessProgramExecutionEngine(options);
var fitness = new SandboxedProgramFitnessEvaluator(runner,
    new[] { new ProgramInputOutputExample { Input = "2\n", ExpectedOutput = "4" } });
// Pass fitness to the existing ProgramEvolutionTask / EvolutionEngine pipeline.
```

`RuntimeVersion` must change with the provisioned interpreter and dependencies.
It is caller-provided identity, not remote attestation. Commands and argument templates
are trusted host configuration; use absolute executables and avoid nested shell quoting.

This is a bounded child-process runner, **not filesystem/network isolation**. Resource
limits include wall time, output and concurrency. Windows job/POSIX memory limits are
attempted, not attested fail-closed containment; `CanEnforceMemoryLimit` reports capability
only. Hostile candidates require an independently provisioned least-privilege OS/container
boundary without secrets or sealed test answers. No serving connector or in-process
unsafe runner is included. Compile-only requests without a separate command are refused.

Cost units count dispatched evaluator calls, not dollars, tokens or runtime. Local proof
uses authored shell fixtures, not generated hostile code or a competitor benchmark.

## Verification and remaining ownership work

Final build, test, package-consumer and formatting results are retained in
[execution verification](../evidence/execution-runtime-relocation/README.md).
The package-only consumer starts a real child process and scores its output through
the standalone evaluator; it uses no project references or model calls.

PR2148 and PR2168 remain open until their entire remaining contribution has a verified
replacement: script/LLM/novelty evaluation, artifacts/outputs, standalone run facade,
then CLI/config/preflight/control/evidence. This partial replacement is not grounds
to close either source PR. After those transfers, publish the separately authorized
clean-breaking AiDotNet removal. US-11/PTX proof follows repository cleanup.
