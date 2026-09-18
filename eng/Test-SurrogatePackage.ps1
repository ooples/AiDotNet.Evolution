param(
    [Parameter(Mandatory = $true)][string] $CorePackagePath,
    [Parameter(Mandatory = $true)][string] $AdapterPackagePath,
    [Parameter(Mandatory = $true)][string] $CoreVersion,
    [Parameter(Mandatory = $true)][string] $AdapterVersion
)
$ErrorActionPreference = 'Stop'
$core = (Resolve-Path -LiteralPath $CorePackagePath).Path
$adapter = (Resolve-Path -LiteralPath $AdapterPackagePath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ((Get-Item -LiteralPath $adapter).Length -gt 5MB) { throw 'Adapter package exceeds its size bound.' }
$archive = [System.IO.Compression.ZipFile]::OpenRead($adapter)
try {
    $required = @('AiDotNet.Evolution.Surrogates.nuspec', 'README.md')
    foreach ($framework in @('net10.0','net8.0','net471')) {
        $required += "lib/$framework/AiDotNet.Evolution.Surrogates.dll", "lib/$framework/AiDotNet.Evolution.Surrogates.xml"
    }
    $entries = @($archive.Entries | ForEach-Object FullName)
    foreach ($entry in $required) { if ($entries -notcontains $entry) { throw "Missing adapter entry '$entry'." } }
    if (@($entries | Where-Object { $_ -match '^lib/.+\.dll$' -and $_ -notmatch '/AiDotNet\.Evolution\.Surrogates\.dll$' }).Count -ne 0) {
        throw 'Adapter embeds an unexpected assembly.'
    }
    $reader = [System.IO.StreamReader]::new($archive.GetEntry('AiDotNet.Evolution.Surrogates.nuspec').Open())
    try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $ns.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)
    $metadata = $nuspec.SelectSingleNode('/n:package/n:metadata', $ns)
    if ($metadata.id -ne 'AiDotNet.Evolution.Surrogates' -or $metadata.version -ne $AdapterVersion -or $metadata.license.InnerText -ne 'Apache-2.0') {
        throw 'Adapter package identity, version or license differs.'
    }
    $groups = @($nuspec.SelectNodes('//n:dependencies/n:group', $ns))
    if ($groups.Count -ne 3) { throw 'Adapter must declare all three framework dependency groups.' }
    foreach ($group in $groups) {
        $dependencies = @($group.SelectNodes('n:dependency', $ns))
        if ($dependencies.Count -ne 1 -or $dependencies[0].id -ne 'AiDotNet.Evolution' -or $dependencies[0].version -ne $CoreVersion) {
            throw 'Adapter must depend only on the matching core in every framework.'
        }
    }
} finally { $archive.Dispose() }

# Never use the global cache: existing published packages can share these unreleased source versions.
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $repo ("TestResults/surrogate-package-" + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $scratch 'feed'; $packages = Join-Path $scratch 'packages'
$null = New-Item -ItemType Directory -Path $feed
Copy-Item -LiteralPath $core -Destination (Join-Path $feed "AiDotNet.Evolution.$CoreVersion.nupkg")
Copy-Item -LiteralPath $adapter -Destination (Join-Path $feed "AiDotNet.Evolution.Surrogates.$AdapterVersion.nupkg")
$project = Join-Path $PSScriptRoot 'SurrogatePackageSmoke/SurrogatePackageSmoke.csproj'
$properties = @("-p:CorePackageVersion=$CoreVersion", "-p:SurrogatePackageVersion=$AdapterVersion",
    "-p:BaseIntermediateOutputPath=$scratch/obj/", "-p:BaseOutputPath=$scratch/bin/", "-p:RestorePackagesPath=$packages", '-p:NuGetAudit=false')
dotnet restore $project --source $feed --packages $packages @properties
if ($LASTEXITCODE -ne 0) { throw 'Isolated package restore failed.' }
foreach ($pair in @(@('aidotnet.evolution',$CoreVersion,$core), @('aidotnet.evolution.surrogates',$AdapterVersion,$adapter))) {
    $installed = Join-Path $packages ("{0}/{1}/{0}.{1}.nupkg" -f $pair[0], $pair[1])
    if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $pair[2] -Algorithm SHA256).Hash) {
        throw 'Consumer did not restore the exact locally packed bytes.'
    }
}
dotnet build $project -c Release --no-restore -m:1 -p:UseSharedCompilation=false @properties
if ($LASTEXITCODE -ne 0) { throw 'Isolated package consumer build failed.' }
dotnet (Join-Path $scratch 'bin/Release/net10.0/SurrogatePackageSmoke.dll')
if ($LASTEXITCODE -ne 0) { throw 'Isolated package consumer failed.' }
Write-Output "Validated optional package and exact local dependency bytes in $scratch"
