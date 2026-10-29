[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$ids = @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.CSharp', 'AiDotNet.Evolution.Ptx',
    'AiDotNet.Evolution.Deployment', 'AiDotNet.Evolution.Surrogates', 'AiDotNet.Evolution.Cli')
# dotnet tool packages: assemblies under tools/<tfm>/any, a DotnetTool package type, and no dependency groups.
$tools = @{ 'AiDotNet.Evolution.Cli' = 'aidotnet-evolve' }
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg')
$expectedNames = @($ids | ForEach-Object { "$_.${ExpectedVersion}.nupkg" })
if (Compare-Object ($expectedNames | Sort-Object) (@($packages.Name) | Sort-Object)) {
    throw 'Release package inventory is missing, unexpected, or version-mismatched.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Every library ships ReadyToRun images for these runtimes beside its IL (E-1); a portable app loads the one that matches.
$readyToRunRuntimes = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
$readyToRunMachines = @{ 'x64' = [Reflection.PortableExecutable.Machine]::Amd64; 'arm64' = [Reflection.PortableExecutable.Machine]::Arm64 }
# crossgen2 marks a non-Windows image by XOR-ing the machine with an OS constant.
$readyToRunOs = @{ 'win' = 0; 'linux' = 0x7B79; 'osx' = 0x4644 }
function Read-PackageImage([IO.Compression.ZipArchiveEntry] $Entry) {
    $buffer = [IO.MemoryStream]::new()
    $stream = $Entry.Open()
    try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
    $buffer.Position = 0
    $pe = [Reflection.PortableExecutable.PEReader]::new($buffer)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        [pscustomobject] @{
            Mvid = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid)
            Machine = [int] $pe.PEHeaders.CoffHeader.Machine
            ReadyToRun = $pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size -gt 0
        }
    } finally { $pe.Dispose() }
}
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
        if ($id -in @('AiDotNet.Evolution', 'AiDotNet.Evolution.Surrogates')) { $frameworks += 'net471', 'net462', 'netstandard2.0' }
        foreach ($framework in $frameworks) {
            $assembly = if ($tools.ContainsKey($id)) { "tools/$framework/any/$($tools[$id]).dll" } else { "lib/$framework/$id.dll" }
            if ($null -eq $archive.GetEntry($assembly)) { throw "Missing $framework assembly: $id" }
        }
        if (-not $tools.ContainsKey($id)) {
            foreach ($framework in @('net8.0', 'net10.0')) {
                $il = Read-PackageImage $archive.GetEntry("lib/$framework/$id.dll")
                if ($il.ReadyToRun) { throw "IL assembly is precompiled: $id [$framework]" }
                foreach ($runtime in $readyToRunRuntimes) {
                    $entry = $archive.GetEntry("runtimes/$runtime/lib/$framework/$id.dll")
                    if ($null -eq $entry) { throw "Missing ReadyToRun image: $id [$runtime/$framework]" }
                    $image = Read-PackageImage $entry
                    $os, $architecture = $runtime -split '-'
                    $machine = [int] $readyToRunMachines[$architecture] -bxor $readyToRunOs[$os]
                    if (-not $image.ReadyToRun -or $image.Machine -ne $machine) { throw "Not a $runtime ReadyToRun image: $id [$framework]" }
                    if ($image.Mvid -ne $il.Mvid) { throw "ReadyToRun image was not compiled from the packaged IL: $id [$runtime/$framework]" }
                }
            }
        }
        if ($tools.ContainsKey($id) -and 'DotnetTool' -notin @($spec.SelectNodes('//*[local-name()="packageType"]').name)) {
            throw "Not a dotnet tool package: $id"
        }
        if ($null -eq $archive.GetEntry('README.md')) { throw "Missing README: $id" }
        $dependencies = @($spec.SelectNodes('//*[local-name()="dependency"]'))
        $requiredDependency = switch ($id) {
            'AiDotNet.Evolution.Programs' { 'AiDotNet.Evolution' }
            'AiDotNet.Evolution.Surrogates' { 'AiDotNet.Evolution' }
            'AiDotNet.Evolution.CSharp' { 'AiDotNet.Evolution.Programs' }
            'AiDotNet.Evolution.Ptx' { 'AiDotNet.Evolution.Programs' }
            'AiDotNet.Evolution.Deployment' { 'AiDotNet.Evolution.Programs' }
        }
        if ($requiredDependency) {
            $groups = @($spec.SelectNodes('//*[local-name()="dependencies"]/*[local-name()="group"]'))
            if ($groups.Count -eq 0) {
                if ($requiredDependency -notin @($dependencies.id)) { throw "Missing internal dependency: $id -> $requiredDependency" }
            } else {
                foreach ($group in $groups) {
                    $groupDependencies = @($group.SelectNodes('*[local-name()="dependency"]'))
                    if ($requiredDependency -notin @($groupDependencies.id)) {
                        throw "Missing internal dependency: $id [$($group.targetFramework)] -> $requiredDependency"
                    }
                }
                foreach ($framework in $frameworks) {
                    $nuspecFramework = switch ($framework) {
                        'net471' { '.NETFramework4.7.1' }
                        'net462' { '.NETFramework4.6.2' }
                        'netstandard2.0' { '.NETStandard2.0' }
                        default { $framework }
                    }
                    if ($nuspecFramework -notin @($groups.targetFramework)) {
                        throw "Missing dependency group: $id [$nuspecFramework]"
                    }
                }
            }
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
