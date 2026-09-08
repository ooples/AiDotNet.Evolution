[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workflowDirectory = Join-Path $repositoryRoot '.github/workflows'
$releasePath = Join-Path $workflowDirectory 'automated-release.yml'
$attestationPath = Join-Path $workflowDirectory 'attest-release.yml'

function Get-WorkflowJobs {
    param([Parameter(Mandatory)][string] $Path)

    $text = Get-Content -LiteralPath $Path -Raw
    $jobsMatch = [regex]::Match($text, '(?m)^jobs:\r?$')
    if (-not $jobsMatch.Success) {
        throw "$Path has no jobs mapping."
    }

    $jobsText = $text.Substring($jobsMatch.Index)
    $matches = [regex]::Matches(
        $jobsText,
        '(?ms)^  (?<name>[A-Za-z0-9_-]+):\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)')

    $jobs = @{}
    foreach ($match in $matches) {
        $jobs[$match.Groups['name'].Value] = $match.Groups['body'].Value
    }

    return $jobs
}

function Assert-Contract {
    param(
        [Parameter(Mandatory)][bool] $Condition,
        [Parameter(Mandatory)][string] $Message)

    if (-not $Condition) {
        throw "Release workflow contract failed: $Message"
    }

    Write-Host "PASS: $Message"
}

$release = Get-Content -LiteralPath $releasePath -Raw
$attestation = Get-Content -LiteralPath $attestationPath -Raw
$jobs = Get-WorkflowJobs -Path $releasePath

Assert-Contract $jobs.ContainsKey('pack') 'release has a pack job'
Assert-Contract $jobs.ContainsKey('attest') 'release has an isolated attestation call job'
Assert-Contract $jobs.ContainsKey('publish') 'release has a publish job'
Assert-Contract ($jobs['pack'] -notmatch 'id-token:\s*write') 'pack cannot request an OIDC token'
Assert-Contract ($jobs['pack'] -notmatch 'attest-build-provenance') 'pack does not run OIDC-dependent attestation'
Assert-Contract ($jobs['pack'] -match 'retention-days:\s*30') 'verified package bytes remain recoverable for 30 days'
Assert-Contract ($jobs['attest'] -match 'uses:\s*\./\.github/workflows/attest-release\.yml') 'attestation uses a distinct workflow identity'
Assert-Contract ($jobs['attest'] -notmatch 'runs-on:') 'the trusted workflow attestation entry has no runner steps'
Assert-Contract ($jobs['publish'] -match 'id-token:\s*write') 'publish can request the NuGet OIDC token'
Assert-Contract ($jobs['publish'] -match 'NuGet/login@') 'publish performs trusted-publishing login'
Assert-Contract ($jobs['publish'] -match 'needs:\s*\[validate, pack, attest\]') 'publish waits for validation, package creation, and provenance'
Assert-Contract (($release | Select-String -Pattern 'NuGet/login@' -AllMatches).Matches.Count -eq 1) 'the trusted workflow has exactly one NuGet login'
Assert-Contract ($attestation -match 'id-token:\s*write') 'the isolated workflow can request provenance OIDC'
Assert-Contract ($attestation -match 'attest-build-provenance@') 'the isolated workflow creates provenance'
Assert-Contract ($attestation -notmatch 'NuGet/login@|dotnet nuget push') 'the isolated OIDC workflow cannot publish packages'

$allWorkflows = Get-ChildItem -LiteralPath $workflowDirectory -Filter '*.yml' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$loginCount = ($allWorkflows | Select-String -Pattern 'NuGet/login@' -AllMatches).Matches.Count
Assert-Contract ($loginCount -eq 1) 'the repository has exactly one NuGet trusted-publishing login'

Write-Host 'Release workflow security contract passed (16 assertions).'
