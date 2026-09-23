# Standalone program foundation migration

This cleanup increment moves the compiler-neutral task, descriptor and edit substrate
from the combined AiDotNet foundation/CLI tree into `AiDotNet.Evolution.Programs`.
It depends on Evolution PR89. It does **not** finish AiDotNet PR2148, PR2168 or PR2203;
they remain open until their remaining runtime and consumer integrations are ported.

## Source and ownership

Source: AiDotNet `9cd7d5d6c366a483874024650d02901f69a1829c` (PR2168's pinned
combined foundation tree). The original 27 source/test/configuration blobs are retained in
[aidotnet-program-foundation-original.zip](aidotnet-program-foundation-original.zip).
Every archive entry was checked against its Git blob SHA-1, including the blob header.
The original BSL license remains in the Programs package.

| Original files | Evolution destination |
|---|---|
| `ProgramEvolutionTask` | Programs task adapter: exact source identity, bounds, descriptor merge, cost/measurement-origin preservation |
| `ProgramDescriptorSet`, length/diversity/token descriptors and three descriptor interfaces | Programs public descriptor APIs, including explicit rebase/version identity |
| Six `ProgramDiff*` implementation/result files, options and failure enum | Programs SEARCH/REPLACE parsing, routing, application and diagnostics |
| Three fenced-code files and selection enum | Programs display extraction and public exact executable-source extraction |
| `ProgramLanguageDetector` | Programs deterministic detection, filename and fence-label mappings |
| Five original test files | CSharp.Tests/ProgramTypes, compiled against Evolution-owned types |
| `ProgramEvolutionOptions` task/edit subset | New `ProgramTaskOptions`; complete old facade options preserved in the archive, **not claimed migrated** |

Existing newer genome, correctness, measurement, compiler and deployment implementations
are reused, not overwritten. Programs has no dependency on the AiDotNet package.
All moved interfaces/options/enums use `AiDotNet.Evolution.Programs`; no compatibility
forwarders or `AiModelBuilder` extensions are introduced.

`ProgramTaskOptions` describes only a task's bounds and edit rules.
Its optional resource settings identify the caller-owned evaluator's cost semantics;
the task does not silently debit that ledger. Use the existing metered program pipeline
for ledger accounting. `Generic` permits any explicitly identified language; a configured
concrete language rejects mismatches before evaluator dispatch.

## Adversarial corrections included

- The old enforced-edit path allowed edits when no evolve regions existed.
  Missing, malformed and injected markers now fail closed.
- The old application rebuilt every source line using one newline convention.
  Edits now splice exact source offsets, preserving every unmatched character,
  including mixed CR/LF/CRLF and protected multiline strings.
- Parsing no longer trims trailing content whitespace from search/replacement blocks.
  Response parsing still interprets line-oriented protocol terminators; exact fenced
  extraction is the API for preserving a full rewritten source.
- Direct block application now enforces block-count and source-growth bounds.
  Response parsing rejects oversized input before splitting it.
- Task dispatch checks cancellation and concrete-language compatibility.
  Descriptor merging retains measured cost, metrics and original sample identities.

`ProgramDiffApplyResult` can contain successfully applied blocks together with failures,
as before. Consumers must check `IsSuccess` before accepting `ModifiedSource`.
Language detection and static descriptors are heuristics, not correctness or speed proofs.
Fenced extraction alone does not authorize execution or prove the requested language.

## Verification

- Release solution build: zero warnings and errors.
- Programs/compiler suite: 350 passing on net8.0 and 350 on net10.0.
  This includes 147 additional cases per framework relative to PR89.
- Deployment regression suite: 41 passing on each framework.
- Stacked-PR workflow contract: four passing.
- Total scoped executions: **786 passing, zero skipped**.
- Package-only consumer exercises the actual engine, standalone task, edit application,
  descriptors and deployment types. Its authored deterministic fitness changes 1 to 2
  in two evaluations; this is a functional integration fixture, **not** an optimization
  benchmark or a competitor advantage.

The fixture has no project references and restores uniquely versioned local packages.
Hosted checks include both Programs target frameworks, the package consumer, the existing
full core gates and formatting. Evidence is retained under
`docs/evidence/program-foundation-relocation`.

## Remaining cleanup

1. Port the rest of PR2148: model-driven prompts/variation/provenance, sandbox/evaluator
   and output contracts, and standalone replacements for builder orchestration.
2. Port PR2168's actual CLI/configuration/preflight/control/evidence functionality.
3. Port PR2203's model-driven program comparison host without replacing the newer
   independent comparison tracks or claiming historical fixture evidence is live proof.
4. Prepare the separate clean-breaking AiDotNet removal PR once runtime consumers have
   verified replacements. PR2092's embedded-core removal alone is insufficient.

No source PR is closed by this partial foundation migration. No merge, branch deletion,
release, paid model run or expensive head-to-head study is performed.
