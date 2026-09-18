param(
    [Parameter(Mandatory=$true)][string]$Report,
    [Parameter(Mandatory=$true)][string]$FailedReport,
    [Parameter(Mandatory=$true)][string]$Python,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Evidence destination already exists; refusing overwrite.' }
$reportPath = (Resolve-Path -LiteralPath $Report).Path
$failedPath = (Resolve-Path -LiteralPath $FailedReport).Path
New-Item -ItemType Directory -Path $destination | Out-Null
& $Python "$PSScriptRoot/../benchmarks/analysis/pareto_study.py" $reportPath (Join-Path $destination 'summary.json')
if ($LASTEXITCODE -ne 0) { throw 'Independent campaign validation failed; no successful report exported.' }
foreach ($item in @(@($reportPath, 'report.json.gz'), @($failedPath, 'failed-09a3a4d.json.gz'))) {
    $inputStream = [IO.File]::OpenRead($item[0])
    $outputStream = [IO.File]::Open((Join-Path $destination $item[1]), [IO.FileMode]::CreateNew)
    $compressed = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionLevel]::Optimal)
    try { $inputStream.CopyTo($compressed) }
    finally { $compressed.Dispose(); $outputStream.Dispose(); $inputStream.Dispose() }
}
