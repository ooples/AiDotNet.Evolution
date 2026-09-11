param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$report = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
$archiveProtocol = $report.SchemaVersion -eq 1 -and $report.Protocol -eq 'archive-partition-development-v1'
$externalProtocol = $report.SchemaVersion -eq 2 -and $report.Protocol -eq 'numeric-development-v4-external'
if (-not $archiveProtocol -and -not $externalProtocol -and ($report.SchemaVersion -ne 2 -or $report.Protocol -ne 'numeric-development-v3-diagonal-cma')) {
    throw 'Unsupported numeric evidence protocol.'
}
$methods = if ($archiveProtocol) { @('SparseGrid', 'FixedCentroid') } else { $report.Methods }
$taskCount = if ($archiveProtocol) { 2 } else { $report.TaskCount }
$expected = $taskCount * $methods.Count * $report.Seeds
if ($report.Runs.Count -ne $expected) { throw 'Scheduled runs are missing; do not export an incomplete campaign.' }
foreach ($pair in ($report.Runs | Group-Object Task, Seed)) {
    if ($pair.Count -ne $methods.Count -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1 -or
        @($pair.Group.Method | Sort-Object -Unique).Count -ne $methods.Count -or
        @($pair.Group | Where-Object { $_.Method -notin $methods }).Count -ne 0) {
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
if ($externalProtocol) {
    foreach ($property in @('BaselineInputSha256', 'ExternalEnvironment', 'ExternalConfiguration', 'EvaluatorBinarySha256', 'WorkingTreeSmoke')) {
        $evidence[$property] = $report.$property
    }
}
if ($archiveProtocol) {
    $evidence.Kind = 'compact-archive-partition-evidence'
    $evidence.Methods = $methods
    $evidence.EliteCapacity = $report.EliteCapacity
    $evidence.SearchDefinition = $report.SearchDefinition
    $evidence.ReferenceDefinition = $report.ReferenceDefinition
    $evidence.Endpoint = $report.Endpoint
    $evidence.Remove('Comparability')
}
if ($archiveProtocol -or $externalProtocol) {
    $evidence.Runs = @($report.Runs | ForEach-Object {
        $row = $_ | Select-Object * -ExcludeProperty Samples
        $row.Resources = $_.Resources | Select-Object * -ExcludeProperty Receipts
        $row | Add-Member -NotePropertyName TrajectoryPoints -NotePropertyValue $_.Samples.Count
        $row
    })
}
$json = ConvertTo-Json -InputObject $evidence -Depth 15
$stream = [System.IO.File]::Open([System.IO.Path]::GetFullPath($OutputPath), [System.IO.FileMode]::CreateNew)
try {
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
    $stream.Write($bytes, 0, $bytes.Length)
} finally { $stream.Dispose() }
Write-Output ("Exported {0} scheduled runs, {1} non-completed; raw trace hash retained." -f $rows.Count, @($report.Runs | Where-Object Status -ne completed).Count)
