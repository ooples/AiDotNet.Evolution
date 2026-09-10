param([string]$SourceRevision = 'working-tree')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repo ('TestResults/quality/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$project = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/AiDotNet.Evolution.Quality.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Quality harness build failed.' }
$first = Join-Path $outputDirectory 'first.json'
$second = Join-Path $outputDirectory 'second.json'
foreach ($reportPath in @($first, $second)) {
    dotnet run --project $project -c Release --no-build -- 2 32 $SourceRevision $reportPath
    if ($LASTEXITCODE -ne 0) { throw 'Quality harness did not complete every scheduled run.' }
}
if ((Get-FileHash -LiteralPath $first).Hash -ne (Get-FileHash -LiteralPath $second).Hash) {
    throw 'Repeated quality experiments produced different semantic records.'
}
$report = Get-Content -LiteralPath $first -Raw | ConvertFrom-Json
if ($report.Runs.Count -ne 32) { throw 'The report omitted a scheduled task/method/seed.' }
foreach ($pair in ($report.Runs | Group-Object Task, Seed)) {
    if ($pair.Count -ne 4 -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) {
        throw 'Methods did not share their complete initial population.'
    }
}
foreach ($run in $report.Runs) {
    if ($run.Status -ne 'completed' -or $run.EvaluatorCalls -ne 32 -or $run.FinalLoss -lt 0 -or
        $run.Samples.Count -ne $run.Proposals -or ($run.Samples | Measure-Object CostUnits -Sum).Sum -ne 32) {
        throw 'The report violated its evaluator budget or trace contract.'
    }
    $previous = [double]::PositiveInfinity
    foreach ($sample in $run.Samples) {
        if ($null -eq $sample.BestLoss -or $sample.BestLoss -gt $previous) { throw 'Best-so-far loss regressed.' }
        $previous = $sample.BestLoss
    }
}
Write-Output "Quality harness verified: 32 paired runs, 1,024 evaluations, identical replay. Reports: $outputDirectory"
