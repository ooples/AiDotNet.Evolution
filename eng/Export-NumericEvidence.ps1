param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$report = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
if ($report.SchemaVersion -ne 2 -or $report.Protocol -ne 'numeric-development-v3-diagonal-cma') {
    throw 'Unsupported numeric evidence protocol.'
}
$expected = $report.TaskCount * $report.Methods.Count * $report.Seeds
if ($report.Runs.Count -ne $expected) { throw 'Scheduled runs are missing; do not export an incomplete campaign.' }
foreach ($pair in ($report.Runs | Group-Object Task, Seed)) {
    if ($pair.Count -ne $report.Methods.Count -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1 -or
        @($pair.Group.Method | Sort-Object -Unique).Count -ne $report.Methods.Count) {
        throw 'Missing or duplicate paired methods, or mismatched starting populations.'
    }
}
$rows = @($report.Runs | ForEach-Object {
    [ordered]@{
        Task = $_.Task; Method = $_.Method; Seed = $_.Seed; InitialPopulationHash = $_.InitialPopulationHash
        Status = $_.Status; Error = $_.Error; EvaluatorCalls = $_.EvaluatorCalls; Proposals = $_.Proposals
        FinalLoss = $_.FinalLoss; MeanBestLoss = $_.MeanBestLoss; OccupiedCells = $_.OccupiedCells; StateHash = $_.StateHash
        Resources = ($_.Resources | Select-Object * -ExcludeProperty Receipts); Cma = $_.Cma
    }
})
$evidence = [ordered]@{
    SchemaVersion = 1; Kind = 'compact-numeric-evidence'; FullTraceSha256 = (Get-FileHash -LiteralPath $InputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Protocol = $report.Protocol; Partition = $report.Partition; SourceRevision = $report.SourceRevision
    Runtime = $report.Runtime; OperatingSystem = $report.OperatingSystem; Seeds = $report.Seeds; Budget = $report.Budget
    Dimensions = $report.Dimensions; InitialPopulation = $report.InitialPopulation; Methods = $report.Methods
    Comparability = $report.Comparability; Limitations = $report.Limitations; Runs = $rows
}
$json = ConvertTo-Json -InputObject $evidence -Depth 15
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputPath), $json + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Output ("Exported {0} scheduled runs, {1} non-completed; raw trace hash retained." -f $rows.Count, @($report.Runs | Where-Object Status -ne completed).Count)
