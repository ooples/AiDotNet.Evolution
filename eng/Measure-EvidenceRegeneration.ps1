#Requires -Version 7.0
<#
.SYNOPSIS
    Counts how much of the committed evidence a reader can actually check against raw data.
.DESCRIPTION
    US-04 (#22) asks for reports that can be checked from raw evidence, and its one metric -
    "report regeneration diff: 0 bytes" - is measured only for the performance family, by
    eng/Test-EvidenceTables.ps1. That leaves the metric reading "partial" with no number behind the
    word. This script supplies the number.

    Every README under the evidence root is classified by how a reader could re-derive its claims:

      Generated     the README carries a generated block, so a script re-renders it from a
                    committed summary and any drift fails a build
      RawAlongside  no generated block, but machine-readable evidence is committed at or beneath
                    the README's own directory, so the claims can be checked by hand
      Unsupported   neither - the README's numbers exist nowhere else in the repository

    A family directory that commits evidence with no README anywhere beneath it is counted
    separately as Undocumented: the raw data is checkable, but nothing says what it shows.

    It reports; it never edits a README. Writing a renderer or committing a missing artifact is
    work, not a measurement, and stays with the person doing it.
.EXAMPLE
    ./eng/Measure-EvidenceRegeneration.ps1 -OutputFile census.json
.EXAMPLE
    ./eng/Measure-EvidenceRegeneration.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [Parameter(ParameterSetName = 'Measure')] [string] $EvidenceRoot = 'benchmarks/evidence',
    [Parameter(ParameterSetName = 'Measure')] [string] $OutputFile,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest,

    # Renders the committed census as the markdown block its README publishes, and measures nothing.
    # The same shape as the profiler's --evidence-tables, so this family regenerates the way the
    # performance family does rather than inventing a second mechanism.
    [Parameter(Mandatory, ParameterSetName = 'Render')] [string] $RenderTables
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The marker pair eng/Test-EvidenceTables.ps1 compares against, so "Generated" here means the same
# thing it means there rather than a second, looser definition.
$script:BeginMarker = '<!-- BEGIN GENERATED TABLES -->'
$script:EndMarker = '<!-- END GENERATED TABLES -->'

function Measure-EvidenceReadme {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $ReadmePath)

    $text = Get-Content -LiteralPath $ReadmePath -Raw -Encoding utf8
    $directory = Split-Path -Parent $ReadmePath
    # At or beneath, so an index README is credited with the revision directories under it and a
    # revision README with the sub-studies under it. Anything but a README counts as an artifact:
    # the families commit .json, .jsonl, .json.gz, .zip, .py and .log, and an extension list would
    # silently reclassify a family that commits a new one.
    #
    # -Force because Get-ChildItem hides dot-prefixed and hidden-attribute files without it, while the
    # C# classifier in EvidenceRegenerationCensusTests uses Directory.EnumerateFiles, which does not.
    # Measured: one hidden .gitkeep beside a README reads as 0 artifacts here and 1 there, so the
    # committed census would say Unsupported where the test says RawAlongside.
    $artifacts = @(Get-ChildItem -LiteralPath $directory -Recurse -File -Force |
        Where-Object { $_.Name -ne 'README.md' })
    $class = if ($text.Contains($script:BeginMarker, [System.StringComparison]::Ordinal) -and
                 $text.Contains($script:EndMarker, [System.StringComparison]::Ordinal)) { 'Generated' }
             elseif ($artifacts.Count -gt 0) { 'RawAlongside' }
             else { 'Unsupported' }
    [pscustomobject]@{
        Path = $ReadmePath
        Class = $class
        ArtifactCount = $artifacts.Count
    }
}

function Measure-EvidenceTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Evidence root not found at $Root." }
    $families = @()
    foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory -Force | Sort-Object Name) {
        $readmes = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File -Force -Filter 'README.md' |
            Sort-Object FullName | ForEach-Object {
                $row = Measure-EvidenceReadme -ReadmePath $_.FullName
                $row.Path = [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
                $row
            })
        $artifacts = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File -Force |
            Where-Object { $_.Name -ne 'README.md' })
        $families += [pscustomobject]@{
            Name = $directory.Name
            Readmes = $readmes
            ArtifactCount = $artifacts.Count
            # Undocumented is a property of the family, not of a README, because the thing it
            # reports is that no README exists to classify.
            Undocumented = ($readmes.Count -eq 0 -and $artifacts.Count -gt 0)
        }
    }
    if ($families.Count -eq 0) { throw "No evidence families under $Root; the census would report an empty tree as clean." }
    $rows = @($families | ForEach-Object { $_.Readmes })
    [pscustomobject]@{
        Root = $Root.Replace('\', '/')
        Families = $families
        Totals = [pscustomobject]@{
            Families = $families.Count
            Readmes = $rows.Count
            Generated = @($rows | Where-Object { $_.Class -eq 'Generated' }).Count
            RawAlongside = @($rows | Where-Object { $_.Class -eq 'RawAlongside' }).Count
            Unsupported = @($rows | Where-Object { $_.Class -eq 'Unsupported' }).Count
            Undocumented = @($families | Where-Object { $_.Undocumented }).Count
        }
    }
}

