#Requires -Version 7.0
<#
.SYNOPSIS
Verifies that the published US-10 performance tables can be regenerated from the committed evidence summary.

.DESCRIPTION
The repository commits a compact evidence summary instead of the multi-megabyte raw profiling report. The tables in
benchmarks/evidence/performance/README.md must therefore be reproducible from that summary alone. This script renders
the tables with the profiler's --evidence-tables mode and compares them with the generated block in the README.
It makes no measurement and asserts no performance threshold.
#>
[CmdletBinding()]
param(
    [string] $EvidenceDirectory = 'benchmarks/evidence/performance',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
$previousEncoding = [Console]::OutputEncoding
try {
    # Decode the profiler's stdout as UTF-8 regardless of the host's active code page.
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $readmePath = Join-Path $EvidenceDirectory 'README.md'
    if (-not (Test-Path -LiteralPath $readmePath)) { throw "Evidence README not found at $readmePath." }
    $summaries = @(Get-ChildItem -Path $EvidenceDirectory -Filter '*-summary.json' |
        Where-Object { $_.Name -notlike '*failed*' } | Sort-Object Name)
    if ($summaries.Count -ne 1) { throw "Expected exactly one passing evidence summary in $EvidenceDirectory; found $($summaries.Count)." }

    $arguments = @('run', '--project', 'benchmarks/AiDotNet.Evolution.Performance', '-c', 'Release')
    if ($NoBuild) { $arguments += '--no-build' }
    $arguments += @('--', '--evidence-tables', $summaries[0].FullName)
    $rendered = (& dotnet @arguments) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Rendering the evidence tables failed with exit code $LASTEXITCODE." }

    $readme = Get-Content -LiteralPath $readmePath -Raw -Encoding utf8
    $begin = '<!-- BEGIN GENERATED TABLES -->'
    $end = '<!-- END GENERATED TABLES -->'
    $startIndex = $readme.IndexOf($begin, [System.StringComparison]::Ordinal)
    $endIndex = $readme.IndexOf($end, [System.StringComparison]::Ordinal)
    if ($startIndex -lt 0 -or $endIndex -lt $startIndex) { throw "The evidence README has no generated-table block." }
    $published = $readme.Substring($startIndex + $begin.Length, $endIndex - $startIndex - $begin.Length)

    $normalize = { param($text) ($text -replace "`r`n", "`n").Trim() }
    $expected = & $normalize $rendered
    $actual = & $normalize $published
    if ($expected -ne $actual) {
        $expectedLines = $expected -split "`n"
        $actualLines = $actual -split "`n"
        for ($index = 0; $index -lt [Math]::Max($expectedLines.Count, $actualLines.Count); $index++) {
            $left = if ($index -lt $expectedLines.Count) { $expectedLines[$index] } else { '<missing>' }
            $right = if ($index -lt $actualLines.Count) { $actualLines[$index] } else { '<missing>' }
            if ($left -ne $right) {
                throw "Published table line $($index + 1) does not match the summary.`nsummary : $left`nREADME  : $right"
            }
        }
        throw 'The published tables differ from the regenerated tables.'
    }
    Write-Host "Published performance tables regenerate exactly from $($summaries[0].Name)."
}
finally {
    [Console]::OutputEncoding = $previousEncoding
    Pop-Location
}
