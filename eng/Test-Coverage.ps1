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
$line = [double] $coverage.coverage.'line-rate' * 100.0
$branch = [double] $coverage.coverage.'branch-rate' * 100.0
$minimumLine = [double] $baseline.line - $Tolerance
$minimumBranch = [double] $baseline.branch - $Tolerance

Write-Host ("Line coverage:   {0:N2}% (minimum {1:N2}%)" -f $line, $minimumLine)
Write-Host ("Branch coverage: {0:N2}% (minimum {1:N2}%)" -f $branch, $minimumBranch)

if ($line -lt $minimumLine) {
    throw ("Line coverage regressed from {0:N2}% to {1:N2}%, beyond the {2:N2} percentage-point tolerance." -f $baseline.line, $line, $Tolerance)
}
if ($branch -lt $minimumBranch) {
    throw ("Branch coverage regressed from {0:N2}% to {1:N2}%, beyond the {2:N2} percentage-point tolerance." -f $baseline.branch, $branch, $Tolerance)
}
