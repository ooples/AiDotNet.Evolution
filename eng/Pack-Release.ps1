[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $SourceDirectory = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') {
    throw 'Invalid release version.'
}
$root = (Resolve-Path -LiteralPath $SourceDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) {
    throw 'Release output directory must be empty; stale packages must never be published.'
}
$projects = @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.CSharp',
    'AiDotNet.Evolution.Deployment', 'AiDotNet.Evolution.Surrogates')
# Fail closed if a new package was added without updating the release set.
$packable = @(Get-ChildItem -Path "$root/src/*/*.csproj" | Where-Object {
    [xml] $projectXml = Get-Content -LiteralPath $_.FullName -Raw
    @($projectXml.Project.PropertyGroup.IsPackable) -notcontains 'false'
} | ForEach-Object BaseName)
if (Compare-Object ($projects | Sort-Object) ($packable | Sort-Object)) {
    throw 'The release package set does not match the packable src projects.'
}
foreach ($project in $projects) {
    # Version must reach ProjectReferences too, not only the top-level nuspec.
    # GeneratePackageOnBuild otherwise makes Pack skip building on clean runners.
    dotnet pack "$root/src/$project/$project.csproj" -c Release -o $output `
        "-p:Version=$Version" "-p:PackageVersion=$Version" -p:GeneratePackageOnBuild=false
    if ($LASTEXITCODE -ne 0) { throw "Release pack failed: $project" }
}
& "$PSScriptRoot/Test-ReleasePackages.ps1" -PackageDirectory $output -ExpectedVersion $Version
