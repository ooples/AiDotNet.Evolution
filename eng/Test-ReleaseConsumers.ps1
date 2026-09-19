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
dotnet run --project "$root/eng/fixtures/DeploymentConsumer" -c Release `
    "-p:DeploymentTestVersion=$Version" "-p:RestoreAdditionalProjectSources=$packages" "-p:RestorePackagesPath=$cache"
if ($LASTEXITCODE -ne 0) { throw 'Release deployment/program consumer failed.' }
dotnet run --project "$root/eng/SurrogatePackageSmoke" -c Release `
    "-p:CorePackageVersion=$Version" "-p:SurrogatePackageVersion=$Version" `
    "-p:RestoreAdditionalProjectSources=$packages" "-p:RestorePackagesPath=$cache"
if ($LASTEXITCODE -ne 0) { throw 'Release surrogate consumer failed.' }
dotnet run --project "$root/eng/fixtures/ReleaseCompilerConsumer" -c Release `
    "-p:ReleaseTestVersion=$Version" "-p:RestoreAdditionalProjectSources=$packages" "-p:RestorePackagesPath=$cache"
if ($LASTEXITCODE -ne 0) { throw 'Release compiler consumer failed.' }
