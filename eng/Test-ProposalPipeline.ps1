param([string] $Python = 'python')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/ProposalPipeline/ProposalPipeline.csproj'
$scratch = Join-Path $repo ('TestResults/proposal-pipeline-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$raw = Join-Path $scratch 'campaign.json'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Proposal pipeline example build failed.' }
$corruption = @(dotnet run --project $project -c Release --no-build -- --verify-replay) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Recorded-response corruption checks failed.' }
$corruptionReport = $corruption | ConvertFrom-Json
if ($corruptionReport.RejectedCases -ne 3 -or $corruptionReport.PhysicalReplayCalls -ne 0) { throw 'Replay corruption evidence is incomplete.' }
dotnet run --project $project -c Release --no-build -- 1 1 $raw
if ($LASTEXITCODE -ne 0) { throw 'Proposal pipeline campaign or replay failed.' }
$report = Get-Content -LiteralPath $raw -Raw | ConvertFrom-Json
if ($report.Protocol -ne 'authored-proposal-pipeline-v1' -or $report.Runs.Count -ne 24 -or $report.WarmupRuns.Count -ne 4 -or
    @($report.WarmupRuns | Where-Object Status -ne completed).Count -ne 0) { throw 'Scheduled or warmup runs are missing/failed.' }
foreach ($row in $report.Runs) {
    $live = $row.Live; $replay = $row.Replay
    if ($row.Status -ne 'completed' -or !$row.ReplayMatches -or $live.Status -ne 'completed' -or $replay.Status -ne 'completed' -or
        $live.StateHash -cne $replay.StateHash -or $live.LogicalEvidenceSha256 -cne $replay.LogicalEvidenceSha256 -or
        $replay.PhysicalProposalCalls -ne 0 -or $replay.PhysicalEvaluationCalls -ne 0 -or
        $replay.ReplayedProposalCalls -ne $live.PhysicalProposalCalls -or $replay.ReplayedEvaluationCalls -ne $live.PhysicalEvaluationCalls -or
        $live.PhysicalProposalCalls -ne 24 -or $live.Counters.Proposals -ne 32 -or $live.PhysicalEvaluationCalls -le 0 -or
        $live.PhysicalEvaluationCalls -gt 32 -or $live.Counters.EvaluationAttempts -ne $live.PhysicalEvaluationCalls -or
        $live.ObservedGenerations.Count -ne 24 -or $live.BestQuality -le 0 -or $live.BestQuality -gt 1) { throw 'Logical replay, response identity or physical work differs.' }
    foreach ($execution in @($live, $replay)) {
        $expected = [decimal]0.08 + [decimal]0.25 * $execution.ProposalResponses.Count + $execution.EvaluationResponses.Count
        if ([decimal]$execution.Resources.Spent.cost_units -ne $expected -or $expected -gt 40 -or
            $execution.Resources.Reserved.cost_units -ne 0 -or $execution.Resources.Unknown -ne 0 -or
            $execution.Resources.MaximumViolated -or $execution.Resources.Admitted -ne $execution.Resources.Settled -or
            $execution.Resources.DroppedReceipts -ne 0) { throw 'Declared work receipts do not reconcile.' }
        if ($null -ne $execution.Pipeline) {
            $workers = if ($row.Method -eq 'PipelineConcurrent') { 4 } else { 1 }
            if ($execution.Pipeline.ProposalWorkers -ne $workers -or $execution.Pipeline.ProposalRunningPeak -gt $workers -or
                $execution.Pipeline.EvaluationRunningPeak -gt 4 -or $execution.Pipeline.ProposalQueuePeak -gt 2 -or
                $execution.Pipeline.EvaluationQueuePeak -gt 2 -or !$execution.Pipeline.IsScheduleComplete -or
                $execution.Pipeline.Schedule.Count -ne 64) { throw 'Queue bounds or complete schedule evidence differs.' }
        }
    }
}
foreach ($pair in ($report.Runs | Group-Object Task, Profile, Seed, TimingRepetition)) {
    if ($pair.Count -ne 4 -or @($pair.Group.Live.InitialPopulationHash | Sort-Object -Unique).Count -ne 1) { throw 'Matched initial cohorts differ.' }
    $reference = ($pair.Group | Where-Object Method -eq Batch).Live.EvaluationResponses | ConvertTo-Json -Depth 10
    foreach ($method in @('PipelineSerial', 'PipelineConcurrent')) {
        $responses = ($pair.Group | Where-Object Method -eq $method).Live.EvaluationResponses | ConvertTo-Json -Depth 10
        if ($reference -cne $responses) { throw 'Fixed-wave pipeline changed the matched batch measurements.' }
    }
}
$source = ($report.AssemblyVersion -split '\+')[-1]
& $Python (Join-Path $repo 'benchmarks/analysis/analyze_pipeline.py') --input $raw --source $source --output-dir (Join-Path $scratch 'analysis')
if ($LASTEXITCODE -ne 0) { throw 'Pipeline failure-inclusive analysis failed.' }
$analysis = Get-Content -LiteralPath (Join-Path $scratch 'analysis/analysis.json') -Raw | ConvertFrom-Json
if ($analysis.Valid -ne 24 -or $analysis.WarmupsValid -ne 4) { throw 'Pipeline analysis did not validate every planned case.' }
Write-Output "Proposal pipeline verified in $scratch`: 24 live cases plus zero-physical-call offline replay, four retained warmups, exact logical evidence, bounded queues, reconciled receipts and three rejected corruptions."
