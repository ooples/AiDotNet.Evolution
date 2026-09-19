# PR #57 checkpoint-validation review proof

## Scope and reviewed baseline

- PR: https://github.com/ooples/AiDotNet.Evolution/pull/57
- Expected remote branch: `feat/evolution-us-15`; base: `feat/competitive-evolution-platform`.
- Baseline and last verified remote head: `dc8b6186896eefa23157d49fdc2eebf30b00ed34`.
- Local verification date: 2026-09-11, Windows, .NET SDK 10.0.401.
- All review-thread and comment pages were read; every `hasNextPage` was false.
- No public API, checkpoint format/version/hash, charging policy, GPU/device policy,
  scheduling budget, assertion tolerance, or skip policy changes.

The four actual baseline failures are CodeQL maintainability findings, not newly
demonstrated behavioral defects. Their analysis used merge commit
`c16d9a53a73b74593117ad6a512055ee7a64e67e`, category `/language:csharp`.
No alert was suppressed or resolved during this local work.

| Alert / thread | Validation change |
| --- | --- |
| [98](https://github.com/ooples/AiDotNet.Evolution/security/code-scanning/98), `PRRT_kwDOUOzYOs6hlTSv` | Name completion and confirmation policies independently. |
| [99](https://github.com/ooples/AiDotNet.Evolution/security/code-scanning/99), `PRRT_kwDOUOzYOs6hlTS_` | Validate required sample metadata before nullable numeric values. |
| [100](https://github.com/ooples/AiDotNet.Evolution/security/code-scanning/100), `PRRT_kwDOUOzYOs6hlTTF` | Match receipt metadata first, then handle unknown and known costs separately; guard decimal conversion with finite/nonnegative/range checks. |
| [101](https://github.com/ooples/AiDotNet.Evolution/security/code-scanning/101), `PRRT_kwDOUOzYOs6hlTTK` | Validate accepted quality and origin before accumulating moments; use a validated typed quality value instead of null-forgiving access. |

All restored batches, moments and continuation slots remain local until validation
returns. A rejected checkpoint does not dispatch an evaluator, call a checkpoint
sink, reserve resources, or change the ledger.

## Before, negative control, and after

| Verification | Result |
| --- | --- |
| Baseline production + 30 new behavioral controls, net10 | 61 passed, 0 failed, 0 skipped, including 31 existing checkpoint tests |
| First refactored fidelity cohort + valid-origin positive control, net10 | 75 passed, 0 failed, 0 skipped |
| Deliberately remove only the positive-double/zero-decimal guard | Exactly 1 failed (`ReportedCostUnderflowsDecimal`), 25 other corruption cases passed |
| Restore guard; full test project, net10 | 741 passed, 0 failed, 0 skipped |
| Restore guard; full test project, net8 | 741 passed, 0 failed, 0 skipped |
| Restore guard; full test project, net471 | 741 passed, 0 failed, 0 skipped |

Final full runs build the actual project for each framework with warnings treated
as errors and no build/test failure; test execution was approximately 9, 9 and 17
seconds respectively. These are 741 tests repeated on three frameworks, not 2,223
distinct tests. The baseline behavioral controls correctly passed before the
refactor: they constrain behavior preservation, rather than manufacturing a
behavior-failure claim for a readability finding.

The 26 typed corruption modes forge and rechecksum the **last** batch after four
valid restored batches. They cover completion/confirmation restrictions, required
sample fields, receipt mismatches, missing/out-of-range quality, bounded numeric
conversion, and wrong/reused/multiple/inconsistent-scope measurement origins.
Each verifies `ArgumentException` with `ParamName == "checkpoint"`, zero evaluator
calls, zero checkpoint writes, and byte-for-byte unchanged ledger state. The same
scheduler and ledger then retry the original checkpoint and must exactly match
an uninterrupted run's report, charges and total evaluator calls.

Four positive cases preserve inclusive quality endpoints and zero/maximum charges.
A separate positive case restores valid measured origins, preserves their exact
JSON, dispatches only the two remaining evaluations, and finishes with 12 settled
receipts / 12 cost units. Thus rejecting every non-null origin cannot satisfy the
test suite.

The underflow mutation replaces:
```csharp
if ((reportedCost > 0 && reportedCharge == 0) || reportedCharge != sample.Charged)
```
with:
```csharp
if (reportedCharge != sample.Charged)
```
The regression uses a genuine zero-charge receipt and a forged reported
`double.Epsilon`. Without the underflow guard that positive double becomes a
valid-looking decimal zero and the expected rejection disappears. The exact guard
was restored before every final full-framework run.

## Local artifacts and tested binary identity

All paths below are relative to this isolated worktree:

- `artifacts/pr57-review-proof/baseline/pr57-baseline-checkpoint-validation.trx`
- `artifacts/pr57-review-proof/negative-control/pr57-underflow-guard-negative-control.trx`
- `artifacts/pr57-review-proof/final/pr57-final-fidelity-net10.trx`
- `artifacts/pr57-review-proof/final/pr57-final-full-net10.0.trx`
- `artifacts/pr57-review-proof/final/pr57-final-full-net8.0.trx`
- `artifacts/pr57-review-proof/final/pr57-final-full-net471.trx`

TRX counters and individual failed cases were inspected; no skipped cases were
hidden by missing counter attributes. Final DLLs are in
`tests/AiDotNet.Evolution.Tests/bin/Release/<framework>/`.

| Framework | SHA-256 AiDotNet.Evolution.dll | SHA-256 AiDotNet.Evolution.Tests.dll |
| --- | --- | --- |
| net10.0 | `B295C7C6F951122CB363FD3885D7A573F2D899248649067113E243B002F9EBA1` | `EA3FF7BE1B928A458BBAB4551DF1A193DD6DFE87E4F517036920F3C9C78DD37B` |
| net8.0 | `E51A922A9FE1043128F713D0912B8C132C53635159ABDCE498ED093B999D32C7` | `8274F373916925C08F237F0B110550D1D318ED707D4AA793BA1E6910FA7F9279` |
| net471 | `B166DFA62D29FFF637090DE4449234B55E93927E7603B32740502B246BEC5A29` | `ABF0C5280961B87483EC24638E74DF57EDFF5E1016B909AAE90C4038F9FC1B0F` |

## Reproduce actual build/test evidence

Run from this worktree in PowerShell; stop on any nonzero exit code. The original
baseline run omitted `--no-restore` and filtered `FullyQualifiedName~EvolutionFidelityCheckpoint`.
The mutation run filtered the new `RechecksummedInvalidLastBatch` method and
required exactly the one expected failing case before restoring the guard.

```powershell
$ErrorActionPreference = 'Stop'
dotnet restore tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
foreach ($framework in @('net10.0', 'net8.0', 'net471')) {
    dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f $framework --no-restore -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false -p:GeneratePackageOnBuild=false --logger "trx;LogFileName=pr57-final-full-$framework.trx" --results-directory artifacts/pr57-review-proof/reproduction -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "Build or full $framework tests failed." }
}
```

## Complexity verification and its limit

The [official CodeQL query](https://github.com/github/codeql/blob/51673312c5e16f17cc83943c70b0e3c16dabb928/csharp/ql/src/Complexity/ComplexCondition.ql)
counts changes between logical operator groups, not merely expression length;
more than three groups triggers this finding. A bounded local Roslyn syntax audit
of this file's `&&`, `||`, and `!` expressions reproduced the same four baseline
locations: lines 78 / 98 / 107 / 121 had 5 / 4 / 5 / 5 groups. The final file has
zero expressions above three groups and a maximum of two.

This local syntax audit is **not a full CodeQL analysis**. The CodeQL CLI is not
installed here; fresh hosted CodeQL and independent review remain required before
claiming the four remote alerts are closed. The audit is reproduced below using
PowerShell's compatible Roslyn assemblies (loading the different SDK Roslyn build
into that process causes an assembly-version conflict):

```powershell
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
function Get-LogicalGroupCount($expression, [Microsoft.CodeAnalysis.CSharp.SyntaxKind]$parentKind = [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::None) {
    while ($expression -is [Microsoft.CodeAnalysis.CSharp.Syntax.ParenthesizedExpressionSyntax]) { $expression = $expression.Expression }
    $kind = [Microsoft.CodeAnalysis.CSharp.SyntaxKind]$expression.RawKind
    if ($kind -eq [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::LogicalAndExpression -or $kind -eq [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::LogicalOrExpression) {
        return [int]($kind -ne $parentKind) + (Get-LogicalGroupCount $expression.Left $kind) + (Get-LogicalGroupCount $expression.Right $kind)
    }
    if ($kind -eq [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::LogicalNotExpression) { return Get-LogicalGroupCount $expression.Operand }
    return 0
}
$sourcePath = 'src/AiDotNet.Evolution/Core/EvolutionFidelityScheduler.Checkpoints.cs'
$baselineText = (git show "dc8b6186896eefa23157d49fdc2eebf30b00ed34:$sourcePath") -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Baseline source extraction failed.' }
foreach ($source in @([pscustomobject]@{ Name = 'baseline'; Text = $baselineText; Expected = 4 }, [pscustomobject]@{ Name = 'final'; Text = (Get-Content -LiteralPath $sourcePath -Raw); Expected = 0 })) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source.Text)
    $syntaxRoot = $tree.GetRoot()
    $overThreshold = @()
    $maximum = 0
    foreach ($node in $syntaxRoot.DescendantNodes()) {
        if ($node -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax]) { continue }
        if ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.ParenthesizedExpressionSyntax]) { continue }
        $parent = $node.Parent
        while ($parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.ParenthesizedExpressionSyntax]) { $parent = $parent.Parent }
        $parentKind = [Microsoft.CodeAnalysis.CSharp.SyntaxKind]$parent.RawKind
        if ($parentKind -in @([Microsoft.CodeAnalysis.CSharp.SyntaxKind]::LogicalAndExpression, [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::LogicalOrExpression, [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::LogicalNotExpression)) { continue }
        $groups = Get-LogicalGroupCount $node
        $maximum = [Math]::Max($maximum, $groups)
        if ($groups -gt 3) { $overThreshold += [pscustomobject]@{ Line = $node.GetLocation().GetLineSpan().StartLinePosition.Line + 1; LogicalGroups = $groups } }
    }
    Write-Output "$($source.Name): $($overThreshold.Count) expressions exceed the official query's 3-group threshold; maximum $maximum."
    $overThreshold | Format-Table -AutoSize
    if ($overThreshold.Count -ne $source.Expected) { throw 'Unexpected checkpoint logical-complexity count.' }
}
```

