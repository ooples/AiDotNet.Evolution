[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PackageDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
# Isolated cache avoids accidentally testing previously published packages.
$cache = Join-Path ([IO.Path]::GetTempPath()) ('evolution-release-consumer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $cache | Out-Null
$configPath = Join-Path $cache 'NuGet.Config'
& "$PSScriptRoot/New-ReleaseNuGetConfig.ps1" -PackageDirectory $packages -ConfigPath $configPath
$restore = @("-p:RestoreConfigFile=$configPath", "-p:RestorePackagesPath=$cache", '-p:RestoreAdditionalProjectSources=', '-p:RestoreFallbackFolders=')
dotnet run --project "$root/eng/fixtures/DeploymentConsumer" -c Release `
    "-p:DeploymentTestVersion=$Version" @restore
if ($LASTEXITCODE -ne 0) { throw 'Release deployment/program consumer failed.' }
dotnet run --project "$root/eng/SurrogatePackageSmoke" -c Release `
    "-p:CorePackageVersion=$Version" "-p:SurrogatePackageVersion=$Version" `
    @restore
if ($LASTEXITCODE -ne 0) { throw 'Release surrogate consumer failed.' }
dotnet run --project "$root/eng/fixtures/ReleaseCompilerConsumer" -c Release `
    "-p:ReleaseTestVersion=$Version" @restore
if ($LASTEXITCODE -ne 0) { throw 'Release compiler consumer failed.' }
foreach ($id in @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.CSharp',
    'AiDotNet.Evolution.Deployment', 'AiDotNet.Evolution.Surrogates')) {
    $metadataPath = Join-Path $cache "$($id.ToLowerInvariant())/$($Version.ToLowerInvariant())/.nupkg.metadata"
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($metadata.source) -ne $packages) { throw "Non-local release package restored: $id" }
}
Write-Host 'Verified local restore origin for all five product packages.'
