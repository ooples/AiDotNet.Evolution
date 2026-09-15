param([string]$Python = 'python', [int]$Seeds = 8, [int]$Budget = 128, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
if ($Seeds -lt 2 -or $Seeds -gt 16 -or $Budget -lt 32 -or $Budget -gt 256) { throw 'Budget outside explicit CPU-only campaign bounds.' }
$repo = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $repo ('TestResults/release-comparison/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$project = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/AiDotNet.Evolution.Quality.csproj'
$dll = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll'
if (!$NoBuild) {
    dotnet build $project -c Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Comparison build failed.' }
}
$revision = (git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Source revision unavailable.' }
$core = Join-Path $directory 'core.json'
dotnet $dll $Seeds $Budget $revision $core
if ($LASTEXITCODE -ne 0) { throw 'Core campaign failed; retain partial evidence.' }
$external = Join-Path $directory 'comparison.json'
& $Python (Join-Path $repo 'benchmarks/external/scipy_baseline.py') --baseline $core --evaluator $dll --output $external --allow-working-tree-smoke
if ($LASTEXITCODE -ne 0) { throw 'External campaign failed; retain partial evidence.' }
$report = Get-Content -LiteralPath $external -Raw | ConvertFrom-Json
if ($report.Runs.Count -ne 28 * $Seeds -or !$report.WorkingTreeSmoke) { throw 'Incomplete campaign or changed evidence classification.' }
foreach ($run in $report.Runs) {
    if ($run.Status -ne 'completed' -or $run.EvaluatorCalls -ne $Budget) { throw 'Incomplete run; do not remove it from the denominator.' }
}
Write-Output "CPU development comparison: $($report.Runs.Count) runs, $($report.Runs.Count * $Budget) evaluations. Not a release-superiority claim. Evidence: $directory"
