# PR 15 review follow-up proof

Baseline: `82d5483796b2506e88edfbe0f3dc16a7eb3c7647`.

## Findings and boundaries

- Split the fully qualified directory predicate into short, explicit platform checks. Relative paths,
  Windows drive-relative paths, root-relative paths, and filesystem roots remain rejected.
- Use non-discarding filename joining for both temporary and final records. The net471 fallback is
  equivalent concatenation against the canonical, trailing-separator-free directory. Both call sites
  use generated names: a GUID or an immutable SHA-256 key, never an identity supplied as a path.
- Document why cleanup deliberately ignores only I/O and permission failures: cleanup must not mask
  a publication failure or cancellation. Atomic replacement and the primary exception policy are unchanged.
- Make the checkpoint-origin checks individually readable without changing null handling or short-circuit
  behavior. Cache, island archives, global elites, and island histories each require the newer schema.

These were static-analysis maintainability findings; the baseline suite was already green. This is not
presented as a red-to-green repair of six runtime failures.

## Local evidence (2026-09-11)

| Execution | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Baseline, net10.0 | 598 | 0 | 0 |
| Follow-up, net10.0 | 608 | 0 | 0 |
| Follow-up, net8.0 | 608 | 0 | 0 |
| Follow-up, net471 | 608 | 0 | 0 |

The all-target solution build completed with zero warnings and zero errors. Ten added cases exercise:

- Five path-like identities, including traversal, absolute, drive-qualified, and UNC text; both initial
  publication and replacement stay in the owned directory and round-trip the exact record.
- Canonical directory aliases retaining the same capacity boundary.
- Each of the four origin locations independently rejecting legacy schema before codec execution, then
  accepting the same payload with the versioned schema. No other origin location can mask a missing check.

```powershell
dotnet restore AiDotNet.Evolution.slnx --disable-parallel
dotnet build AiDotNet.Evolution.slnx -c Release --no-restore -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($targetFramework in @('net10.0', 'net8.0', 'net471')) {
    dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f $targetFramework --no-build --no-restore `
        --logger "trx;LogFileName=after-$targetFramework.trx" --results-directory artifacts/pr15-review
    if ($LASTEXITCODE -ne 0) { throw "Tests failed on $targetFramework." }
}
```

Local reports are `artifacts/pr15-review/baseline-net10.0.trx` and
`artifacts/pr15-review/after-{net10.0,net8.0,net471}.trx`. CodeQL's hosted rerun is a separate gate;
the local unit tests do not establish its alert status. This follow-up does not claim completion of
the draft PR's broader roadmap.
