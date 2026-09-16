$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/TypedParameterSearch/TypedParameterSearch.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Typed search example build failed.' }
$first = (dotnet run --project $project -c Release --no-build -- 32) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Typed search example failed.' }
$second = (dotnet run --project $project -c Release --no-build -- 32) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Typed search replay failed.' }
if ($first -ne $second) { throw 'Typed search replay differs.' }
$result = $first | ConvertFrom-Json
if ($result.Kind -ne 'synthetic-api-example' -or $result.EvaluationCostUnits -ne 32 -or
    $result.BestQuality -lt $result.InitialQuality -or [string]::IsNullOrWhiteSpace($result.Genome.schema) -or
    [string]::IsNullOrWhiteSpace($result.StateHash)) { throw 'Typed search result violated its budget or incumbent contract.' }
Write-Output 'Typed search example: valid incumbent, 32 cost units, identical replay.'
