[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    # Local packages (before publishing): a directory of npm tarballs (main and per-platform hosts) and one of Python
    # distributions. Without them, -FromRegistry installs the published packages.
    [string] $NpmDirectory,
    [string] $PythonDirectory,
    [switch] $FromRegistry,
    [int] $RegistryAttempts = 10
)
# E-5: installs the npm and PyPI packages into fresh, empty consumers and runs them. Before publishing it installs the
# packed files; after publishing it installs from npmjs.org and pypi.org, retrying while a new version propagates.
$ErrorActionPreference = 'Stop'
if (-not $FromRegistry -and (-not $NpmDirectory -or -not $PythonDirectory)) {
    throw 'Pass -NpmDirectory and -PythonDirectory, or -FromRegistry.'
}
$fixtures = Join-Path $PSScriptRoot 'fixtures/RegistryConsumers'
$work = Join-Path ([IO.Path]::GetTempPath()) ("registry-consumers-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Invoke-Checked([string] $What, [scriptblock] $Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

function Invoke-WithRetry([string] $What, [scriptblock] $Command) {
    for ($attempt = 1; ; $attempt++) {
        & $Command
        if ($LASTEXITCODE -eq 0) { return }
        if ($attempt -ge $RegistryAttempts) { throw "$What failed after $attempt attempts." }
        Write-Host "$What failed (attempt $attempt of $RegistryAttempts); the registry may still be propagating."
        Start-Sleep -Seconds 30
    }
}

try {
    # npm: a consumer with nothing installed. Local tarballs go in through overrides, so the main package's optional
    # host dependencies resolve to the packed hosts rather than to the registry, where this version does not exist yet.
    $npm = Join-Path $work 'npm'
    New-Item -ItemType Directory -Path $npm | Out-Null
    Copy-Item (Join-Path $fixtures 'package.json'), (Join-Path $fixtures 'smoke.mjs') $npm
    Push-Location $npm
    try {
        if ($FromRegistry) {
            Invoke-WithRetry 'npm install from the registry' { npm install --no-audit --no-fund "@aidotnet/evolution@$Version" }
        }
        else {
            $packages = (Resolve-Path -LiteralPath $NpmDirectory).Path
            $main = Get-Item -LiteralPath (Join-Path $packages "aidotnet-evolution-$Version.tgz")
            $manifest = Get-Content (Join-Path $npm 'package.json') -Raw | ConvertFrom-Json -AsHashtable
            $manifest.overrides = [ordered]@{}
            # The release packs all three hosts; a single platform's CI job packs only its own, which is the one this
            # machine installs, so that one is required and the others are used when present.
            $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
            $current = if ($IsWindows) { "win-$architecture" } elseif ($IsMacOS) { "osx-$architecture" } else { "linux-$architecture" }
            foreach ($rid in 'win-x64', 'linux-x64', 'osx-arm64') {
                $hostPath = Join-Path $packages "aidotnet-evolution-host-$rid-$Version.tgz"
                if (Test-Path -LiteralPath $hostPath) {
                    $manifest.overrides["@aidotnet/evolution-host-$rid"] = "file:$((Get-Item -LiteralPath $hostPath).FullName)"
                }
                elseif ($rid -eq $current) { throw "No packed host for this machine: $hostPath" }
            }
            $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $npm 'package.json')
            Invoke-Checked 'npm install of the packed tarballs' { npm install --no-audit --no-fund $main.FullName }
        }
        Invoke-Checked 'the npm consumer' { node smoke.mjs $Version }
    }
    finally { Pop-Location }

    # PyPI: a fresh virtual environment with only the worker package.
    $venv = Join-Path $work 'venv'
    Invoke-Checked 'python -m venv' { python -m venv $venv }
    $python = if ($IsWindows) { Join-Path $venv 'Scripts/python.exe' } else { Join-Path $venv 'bin/python' }
    if ($FromRegistry) {
        Invoke-WithRetry 'pip install from the registry' { & $python -m pip install --no-cache-dir "aidotnet-evolution-worker==$Version" }
    }
    else {
        $wheel = Get-Item -Path (Join-Path (Resolve-Path -LiteralPath $PythonDirectory).Path "aidotnet_evolution_worker-$Version-*.whl")
        Invoke-Checked 'pip install of the wheel' { & $python -m pip install --no-index $wheel.FullName }
    }
    Push-Location $work
    try { Invoke-Checked 'the Python consumer' { & $python (Join-Path $fixtures 'smoke.py') $Version } }
    finally { Pop-Location }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}