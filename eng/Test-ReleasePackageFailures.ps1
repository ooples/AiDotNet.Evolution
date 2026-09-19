[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PackageDirectory).Path
$validator = Join-Path $PSScriptRoot 'Test-ReleasePackages.ps1'
& $validator -PackageDirectory $source -ExpectedVersion $Version
$tagFixture = Join-Path ([IO.Path]::GetTempPath()) ('evolution-release-tag-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tagFixture | Out-Null
git init --quiet $tagFixture
if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated tag fixture.' }
[IO.File]::WriteAllText((Join-Path $tagFixture 'release-please-config.json'), '{"packages":{".":{}}}')
[IO.File]::WriteAllText((Join-Path $tagFixture '.release-please-manifest.json'), (@{ '.' = $Version } | ConvertTo-Json))
$coreValidator = Join-Path $PSScriptRoot 'Test-Package.ps1'
$corePackage = Join-Path $source "AiDotNet.Evolution.$Version.nupkg"
& $coreValidator -PackagePath $corePackage -ExpectedVersion $Version -RepositoryRoot $tagFixture
$missingTagRejected = $false
try {
    & $coreValidator -PackagePath $corePackage -ExpectedVersion $Version -RepositoryRoot $tagFixture -RequireReleaseTag
} catch {
    if ($_.Exception.Message -notlike 'Release tag * does not exist.') { throw }
    $missingTagRejected = $true
}
if (-not $missingTagRejected) { throw 'Release accepted a missing tag.' }
Write-Host 'PR package passed before tagging; release package rejected missing tag.'
$cases = @('missing-package', 'extra-package', 'dependency-version', 'missing-assembly', 'missing-license', 'missing-dependency')
foreach ($case in $cases) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('evolution-release-negative-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Get-ChildItem -LiteralPath $source -Filter '*.nupkg' | Copy-Item -Destination $fixture
    $target = Join-Path $fixture "AiDotNet.Evolution.Programs.$Version.nupkg"
    switch ($case) {
        'missing-package' { Move-Item -LiteralPath $target -Destination "$target.held" }
        'extra-package' { Copy-Item -LiteralPath $target -Destination (Join-Path $fixture 'unexpected.nupkg') }
        default {
            $archive = [IO.Compression.ZipFile]::Open($target, [IO.Compression.ZipArchiveMode]::Update)
            try {
                if ($case -eq 'missing-assembly') { $archive.GetEntry('lib/net8.0/AiDotNet.Evolution.Programs.dll').Delete() }
                elseif ($case -eq 'missing-license') { $archive.GetEntry('AIDOTNET-LICENSE.txt').Delete() }
                else {
                    $entry = $archive.GetEntry('AiDotNet.Evolution.Programs.nuspec')
                    $reader = [IO.StreamReader]::new($entry.Open())
                    try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    foreach ($dependency in @($spec.SelectNodes('//*[local-name()="dependency" and @id="AiDotNet.Evolution"]'))) {
                        if ($case -eq 'dependency-version') { $dependency.SetAttribute('version', '9.9.9') }
                        else { $dependency.ParentNode.RemoveChild($dependency) | Out-Null }
                    }
                    $entry.Delete()
                    $writer = [IO.StreamWriter]::new($archive.CreateEntry('AiDotNet.Evolution.Programs.nuspec').Open())
                    try { $writer.Write($spec.OuterXml) } finally { $writer.Dispose() }
                }
            } finally { $archive.Dispose() }
        }
    }
    $rejected = $false
    try { & $validator -PackageDirectory $fixture -ExpectedVersion $Version } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid release accepted: $case" }
    Write-Host "Rejected $case"
}
