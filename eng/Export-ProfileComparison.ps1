param(
    [Parameter(Mandatory = $true)][string] $BeforeReport,
    [Parameter(Mandatory = $true)][string] $AfterReport,
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [string] $Python = 'python'
)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Choose a new evidence directory; existing evidence is preserved.' }
[IO.Directory]::CreateDirectory($target) | Out-Null
$reports = @($BeforeReport, $AfterReport)
$names = @('before', 'after')
for ($index = 0; $index -lt 2; $index++) {
    $source = (Resolve-Path -LiteralPath $reports[$index]).Path
    $output = [IO.File]::Open((Join-Path $target ($names[$index] + '-report.json.gz')), [IO.FileMode]::CreateNew)
    try {
        $gzip = [IO.Compression.GZipStream]::new($output, [IO.Compression.CompressionLevel]::Optimal, $true)
        try {
            $inputStream = [IO.File]::OpenRead($source)
            try { $inputStream.CopyTo($gzip) } finally { $inputStream.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $output.Dispose() }
    & dotnet benchmarks/AiDotNet.Evolution.Performance/bin/Release/net10.0/AiDotNet.Evolution.Performance.dll --evidence $source (Join-Path $target ($names[$index] + '-summary.json'))
    if ($LASTEXITCODE -ne 0) { throw 'Summary export failed; partial artifacts retained.' }
}
& $Python benchmarks/analysis/compare_profiles.py (Join-Path $target 'before-report.json.gz') (Join-Path $target 'after-report.json.gz') (Join-Path $target 'comparison.json')
if ($LASTEXITCODE -ne 0) { throw 'Comparison failed; both raw reports retained.' }
