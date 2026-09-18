$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/SurrogateSearch/SurrogateSearch.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Surrogate example build failed.' }
dotnet run --project $project -c Release --no-build -- --verify-model
if ($LASTEXITCODE -ne 0) { throw 'Numeric surrogate model checks failed.' }
foreach ($scenario in @(@{ Arguments=@('2','64'); Runs=16 }, @{ Arguments=@('--cost-ratios','1','32'); Runs=24 })) {
$scenarioArguments = $scenario.Arguments
$first = @(dotnet run --project $project -c Release --no-build -- @scenarioArguments) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Surrogate example did not complete.' }
$second = @(dotnet run --project $project -c Release --no-build -- @scenarioArguments) -join "`n"
if ($LASTEXITCODE -ne 0 -or $first -cne $second) { throw 'Surrogate example replay differed.' }
$report = $first | ConvertFrom-Json
if ($report.Protocol -ne 'synthetic-surrogate-example-v3-cost-ratios' -or $report.Runs.Count -ne $scenario.Runs) { throw 'Missing scheduled task/method/seed.' }
foreach ($pair in ($report.Runs | Group-Object Task,Seed,EvaluationUnitCost)) {
    if ($pair.Count -ne 4 -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) { throw 'Starting populations differ.' }
}
foreach ($run in $report.Runs) {
    if ([decimal]$run.CostCap -ne [decimal]$report.BaseCostCap * [decimal]$run.EvaluationUnitCost -or
        $run.Status -ne 'completed' -or $run.Resources.Spent.cost_units -gt $run.CostCap -or $run.Resources.Unknown -ne 0 -or
        $run.Resources.Reserved.cost_units -ne 0 -or $run.Resources.Admitted -ne $run.Resources.Settled -or $run.Resources.DroppedReceipts -ne 0 -or
        $run.Resources.MaximumViolated -or $run.Measured.Count -ne $run.EvaluatorCalls -or $run.EvaluatorCalls -lt 8) { throw 'Invalid run accounting.' }
    $best = ($run.Measured | Measure-Object Loss -Minimum).Minimum
    [decimal]$evaluationCharge = 0
    foreach ($receipt in ($run.Resources.Receipts | Where-Object Stage -eq Evaluation)) { $evaluationCharge += [decimal]$receipt.Charged.Amounts.cost_units }
    if ($evaluationCharge -ne [decimal]$run.EvaluatorCalls * [decimal]$run.EvaluationUnitCost) { throw 'Evaluation tariff was not charged exactly.' }
    # Windows PowerShell parses these JSON numbers as Decimal; Measure-Object returns Double.
    # Compare in the evaluator's Double representation, without introducing a tolerance.
    if ([double]$run.FinalLoss -ne [double]$best) { throw 'The archive winner is not the best true measurement.' }
    $known = @{}; foreach ($measurement in $run.Measured) { $known[$measurement.Id] = $true }
    foreach ($decision in $run.Decisions) {
        if ($decision.Evaluated -and -not $known.ContainsKey($decision.Selected)) { throw 'Selection was labeled evaluated without measurement.' }
        if ($run.Method -eq 'ValidatedPool' -and $null -ne $decision.ModelVersionHash -and $decision.Reason -ne 'TrainingFailure') {
            if ($null -eq $decision.ValidationReport) { throw 'Numeric reliability evidence is missing.' }
            if ($decision.Reason -eq 'Acquisition' -and $decision.ValidationReport.Reason -ne 'accepted') { throw 'Unreliable model acquired a candidate.' }
            $receipt = @($run.Resources.Receipts | Where-Object OperationId -eq ("surrogate/" + $decision.OperationIdentity + "/training"))
            $expected = [decimal]0.02 + [decimal]0.000001 * [decimal]$decision.ValidationReport.Metrics.coordinate_work
            if ($receipt.Count -ne 1 -or [decimal]$receipt[0].Charged.Amounts.cost_units -ne $expected) { throw 'Numeric fitting work was not charged exactly.' }
        }
    }
    $proposalCost = ($run.Resources.Receipts | Where-Object Stage -eq Proposal | ForEach-Object { $_.Charged.Amounts.cost_units } | Measure-Object -Sum).Sum
    if ([Math]::Abs($proposalCost - 0.01 * $run.GeneratedProposals) -gt 1e-8) { throw 'Rejected/generated proposal cost is missing.' }
    if ($run.Method -notin @('SurrogatePool','ValidatedPool') -and @($run.Resources.Receipts | Where-Object { $_.Stage -in @('SurrogateTraining','SurrogateInference') }).Count -ne 0) {
        throw 'Non-surrogate controls performed hidden model work.'
    }
}
$acquisitions = ($report.Runs | Where-Object Method -eq SurrogatePool | ForEach-Object { $_.ReasonCounts.Acquisition } | Measure-Object -Sum).Sum
if ($acquisitions -le 0) { throw 'The numeric backend never exercised acquisition.' }
Write-Output "Surrogate example verified: $($scenario.Runs) paired runs, both numeric backends, all-stage tariffs, reliability evidence, true-measurement-only winners, $acquisitions heuristic acquisitions and exact replay."
& (Join-Path $PSScriptRoot 'Test-SurrogateEvidence.ps1') -RawJson $first
}
