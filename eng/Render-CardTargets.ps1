#Requires -Version 7.0
<#
.SYNOPSIS
    Renders an evidence card's declared targets against its measured values, as a markdown table.
.DESCRIPTION
    Several cards under benchmarks/evidence declare a `targets` object whose keys name fields in the
    same card, so the comparison a reader wants - target against measured, and whether it was met -
    is already fully determined by the committed JSON. Those tables were typed by hand, which US-04
    (#22) counts as `RawAlongside`: checkable, but only if someone checks.

    This renders them instead, so the card carries a generated block like the performance family's
    and any drift fails a build rather than sitting there.

    A target is either an exact value, which is met when the measured value equals it, or a string
    of the form "< x" or "<= x", which is met when the measured value satisfies it. Anything else
    throws: a target this cannot interpret must be a visible failure, never a silently blank verdict.

    It reads one card and writes a table. It measures nothing and never edits a README.
.EXAMPLE
    ./eng/Render-CardTargets.ps1 -CardPath benchmarks/evidence/claude-transport/bbbaf5e/summary.json
.EXAMPLE
    ./eng/Render-CardTargets.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Render')] [string] $CardPath,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Format-CardValue {
    [CmdletBinding()]
    param([Parameter()] $Value)

    # 'R' round-trips a double to the text the JSON held, so a rendered block can be compared with a
    # committed one without a tolerance, and the C# side formats it the same way.
    if ($null -eq $Value) { return 'absent' }
    if ($Value -is [double] -or $Value -is [single]) {
        return $Value.ToString('R', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [bool]) { return $Value.ToString().ToLowerInvariant() }
    return [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0}', $Value)
}

function Test-CardTarget {
    [CmdletBinding()]
    param([Parameter()] $Target, [Parameter()] $Measured)

    if ($null -eq $Measured) { return $false }
    if ($Target -is [string]) {
        if ($Target -match '^\s*(<=|<)\s*(-?[0-9]+(\.[0-9]+)?)\s*$') {
            $bound = [double]::Parse($Matches[2], [System.Globalization.CultureInfo]::InvariantCulture)
            $value = [double]$Measured
            return $(if ($Matches[1] -eq '<') { $value -lt $bound } else { $value -le $bound })
        }
        throw "Cannot interpret the target '$Target'. Use an exact value, '< x' or '<= x'."
    }
    return [double]$Measured -eq [double]$Target
}

function Format-CardTargets {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    $card = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    if ($null -eq $card.PSObject.Properties['targets']) {
        throw "The card at $Path declares no targets, so there is nothing to render."
    }
    $targets = @($card.targets.PSObject.Properties)
    if ($targets.Count -eq 0) { throw "The card at $Path declares an empty targets object." }
    $lines = @('| field | target | measured | met |', '| --- | --- | ---: | --- |')
    foreach ($target in $targets) {
        $measured = if ($null -ne $card.PSObject.Properties[$target.Name]) { $card.($target.Name) } else { $null }
        # A target naming a field the card does not carry is a defect in the card, not a blank row.
        if ($null -eq $measured -and $null -eq $card.PSObject.Properties[$target.Name]) {
            throw "The card at $Path declares a target for '$($target.Name)' but carries no such field."
        }
        $met = Test-CardTarget -Target $target.Value -Measured $measured
        $lines += ('| `{0}` | {1} | {2} | {3} |' -f $target.Name, (Format-CardValue $target.Value),
            (Format-CardValue $measured), $(if ($met) { 'yes' } else { '**no**' }))
    }
    ($lines -join "`n")
}

function Test-CardTargetRenderer {
    [CmdletBinding()]
    param()

    # Known-answer arms, including ones that must fail. A renderer whose every arm is met leaves the
    # verdict column untested, and "yes" everywhere is what a broken comparison also prints.
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('card-targets-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    try {
        $path = Join-Path $root 'card.json'
        @{ targets = [ordered]@{ exact_met = 0; exact_missed = 1; bound_met = '< 5'; bound_missed = '< 5'; inclusive_met = '<= 2' }
           exact_met = 0; exact_missed = 2; bound_met = 0.818; bound_missed = 7.5; inclusive_met = 2
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding utf8
        $table = Format-CardTargets -Path $path
        foreach ($pair in @(@('exact_met', 'yes'), @('exact_missed', '**no**'), @('bound_met', 'yes'),
                            @('bound_missed', '**no**'), @('inclusive_met', 'yes'))) {
            # -match, not -like: the backtick is the wildcard escape character, so a pattern holding
            # the markdown code fence silently matches nothing.
            $needle = [regex]::Escape('`' + $pair[0] + '`')
            $row = @($table -split "`n" | Where-Object { $_ -match $needle })
            if ($row.Count -ne 1) { throw "Self-test: expected one row for $($pair[0]), found $($row.Count)." }
            if ($row[0] -notmatch [regex]::Escape('| ' + $pair[1] + ' |')) {
                throw "Self-test: $($pair[0]) rendered '$($row[0])', expected a $($pair[1]) verdict."
            }
        }

        # An uninterpretable target must throw rather than render a blank verdict.
        $bad = Join-Path $root 'bad.json'
        @{ targets = @{ odd = 'roughly 5' }; odd = 5 } | ConvertTo-Json | Set-Content -LiteralPath $bad -Encoding utf8
        try { Format-CardTargets -Path $bad; throw 'Self-test: an uninterpretable target rendered instead of throwing.' }
        catch { if ($_.Exception.Message -notlike "*Cannot interpret the target*") { throw } }

        # A target naming a field the card lacks must throw too.
        $missing = Join-Path $root 'missing.json'
        @{ targets = @{ absent_field = 0 } } | ConvertTo-Json | Set-Content -LiteralPath $missing -Encoding utf8
        try { Format-CardTargets -Path $missing; throw 'Self-test: a target without a field rendered instead of throwing.' }
        catch { if ($_.Exception.Message -notlike "*carries no such field*") { throw } }

        Write-Output 'Self-test passed: met and missed verdicts both render, and two malformed cards throw.'
    }
    finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -Confirm:$false } }
}

if ($SelfTest) { Test-CardTargetRenderer; return }
Write-Output (Format-CardTargets -Path $CardPath)
