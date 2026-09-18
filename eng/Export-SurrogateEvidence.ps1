param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$RawGzipPath
)
$ErrorActionPreference = 'Stop'
if ((Get-Item -LiteralPath $InputPath).Length -gt 64MB) { throw 'Surrogate evidence exceeds its input bound.' }
$report = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
if ($report.Protocol -notin @('synthetic-surrogate-example-v1','synthetic-surrogate-example-v2-validated-backend','synthetic-surrogate-example-v3-cost-ratios') -or
    $report.Seeds -lt 1 -or $report.Seeds -gt 20 -or $report.AssemblyVersion -notmatch '\+([0-9a-f]{40})$') { throw 'Invalid pinned surrogate example campaign.' }
$revision = $Matches[1]
$methods = @('Ordinary','UniformPool','SurrogatePool')
if ($report.Protocol -ne 'synthetic-surrogate-example-v1') { $methods += 'ValidatedPool' }
$ratios = $report.Protocol -eq 'synthetic-surrogate-example-v3-cost-ratios'
$baseCap = if ($ratios) { [decimal]$report.BaseCostCap } else { [decimal]$report.CostCap }
$prices = if ($ratios) { @($report.EvaluationUnitCosts | ForEach-Object { [decimal]$_ }) } else { @([decimal]1) }
if ($baseCap -lt 16 -or $baseCap -gt 256 -or $prices.Count -notin @(1,3) -or
    @($prices | Where-Object { $_ -notin @([decimal]0.1,[decimal]1,[decimal]10) }).Count -ne 0 -or
    @($prices | Sort-Object -Unique).Count -ne $prices.Count -or
    $report.Runs.Count -ne 2 * $methods.Count * $report.Seeds * $prices.Count -or
    ($ratios -and (($report.CostRatios -eq $true -and $prices.Count -ne 3) -or ($report.CostRatios -ne $true -and ($prices.Count -ne 1 -or $prices[0] -ne 1))))) {
    throw 'Invalid tariff, budget or scheduled run count.'
}
foreach ($run in $report.Runs) {
    if ($run.Task -notin @('ShiftedQuadratic4','RippledQuadratic4') -or $run.Seed -lt 0 -or $run.Seed -ge $report.Seeds -or
        ($ratios -and ([decimal]$run.EvaluationUnitCost -notin $prices -or [decimal]$run.CostCap -ne $baseCap * [decimal]$run.EvaluationUnitCost))) {
        throw 'Unplanned task, seed or run tariff.'
    }
}
foreach ($pair in ($report.Runs | Group-Object Task,Seed,EvaluationUnitCost)) {
    if ($pair.Count -ne $methods.Count -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1 -or
        @($pair.Group.Method | Sort-Object -Unique).Count -ne $methods.Count -or @($pair.Group | Where-Object { $_.Method -notin $methods }).Count -ne 0) {
        throw 'Missing, duplicate or mismatched paired runs.'
    }
}
$rows = @($report.Runs | ForEach-Object {
    $run = $_
    $stages = [ordered]@{}
    foreach ($stage in ($run.Resources.Receipts | Group-Object Stage)) {
        [decimal]$total = 0
        foreach ($receipt in $stage.Group) { $total += [decimal]$receipt.Charged.Amounts.cost_units }
        $stages[$stage.Name] = $total
    }
    [ordered]@{
        Task=$run.Task; Method=$run.Method; Seed=$run.Seed; Status=$run.Status; StopReason=$run.StopReason
        EvaluationUnitCost=$(if ($ratios) { $run.EvaluationUnitCost } else { 1 }); CostCap=$(if ($ratios) { $run.CostCap } else { $baseCap })
        InitialPopulationHash=$run.InitialPopulationHash; EvaluatorCalls=$run.EvaluatorCalls; GeneratedProposals=$run.GeneratedProposals
        FinalLoss=$run.FinalLoss; ReasonCounts=$run.ReasonCounts; StageCostUnits=$stages
        Resources=($run.Resources | Select-Object * -ExcludeProperty Receipts)
        MeasurementCount=$run.Measured.Count; DecisionCount=$run.Decisions.Count
        UnmeasuredSelections=@($run.Decisions | Where-Object { -not $_.Evaluated }).Count
        ReliabilityReasons=@($run.Decisions | Where-Object { $null -ne $_.ValidationReport } | Group-Object { $_.ValidationReport.Reason } | Select-Object Name,Count)
    }
})
$rawGzipHash = $null
if (-not [string]::IsNullOrWhiteSpace($RawGzipPath)) {
    if ([System.IO.Path]::GetFullPath($RawGzipPath) -eq [System.IO.Path]::GetFullPath($OutputPath)) { throw 'Raw and summary outputs must differ.' }
    # Preserve all decisions, measurements and receipts, not only successful rows or aggregate statistics.
    $source = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($InputPath))
    try {
        $destination = [System.IO.File]::Open([System.IO.Path]::GetFullPath($RawGzipPath), [System.IO.FileMode]::CreateNew)
        try {
            $gzip = [System.IO.Compression.GZipStream]::new($destination, [System.IO.Compression.CompressionLevel]::Optimal, $true)
            try { $source.CopyTo($gzip) } finally { $gzip.Dispose() }
        } finally { $destination.Dispose() }
    } finally { $source.Dispose() }
    $rawGzipHash = (Get-FileHash -LiteralPath $RawGzipPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$evidence = [ordered]@{
    SchemaVersion=2; Kind='compact-surrogate-example-evidence'; Protocol=$report.Protocol; SourceRevision=$revision
    AssemblyVersion=$report.AssemblyVersion; AssemblySha256=$report.AssemblySha256
    FullTraceSha256=(Get-FileHash -LiteralPath $InputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    RawGzipSha256=$rawGzipHash
    Seeds=$report.Seeds; BaseCostCap=$baseCap; EvaluationUnitCosts=$prices; Interpretation=$report.Interpretation; Runs=$rows
}
$stream = [System.IO.File]::Open([System.IO.Path]::GetFullPath($OutputPath), [System.IO.FileMode]::CreateNew)
try {
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $evidence -Depth 12) + "`n")
    $stream.Write($bytes, 0, $bytes.Length)
} finally { $stream.Dispose() }
Write-Output ("Exported {0} scheduled surrogate-example runs with raw trace hash and stage charges." -f $rows.Count)
