param(
    [Parameter(Mandatory = $true)]
    [string] $CoverageFile,

    [Parameter(Mandatory = $true)]
    [string] $BaselineFile,

    [double] $Tolerance = 1.0
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CoverageFile -PathType Leaf)) {
    throw "Coverage report not found: $CoverageFile"
}
if (-not (Test-Path -LiteralPath $BaselineFile -PathType Leaf)) {
    throw "Coverage baseline not found: $BaselineFile"
}

[xml] $coverage = Get-Content -LiteralPath $CoverageFile -Raw
$baseline = Get-Content -LiteralPath $BaselineFile -Raw | ConvertFrom-Json

function Get-BoundedNumber {
    param(
        [object] $Value,
        [string] $Name,
        [double] $Minimum,
        [double] $Maximum
    )

    [double] $number = 0
    if ($null -eq $Value -or
        -not [double]::TryParse(
            [string] $Value,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref] $number) -or
        [double]::IsNaN($number) -or
        [double]::IsInfinity($number) -or
        $number -lt $Minimum -or
        $number -gt $Maximum) {
        throw "$Name must be a finite number in [$Minimum, $Maximum]."
    }

    return $number
}

$baselineLine = Get-BoundedNumber -Value $baseline.line -Name 'baseline.line' -Minimum 0 -Maximum 100
$baselineBranch = Get-BoundedNumber -Value $baseline.branch -Name 'baseline.branch' -Minimum 0 -Maximum 100
$reportLine = Get-BoundedNumber -Value $coverage.coverage.'line-rate' -Name 'coverage line-rate' -Minimum 0 -Maximum 1
$reportBranch = Get-BoundedNumber -Value $coverage.coverage.'branch-rate' -Name 'coverage branch-rate' -Minimum 0 -Maximum 1
if ([double]::IsNaN($Tolerance) -or [double]::IsInfinity($Tolerance) -or $Tolerance -lt 0) {
    throw 'Tolerance must be finite and non-negative.'
}

$line = $reportLine * 100.0
$branch = $reportBranch * 100.0
$minimumLine = $baselineLine - $Tolerance
$minimumBranch = $baselineBranch - $Tolerance

Write-Host ("Line coverage:   {0:N2}% (minimum {1:N2}%)" -f $line, $minimumLine)
Write-Host ("Branch coverage: {0:N2}% (minimum {1:N2}%)" -f $branch, $minimumBranch)

if ($line -lt $minimumLine) {
    throw ("Line coverage regressed from {0:N2}% to {1:N2}%, beyond the {2:N2} percentage-point tolerance." -f $baseline.line, $line, $Tolerance)
}
if ($branch -lt $minimumBranch) {
    throw ("Branch coverage regressed from {0:N2}% to {1:N2}%, beyond the {2:N2} percentage-point tolerance." -f $baseline.branch, $branch, $Tolerance)
}
