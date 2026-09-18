param([string]$SourceRevision = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceRevision)) { $SourceRevision = (& git -C $repo rev-parse HEAD).Trim() }

$evidencePath = Join-Path $repo 'benchmarks/evidence/pareto/b097034.json'
$outputDirectory = Join-Path $repo ('TestResults/pareto/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$reportPath = Join-Path $outputDirectory 'campaign.json'

dotnet run --project (Join-Path $repo 'examples/ParetoSearch/ParetoSearch.csproj') -c Release -- $reportPath $SourceRevision
if ($LASTEXITCODE -ne 0) { throw 'The matched-budget Pareto campaign did not complete.' }

$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json

# The declared revision is an argument; the embedded one is what the engine was actually built from. The campaign
# itself refuses a mismatch, and this asserts the report carries the proof rather than only the claim.
if ([string]::IsNullOrWhiteSpace($report.EmbeddedCommit)) { throw 'The campaign report carries no embedded build commit.' }
if ($report.EmbeddedCommit -ne $report.SourceCommit) {
    throw "Campaign evidence records source commit $($report.SourceCommit) but was built from $($report.EmbeddedCommit)."
}
if ($report.SourceCommit -ne $SourceRevision) {
    throw "Campaign evidence records source commit $($report.SourceCommit) instead of the requested $SourceRevision."
}

foreach ($field in 'Schema', 'DefinitionHash', 'EvaluationBudget', 'SeedCount', 'CostUnitsPerEvaluation') {
    if ($report.$field -ne $evidence.$field) {
        throw "Campaign manifest field $field drifted from the retained evidence: $($report.$field) vs $($evidence.$field)."
    }
}
if (@($report.Runs).Count -ne @($evidence.Runs).Count) { throw 'The campaign produced a different number of runs than the retained evidence.' }

$expected = @{}
foreach ($run in $evidence.Runs) { $expected['{0}|{1}|{2}' -f $run.Task, $run.Seed, $run.Method] = $run }
$tolerance = 1e-12
foreach ($run in $report.Runs) {
    $key = '{0}|{1}|{2}' -f $run.Task, $run.Seed, $run.Method
    if (-not $expected.ContainsKey($key)) { throw "The campaign produced an unrecorded run: $key." }
    $reference = $expected[$key]
    # Wall time is observational and deliberately excluded; everything the search decided is compared exactly.
    if ($run.StateHash -ne $reference.StateHash) {
        throw "State hash drift for ${key}: $($run.StateHash) vs recorded $($reference.StateHash)."
    }
    if ($run.Evaluations -ne $reference.Evaluations -or $run.Proposals -ne $reference.Proposals -or $run.Capacity -ne $reference.Capacity) {
        throw "Budget drift for $key."
    }
    foreach ($metric in 'Hypervolume', 'MinimumF1', 'MinimumF2', 'BestScalar') {
        if ([math]::Abs([double]$run.$metric - [double]$reference.$metric) -gt $tolerance) {
            throw "$metric drift for ${key}: $($run.$metric) vs recorded $($reference.$metric)."
        }
    }
    $front = @($run.Front)
    $referenceFront = @($reference.Front)
    if ($front.Count -ne $referenceFront.Count) { throw "Front size drift for ${key}: $($front.Count) vs $($referenceFront.Count)." }
    for ($i = 0; $i -lt $front.Count; $i++) {
        if ($front[$i].GenomeId -ne $referenceFront[$i].GenomeId) { throw "Front member drift for $key at position $i." }
        $objectives = @($front[$i].Objectives)
        $referenceObjectives = @($referenceFront[$i].Objectives)
        if ($objectives.Count -ne $referenceObjectives.Count) { throw "Objective count drift for $key at position $i." }
        for ($j = 0; $j -lt $objectives.Count; $j++) {
            if ([math]::Abs([double]$objectives[$j] - [double]$referenceObjectives[$j]) -gt $tolerance) {
                throw "Objective drift for $key at position $i, objective $j."
            }
        }
        if ([math]::Abs([double]$front[$i].ScalarQuality - [double]$referenceFront[$i].ScalarQuality) -gt $tolerance) {
            throw "Scalar quality drift for $key at position $i."
        }
    }
}

Write-Output "Pareto campaign reproduced $(@($report.Runs).Count) runs with the state hashes, fronts and hypervolumes recorded in $(Split-Path -Leaf $evidencePath); built from $($report.EmbeddedCommit). Report: $reportPath"
