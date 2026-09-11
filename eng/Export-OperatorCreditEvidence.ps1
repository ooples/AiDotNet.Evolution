param(
    [Parameter(Mandatory = $true)][string]$InputFile,
    [Parameter(Mandatory = $true)][string]$ReplayFile,
    [Parameter(Mandatory = $true)][string]$SourceRevision,
    [Parameter(Mandatory = $true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
if ($SourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'Require an exact source revision.' }
$inputHash = (Get-FileHash -LiteralPath $InputFile -Algorithm SHA256).Hash.ToLowerInvariant()
$replayHash = (Get-FileHash -LiteralPath $ReplayFile -Algorithm SHA256).Hash.ToLowerInvariant()
if ($inputHash -cne $replayHash) { throw 'Raw campaign replay differs.' }
$source = Get-Content -LiteralPath $InputFile -Raw | ConvertFrom-Json
if ($source.Protocol -ne 'synthetic-operator-credit-example-v1' -or -not $source.AssemblyVersion.EndsWith('+' + $SourceRevision) -or
    $source.Runs.Count -ne 12 * $source.Seeds) { throw 'Source metadata or campaign size is inconsistent.' }
[decimal]$totalCost = 0; [long]$calls = 0
$runs = @($source.Runs | ForEach-Object {
    $run = $_
    if ($run.Status -ne 'completed' -or $run.Resources.Unknown -ne 0 -or $run.Resources.MaximumViolated -or
        $run.Resources.Spent.cost_units -gt $source.CostCap -or $run.Measurements.Count -ne $run.EvaluatorCalls) { throw 'Invalid run evidence.' }
    $totalCost += [decimal]$run.Resources.Spent.cost_units; $calls += $run.EvaluatorCalls
    [ordered]@{ Task=$run.Task; Method=$run.Method; Seed=$run.Seed; Status=$run.Status; StopReason=$run.StopReason;
        InitialPopulationHash=$run.InitialPopulationHash; EvaluatorCalls=$run.EvaluatorCalls; SmallProposals=$run.SmallProposals;
        LargeProposals=$run.LargeProposals; FinalLoss=$run.FinalLoss; Statistics=$run.Statistics; Spent=$run.Resources.Spent;
        Reserved=$run.Resources.Reserved; Admitted=$run.Resources.Admitted; Settled=$run.Resources.Settled;
        Denied=$run.Resources.Denied; Unknown=$run.Resources.Unknown; MaximumViolated=$run.Resources.MaximumViolated;
        DroppedReceipts=$run.Resources.DroppedReceipts; OperatorStateHash=$run.OperatorStateHash }
})
$evidence = [ordered]@{ Protocol='operator-credit-example-evidence-v1'; SourceRevision=$SourceRevision;
    RawSha256=$inputHash; ReplaySha256=$replayHash; RawFileName=(Split-Path -Leaf $InputFile); AssemblyVersion=$source.AssemblyVersion;
    AssemblySha256=$source.AssemblySha256; Seeds=$source.Seeds; CostCap=$source.CostCap; TotalEvaluatorCalls=$calls;
    TotalSyntheticCostUnits=$totalCost; Interpretation=$source.Interpretation;
    Retention='Per-run outcomes, attribution totals and resources retained; full measurements/proposals/receipts stay in the hashed raw local artifacts.'; Runs=$runs }
$json = $evidence | ConvertTo-Json -Depth 20
$stream = [System.IO.File]::Open($OutputFile, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
try { $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false))); $writer.WriteLine($json); $writer.Flush() }
finally { if ($null -ne $writer) { $writer.Dispose() }; $stream.Dispose() }
Write-Output "Exported $($runs.Count) runs, $calls evaluator calls and $totalCost synthetic cost units; raw/replay SHA256 $inputHash."
