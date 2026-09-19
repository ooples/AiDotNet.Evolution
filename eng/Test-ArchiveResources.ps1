param([string]$SourceRevision = 'working-tree-smoke')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo ('TestResults/archive-resources/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$project = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/AiDotNet.Evolution.Quality.csproj'
$worker = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll'
$runner = Join-Path $repo 'benchmarks/analysis/run_archive_resources.py'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Archive resource worker build failed.' }
python $runner --worker $worker --output "$output/campaign" --revision $SourceRevision --smoke
if ($LASTEXITCODE -ne 0) { throw 'Archive resource smoke/replay failed; all scheduled cases were retained.' }
python $runner --verify "$output/campaign"
if ($LASTEXITCODE -ne 0) { throw 'Raw archive resource evidence did not reproduce its summary.' }

foreach ($method in @('SparseGrid','FixedCentroid')) {
    $file = "$output/low-memory-$method.json"
    dotnet $worker --archive-resource-case Quadratic $method 0 12 8 1 $SourceRevision $file
    if ($LASTEXITCODE -ne 1) { throw 'Expected an observed-memory-budget failure.' }
    $report = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($report.Runs[0].Status -ne 'memory-budget-exceeded' -or $report.Runs[0].EvaluatorCalls -ne 0 -or
        $report.Runs[0].ReferenceUtility -ne 0 -or -not $report.Measurement.MemoryBudgetExceeded) {
        throw 'An observed memory failure must stop admission, retain zero utility and account for physical work.'
    }
}
foreach ($method in @('SparseGrid','FixedCentroid')) {
    $file = "$output/64d-$method.json"
    dotnet $worker --archive-resource-case Quadratic $method 0 64 8 256 $SourceRevision $file
    $expected = if ($method -eq 'SparseGrid') { 1 } else { 0 }
    if ($LASTEXITCODE -ne $expected) { throw 'Unexpected high-dimensional configuration result.' }
    $report = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($method -eq 'SparseGrid' -and ($report.Runs[0].Status -ne 'configuration-failed' -or $report.Runs[0].EvaluatorCalls -ne 0)) {
        throw 'Logical-grid guard must be a retained configuration failure, not a memory/quality comparison.'
    }
    if ($method -eq 'FixedCentroid' -and ($report.Runs[0].EvaluatorCalls -ne 8 -or $report.Runs[0].OccupiedSearchCells -gt 32)) {
        throw 'High-dimensional centroid capacity or accounting mismatch.'
    }
}
Write-Output "Archive resource contracts verified: paired memory/evaluation budgets, raw replay, low-memory admission and separate grid-support failures. Evidence: $output"
