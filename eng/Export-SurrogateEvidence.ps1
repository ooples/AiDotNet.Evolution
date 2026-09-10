param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ((Get-Item -LiteralPath $InputPath).Length -gt 64MB) { throw 'Surrogate evidence exceeds its input bound.' }
$report = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
if ($report.Protocol -ne 'synthetic-surrogate-example-v1' -or $report.Seeds -lt 1 -or $report.Seeds -gt 20 -or
    $report.CostCap -lt 16 -or $report.CostCap -gt 256 -or $report.Runs.Count -ne 6 * $report.Seeds -or
    $report.AssemblyVersion -notmatch '\+([0-9a-f]{40})$') { throw 'Invalid pinned surrogate example campaign.' }
$revision = $Matches[1]
$methods = @('Ordinary','UniformPool','SurrogatePool')
foreach ($pair in ($report.Runs | Group-Object Task,Seed)) {
    if ($pair.Count -ne 3 -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1 -or
        @($pair.Group.Method | Sort-Object -Unique).Count -ne 3 -or @($pair.Group | Where-Object { $_.Method -notin $methods }).Count -ne 0) {
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
        InitialPopulationHash=$run.InitialPopulationHash; EvaluatorCalls=$run.EvaluatorCalls; GeneratedProposals=$run.GeneratedProposals
        FinalLoss=$run.FinalLoss; ReasonCounts=$run.ReasonCounts; StageCostUnits=$stages
        Resources=($run.Resources | Select-Object * -ExcludeProperty Receipts)
        MeasurementCount=$run.Measured.Count; DecisionCount=$run.Decisions.Count
        UnmeasuredSelections=@($run.Decisions | Where-Object { -not $_.Evaluated }).Count
    }
})
$evidence = [ordered]@{
    SchemaVersion=1; Kind='compact-surrogate-example-evidence'; Protocol=$report.Protocol; SourceRevision=$revision
    AssemblyVersion=$report.AssemblyVersion; AssemblySha256=$report.AssemblySha256
    FullTraceSha256=(Get-FileHash -LiteralPath $InputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Seeds=$report.Seeds; CostCap=$report.CostCap; Interpretation=$report.Interpretation; Runs=$rows
}
$stream = [System.IO.File]::Open([System.IO.Path]::GetFullPath($OutputPath), [System.IO.FileMode]::CreateNew)
try {
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $evidence -Depth 12) + "`n")
    $stream.Write($bytes, 0, $bytes.Length)
} finally { $stream.Dispose() }
Write-Output ("Exported {0} scheduled surrogate-example runs with raw trace hash and stage charges." -f $rows.Count)
