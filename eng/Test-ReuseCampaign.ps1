param([string]$SourceRevision = 'working-tree-smoke')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo ('TestResults/reuse-campaign/' + [Guid]::NewGuid().ToString('N'))
dotnet build "$repo/examples/PersistentEvaluation" -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Persistent evaluation campaign build failed.' }
dotnet "$repo/examples/PersistentEvaluation/bin/Release/net10.0/PersistentEvaluation.dll" --campaign 2 $SourceRevision $output
if ($LASTEXITCODE -ne 0) { throw 'Reuse campaign failed; incomplete evidence is retained.' }
python "$repo/benchmarks/analysis/analyze_reuse.py" $output --output "$output/summary.json"
if ($LASTEXITCODE -ne 0) { throw 'Reuse campaign raw evidence, paired priors, accounting or replay failed.' }
Write-Output "Noisy reuse and equivalent-prior contracts passed: $output"