function Test-EvidenceCensus {
    [CmdletBinding()]
    param()

    # A positive control. "0 Unsupported" is the answer a severed detector gives as readily as a
    # clean tree, so the census is first run against a tree authored to contain one of every class.
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('evidence-census-' + [Guid]::NewGuid().ToString('N'))
    try {
        $generated = Join-Path $root 'fam-generated'
        New-Item -ItemType Directory -Path $generated -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $generated 'README.md') -Encoding utf8 -Value @(
            '# generated', $script:BeginMarker, '| a | b |', $script:EndMarker)
        Set-Content -LiteralPath (Join-Path $generated 'summary.json') -Encoding utf8 -Value '{}'

        $raw = Join-Path $root 'fam-raw/abc1234'
        New-Item -ItemType Directory -Path $raw -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $raw 'README.md') -Encoding utf8 -Value '# raw, 12.5 ms'
        Set-Content -LiteralPath (Join-Path $raw 'runs.jsonl') -Encoding utf8 -Value '{"ms":12.5}'

        $unsupported = Join-Path $root 'fam-unsupported/abc1234'
        New-Item -ItemType Directory -Path $unsupported -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $unsupported 'README.md') -Encoding utf8 -Value '# 1 call in 5, from nowhere'

        # Half a marker pair is not a generated block. Without this arm a renderer that emitted only
        # the opening marker would be counted as regenerating, which is the one claim that must not
        # be cheap to make.
        $half = Join-Path $root 'fam-half-marker'
        New-Item -ItemType Directory -Path $half -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $half 'README.md') -Encoding utf8 -Value @('# half', $script:BeginMarker, '| a |')

        $undocumented = Join-Path $root 'fam-undocumented'
        New-Item -ItemType Directory -Path $undocumented -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $undocumented 'analysis.json') -Encoding utf8 -Value '{}'

        # A hidden artifact still counts. Get-ChildItem skips dot-prefixed and hidden-attribute files
        # without -Force, while the C# classifier's Directory.EnumerateFiles does not, so without this
        # arm the two implementations disagree on exactly this directory and neither one says so.
        $hidden = Join-Path $root 'fam-hidden-artifact'
        New-Item -ItemType Directory -Path $hidden -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $hidden 'README.md') -Encoding utf8 -Value '# 42 ms'
        $keep = Join-Path $hidden '.gitkeep'
        Set-Content -LiteralPath $keep -Encoding utf8 -Value ''
        if ($IsWindows) { (Get-Item -LiteralPath $keep -Force).Attributes = 'Hidden' }

        $census = Measure-EvidenceTree -Root $root
        $expected = @{ Families = 6; Readmes = 5; Generated = 1; RawAlongside = 2; Unsupported = 2; Undocumented = 1 }
        foreach ($key in ($expected.Keys | Sort-Object)) {
            $actual = $census.Totals.$key
            if ($actual -ne $expected[$key]) { throw "Self-test: expected $key = $($expected[$key]), measured $actual." }
        }
        $classes = @{}
        foreach ($family in $census.Families) { foreach ($row in $family.Readmes) { $classes[$family.Name] = $row.Class } }
        foreach ($pair in @(@('fam-generated', 'Generated'), @('fam-raw', 'RawAlongside'),
                            @('fam-unsupported', 'Unsupported'), @('fam-half-marker', 'Unsupported'),
                            @('fam-hidden-artifact', 'RawAlongside'))) {
            if ($classes[$pair[0]] -ne $pair[1]) { throw "Self-test: $($pair[0]) classified $($classes[$pair[0]]), expected $($pair[1])." }
        }
        Write-Output "Self-test passed: one of every class is detected, half a marker pair is not Generated, and a hidden artifact still counts."
    }
    finally {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -Confirm:$false }
    }
}

function Format-CensusTables {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $CensusPath)

    $census = Get-Content -LiteralPath $CensusPath -Raw -Encoding utf8 | ConvertFrom-Json
    $totals = $census.Totals
    # Counts only. The prose that explains each class is editorial and stays outside the block, so
    # nothing here can be regenerated differently from how it was written.
    $lines = @('', '| measure | count |', '| --- | ---: |')
    foreach ($pair in @(@('`Generated` READMEs', $totals.Generated), @('`RawAlongside` READMEs', $totals.RawAlongside),
                        @('`Unsupported` READMEs', $totals.Unsupported), @('READMEs in total', $totals.Readmes),
                        @('families', $totals.Families), @('families with no README', $totals.Undocumented))) {
        $lines += ('| {0} | {1} |' -f $pair[0], $pair[1])
    }
    $lines += ''
    $lines -join "`n"
}

if ($SelfTest) { Test-EvidenceCensus; return }
if ($RenderTables) { Write-Output (Format-CensusTables -CensusPath $RenderTables); return }

$repository = Split-Path -Parent $PSScriptRoot
$resolved = if ([System.IO.Path]::IsPathRooted($EvidenceRoot)) { $EvidenceRoot } else { Join-Path $repository $EvidenceRoot }
$result = Measure-EvidenceTree -Root $resolved
$result | Add-Member -NotePropertyName Story -NotePropertyValue 'US-04 (#22): how much committed evidence can be checked against raw data'
$result | Add-Member -NotePropertyName MeasuredAtUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o'))
$result.Root = $EvidenceRoot.Replace('\', '/')

foreach ($family in $result.Families) {
    $state = if ($family.Undocumented) { 'undocumented' } else { ($family.Readmes | ForEach-Object { $_.Class }) -join ',' }
    Write-Output ("{0,-30} {1,3} files  {2}" -f $family.Name, $family.ArtifactCount, $state)
}
$t = $result.Totals
Write-Output ("TOTAL families={0} readmes={1}; Generated={2} RawAlongside={3} Unsupported={4}; undocumented families={5}" -f
    $t.Families, $t.Readmes, $t.Generated, $t.RawAlongside, $t.Unsupported, $t.Undocumented)
if ($OutputFile) {
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputFile -Encoding utf8
    Write-Output "Wrote $OutputFile"
}
