# Compiler-guided program improvement (US-17)

Implementation lives entirely in **AiDotNet.Evolution**. `AiDotNet.Evolution.Programs` depends only on the core;
`AiDotNet.Evolution.CSharp` adds optional Roslyn compilation. Neither references AiDotNet or AiDotNet.Tensors.
This is an Evolution-owned entry point, not an AiModelBuilder extension. Existing AiDotNet compatibility APIs and
their eventual removal remain a separate migration decision/PR; this work does not silently remove them.

## Contracts and usage

Supply an immutable `ProgramSnapshot`, a charged compiler factory, a `ProgramPlanner`, separate public and held-out
`ProgramVerifier` delegates, one `EvolutionResourceLedger`, and `ImprovementOptions` to `ProgramImprovement.RunAsync`.
Use `ProgramImprovement.Units` for the ledger and every provider/verifier receipt. Construct `CSharpProgramCompiler`
inside the factory with pinned trusted reference paths and target/runtime identity. A different compiler implements
`IProgramCompiler`; the orchestration layer is language-neutral and has no Roslyn dependency.

The model-facing `SearchRequest` contains only original source, a caller-supplied measured bottleneck, original syntax
catalog, bounded public diagnostics and attempt number. A plan records a testable hypothesis, original fingerprint,
and up to 16 disjoint syntax-addressed edits across up to 16 files. All repairs target the same original snapshot;
they do not accidentally apply stale offsets to the last failed edit. Syntax replacement cannot change declarations,
insert directives or escape method bodies. Inputs are detached; logical paths never instruct filesystem writes.

The compiler owns and hashes bounded metadata images, including compiler/runtime-target identity and emit settings.
It parses C# 12, emits deterministic safe release libraries, and never loads generated code. Its contract fingerprint
preserves **all non-method-body source**, a conservative rule stronger than public-API compatibility. Changing a
private declaration, attribute, field initializer or using alias is rejected too. This first compiler intentionally
does not support declaration changes, new files, top-level programs, unsafe code or arbitrary build-system commands.
Coordinated changes to existing method bodies in multiple source files are supported and tested.

Compiler diagnostics expose error IDs and physical spans, not source snippets or mapped `#line` payloads. Public-test
failures feed bounded repair; compile failures, invalid edits and correctness failures consume the attempt allowance.
Provider errors, malformed/unbounded responses, infrastructure failures, cancellation and accounting violations abort
without promotion. A baseline must compile and pass public correctness before any proposal dispatch.

## Promotion and held-out custody

Each artifact binds **all source files + reference/compiler fingerprint + contract fingerprint + exact emitted bytes**.
Both public and final receipts must echo the artifact fingerprint, fresh request nonce and configured oracle identity,
with finite positive durations. Stale/wrong-target/invalid receipts fail closed. Compiler/API changes are rejected
before execution. One eligible candidate receives one baseline/candidate held-out comparison; there is **no transition
back to repair afterward**, even on held-out failure. Promotion requires both correctness results and strict speedup
above `MinimumSpeedup`; equality, regression, failed work and mismatched artifacts do not promote.

This gate verifies receipt consistency, **not the truth of a verifier's assertions**. The caller must provide an
independent trusted oracle, pinned target runtime/dependencies, a statistically justified measurement protocol and
sealed held-out custody. Use the *provided emitted image*, not a rebuild. Do not put sealed tests or secrets into
`MeasuredBottleneck`, source, public feedback or the planner's closure. Separate delegates prevent accidental dataflow,
not a malicious same-process planner from reading memory or files. Production secret custody and hostile execution
must reside in an external service/security boundary. Repeated runs against the same holdout require an independent
evaluation policy; fresh run IDs are not a statistical defense against adaptive holdout reuse.

## Resource and evidence boundaries

Setup, baseline build/test, catalog, every dispatched model/repair/build/test, confirmations and artifact persistence
reserve on the same ledger before work. Positive actual provider/verifier receipts reconcile reservations. Exceptions
without receipts retain the maximum as `Unknown`; returned failures are charged but cannot supply successful values.
Overruns retain full actual cost and fail closed. Synthetic `program_work_units` are not dollars, tokens, CPU limits or
claims about physical usage. Configure common measured prices/caps and make producers enforce their declared maxima;
use an external supervisor for uncooperative model/compiler/worker processes. Roslyn timeout is cooperative, not hard
CPU/memory isolation. The API itself does not select a provider, spend money or invoke a paid model.

Evidence is create-new per run/attempt: configuration, immutable source, hypothesis/edits, successful emitted images,
failed build diagnostics, public receipts, both held-out receipts and final decision. Failure midway leaves prior
evidence and ledger charges intact; it never returns a promoted result. A locked existing run cannot be overwritten or
resumed. The caller's ledger is authoritative; the persisted ledger explicitly excludes its own final persistence
charge, avoiding circular self-accounting. Protect the caller-owned local evidence root against concurrent writers,
symlinks and unauthorized access. Sources, diagnostics and final results are unredacted; use private storage/retention.
Successful evidence writes flush the file but are not a portable directory-fsync or transactional checkpoint guarantee.

## Runnable verification

```powershell
dotnet test tests/AiDotNet.Evolution.CSharp.Tests -c Release -f net10.0
dotnet test tests/AiDotNet.Evolution.CSharp.Tests -c Release -f net8.0
dotnet run --project examples/CompilerGuidedSearch -c Release -f net10.0 -- <new-private-directory>
```

The example deliberately fails public correctness on its first authored multi-file plan, then repairs to the closed
form of integer summation. Separate child processes execute the **same emitted images** used by the gates. A supervisor
oracle checks all outputs; raw evidence retains every input/output, seven timing samples, runtime, artifact identity,
phase and complete final ledger. Seven samples of 64 calls follow 32 warmups; the median is the demonstration metric.
Startup/build time is outside that algorithm metric and work costs remain separately charged. The fixed small fixture
is an integration demonstration, **not representative workload superiority or a statistical release claim**. Timing
regression remains a valid non-promotion outcome rather than making CI depend on an assumed speedup.

The example executes **trusted authored fixtures only**. Process separation, bounded output and a kill timeout are
failure containment, NOT filesystem/network/credential isolation. Do not reuse it to execute arbitrary LLM output.
Production applications must supply an appropriately isolated verifier. Tests use authored fixtures only as well.

## Adversarial review

The review explicitly covers stale parent/node hashes; overlapping/no-op/boundary-escaping patches; multiple files;
source/image aliasing; target/API drift; forged or replayed artifact/oracle/nonce identities; NaN/zero durations;
hidden feedback after rejection; failed receipts disguised as successes; missing usage; budget denial before dispatch;
overruns; bounded repair; duplicate evidence runs; and alternate-compiler substitution. The suite does not pretend
these checks authenticate a hostile same-process provider or replace OS isolation.

The compiler adapts the earlier `CSharpPatchCompiler` method-body catalog and safe emit approach from AiDotNet commit
`66d7602c9`, while replacing its facade/single-file coupling with Evolution-owned compiler-neutral contracts. Original
consumer drafts are preserved; no consumer feature PR is created for US-17.
