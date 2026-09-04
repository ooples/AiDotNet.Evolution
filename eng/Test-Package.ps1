param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$maximumPackageBytes = 5MB

$packageInfo = Get-Item -LiteralPath $resolvedPackage
if ($packageInfo.Length -gt $maximumPackageBytes) {
    throw "Package is unexpectedly large: $($packageInfo.Length) bytes."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    $entries = @($archive.Entries | ForEach-Object FullName)
    $required = @(
        'AiDotNet.Evolution.nuspec',
        'README.md',
        'THIRD-PARTY-NOTICES.md',
        'lib/net10.0/AiDotNet.Evolution.dll',
        'lib/net10.0/AiDotNet.Evolution.xml',
        'lib/net8.0/AiDotNet.Evolution.dll',
        'lib/net8.0/AiDotNet.Evolution.xml',
        'lib/net471/AiDotNet.Evolution.dll',
        'lib/net471/AiDotNet.Evolution.xml'
    )
    foreach ($entry in $required) {
        if ($entries -notcontains $entry) {
            throw "Package is missing required entry '$entry'."
        }
    }

    $unexpectedAssemblies = @($entries | Where-Object {
        $_ -match '^lib/.+\.dll$' -and $_ -notmatch '/AiDotNet\.Evolution\.dll$'
    })
    if ($unexpectedAssemblies.Count -ne 0) {
        throw "Package contains unexpected assemblies: $($unexpectedAssemblies -join ', ')"
    }

    $nuspecEntry = $archive.GetEntry('AiDotNet.Evolution.nuspec')
    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try {
        [xml] $nuspec = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $namespace = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $namespace.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)
    $id = $nuspec.SelectSingleNode('/n:package/n:metadata/n:id', $namespace).InnerText
    $version = $nuspec.SelectSingleNode('/n:package/n:metadata/n:version', $namespace).InnerText
    $license = $nuspec.SelectSingleNode('/n:package/n:metadata/n:license', $namespace).InnerText
    if ($id -ne 'AiDotNet.Evolution') { throw "Unexpected package ID '$id'." }
    if ($version -ne $ExpectedVersion) { throw "Expected version '$ExpectedVersion', found '$version'." }
    if ($license -ne 'Apache-2.0') { throw "Unexpected license '$license'." }

    $forbidden = @('AiDotNet', 'AiDotNet.Tensors', 'Newtonsoft.Json')
    $dependencies = @($nuspec.SelectNodes('//n:dependency', $namespace) | ForEach-Object { $_.id })
    foreach ($dependency in $forbidden) {
        if ($dependencies -contains $dependency) {
            throw "Package must not depend on '$dependency'."
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Validated $resolvedPackage"
