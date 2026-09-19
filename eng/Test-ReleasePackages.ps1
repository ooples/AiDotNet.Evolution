[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$ids = @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.CSharp',
    'AiDotNet.Evolution.Deployment', 'AiDotNet.Evolution.Surrogates')
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg')
$expectedNames = @($ids | ForEach-Object { "$_.${ExpectedVersion}.nupkg" })
if (Compare-Object ($expectedNames | Sort-Object) (@($packages.Name) | Sort-Object)) {
    throw 'Release package inventory is missing, unexpected, or version-mismatched.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($id in $ids) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageDirectory "$id.$ExpectedVersion.nupkg"))
    try {
        $entry = $archive.GetEntry("$id.nuspec")
        if ($null -eq $entry) { throw "Missing nuspec: $id" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.package.metadata
        if ($metadata.id -cne $id -or $metadata.version -cne $ExpectedVersion) { throw "Invalid identity: $id" }
        $frameworks = @('net8.0', 'net10.0')
        if ($id -in @('AiDotNet.Evolution', 'AiDotNet.Evolution.Surrogates')) { $frameworks += 'net471' }
        foreach ($framework in $frameworks) {
            if ($null -eq $archive.GetEntry("lib/$framework/$id.dll")) { throw "Missing $framework assembly: $id" }
        }
        if ($null -eq $archive.GetEntry('README.md')) { throw "Missing README: $id" }
        $dependencies = @($spec.SelectNodes('//*[local-name()="dependency"]'))
        $requiredDependency = switch ($id) {
            'AiDotNet.Evolution.Programs' { 'AiDotNet.Evolution' }
            'AiDotNet.Evolution.Surrogates' { 'AiDotNet.Evolution' }
            'AiDotNet.Evolution.CSharp' { 'AiDotNet.Evolution.Programs' }
            'AiDotNet.Evolution.Deployment' { 'AiDotNet.Evolution.Programs' }
        }
        if ($requiredDependency -and $requiredDependency -notin @($dependencies.id)) {
            throw "Missing internal dependency: $id -> $requiredDependency"
        }
        foreach ($dependency in $dependencies) {
            if ($dependency.id -in $ids -and $dependency.version -notin @($ExpectedVersion, "[$ExpectedVersion, )", "[$ExpectedVersion]")) {
                throw "Release dependency drift: $id -> $($dependency.id) $($dependency.version)"
            }
        }
        if ($id -in @('AiDotNet.Evolution', 'AiDotNet.Evolution.Surrogates')) {
            if ($metadata.license.InnerText -cne 'Apache-2.0') { throw "Invalid license: $id" }
        } elseif ($null -eq $archive.GetEntry('AIDOTNET-LICENSE.txt') -or $metadata.license.InnerText -cne 'AIDOTNET-LICENSE.txt') {
            throw "Missing original license: $id"
        }
    } finally { $archive.Dispose() }
}
Write-Host "Verified all $($ids.Count) release packages at $ExpectedVersion."
