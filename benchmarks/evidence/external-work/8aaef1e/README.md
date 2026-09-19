# US-21 schema-2 delivery, live-engine and interop evidence

Runtime source: `8aaef1e40dbbad7b2f3f37addc810b959c7bc96c`, including the published
US-20 cleanup fix `96d245e1bbc822945a459e2b82c4844add5feb5a`. This is authored local
Windows x64 correctness/recovery evidence, not a competitor or optimization-quality trial.

`verification.zip`: **1,162,950 bytes**, SHA-256
`be547da442e220869cb6fa6e35159004976dd4ea0aa498ed2435bdff67ae1e6b`.
It contains managed/native reports, six authoritative schema-2 journals and owner markers,
raw completed-boundary engine checkpoints, build/binding logs, package-byte checks,
passing test/coverage reports, and the earlier failed AOT build and timing-sensitive test.
Native binaries and NuGet packages are not distributed here; their hashes are retained.

```powershell
python benchmarks/evidence/external-work/8aaef1e/verify.py
```

The offline verifier checks source/binary cross-references, every journal checksum and
owner marker, exact operation/lease correspondence, retained liabilities, source-session
fences and checkpoint payload hashes. It never extracts into or opens a live work store.
Checksums detect accidental changes, not hostile tampering or rollback.

## Observed results

| Probe | Result |
| --- | --- |
| Full core tests | 1,000 net10.0 / 1,000 net8.0 / 800 net471; zero failed/skipped after correction. |
| Coverage | 92.96% line / 79.64% branch; unchanged ratchet; generated code excluded as before. |
| Real coordinator/worker crashes | Managed and native each kill three owned children and execute three square evaluations. Coordinator recovery spends 2, reserves 0; lost-worker retry spends 2 and retains the original unresolved reservation of 5. |
| Live engine bridge | Managed and native each execute two square evaluations: one metered delivery and one separate completed-boundary checkpoint probe. Original live session receives quality 49; a new matching engine cannot attach. Delivery ledger spends 2, reserves 0, settles 1; it does not meter the second checkpoint probe. |
| Exact context/ownership | UInt64 maximum root seed preserved; explicit custom-struct ownership required when dynamic code is disabled; completed checkpoint serialization/resume passes. |
| Native builds | Standalone delivery, live-engine bridge and C ABI use strict .NET 10 NativeAOT; all complete with zero warnings. Native IPC host also builds. |
| Worker bindings | 139 TypeScript cases against native IPC; six Python cases against both managed/native IPC; six ctypes cases against actual C ABI exports. No skipped fallback. |
| Package | All three packaged DLLs byte-match their explicit Release target builds. No publication. |

Native SHA-256 identities:

- Delivery executable: `629f664152536b32deccb0a5d63cd9a0fab7f121e975a6a4658bdbaf214cb01f`.
- Live-engine executable: `f1a4229a4be6bc99f09d7e7dd486efe2c44bb94a3a39ae60c67a3d0581389810`.
- C ABI shared library: `9006f72d38957651499a310a4594ef2d549043341291f6da4bec19df94560b0f`.

The first strict live-engine publish failed reflection/AOT analysis. Non-generic DTO
extraction and generated metadata fixed it without warning suppression; three tests compare
the previous and new JSON bytes. The retained full-test failure occurred in a 250 ms
wall-clock retry-fencing test. Explicit Failed/TimedOut result transitions now exercise
both retry-fencing paths; the separate real-timeout queue test remains.

## Reproduce

Check out the pinned runtime revision in a clean worktree. Install its .NET SDKs, Windows
NativeAOT linker, Node dependencies and Python. Use a new output directory for every probe.

```powershell
dotnet build examples/DurableWork -c Release
dotnet examples/DurableWork/bin/Release/net10.0/DurableWork.dll verify TestResults/reproduce-managed-work
dotnet publish examples/DurableWork -c Release -r win-x64 -o TestResults/reproduce-work-native
TestResults/reproduce-work-native/DurableWork.exe verify TestResults/reproduce-native-work
dotnet build examples/DurableSession -c Release
dotnet examples/DurableSession/bin/Release/net10.0/DurableSession.dll TestResults/reproduce-managed-session
dotnet publish examples/DurableSession -c Release -r win-x64 -o TestResults/reproduce-session-native
TestResults/reproduce-session-native/DurableSession.exe TestResults/reproduce-native-session
dotnet publish src/AiDotNet.Evolution.Native -c Release -r win-x64 -o TestResults/reproduce-cabi
python bindings/c/test_native.py TestResults/reproduce-cabi/aidotnet-evolution-native.dll
```

Worker expiry in the crash harness uses a scripted clock; the process kills and evaluator
calls are real. Cost units are authored test units, not measured money or CPU time.
The engine checkpoint probe uses an in-memory checkpoint store at a fully settled boundary;
it is not proof of exact partial-batch engine recovery. An AOT-configured managed host also
disables dynamic code, so it is not an ordinary JIT control for struct ownership.
Process IDs, lease IDs, timestamps and resulting checksums change between executions.
Neither bit-identical native rebuilds nor Linux/macOS results are claimed from this run.
Hosted CI/review and dependency readiness remain separate merge gates.
