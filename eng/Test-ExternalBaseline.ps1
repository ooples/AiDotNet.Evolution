param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/AiDotNet.Evolution.Quality.csproj'
$dll = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll'
$directory = Join-Path $repo ('TestResults/quality/external-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'External objective service build failed.' }
& $Python -m unittest discover -s (Join-Path $repo 'benchmarks/external') -v
if ($LASTEXITCODE -ne 0) { throw 'External baseline contract tests failed.' }
$revision = (git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision.' }
$baseline = Join-Path $directory 'baseline.json'
dotnet $dll 2 32 $revision $baseline
if ($LASTEXITCODE -ne 0) { throw 'Core smoke campaign failed.' }
$first = Join-Path $directory 'first.json'
$second = Join-Path $directory 'second.json'
foreach ($output in @($first, $second)) {
    & $Python (Join-Path $repo 'benchmarks/external/scipy_baseline.py') --baseline $baseline --evaluator $dll --output $output --allow-working-tree-smoke
    if ($LASTEXITCODE -ne 0) { throw 'External smoke campaign failed.' }
}
if ((Get-FileHash -LiteralPath $first).Hash -ne (Get-FileHash -LiteralPath $second).Hash) { throw 'External replay differs.' }
$report = Get-Content -LiteralPath $first -Raw | ConvertFrom-Json
$external = @($report.Runs | Where-Object { $_.Method -eq 'ScipyDifferentialEvolutionMatched8' })
if ($report.Runs.Count -ne 56 -or $external.Count -ne 8 -or -not $report.WorkingTreeSmoke) { throw 'Missing scheduled or smoke provenance.' }
foreach ($run in $external) {
    if ($run.Status -ne 'completed' -or $run.EvaluatorCalls -ne 32 -or $run.OptimizerNfev -ne 32 -or
        $run.ControllerDispatches -ne 32 -or $run.IndependentEvaluatorCalls -ne 32 -or $run.UnknownWork -or
        $run.Resources.Spent.cost_units -ne 32 -or $run.Resources.Spent.proposal_calls -ne 24) { throw 'External accounting differs.' }
}
Write-Output "External baseline verified: eight runs / 256 shared C# evaluations and exact replay. Smoke only: $directory"
