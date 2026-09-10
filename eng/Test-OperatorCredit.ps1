$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/OperatorCreditSearch/OperatorCreditSearch.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Operator-credit example build failed.' }
$first = @(dotnet run --project $project -c Release --no-build -- 2 64) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Operator-credit example did not complete.' }
$second = @(dotnet run --project $project -c Release --no-build -- 2 64) -join "`n"
if ($LASTEXITCODE -ne 0 -or $first -cne $second) { throw 'Operator-credit example replay differed.' }
$report = $first | ConvertFrom-Json
if ($report.Runs.Count -ne 24) { throw 'Missing scheduled task/method/seed.' }
foreach ($pair in ($report.Runs | Group-Object Task,Seed)) {
    if ($pair.Count -ne 6 -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) { throw 'Starting populations differ.' }
}
foreach ($run in $report.Runs) {
    if ($run.Status -ne 'completed' -or $run.Resources.Spent.cost_units -gt 64 -or $run.Resources.Unknown -ne 0 -or
        $run.Resources.MaximumViolated -or $run.Measurements.Count -ne $run.EvaluatorCalls -or $run.EvaluatorCalls -lt 8) { throw 'Invalid run accounting.' }
    $best = ($run.Measurements | Measure-Object Loss -Minimum).Minimum
    if ([double]$run.FinalLoss -ne [double]$best) { throw 'Winner is not the best actual measurement.' }
    [decimal]$proposal = 0; [decimal]$evaluation = 0; [decimal]$setup = 0
    foreach ($receipt in $run.Resources.Receipts) {
        if ($receipt.Stage -eq 'Proposal') { $proposal += [decimal]$receipt.Charged.Amounts.cost_units }
        elseif ($receipt.Stage -eq 'Evaluation') { $evaluation += [decimal]$receipt.Charged.Amounts.cost_units }
        elseif ($receipt.Stage -eq 'Setup') { $setup += [decimal]$receipt.Charged.Amounts.cost_units }
        else { throw 'Unexpected hidden work stage.' }
    }
    if ($proposal -ne ([decimal]$run.SmallProposals / 10 + [decimal]$run.LargeProposals) -or $evaluation -ne $run.EvaluatorCalls -or
        $setup -ne [decimal]0.08 -or ($proposal + $evaluation + $setup) -ne [decimal]$run.Resources.Spent.cost_units) { throw 'All-stage costs do not reconcile.' }
    foreach ($stat in $run.Statistics) {
        if ($stat.Outcomes -ne $stat.Proposals -or $stat.RewardSum -lt 0 -or $stat.RewardSum -gt $stat.Outcomes) { throw 'Invalid operator attribution or reward.' }
    }
    if ($run.Method -eq 'StaticSmall' -and $run.LargeProposals -ne 0) { throw 'Static small control used another operator.' }
    if ($run.Method -eq 'StaticLarge' -and $run.SmallProposals -ne 0) { throw 'Static large control used another operator.' }
}
Write-Output 'Operator-credit example verified: 24 paired runs, six policies/static controls, proposal-plus-evaluator costs, measured winners and exact replay.'
