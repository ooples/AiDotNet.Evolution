<#
.SYNOPSIS
    Counts acceptance-checkbox state across the roadmap story issues, so V1-01 (#108) has the
    instrument its metrics table names instead of a number someone typed once.
.DESCRIPTION
    The audit's own metric is "stale unchecked boxes", measured by "audit script over issue
    bodies". This is that script. It reads each issue body through `gh`, counts ticked and unticked
    boxes, and flags the one state that cannot be left alone: an issue that is CLOSED while its
    acceptance criteria are still unticked, which is what makes the epic's progress untruthful.

    It reports; it never edits an issue. Ticking a box is a judgement about evidence and stays with
    the person doing the audit.
.EXAMPLE
    ./eng/Measure-StoryCheckboxAudit.ps1 -Repository ooples/AiDotNet.Evolution -OutputFile audit.json
#>
[CmdletBinding()]
param(
    [Parameter(ParameterSetName = 'Measure')] [string] $Repository = 'ooples/AiDotNet.Evolution',

    # The twelve stories V1-01 (#108) names. Override to audit a different set.
    [Parameter(ParameterSetName = 'Measure')] [int[]] $Issues = @(21, 22, 24, 29, 30, 35, 36, 37, 38, 41, 42, 43),

    [Parameter(ParameterSetName = 'Measure')] [string] $OutputFile,

    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Measure-CheckboxBody {
    <#
        Counts checkbox state in one issue body. Separated from the API call so the counting is
        testable without the network, which is what the self-test exercises.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Body)

    $unchecked = 0
    $checked = 0
    $dependencyRefs = [System.Collections.Generic.List[int]]::new()

    foreach ($line in ($Body -split "`n")) {
        # A list item whose marker is "[ ]" or "[x]". Indented sub-items count too: a nested
        # criterion is still a criterion.
        if ($line -match '^\s*[-*]\s*\[\s\]') {
            $unchecked++
            # An unticked box that points at another issue is a dependency box; #108 says those are
            # ticked with the closing PR link, never deleted, so they are worth counting apart.
            foreach ($m in [regex]::Matches($line, '#(\d+)')) {
                $dependencyRefs.Add([int] $m.Groups[1].Value)
            }
        }
        elseif ($line -match '^\s*[-*]\s*\[[xX]\]') {
            $checked++
        }
    }

    return [pscustomobject]@{
        Unchecked       = $unchecked
        Checked         = $checked
        DependencyRefs  = $dependencyRefs.ToArray()
    }
}

function Invoke-SelfTest {
    [CmdletBinding()]
    param()

    $body = @'
## Acceptance criteria
- [ ] **Given** a thing, **when** it happens, **then** it holds
- [x] Already done, see #45
  - [ ] A nested criterion that is still a criterion
* [X] An asterisk marker, ticked
- [ ] Depends on #108 and #42
Not a checkbox: - [ ] inside prose is still matched only at line start.
'@
    $result = Measure-CheckboxBody -Body $body
    if ($result.Unchecked -ne 3) { throw "Unchecked count wrong: $($result.Unchecked), expected 3." }
    if ($result.Checked -ne 2) { throw "Checked count wrong: $($result.Checked), expected 2." }
    if (($result.DependencyRefs -join ',') -ne '108,42') {
        throw "Dependency refs wrong: $($result.DependencyRefs -join ',')."
    }

    $empty = Measure-CheckboxBody -Body ''
    if ($empty.Unchecked -ne 0 -or $empty.Checked -ne 0) { throw 'An empty body must count zero.' }

    Write-Host '[OK] Measure-StoryCheckboxAudit self-test: markers, nesting, case and dependency refs all counted.'
}

if ($SelfTest) {
    Invoke-SelfTest
    return
}

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($number in $Issues) {
    $json = gh issue view $number --repo $Repository --json number,state,title,body 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "Could not read issue $number from $Repository."
    }

    $issue = $json | ConvertFrom-Json
    $counts = Measure-CheckboxBody -Body ([string] $issue.body)
    $rows.Add([pscustomobject]@{
        Number          = $issue.number
        State           = $issue.state
        Title           = $issue.title
        Unchecked       = $counts.Unchecked
        Checked         = $counts.Checked
        DependencyRefs  = $counts.DependencyRefs
        # The state the audit exists to remove: closed, yet its own criteria are unticked.
        ClosedWithUnticked = ($issue.state -eq 'CLOSED' -and $counts.Unchecked -gt 0)
    })
}

$totalUnchecked = ($rows | Measure-Object -Property Unchecked -Sum).Sum
$totalChecked = ($rows | Measure-Object -Property Checked -Sum).Sum
$closedWithUnticked = @($rows | Where-Object { $_.ClosedWithUnticked })

foreach ($row in $rows) {
    $flag = if ($row.ClosedWithUnticked) { ' <- CLOSED with unticked criteria' } else { '' }
    Write-Host ("#{0,-4} {1,-6} unchecked={2,-3} checked={3,-3} {4}{5}" -f
        $row.Number, $row.State, $row.Unchecked, $row.Checked, $row.Title.Substring(0, [Math]::Min(44, $row.Title.Length)), $flag)
}
Write-Host ("TOTAL unchecked={0} checked={1}; closed-with-unticked: {2}" -f
    $totalUnchecked, $totalChecked, ($closedWithUnticked.Count))

$report = [pscustomobject]@{
    Repository              = $Repository
    MeasuredAtUtc           = (Get-Date).ToUniversalTime().ToString('o')
    Issues                  = $rows.ToArray()
    TotalUnchecked          = $totalUnchecked
    TotalChecked            = $totalChecked
    ClosedWithUntickedCount = $closedWithUnticked.Count
    ClosedWithUnticked      = @($closedWithUnticked | ForEach-Object { $_.Number })
}

if (-not [string]::IsNullOrWhiteSpace($OutputFile)) {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputFile))
    if (-not [string]::IsNullOrWhiteSpace($directory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    ($report | ConvertTo-Json -Depth 6) | Out-File -FilePath $OutputFile -Encoding utf8
    Write-Host "Wrote $OutputFile"
}
