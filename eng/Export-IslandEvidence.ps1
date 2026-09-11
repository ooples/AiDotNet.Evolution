param(
    [Parameter(Mandatory = $true)][string] $InputFile,
    [Parameter(Mandatory = $true)][string] $Revision,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Revision -notmatch '^[0-9a-f]{40}$') { throw 'Supply the full production revision.' }
$source = (Resolve-Path -LiteralPath $InputFile).Path
$report = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
if ($report.Protocol -ne 'fixed-adaptive-islands-pilot-v1' -or $report.AssemblyVersion -notlike "*$Revision*") {
    throw 'The artifact protocol or embedded source revision does not match.'
}
$directory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$rawTarget = [IO.Path]::Combine($directory, 'raw.json.gz')
$summaryTarget = [IO.Path]::Combine($directory, 'summary.json')
if ([IO.File]::Exists($rawTarget) -or [IO.File]::Exists($summaryTarget)) { throw 'Evidence outputs must not already exist.' }
$inputStream = [IO.File]::OpenRead($source)
try {
    $outputStream = [IO.File]::Open($rawTarget, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $gzip = New-Object IO.Compression.GZipStream($outputStream, [IO.Compression.CompressionLevel]::Optimal, $true)
        try { $inputStream.CopyTo($gzip) } finally { $gzip.Dispose() }
    } finally { $outputStream.Dispose() }
} finally { $inputStream.Dispose() }
$runs = @($report.Runs | ForEach-Object {
    [ordered]@{
        Task = $_.Task; Method = $_.Method; Seed = $_.Seed; Status = $_.Status
        InitialPopulationHash = $_.InitialPopulationHash; EvaluatorCalls = $_.EvaluatorCalls; Proposals = $_.Proposals
        FinalQuality = $_.FinalQuality; StateHash = $_.StateHash; ReplayStateHash = $_.ReplayStateHash; ResumeStateHash = $_.ResumeStateHash
        Statistics = $_.Statistics; Spent = $_.Resources.Spent; Unknown = $_.Resources.Unknown; MaximumViolated = $_.Resources.MaximumViolated
    }
})
$primaryCalls = ($report.Runs | Measure-Object -Property EvaluatorCalls -Sum).Sum
$resumes = @($report.Runs | Where-Object { $null -ne $_.ResumeStateHash }).Count
$summary = [ordered]@{
    Protocol = $report.Protocol; SourceRevision = $Revision; AssemblyVersion = $report.AssemblyVersion; AssemblySha256 = $report.AssemblySha256
    Seeds = $report.Seeds; EvaluatorCallCap = $report.EvaluatorCallCap; AllValid = $report.AllValid
    RawFile = 'raw.json.gz'; RawSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    GzipSha256 = (Get-FileHash -LiteralPath $rawTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    RawBytes = (Get-Item -LiteralPath $source).Length; GzipBytes = (Get-Item -LiteralPath $rawTarget).Length
    PrimaryEvaluatorCalls = $primaryCalls; ReplayEvaluatorCalls = $primaryCalls
    CheckpointValidationEvaluatorCalls = $resumes * $report.EvaluatorCallCap
    Interpretation = $report.Interpretation; Runs = $runs
}
$stream = [IO.File]::Open($summaryTarget, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try {
    $writer = New-Object IO.StreamWriter($stream, (New-Object Text.UTF8Encoding($false)))
    try { $writer.Write(($summary | ConvertTo-Json -Depth 16)) } finally { $writer.Dispose() }
} finally { $stream.Dispose() }
Write-Host ("Exported {0} primary runs; {1} raw bytes compressed to {2}; valid={3}" -f $runs.Count, $summary.RawBytes, $summary.GzipBytes, $report.AllValid)
if (-not $report.AllValid) { throw 'The retained campaign includes failed validation.' }
