$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'examples/MultiFidelitySearch/MultiFidelitySearch.csproj'
$scratch = Join-Path $repo ("TestResults/fidelity-recovery-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Recovery example build failed.' }
$baselinePath = Join-Path $scratch 'baseline.json'; $startPath = Join-Path $scratch 'start.json'; $resumePath = Join-Path $scratch 'resume.json'
dotnet run --project $project -c Release --no-build -- --checkpoint-regression baseline $baselinePath
if ($LASTEXITCODE -ne 0) { throw 'Uninterrupted regression baseline failed.' }
dotnet run --project $project -c Release --no-build -- --checkpoint-regression start $startPath
if ($LASTEXITCODE -ne 0) { throw 'Checkpointed training start failed.' }
dotnet run --project $project -c Release --no-build -- --checkpoint-regression resume $resumePath $startPath
if ($LASTEXITCODE -ne 0) { throw 'Separate-process training resume failed.' }
$baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
$start = Get-Content -LiteralPath $startPath -Raw | ConvertFrom-Json
$resume = Get-Content -LiteralPath $resumePath -Raw | ConvertFrom-Json
if (@($baseline.ProcessId,$start.ProcessId,$resume.ProcessId | Sort-Object -Unique).Count -ne 3) { throw 'Recovery did not use three distinct processes.' }
if ($baseline.Status -ne 'completed' -or $start.Status -ne 'completed' -or $resume.Status -ne 'completed' -or
    $start.Report.StopReason -ne 'Paused' -or $resume.Report.StopReason -ne 'Completed' -or
    $start.EvaluatorCalls + $resume.EvaluatorCalls -ne $baseline.EvaluatorCalls -or $baseline.EvaluatorCalls -ne 32 -or
    $start.ExecutedEpochs + $resume.ExecutedEpochs -ne $baseline.ExecutedEpochs -or $baseline.ExecutedEpochs -ne 608) { throw 'Recovery repeated or lost model work.' }
if ((ConvertTo-Json -InputObject $baseline.Report -Depth 40) -cne (ConvertTo-Json -InputObject $resume.Report -Depth 40)) {
    throw 'Final restored report, promotions or resource receipts differ from uninterrupted execution.'
}
$combined = @($start.Measurements) + @($resume.Measurements)
if ((ConvertTo-Json -InputObject $combined -Depth 12) -cne (ConvertTo-Json -InputObject $baseline.Measurements -Depth 12)) {
    throw 'Restored training data, per-sample weights, quality, continuation or actual work differs.'
}
Write-Output "Fidelity recovery verified in $scratch`: three separate processes, actual persisted model tokens, identical final report/ledger and 32 total evaluator calls without duplicated epochs."
