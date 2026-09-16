param([string] $OutputDirectory = 'TestResults/deployment-package')
$ErrorActionPreference = 'Stop'
$version = '0.1.0-relocation.' + [Guid]::NewGuid().ToString('N')
$packages = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($project in @('AiDotNet.Evolution', 'AiDotNet.Evolution.Programs', 'AiDotNet.Evolution.Deployment')) {
    # GeneratePackageOnBuild makes Pack skip its normal build. Override it so a
    # clean consumer job builds every target (including net471) before packing.
    dotnet pack "src/$project/$project.csproj" -c Release -o $packages "-p:Version=$version" -p:GeneratePackageOnBuild=false
    if ($LASTEXITCODE -ne 0) { throw "Package failed: $project" }
}
dotnet run --project eng/fixtures/DeploymentConsumer -c Release "-p:DeploymentTestVersion=$version" "-p:RestoreAdditionalProjectSources=$packages"
if ($LASTEXITCODE -ne 0) { throw 'Standalone deployment package consumer failed.' }
