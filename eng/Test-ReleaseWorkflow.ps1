[CmdletBinding()]
param([switch] $NoRestore)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repositoryRoot 'tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj'
$arguments = @(
    'test',
    $testProject,
    '--configuration', 'Release',
    '--framework', 'net10.0',
    '--filter', 'FullyQualifiedName~ReleaseWorkflowSecurityContractTests',
    '--verbosity', 'minimal')
if ($NoRestore) {
    $arguments += '--no-restore'
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw 'Release workflow security contract tests failed.'
}
