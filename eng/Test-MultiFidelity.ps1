$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/MultiFidelitySearch/MultiFidelitySearch.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Multi-fidelity example build failed.' }
$first = @(dotnet run --project $project -c Release --no-build -- 2) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Multi-fidelity example did not complete.' }
$second = @(dotnet run --project $project -c Release --no-build -- 2) -join "`n"
if ($LASTEXITCODE -ne 0 -or $first -cne $second) { throw 'Multi-fidelity replay differed.' }
$report = $first | ConvertFrom-Json
if ($report.Runs.Count -ne 8) { throw 'Missing scheduled run.' }
foreach ($pair in ($report.Runs | Group-Object Task,Seed)) {
    if ($pair.Count -ne 2 -or @($pair.Group.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) { throw 'Initial candidate sets differ.' }
}
foreach ($run in $report.Runs) {
    if ($run.Status -ne 'completed' -or $run.EvaluatorCalls -ne 32 -or $run.ResumedCalls -ne 12 -or
        $run.ConfirmationCalls -ne 4 -or $run.ExecutedSteps -ne 92 -or $run.Report.ChargedCostUnits -ne 92 -or
        [decimal]$run.Report.Resources.Spent.cost_units -ne 92.08d) { throw 'Measurement and actual incremental-step accounting differ.' }
    $samples = @($run.Report.Batches | ForEach-Object { $_.Measurements.Samples })
    if (@($samples.Context.SampleIdentity | Sort-Object -Unique).Count -ne 32) { throw 'Fidelity/replicate/confirmation identities collided.' }
    $confirmed = @($run.Report.Batches | Where-Object Purpose -eq Confirmation)
    if ($confirmed.Count -ne 2 -or @($confirmed | Where-Object { $_.Level.ResourceLevel -ne 9 }).Count -ne 0 -or
        @($confirmed | ForEach-Object ResumedFromSampleIdentities | Where-Object { $null -ne $_ }).Count -ne 0) { throw 'Independent full-fidelity confirmation boundary failed.' }
    if ($run.Method -eq 'GreedyHalving' -and @($run.Report.Promotions | Where-Object IsExploration).Count -ne 0) { throw 'Greedy control performed hidden exploration.' }
    if ($run.Method -eq 'ExploratoryHalving' -and @($run.Report.Promotions | Where-Object IsExploration).Count -ne 2) { throw 'Exploration allocation changed.' }
}
Write-Output 'Multi-fidelity example verified: eight paired runs, distinct fidelity/replicate IDs, incremental resume charges, fresh full confirmation and exact replay.'
