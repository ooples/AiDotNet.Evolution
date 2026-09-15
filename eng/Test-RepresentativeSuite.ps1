param(
    [Parameter(Mandatory = $true)][string]$Python,
    [Parameter(Mandatory = $true)][string]$Upstream,
    [Parameter(Mandatory = $true)][string]$SourceRevision,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/AiDotNet.Evolution.Quality.csproj'
$dll = Join-Path $repo 'benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll'
if (-not $NoBuild) {
    dotnet build $project -c Release -p:GeneratePackageOnBuild=false --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Representative suite build failed.' }
}
$runner = Join-Path $repo 'benchmarks/suite/run_suite.py'
& $Python -m unittest discover -s (Join-Path $repo 'benchmarks/suite') -v
if ($LASTEXITCODE -ne 0) { throw 'Suite protocol unit tests failed.' }
$output = Join-Path $repo ('TestResults/representative-suite/' + [Guid]::NewGuid().ToString('N'))
$plan = Join-Path $output 'plan'
& $Python $runner prepare $plan --source-revision $SourceRevision --instances 1 --replicates 1 --budget 16 --contract-smoke
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare contract-smoke plan.' }
$programInstances = 0
$numericRuns = 0
foreach ($partition in @('development', 'selection', 'final')) {
    $destination = Join-Path $output $partition
    & $Python $runner run $plan $partition --numeric-dll $dll --upstream $Upstream --output $destination
    if ($LASTEXITCODE -ne 0) { throw "Suite $partition execution failed; raw output retained at $destination" }
    $report = Get-Content -LiteralPath (Join-Path $destination 'report.json') -Raw | ConvertFrom-Json
    if ($report.mode -ne 'contract-smoke' -or $report.status -ne 'completed' -or $report.numeric.runs.Count -ne 18) {
        throw 'Suite report omitted scheduled runs or mislabeled smoke evidence.'
    }
    foreach ($pair in ($report.numeric.runs | Group-Object task_id, instance_seed, search_seed)) {
        if ($pair.Count -ne 6 -or @($pair.Group.measurement.initial_population_hash | Sort-Object -Unique).Count -ne 1) {
            throw 'Numeric methods did not receive equivalent initial populations.'
        }
    }
    foreach ($run in $report.numeric.runs) {
        if ($run.measurement.status -ne 'completed' -or $run.measurement.evaluator_calls -ne 16 -or
            $run.measurement.resources.spent.cost_units -ne (16 * $run.work_units_per_evaluation)) {
            throw 'Numeric work accounting failed.'
        }
    }
    foreach ($reference in $report.program_references) {
        if ($reference.result.status -ne 'completed' -or $reference.result.solver_calls -ne 4 -or
            $reference.result.validator_calls -ne 4 -or $reference.result.samples.Count -ne 4 -or
            @($reference.result.samples | Where-Object { -not $_.valid }).Count -ne 0) {
            throw 'An AlgoTune starting implementation did not validate.'
        }
    }
    $programInstances += $report.program_references.Count
    $numericRuns += $report.numeric.runs.Count
}
if ($programInstances -ne 11 -or $numericRuns -ne 54) { throw 'The full predeclared panel was not executed.' }
$registered = Join-Path $output 'registered-lifecycle-contract'
& $Python $runner prepare $registered --source-revision $SourceRevision --instances 1 --replicates 1 --budget 16
if ($LASTEXITCODE -ne 0) { throw 'Registered lifecycle fixture preparation failed.' }
$selectionOutput = Join-Path $output 'registered-selection'
& $Python $runner run $registered selection --numeric-dll $dll --upstream $Upstream --output $selectionOutput
if ($LASTEXITCODE -ne 0) { throw 'Registered selection fixture failed.' }
& $Python $runner freeze $registered (Join-Path $repo 'benchmarks/suite/example-configuration.json') (Join-Path $selectionOutput 'report.json')
if ($LASTEXITCODE -ne 0) { throw 'Configuration freeze failed.' }
$finalOutput = Join-Path $output 'registered-final'
& $Python $runner run $registered final --numeric-dll $dll --upstream $Upstream --output $finalOutput
if ($LASTEXITCODE -ne 0) { throw 'Registered final fixture failed.' }
$final = Get-Content -LiteralPath (Join-Path $finalOutput 'report.json') -Raw | ConvertFrom-Json
if ($final.numeric.runs.Count -ne 9 -or @($final.numeric.runs.measurement.method | Sort-Object -Unique).Count -ne 3 -or
    @($final.numeric.runs.measurement.method | Where-Object { $_ -notin @('RandomSearch', 'HillClimb', 'FixedMapElites') }).Count -ne 0) {
    throw 'Final evaluation did not execute exactly the frozen methods.'
}
$finalHash = (Get-FileHash -LiteralPath (Join-Path $finalOutput 'report.json')).Hash
& $Python $runner run $registered final --numeric-dll $dll --upstream $Upstream --output (Join-Path $output 'refused-repeat') 2>&1 |
    Out-File -LiteralPath (Join-Path $output 'refused-repeat.log')
if ($LASTEXITCODE -eq 0) { throw 'The final panel was consumed twice.' }
if ((Get-FileHash -LiteralPath (Join-Path $finalOutput 'report.json')).Hash -ne $finalHash) { throw 'Rejected repeat changed final evidence.' }
Write-Output "Verified 54 smoke numeric runs and 11 pinned AlgoTune starting implementations; registered lifecycle fixture also verified 18 selection and 9 final runs plus one-use refusal. Protocol evidence only: $output"
exit 0
