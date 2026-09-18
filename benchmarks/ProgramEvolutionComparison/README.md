# Standalone model-driven program comparison host

Relocates AiDotNet PR2203 to AiDotNet.Evolution. This host uses the actual
`ProgramEvolutionTask`, `LlmProgramVariationOperator`, `ProgramPromptBuilder` and
descriptor archive from the standalone Programs package. It does not reference AiDotNet.
The newer `benchmarks/EvolutionComparison` host is separate and remains unchanged.

```powershell
dotnet build benchmarks/ProgramEvolutionComparison -c Release
python benchmarks/ProgramEvolutionComparison/test_host.py
```

The tests use scripted text providers and trusted fixture scoring only. They exercise
both `controlled` and `native-bounded` modes, then the existing shared ProgramBroker's
admission/accounting. They also reject unknown work, wrong candidate hashes, malformed
status, negative work, unsafe endpoints, missing capabilities and output overwrites.
No paid model calls or execution of generated source occurs.

Real broker invocation retains the original seven-argument contract:

```text
dotnet ProgramEvolutionComparison.dll <initial.py> <new-output.json> <model> <iterations1..64> <uint-seed> <task-description.txt> <controlled|native-bounded>
```

The caller supplies `EVOLUTION_BROKER_ENDPOINT` and `EVOLUTION_BROKER_CAPABILITY`.
Only authenticated IPv4 loopback HTTP is accepted; proxies and redirects are disabled.
The broker—not this adapter—owns quota admission and isolation of candidate evaluation.
Never give its capability to generated candidates. No paid API fallback is supplied.

Controlled mode retains the literal system/user prompts; native-bounded mode uses the
relocated context-aware prompt builder. Both run sequential full-rewrite proposals,
no retries, one island, a 64-cell length archive, and at most iterations+1 evaluations.
Reports retain source/artifact hashes, evaluations, attempts and usage. Actual provider
token accounting stays in broker receipts; missing provider usage is not proof of free work.
Evaluator exceptions or unreconciled work now produce a failed report and nonzero exit.

The functional tests do not establish optimization quality or superiority over OpenEvolve.
Historical evidence remains historical. See [migration details](../../docs/migration/MODEL_RUNTIME_MIGRATION.md).
