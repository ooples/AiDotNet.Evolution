$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/SurrogateSearch/SurrogateSearch.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Surrogate example build failed.' }
dotnet run --project $project -c Release --no-build -- --verify-model
if ($LASTEXITCODE -ne 0) { throw 'Numeric surrogate model checks failed.' }
$first = @(dotnet run --project $project -c Release --no-build -- 2 64) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Surrogate example did not complete.' }
$second = @(dotnet run --project $project -c Release --no-build -- 2 64) -join "`n"
if ($LASTEXITCODE -ne 0 -or $first -cne $second) { throw 'Surrogate example replay differed.' }
$report = $first | ConvertFrom-Json
if ($report.Runs.Count -ne 12) { throw 'Missing scheduled task/method/seed.' }
foreach ($pair in ($report.Runs | Group-Object Task,Seed)) {
    if ($pair.Count -ne 3 -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) { throw 'Starting populations differ.' }
}
foreach ($run in $report.Runs) {
    if ($run.Status -ne 'completed' -or $run.Resources.Spent.cost_units -gt 64 -or $run.Resources.Unknown -ne 0 -or
        $run.Resources.MaximumViolated -or $run.Measured.Count -ne $run.EvaluatorCalls -or $run.EvaluatorCalls -lt 8) { throw 'Invalid run accounting.' }
    $best = ($run.Measured | Measure-Object Loss -Minimum).Minimum
    # Windows PowerShell parses these JSON numbers as Decimal; Measure-Object returns Double.
    # Compare in the evaluator's Double representation, without introducing a tolerance.
    if ([double]$run.FinalLoss -ne [double]$best) { throw 'The archive winner is not the best true measurement.' }
    $known = @{}; foreach ($measurement in $run.Measured) { $known[$measurement.Id] = $true }
    foreach ($decision in $run.Decisions) {
        if ($decision.Evaluated -and -not $known.ContainsKey($decision.Selected)) { throw 'Selection was labeled evaluated without measurement.' }
    }
    $proposalCost = ($run.Resources.Receipts | Where-Object Stage -eq Proposal | ForEach-Object { $_.Charged.Amounts.cost_units } | Measure-Object -Sum).Sum
    if ([Math]::Abs($proposalCost - 0.01 * $run.GeneratedProposals) -gt 1e-8) { throw 'Rejected/generated proposal cost is missing.' }
    if ($run.Method -ne 'SurrogatePool' -and @($run.Resources.Receipts | Where-Object { $_.Stage -in @('SurrogateTraining','SurrogateInference') }).Count -ne 0) {
        throw 'Non-surrogate controls performed hidden model work.'
    }
}
$acquisitions = ($report.Runs | Where-Object Method -eq SurrogatePool | ForEach-Object { $_.ReasonCounts.Acquisition } | Measure-Object -Sum).Sum
if ($acquisitions -le 0) { throw 'The numeric backend never exercised acquisition.' }
Write-Output "Surrogate example verified: 12 paired runs, all-stage synthetic costs, true-measurement-only winners, $acquisitions acquisitions and exact replay."
