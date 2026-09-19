[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $ConfigPath,
    [string] $DependencySource = 'https://api.nuget.org/v3/index.json'
)
$ErrorActionPreference = 'Stop'
$localSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
[xml] $config = '<configuration><packageSources><clear /></packageSources><packageSourceMapping><clear /></packageSourceMapping><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>'
foreach ($source in @(@{ Key = 'release-local'; Value = $localSource }, @{ Key = 'dependencies'; Value = $DependencySource })) {
    $entry = $config.CreateElement('add')
    $entry.SetAttribute('key', $source.Key)
    $entry.SetAttribute('value', $source.Value)
    $config.configuration.packageSources.AppendChild($entry) | Out-Null
    $mapping = $config.CreateElement('packageSource')
    $mapping.SetAttribute('key', $source.Key)
    $patterns = if ($source.Key -eq 'release-local') {
        @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.CSharp',
            'AiDotNet.Evolution.Deployment', 'AiDotNet.Evolution.Surrogates')
    } else { @('*') }
    foreach ($pattern in $patterns) {
        $package = $config.CreateElement('package')
        $package.SetAttribute('pattern', $pattern)
        $mapping.AppendChild($package) | Out-Null
    }
    $config.configuration.packageSourceMapping.AppendChild($mapping) | Out-Null
}
$config.Save([IO.Path]::GetFullPath($ConfigPath))
