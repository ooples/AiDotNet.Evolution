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

    # Also measure the two generic delivery boxes every story carries - "merged PR", "hosted CI green
    # on the merged head" and "CodeRabbit review confirmed present" - which are otherwise answered by
    # hand annotation. Each issue's closing pull requests are read from
    # closedByPullRequestsReferences, then each one's reviews are counted by author and its merge
    # commit's check runs by conclusion. Costs a few API calls per issue, so it is opt-in.
    [Parameter(ParameterSetName = 'Measure')] [switch] $WithDelivery,

    # Classify every unticked box into exactly one bucket, first rule wins, so a reader gets the
    # shape of what is left rather than a count. Costs nothing extra: it reads bodies already
    # fetched. The rule order is part of the measurement - a box naming both a sibling story and the
    # pilot lands in the first bucket - and an earlier hand-eyeballed version of this classification
    # put ~34 of 66 boxes on the pilot when the measured figure is 8 (#108).
    [Parameter(ParameterSetName = 'Measure')] [switch] $Classify,

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

function Measure-BoxBucket {
    <#
    .SYNOPSIS
        Which kind of thing one unticked box is, by the first rule it matches.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)

    # Every pattern is matched case-insensitively and explicitly: PowerShell's -match is
    # case-insensitive by default, so the two anchored rules were silently case-insensitive too and
    # the table read as if it mixed both. The issue references carry a boundary - without it "#1160"
    # matched "#116" - and the first rule's alternation is grouped so "/issues/" cannot match a
    # reference buried mid-sentence while the anchor applies only to the first branch.
    $rules = [ordered] @{
        'dependency on a sibling story'               = '(?i)^(\[(US|V1)-\d+|.{0,40}https?://\S*/issues/\d+)'
        'a merge-time judgement'                      = '(?i)confirm dependency readiness'
        'the paired pilot or the gate campaign'       = '(?i)pilot|campaign|#116|#121|#122|metrics table'
        'a merged PR, hosted CI, or a counted review' = '(?i)merged pr|hosted ci|coderabbit|ci/review|independent review'
        'attached current-revision evidence'          = '(?i)evidence|attach current-revision'
        'an acceptance criterion still unmet'         = '(?i)^\*\*given\*\*'
        'implement and verify every criterion'        = '(?i)^implement and verify'
    }
    foreach ($bucket in $rules.Keys) {
        if ($Text -match $rules[$bucket]) { return $bucket }
    }
    return 'other'
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

    # Measure-BoxBucket, which had no coverage at all: the rule order is the measurement, so the
    # case that names both a sibling story and the pilot has to land in the first bucket, and an
    # unmatched box has to land in 'other' rather than in whichever rule is loosest.
    $cases = @(
        @{ Text = '[US-03: Prevent invalid improvements](https://github.com/o/r/issues/21) and the pilot #116';
           Expect = 'dependency on a sibling story' }
        @{ Text = 'Metrics table filled with measured values'; Expect = 'the paired pilot or the gate campaign' }
        @{ Text = 'A box naming issue #1160, which is not #116'; Expect = 'other' }
        @{ Text = '**Given** a thing, **then** it holds'; Expect = 'an acceptance criterion still unmet' }
        @{ Text = '**GIVEN** shouting, **then** case must not decide the bucket';
           Expect = 'an acceptance criterion still unmet' }
        @{ Text = 'Confirm dependency readiness before merge'; Expect = 'a merge-time judgement' }
        @{ Text = 'Hosted CI green on the merged head'; Expect = 'a merged PR, hosted CI, or a counted review' }
        @{ Text = 'Evidence committed under benchmarks/evidence/'; Expect = 'attached current-revision evidence' }
        @{ Text = 'Something nobody wrote a rule for'; Expect = 'other' }
    )
    foreach ($case in $cases) {
        $actual = Measure-BoxBucket -Text $case.Text
        if ($actual -ne $case.Expect) {
            throw "Bucket wrong for '$($case.Text)': got '$actual', expected '$($case.Expect)'."
        }
    }

    Write-Host '[OK] Measure-StoryCheckboxAudit self-test: markers, nesting, case, dependency refs and all nine bucket cases.'
}

if ($SelfTest) {
    Invoke-SelfTest
    return
}


