param([string] $OutputDirectory = 'TestResults/deployment-package')
$ErrorActionPreference = 'Stop'
# Read the version, never hardcode it: package validation compares against the released baseline, so a
# literal 0.1.0 failed CP0003 (assembly version below the 0.2.0 baseline) once that baseline existed. The next
# patch as a prerelease sorts above the release and cannot collide with a published package.
[xml] $coreProject = Get-Content -LiteralPath 'src/AiDotNet.Evolution/AiDotNet.Evolution.csproj' -Raw
$released = [version] ([string] ($coreProject.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1))
$version = '{0}.{1}.{2}-relocation.{3}' -f $released.Major, $released.Minor, ($released.Build + 1), [Guid]::NewGuid().ToString('N')
$packages = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($project in @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.Deployment')) {
    # GeneratePackageOnBuild makes Pack skip its normal build. Override it so a
    # clean consumer job builds every target (including net471) before packing.
    dotnet pack "src/$project/$project.csproj" -c Release -o $packages "-p:Version=$version" -p:GeneratePackageOnBuild=false
    if ($LASTEXITCODE -ne 0) { throw "Package failed: $project" }
}
dotnet run --project eng/fixtures/DeploymentConsumer -c Release "-p:DeploymentTestVersion=$version" "-p:RestoreAdditionalProjectSources=$packages"
if ($LASTEXITCODE -ne 0) { throw 'Standalone deployment package consumer failed.' }
