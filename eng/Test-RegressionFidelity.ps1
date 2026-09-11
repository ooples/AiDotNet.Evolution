$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/MultiFidelitySearch/MultiFidelitySearch.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Regression fidelity example build failed.' }
dotnet run --project $project -c Release --no-build -- --verify-training
if ($LASTEXITCODE -ne 0) { throw 'Actual learning/token checks failed.' }
$first = @(dotnet run --project $project -c Release --no-build -- --regression 2) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Regression fidelity campaign failed.' }
$second = @(dotnet run --project $project -c Release --no-build -- --regression 2) -join "`n"
if ($LASTEXITCODE -ne 0 -or $first -cne $second) { throw 'Regression fidelity replay differed.' }
$report = $first | ConvertFrom-Json
if ($report.Protocol -ne 'trained-regression-fidelity-v1' -or $report.Runs.Count -ne 8) { throw 'Missing scheduled regression run.' }
foreach ($pair in ($report.Runs | Group-Object Seed)) {
    if ($pair.Count -ne 4 -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) { throw 'Regression cohorts differ.' }
    $resumed = $pair.Group | Where-Object Method -eq GreedyHalving
    $restarted = $pair.Group | Where-Object Method -eq RestartOnlyHalving
    if ($resumed.BestConfirmedQuality -ne $restarted.BestConfirmedQuality -or $resumed.BestConfirmedGenome -ne $restarted.BestConfirmedGenome -or
        $resumed.ExecutedEpochs -ge $restarted.ExecutedEpochs) { throw 'Incremental continuation changed the trained result or failed to save repeated training.' }
}
foreach ($run in $report.Runs) {
    $full = $run.Method -eq 'FullCohort'; $restart = $run.Method -eq 'RestartOnlyHalving'
    $calls = if ($full) { 64 } else { 32 }
    $epochs = if ($full) { 2048 } elseif ($restart) { 704 } else { 608 }
    $confirmed = if ($full) { 16 } else { 4 }
    $resumed = if ($full) { 32 } elseif ($restart) { 0 } else { 12 }
    if ($run.Status -ne 'completed' -or $run.EvaluatorCalls -ne $calls -or $run.ExecutedEpochs -ne $epochs -or
        $run.ConfirmationCalls -ne $confirmed -or $run.ResumedCalls -ne $resumed -or
        $run.TrainingRowVisits -ne $epochs * 128 -or $run.ValidationRowVisits -ne $calls * 64 -or
        [decimal]$run.Report.Resources.Spent.cost_units -ne [decimal]$epochs + [decimal]0.25 * $calls + [decimal]0.08 -or
        $run.Report.Resources.Unknown -ne 0 -or $run.Report.Resources.Admitted -ne $run.Report.Resources.Settled) { throw 'Training/validation work accounting differs.' }
    if (@($run.Measurements.SampleIdentity | Sort-Object -Unique).Count -ne $calls) { throw 'Fidelity/replicate identity collision.' }
    $searchData = @($run.Measurements | Where-Object Purpose -eq Search | ForEach-Object DataIdentity | Sort-Object -Unique)
    $confirmData = @($run.Measurements | Where-Object Purpose -eq Confirmation | ForEach-Object DataIdentity | Sort-Object -Unique)
    if (@($searchData | Where-Object { $_ -in $confirmData }).Count -ne 0) { throw 'Confirmation reused search data.' }
    foreach ($measurement in $run.Measurements) {
        if ($measurement.MeanSquaredError -lt 0 -or [Math]::Abs([double]$measurement.Quality - 1 / (1 + [double]$measurement.MeanSquaredError)) -gt 1e-15) {
            throw 'Reported quality does not reflect actual held-out prediction error.'
        }
        if ($measurement.Purpose -eq 'Confirmation' -and ($null -ne $measurement.ResumedFrom -or $measurement.ActualEpochs -ne 64)) {
            throw 'Full-fidelity confirmation did not retrain independently.'
        }
    }
}
Write-Output 'Regression fidelity verified: eight paired real-training runs, exact replay, token integrity, actual epoch/row counts, costed resume savings and independent full retraining.'
