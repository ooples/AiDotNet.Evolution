# PR #14 review follow-up: reproducible evidence

## Reviewed baseline

The audited PR head was `72726c059766edaf1d364da6b32c7d5b843d4d6f`.
All 29 inline review threads were already resolved, but the last CodeRabbit
`CHANGES_REQUESTED` review still referred to `f2e21b583406a4ad24e58af05d2507ae803ef7e7`.
Commit `ad74c6870dc1a18abdc8ff61ddef6594f2b7b712` had already replaced the vacuous
variation-exception assertion with separate recoverable/fatal cases and pinned the
host version assertion. A recoverable variation failure must produce its diagnostic,
not change the engine's established exception policy merely to satisfy an outdated
suggestion.

The additional adversarial audit reproduced defects that were still present at the
reviewed head. These fixes do not merely mark review threads resolved.

## Before/after controls

The baseline NativeAOT Windows executable came from the successful
[TypeScript binding run 34548611644](https://github.com/ooples/AiDotNet.Evolution/actions/runs/34548611644),
artifact `evolution-host-win-x64` (artifact ID `10180053627`).
The downloaded executable's SHA-256 is
`1C8DDB241DAECEBB2071177873A0C8CE89FBB95CF6CC8D26334ABD2DD5F34563`.
The baseline artifact was retained separately; it was not overwritten by the new build.

| Regression | Observed baseline result | Required corrected result |
| --- | --- | --- |
| Integral parameter with bounds `[0.1, 2.4]` | The host returned `0.1`. | Every returned value is an integer inside the bounds. |
| Binary integral parameter, default step, seed `1` | Ten proposals evaluated only `1`. | The evaluated set includes both `0` and `1`. |
| Pending ask followed by the tell it needs | No reply before the 3-second request timeout. | Tell succeeds and the pending ask returns a distinct next evaluation. |
| Pending ask followed by close | No reply before the 3-second request timeout. | Close confirms a stop reason; the pending ask is canceled, not reported complete. |
| 22 malformed success-payload cases | All 22 tests failed against the envelope-only client. | Payload validation preserves the subsequent real response or times out and terminates the host. |

The four real-host regression cases above were run together against the baseline
executable: **0 passed, 4 failed, 0 skipped**, with output saved locally in
`artifacts/pr14-native-before.tap`. The same test source is used against the new binary.
The original client additionally crashed on a `null` JSON frame and accepted a string
`ok` field; both have their own malformed-envelope regression tests.

Further deterministic C# cases cover typed conversion errors retaining correlation
IDs, EOF with pending asks, the 32-waiting-ask bound, normalization idempotence, empty
integer domains, large finite midpoints, unrepresentable numeric domains, and transport
read/write failure cleanup. No null-forgiving operators are used in the follow-up.

## Commands

From the repository root:

```powershell
dotnet restore AiDotNet.Evolution.slnx -m:2 -nodeReuse:false
dotnet build AiDotNet.Evolution.slnx -c Release --no-restore -m:2 -nodeReuse:false
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release --no-build --no-restore --logger trx --results-directory artifacts/pr14-tests
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
./eng/Test-ReleaseWorkflow.ps1 -NoRestore
dotnet pack src/AiDotNet.Evolution/AiDotNet.Evolution.csproj -c Release --no-build --no-restore --output artifacts/pr14-package
```

From `bindings/typescript`, with `AIDOTNET_EVOLUTION_HOST` pointing at the executable
being tested (the baseline artifact or the new NativeAOT binary):

```powershell
npm run build
npm run lint
$bindingTests = @(rg --files test -g '*.test.mjs')
node --test --test-reporter=tap @bindingTests
```

Use an explicit file list on Windows for Node 18.17.0, whose test runner does not expand
the wildcard itself. The declared minimum runtime was downloaded from the official
Node distribution and verified against its published SHA-256 sums; no global runtime
settings were changed.

## Local results for the completed follow-up

| Check | Result |
| --- | --- |
| Full Release solution build | 0 warnings, 0 errors. |
| Full test suite, .NET 10 | 446 passed, 0 failed, 0 skipped. |
| Full test suite, .NET 8 | 446 passed, 0 failed, 0 skipped. |
| Full test suite, .NET Framework 4.7.1 | 305 passed, 0 failed, 0 skipped. |
| TypeScript suite, Node 25.6.0 | 55 passed, 0 failed, 0 skipped, actual managed host process. |
| TypeScript suite, declared minimum Node 18.17.0 | 55 passed, 0 failed, 0 skipped, same managed host process. |
| Coverage gate | 89.47% line, 75.13% branch; existing minima 88.80% / 73.51% unchanged. |
| Formatting and TypeScript type checking | Passed. |
| Release workflow security contract | 4 passed, 0 failed, 0 skipped. |
| NuGet pack and package-content/dependency validation | Passed for `0.1.0-preview.1`; nothing published. |

The final .NET reports are in `artifacts/pr14-tests-final`; the binding reports are
`artifacts/pr14-node25-managed-after.tap` and `artifacts/pr14-node18-managed-after.tap`.
All four real-host baseline regressions pass against the new managed host.
This proves the corrected behavior but is **not yet after-fix NativeAOT proof**.

The after-fix managed host was built with:

```powershell
dotnet publish src/AiDotNet.Evolution.Host/AiDotNet.Evolution.Host.csproj -c Release -f net8.0 -r win-x64 --self-contained false -p:PublishAot=false -p:UseAppHost=true --output artifacts/pr14-managed-host
```

Its host DLL SHA-256 is
`BC8D294FBA05228BC8347D6E7900CCEC91945BA795154015197F2CB25A8E1493`.
Its executable launcher SHA-256 is
`DF53F33AE64163CCA44C7A3A5DB72C2E10B3A60DB1DC0D1D37E35CC9B2DF57DD`.
The DLL hash, not the shared runtime launcher alone, identifies the implementation.

## Scope of the evidence

The baseline NativeAOT executable was run locally on Windows x64, not emulated.
After-fix NativeAOT publication is blocked locally by missing MSVC runtime/Windows SDK
libraries. The managed-host publish above is explicitly a separate validation path,
not a change to the product's NativeAOT settings. The PR's Windows, Linux and macOS
native CI jobs must provide the after-fix NativeAOT proof. All real-host binding runs
must report **zero skipped tests**.

Windows standard-input pipe reads can ignore cancellation after starting. The
exceptional shutdown policy therefore requests cancellation and observes a late read
fault without waiting indefinitely for an uncooperative borrowed reader. It does not
claim to forcibly cancel arbitrary operating-system I/O.

No npm/NuGet publication or PR merge is part of this validation.
