# Portable warm-start repertoires

US-23 implementation slice: bounded seed export/import with applicability and prior-cost provenance. This is
not checkpoint resume, persistent fitness caching, a fresh statistical sample, or authorization to deploy a seed.

## Contract

Given a selected set of genomes, when `EvolutionRepertoire.ExportAsync` runs, then the source task canonicalizes
them, the codec round trip preserves their identity and exact payload, duplicate seeds are collapsed, and bounded
source provenance accompanies the export. Export does not call the evaluator or claim the seeds are measured
winners. Callers selecting archive winners retain their actual run/evaluation evidence separately.

Given an exported repertoire, when `FromJson` reads it, then schema, required fields, duplicate fields, checksums,
strict Unicode, entry counts and byte budgets are validated before import. An envelope checksum proves integrity
relative to its contents, not trusted authorship. The application must authenticate external provenance separately.

Given compatible task identity and codec schema, when import runs, then the current task decodes and canonicalizes
each seed and checks a current codec round trip. Its canonicalizer must enforce current hard constraints. Changed
task/evaluator/data/fidelity/compiler/runtime/hardware/correctness versions can supply seeds for reevaluation but
never old fitness. A changed task id or codec schema requires an explicit external migration.

Given unchanged task semantics, when a decoded seed disagrees with its source canonical identity, then it is
rejected. Given changed canonicalization semantics, when multiple source entries normalize to one current seed,
then import retains one seed and reports the duplicates. Every source entry gets an accepted/duplicate/rejected
decision with source/current identities; raw exception messages are not returned as provenance.

Given accepted seeds, when a new engine run starts from them, then that run evaluates them normally. A matching
scope does not import quality, descriptors, sample counts, uncertainty or measurement cost into the run-local
fitness memo. Timing and stochastic fitness therefore cannot silently become fresh measurements through this API.

Given cancellation or task/codec version drift during import, when the operation ends, then it does not return
partial successful output. Ordinary malformed seeds are counted as rejected; fatal memory failures propagate.

## Usage

```csharp
var scope = new EvolutionReuseScope(
    task.Id, task.VersionHash, task.EvaluatorVersionHash,
    codec.Id, codec.VersionHash,
    constraintsVersion: constraintsFingerprint,
    dataVersion: dataAndPartitionFingerprint,
    fidelityVersion: fidelityFingerprint,
    compilerVersion: compilerAndDependenciesFingerprint,
    runtimeVersion: runtimeFingerprint,
    hardwareVersion: hardwareFingerprint,
    correctnessPolicyVersion: correctnessPolicyFingerprint);

var provenance = new EvolutionRepertoireProvenance(
    sourceRunId, sourceRun.StateHash, retainedEvidenceSha256,
    exportTime, totalPriorInformationCost, versionedCostUnit);
var repertoire = await EvolutionRepertoire.ExportAsync(
    selectedGenomes, task, codec, scope, provenance, cancellationToken);
string portableJson = repertoire.ToJson();

var restored = EvolutionRepertoire.FromJson(portableJson);
var imported = await restored.ImportAsync(currentTask, currentCodec, currentScope, cancellationToken);
// Inspect imported.Decisions and SourceProvenance before choosing the new run's starting population.
var result = await currentEngine.RunAsync(imported.Seeds.Select(seed => seed.Genome), cancellationToken);
```

This is an application integration fragment: provide actual adapters, fingerprints, selected genomes, source
evidence and a correctly configured engine. Irrelevant facets must use an explicit versioned not-applicable value;
the core does not infer environmental identity. Import can return no valid seeds, which the application must
handle with its established cold-start population. It does not invent a fallback genome for an opaque domain.

## Bounds and accounting

At most 256 input seeds per export, 64 KiB strict UTF8 per payload, 2 MiB aggregate payload, 8 MiB complete JSON,
and 256 printable characters per metadata label. Enumeration and retained output are bounded. A custom codec or
canonicalizer remains trusted application code: its CPU, allocation and external work require application limits.
No filesystem, network, model or evaluator call is performed by the repertoire itself; supplied adapters can do
work and must be accounted for. Transport/file persistence remains application-owned.

The provenance cost is an explicit declaration, not an automatically measured ledger receipt. Fair comparisons
must give both methods equivalent admissible prior information and report construction cost separately from
import/validation and new-run spend. Do not charge the old construction cost again as if it were freshly spent;
do not omit it when reporting total cost of obtaining the warm-start advantage.

Payloads are exact untrusted data, not scrubbed source. Do not export secrets or final-test-derived information.
Base64 is transport encoding, not encryption, redaction or a sandbox.

## Remaining US-23 work

[Persistent evaluation reuse](PERSISTENT_EVALUATION_REUSE.md) now provides separate storage, exact sample/fidelity
keys, original-sample uncertainty/age policies and cache-hit provenance/accounting. Representative fair warm/cold
campaigns and consumer-facade integration remain. Seed-only repertoires cannot substitute for those contracts;
no competitive advantage is claimed by deterministic contract tests.

## Local verification

All 531 core tests pass separately on net10.0, net8.0 and net471, including 53 repertoire cases. The two new
source files cover 227/228 executable lines in the net10 run; overall core coverage is 7,741/8,453 lines and
4,563/5,916 branches. Production/test-copy DLL hashes match across all targets and whitespace checks pass.
The tests use real engine runs with deterministic integer tasks; they do not use a model provider or claim
timing-based optimization gains.

```text
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net10.0
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net8.0
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net471
```