function Measure-Delivery {
    <#
    .SYNOPSIS
        Closing pull requests for one issue, with each one's review authors and merged-head checks.
    .DESCRIPTION
        Answers the delivery boxes from the repository rather than from an annotation. Two findings
        this exists to keep measurable: CodeRabbit's free tier passes a PR's checks while posting no
        review at all (#238, #293, #294-#296), and before #299 every merge to main cancelled the
        previous merge's run, so "hosted CI green on the merged head" was unobtainable rather than
        unmet - the merge commit's checks show which of the two a box is.

        Measured on three stories when this was written, and the three answers were all different:
        #167's closing PR #253 has 12 CodeRabbit reviews (so its review box is met, where the issue
        says "partially") with 3 cancelled merged-head checks; #169's #258 the same shape; #181's
        #229 has 13 green merged-head checks and no CodeRabbit review at all, only Copilot's.

        A caveat the data carries: closedByPullRequestsReferences can include a release pull request
        that merely mentions the issue (#228 appears under #169), so a reader weighs the PR that did
        the work rather than counting rows.
    #>
    param([int] $Number, [string] $Repository)

    $owner, $name = $Repository.Split('/')
    $query = @"
{ repository(owner: "$owner", name: "$name") { issue(number: $Number) {
    closedByPullRequestsReferences(first: 10, includeClosedPrs: true) { nodes {
        number state mergeCommit { oid }
        reviews(first: 50) { nodes { author { login } state } } } } } } }
"@
    $json = gh api graphql -f query=$query 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "Could not read closing pull requests for issue $Number."
    }

    $pulls = [System.Collections.Generic.List[object]]::new()
    foreach ($pull in ($json | ConvertFrom-Json).data.repository.issue.closedByPullRequestsReferences.nodes) {
        $authors = @{}
        foreach ($review in $pull.reviews.nodes) {
            $login = [string] $review.author.login
            if ($login) { $authors[$login] = 1 + ([int] $authors[$login]) }
        }
        $checks = $null
        if ($pull.mergeCommit -and $pull.mergeCommit.oid) {
            $runs = gh api ("repos/$Repository/commits/" + $pull.mergeCommit.oid + "/check-runs?per_page=100") 2>$null
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($runs)) {
                $conclusions = @{}
                foreach ($run in ($runs | ConvertFrom-Json).check_runs) {
                    $key = if ($run.conclusion) { [string] $run.conclusion } else { [string] $run.status }
                    $conclusions[$key] = 1 + ([int] $conclusions[$key])
                }
                $checks = [pscustomobject] $conclusions
            }
        }
        $pulls.Add([pscustomobject]@{
            Number          = $pull.number
            State           = $pull.state
            MergeCommit     = if ($pull.mergeCommit) { $pull.mergeCommit.oid } else { $null }
            ReviewsByAuthor = [pscustomobject] $authors
            # A review by anything other than the author of the pull request; the box asks for a
            # review, and a green check is not one.
            ReviewCount     = ($pull.reviews.nodes | Measure-Object).Count
            MergedHeadChecks = $checks
        })
    }
    return $pulls.ToArray()
}

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($number in $Issues) {
    $json = gh issue view $number --repo $Repository --json number,state,title,body 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "Could not read issue $number from $Repository."
    }

    $issue = $json | ConvertFrom-Json
    $counts = Measure-CheckboxBody -Body ([string] $issue.body)
    $buckets = $null
    if ($Classify) {
        $buckets = @{}
        # The same matcher Measure-CheckboxBody uses, so the buckets sum to Unchecked instead of to
        # a different, smaller set: that one allows a "*" marker and leading whitespace, and this one
        # only matched a line starting exactly "- [ ]". Review caught the mismatch.
        foreach ($line in ([string] $issue.body) -split "`n") {
            if ($line -notmatch '^\s*[-*]\s*\[\s\]') { continue }
            $bucket = Measure-BoxBucket -Text ($line -replace '^\s*[-*]\s*\[\s\]\s*', '')
            $buckets[$bucket] = 1 + ([int] $buckets[$bucket])
        }
        if (($buckets.Values | Measure-Object -Sum).Sum -ne $counts.Unchecked) {
            throw "Issue $($issue.number): classified $(($buckets.Values | Measure-Object -Sum).Sum) boxes but counted $($counts.Unchecked)."
        }
        $buckets = [pscustomobject] $buckets
    }
    $rows.Add([pscustomobject]@{
        Number          = $issue.number
        State           = $issue.state
        Title           = $issue.title
        Unchecked       = $counts.Unchecked
        Checked         = $counts.Checked
        DependencyRefs  = $counts.DependencyRefs
        # The state the audit exists to remove: closed, yet its own criteria are unticked.
        ClosedWithUnticked = ($issue.state -eq 'CLOSED' -and $counts.Unchecked -gt 0)
        Delivery        = if ($WithDelivery) { Measure-Delivery -Number $issue.number -Repository $Repository } else { $null }
        Buckets         = $buckets
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
