[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [string] $SourceDirectory = (Split-Path -Parent $PSScriptRoot)
)
# E-5: one CycloneDX SBOM per released NuGet package, written beside it as <id>.<version>.cdx.json and attached to the
# GitHub release. It lists what a consumer of the package receives. Restore records which references are build-only
# (suppressParent "All": PrivateAssets="all" in a project, and the SDK's implicit Source Link and .NET Framework
# reference assemblies), so each package's components must be exactly the closure of its other references: nothing
# build-only listed, nothing shipped missing. Needs a restored source tree (eng/Pack-Release.ps1 restores it).
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $SourceDirectory).Path
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
$ids = @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.CSharp', 'AiDotNet.Evolution.Ptx',
    'AiDotNet.Evolution.Deployment', 'AiDotNet.Evolution.Surrogates', 'AiDotNet.Evolution.Cli')

function Get-Closure($Target, [string[]] $Roots) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($name in $Roots) { if ($seen.Add($name)) { $queue.Enqueue($name) } }
    while ($queue.Count) {
        $name = $queue.Dequeue()
        $entry = $Target.PSObject.Properties | Where-Object { ($_.Name -split '/')[0] -ieq $name } | Select-Object -First 1
        if ($null -eq $entry -or $null -eq $entry.Value.dependencies) { continue }
        foreach ($dependency in $entry.Value.dependencies.PSObject.Properties.Name) {
            if ($seen.Add($dependency)) { $queue.Enqueue($dependency) }
        }
    }
    , $seen
}

# Per project: the packages it ships (by framework, unioned) and those only its build uses.
$shippedBy = @{}
$referencesBy = @{}
$buildOnly = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($project in $ids | ForEach-Object { Get-Item -LiteralPath "$root/src/$_/$_.csproj" }) {
    $assetsPath = Join-Path $project.DirectoryName 'obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) { throw "Not restored: $($project.Name)" }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $shipped = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $referencesBy[$project.BaseName] = @($assets.project.restore.frameworks.PSObject.Properties | ForEach-Object { $_.Value.projectReferences.PSObject.Properties.Name } |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) } | Where-Object { $_ -in $ids } | Sort-Object -Unique)
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        $roots = @($framework.Value.dependencies.PSObject.Properties)
        $private = @($roots | Where-Object { $_.Value.suppressParent -ieq 'All' } | ForEach-Object Name)
        $public = @($roots | Where-Object { $_.Value.suppressParent -ine 'All' } | ForEach-Object Name)
        # Project references ship their own packages too.
        $target = $assets.targets.PSObject.Properties | Where-Object { $_.Name -ieq $framework.Value.targetAlias -or $_.Name -like "*$($framework.Name)" } |
            Where-Object { $_.Name -notlike '*/*' } | Select-Object -First 1
        if ($null -eq $target) { $target = $assets.targets.PSObject.Properties | Select-Object -First 1 }
        $public += @($target.Value.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' } | ForEach-Object { ($_.Name -split '/')[0] })
        $publicClosure = Get-Closure $target.Value $public
        $privateClosure = Get-Closure $target.Value $private
        foreach ($name in $publicClosure) { $shipped.Add($name) | Out-Null }
        foreach ($name in $privateClosure) { if (-not $publicClosure.Contains($name)) { $buildOnly.Add($name) | Out-Null } }
    }
    $shippedBy[$project.BaseName] = $shipped
}
$everShipped = @($shippedBy.Values | ForEach-Object { $_ })
$conflict = @($buildOnly | Where-Object { $_ -in $everShipped })
if ($conflict.Count) { throw "Build-only in one project and shipped by another, so the SBOM cannot exclude it: $($conflict -join ', ')" }

foreach ($id in $ids) {
    $file = "$id.$Version.cdx.json"
    dotnet tool run dotnet-CycloneDX "$root/src/$id/$id.csproj" --recursive --disable-package-restore --output-format Json `
        --output $packages --filename $file --set-name $id --set-version $Version --set-type Library --exclude-filter ($buildOnly -join ',')
    if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed: $id" }
    $bom = Get-Content -LiteralPath (Join-Path $packages $file) -Raw | ConvertFrom-Json
    if ($bom.bomFormat -cne 'CycloneDX' -or $bom.metadata.component.name -cne $id -or $bom.metadata.component.version -cne $Version) {
        throw "SBOM does not describe $id $Version."
    }
    $listed = @($bom.components | ForEach-Object name | Sort-Object -Unique)
    # --recursive lists what the referenced product packages ship on all their frameworks, which this package's
    # consumers receive through them; each of those packages' own SBOM is checked against its own closure.
    $referenced = [Collections.Generic.HashSet[string]]::new()
    $pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($id)
    while ($pending.Count) { foreach ($next in $referencesBy[$pending.Dequeue()]) { if ($referenced.Add($next)) { $pending.Enqueue($next) } } }
    $closure = @($shippedBy[$id]) + @($referenced | ForEach-Object { @($shippedBy[$_]) })
    $expected = @($closure | Where-Object { $_ -notin $ids } | Sort-Object -Unique)
    $extra = @($listed | Where-Object { $_ -notin $expected })
    $missing = @($expected | Where-Object { $_ -notin $listed })
    if ($extra.Count -or $missing.Count) {
        throw "SBOM for $id does not match what it ships. Not shipped: $($extra -join ', '). Missing: $($missing -join ', ')"
    }
    Write-Host "SBOM $file`: $($listed.Count) components, exactly the shipped closure."
}
