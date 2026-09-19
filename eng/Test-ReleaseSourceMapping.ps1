[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('evolution-release-mapping-' + [Guid]::NewGuid().ToString('N'))
$local = New-Item -ItemType Directory -Path (Join-Path $fixture 'local')
$other = New-Item -ItemType Directory -Path (Join-Path $fixture 'other')
$name = "AiDotNet.Evolution.$Version.nupkg"
Copy-Item -LiteralPath (Join-Path $PackageDirectory $name) -Destination $other.FullName
$config = Join-Path $fixture 'NuGet.Config'
& "$PSScriptRoot/New-ReleaseNuGetConfig.ps1" -PackageDirectory $local.FullName -ConfigPath $config -DependencySource $other.FullName
[xml] $project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="AiDotNet.Evolution" /></ItemGroup></Project>'
$project.Project.ItemGroup.PackageReference.SetAttribute('Version', "[$Version]")
$projectPath = Join-Path $fixture 'Mapping.csproj'
$project.Save($projectPath)
$cache = Join-Path $fixture 'cache'
$log = Join-Path $fixture 'restore.log'
dotnet restore $projectPath --configfile $config --packages $cache --force --no-http-cache *> $log
if ($LASTEXITCODE -eq 0) { throw 'Product package incorrectly restored from the non-local feed.' }
if ((Get-Content -LiteralPath $log -Raw) -notmatch 'NU1101') { throw "Unexpected restore failure; inspect $log" }
Copy-Item -LiteralPath (Join-Path $PackageDirectory $name) -Destination $local.FullName
dotnet restore $projectPath --configfile $config --packages $cache --force --no-http-cache *> $log
if ($LASTEXITCODE -ne 0) { throw "Local product restore failed; inspect $log" }
$metadata = Get-Content -LiteralPath (Join-Path $cache "aidotnet.evolution/$Version/.nupkg.metadata") -Raw | ConvertFrom-Json
if ([IO.Path]::GetFullPath($metadata.source) -ne $local.FullName) { throw 'Restored package did not originate in the local feed.' }
Write-Host 'Source mapping rejected non-local product and accepted local product with verified origin.'
