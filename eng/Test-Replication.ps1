$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/ReplicatedEvaluation/ReplicatedEvaluation.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Replication example build failed.' }
$first = @(dotnet run --project $project -c Release --no-build -- 16) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Replication example failed.' }
$second = @(dotnet run --project $project -c Release --no-build -- 16) -join "`n"
if ($LASTEXITCODE -ne 0 -or $first -cne $second) { throw 'Replication replay differed.' }
$report = $first | ConvertFrom-Json
if ($report.EvaluatorCalls -ne 64 -or $report.CostUnits -ne 64 -or -not $report.AllSampleIdentitiesUnique -or $report.Reports.Count -ne 4) {
    throw 'Replication counts or accounting differ.'
}
foreach ($batch in $report.Reports) {
    if (-not $batch.IsComplete -or $batch.Samples.Count -ne 16 -or $batch.LowerBound -gt $batch.MeanQuality -or
        $batch.UpperBound -lt $batch.MeanQuality -or $batch.StandardError -le 0 -or $batch.ChargedCostUnits -ne 16) {
        throw 'Invalid replication evidence.'
    }
}
Write-Output 'Replication example verified: 64 fresh dispatches/charges, separate confirmation identities and exact replay.'
