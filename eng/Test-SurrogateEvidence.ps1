param([Parameter(Mandatory = $true)][string] $RawJson)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $repo ("TestResults/surrogate-evidence-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
function Save-Fixture([string]$Path, [string]$Json) {
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew)
    try { $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Json); $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}
$raw = Join-Path $scratch 'raw.json'; $summary = Join-Path $scratch 'summary.json'; $compressed = Join-Path $scratch 'raw.json.gz'
Save-Fixture $raw $RawJson
& (Join-Path $PSScriptRoot 'Export-SurrogateEvidence.ps1') -InputPath $raw -OutputPath $summary -RawGzipPath $compressed
$report = Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json
if ($report.FullTraceSha256 -ne (Get-FileHash -LiteralPath $raw -Algorithm SHA256).Hash.ToLowerInvariant() -or
    $report.RawGzipSha256 -ne (Get-FileHash -LiteralPath $compressed -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Evidence digests differ.' }
$source = [System.IO.File]::OpenRead($compressed)
try {
    $gzip = [System.IO.Compression.GZipStream]::new($source, [System.IO.Compression.CompressionMode]::Decompress, $true)
    try { $reader = [System.IO.StreamReader]::new($gzip); try { $roundTrip = $reader.ReadToEnd() } finally { $reader.Dispose() } }
    finally { $gzip.Dispose() }
} finally { $source.Dispose() }
if ($RawJson -cne $roundTrip) { throw 'Compressed evidence lost raw measurements, predictions or receipts.' }

foreach ($corruption in @('missing','duplicate','initialization','tariff','revision','mode')) {
    $bad = $RawJson | ConvertFrom-Json
    switch ($corruption) {
        'missing' { $bad.Runs = @($bad.Runs | Select-Object -Skip 1) }
        'duplicate' { $bad.Runs[0] = $bad.Runs[1] }
        'initialization' { $bad.Runs[0].InitialPopulationHash = 'mismatched' }
        'tariff' { $bad.Runs[0].CostCap = 999 }
        'revision' { $bad.AssemblyVersion = 'unversioned' }
        'mode' { $bad.CostRatios = -not $bad.CostRatios }
    }
    $path = Join-Path $scratch "$corruption.json"; $output = Join-Path $scratch "$corruption-summary.json"
    Save-Fixture $path (ConvertTo-Json -InputObject $bad -Depth 40)
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Export-SurrogateEvidence.ps1') -InputPath $path -OutputPath $output | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath $output)) { throw "Exporter accepted $corruption corruption." }
}
$failed = $RawJson | ConvertFrom-Json; $failed.Runs[0].Status = 'failed'
$failedPath = Join-Path $scratch 'failed.json'; $failedSummary = Join-Path $scratch 'failed-summary.json'
Save-Fixture $failedPath (ConvertTo-Json -InputObject $failed -Depth 40)
& (Join-Path $PSScriptRoot 'Export-SurrogateEvidence.ps1') -InputPath $failedPath -OutputPath $failedSummary | Out-Null
$retained = Get-Content -LiteralPath $failedSummary -Raw | ConvertFrom-Json
if ($retained.Runs.Count -ne $failed.Runs.Count -or $retained.Runs[0].Status -ne 'failed') { throw 'Exporter dropped a failed row.' }
Write-Output 'Surrogate evidence verified: exact gzip roundtrip, both hashes, six corruptions rejected, failed row retained.'
