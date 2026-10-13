# US-12 (#30) bounded CPU-only development comparison for the scheduled/dispatched release-evidence job.
# The core methods and SciPy differential evolution share one C# evaluator. No model calls, no
# network beyond the pinned pip install, and hard caps on seeds and budget, so a dispatch cannot
# become an unbounded campaign. This is development evidence, never a release-superiority claim:
# claims come only from a pre-registered campaign assessed by benchmarks/analysis/release_gate.py.
param([string]$Python = 'python', [int]$Seeds = 8, [int]$Budget = 128, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
if ($Seeds -lt 2 -or $Seeds -gt 16 -or $Budget -lt 32 -or $Budget -gt 256) {
    throw "Seeds $Seeds / budget $Budget is outside the explicit CPU-only bounds (2..16 seeds, 32..256 evaluations)."
}
$repo = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $repo ('TestResults/release-comparison/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$project = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/AiDotNet.Evolution.Quality.csproj'
$dll = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll'
if (-not $NoBuild) {
    dotnet build $project -c Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Comparison build failed.' }
}
$revision = (git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Source revision unavailable.' }
$core = Join-Path $directory 'core.json'
dotnet $dll $Seeds $Budget $revision $core
if ($LASTEXITCODE -ne 0) { throw "Core campaign failed; partial evidence retained in $directory." }
$external = Join-Path $directory 'comparison.json'
& $Python (Join-Path $repo 'benchmarks/external/scipy_baseline.py') --baseline $core --evaluator $dll --output $external --allow-working-tree-smoke
if ($LASTEXITCODE -ne 0) { throw "External campaign failed; partial evidence retained in $directory." }
$baseline = Get-Content -LiteralPath $core -Raw | ConvertFrom-Json
$report = Get-Content -LiteralPath $external -Raw | ConvertFrom-Json
# Every core method plus the matched SciPy baseline, on every task and seed: nothing leaves the denominator.
$expected = $baseline.TaskCount * ($baseline.Methods.Count + 1) * $Seeds
if ($report.Runs.Count -ne $expected -or -not $report.WorkingTreeSmoke) {
    throw "Expected $expected runs with smoke provenance, found $($report.Runs.Count)."
}
foreach ($run in $report.Runs) {
    if ($run.Status -ne 'completed' -or $run.EvaluatorCalls -ne $Budget) {
        throw "Incomplete run $($run.Task)/$($run.Method)/$($run.Seed); it stays in the evidence and fails the job."
    }
}
Write-Output "CPU development comparison: $($report.Runs.Count) runs, $($report.Runs.Count * $Budget) evaluations. Not a release-superiority claim. Evidence: $directory"