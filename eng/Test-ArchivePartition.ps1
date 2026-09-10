param([string]$SourceRevision = 'working-tree-smoke')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repo ('TestResults/quality/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$project = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/AiDotNet.Evolution.Quality.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Archive pilot build failed.' }
$first = Join-Path $outputDirectory 'first.json'
$second = Join-Path $outputDirectory 'second.json'
foreach ($reportPath in @($first, $second)) {
    dotnet run --project $project -c Release --no-build -- --archive-partition 2 32 $SourceRevision $reportPath
    if ($LASTEXITCODE -ne 0) { throw 'Archive pilot did not complete every scheduled run.' }
}
if ((Get-FileHash -LiteralPath $first).Hash -ne (Get-FileHash -LiteralPath $second).Hash) { throw 'Archive pilot replay differed.' }
$report = Get-Content -LiteralPath $first -Raw | ConvertFrom-Json
if ($report.Runs.Count -ne 8 -or $report.EliteCapacity -ne 32 -or
    $report.SearchDefinition.DefinitionHash -eq $report.ReferenceDefinition.DefinitionHash) { throw 'Invalid experiment manifest.' }
foreach ($pair in ($report.Runs | Group-Object Task, Seed)) {
    if ($pair.Count -ne 2 -or @($pair.Group.Method | Sort-Object -Unique).Count -ne 2 -or
        @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) { throw 'Unpaired archive cases.' }
}
foreach ($run in $report.Runs) {
    if ($run.Status -ne 'completed' -or $run.EvaluatorCalls -ne 32 -or $run.FinalLoss -lt 0 -or
        $run.OccupiedSearchCells -gt 32 -or $run.OccupiedReferenceCells -gt 32 -or
        $run.ReferenceUtility -lt 0 -or $run.ReferenceUtility -gt 1 -or
        $run.Resources.Spent.cost_units -ne 32 -or $run.Resources.Spent.proposal_calls -ne ($run.Proposals - 8) -or
        $run.Resources.Unknown -ne 0 -or $run.Resources.MaximumViolated -or
        $run.Resources.Reserved.cost_units -ne 0 -or $run.Resources.Reserved.proposal_calls -ne 0 -or
        $run.Samples.Count -ne $run.Proposals -or ($run.Samples | Measure-Object CostUnits -Sum).Sum -ne 32 -or
        ($run.Samples | Measure-Object Attempts -Sum).Sum -ne 32) { throw 'Archive budget, capacity or trace mismatch.' }
    $previous = [double]::PositiveInfinity
    foreach ($sample in $run.Samples) {
        if ($null -eq $sample.BestLoss -or $sample.BestLoss -gt $previous) { throw 'Best-so-far loss regressed.' }
        $previous = $sample.BestLoss
    }
}
Write-Output "Archive pilot verified: eight paired runs, 256 evaluations, common reference geometry and identical replay. Reports: $outputDirectory"
