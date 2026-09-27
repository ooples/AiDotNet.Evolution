[CmdletBinding()]
param(
    [string] $TensorsSourceDirectory,
    [string] $OutputDirectory = 'TestResults/gpu-consumer',
    [switch] $ContractsOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; evidence must not be overwritten.' }
New-Item -ItemType Directory -Path $output | Out-Null
$properties = @('-p:GeneratePackageOnBuild=false', '-p:TargetFrameworks=net10.0')
$sourceRevision = 'NuGet:AiDotNet.Tensors/0.130.3'
if ($TensorsSourceDirectory) {
    $sourceRoot = (Resolve-Path -LiteralPath $TensorsSourceDirectory).Path
    $sourceRevision = git -C $sourceRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Tensors source revision.' }
    $dirty = git -C $sourceRoot status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Use a clean Tensors source checkout for reproducible evidence.' }
    $properties += "-p:TensorsProject=$sourceRoot/src/AiDotNet.Tensors/AiDotNet.Tensors.csproj"
    $properties += '-p:UseLocalEvolution=true'
    $properties += "-p:EvolutionProjectPath=$root/src/AiDotNet.Evolution/AiDotNet.Evolution.csproj"
}
dotnet build "$root/benchmarks/EvolutionGpu/EvolutionGpu.csproj" -c Release @properties *> (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'GPU consumer build failed; inspect build.log.' }
$binary = "$root/benchmarks/EvolutionGpu/bin/Release/net10.0/EvolutionGpu.dll"
dotnet $binary --self-test *> (Join-Path $output 'contracts.log')
if ($LASTEXITCODE -ne 0) { throw 'GPU consumer contracts failed.' }
if ($ContractsOnly) { return }
# WDDM desktop activity cannot be reliably excluded. Preserve environment and do
# not label these captures as isolated device-only measurements.
$snapshot = & nvidia-smi --query-gpu=name,uuid,driver_version,utilization.gpu,memory.used --format=csv
if ($LASTEXITCODE -ne 0) { throw 'nvidia-smi failed; no synthetic replacement.' }
$metadata = @{ TensorsRevision = $sourceRevision; CapturedUtc = [DateTime]::UtcNow.ToString('o'); NvidiaSmi = $snapshot }
[IO.File]::WriteAllText((Join-Path $output 'environment.json'), ($metadata | ConvertTo-Json))
foreach ($run in 1..3) {
    dotnet $binary (Join-Path $output "run-$run.json") *> (Join-Path $output "run-$run.log")
    if ($LASTEXITCODE -ne 0) { throw "GPU run $run failed; retained log and earlier results. Do not promote." }
}
Write-Host "Three real GPU processes completed; evidence: $output"
