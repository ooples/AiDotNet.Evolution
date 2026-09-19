param(
    [Parameter(Mandatory)][string] $InputPath,
    [Parameter(Mandatory)][string] $OutputPath
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $InputPath).Path
$destination = [IO.Path]::GetFullPath($OutputPath)
if ($destination.StartsWith($root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence archive must be outside the input tree.'
}
# Content addressing preserves every file while storing repeated benchmark inputs/outputs once.
# Run only after the campaign has stopped; the second pass detects changes during export.
$files = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File)
if (@(Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Evidence tree must not contain links.'
}
$index = [ordered]@{ schema = 'evolution-content-addressed-evidence-v1'; files = @() }
$seen = [Collections.Generic.HashSet[string]]::new()
$stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in ($files | Sort-Object FullName)) {
            if ($file.Length -gt 32MB) { throw 'Individual evidence file exceeds 32 MiB.' }
            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            $index.files += @{ path = $relative; sha256 = $hash; bytes = $bytes.Length }
            if ($seen.Add($hash)) {
                $entry = $zip.CreateEntry('blobs/' + $hash, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $body = $entry.Open()
                try { $body.Write($bytes) } finally { $body.Dispose() }
            }
        }
        $entry = $zip.CreateEntry('index.json')
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $body = $entry.Open()
        try { $body.Write([Text.Encoding]::UTF8.GetBytes(($index | ConvertTo-Json -Depth 5 -Compress))) } finally { $body.Dispose() }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead($destination)
try {
    $current = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File | Sort-Object FullName)
    if (($current.FullName -join "`n") -cne (($files | Sort-Object FullName).FullName -join "`n")) {
        throw 'Evidence file set changed during export.'
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('index.json').Open(), [Text.Encoding]::UTF8)
    try { $storedIndex = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($storedIndex -cne ($index | ConvertTo-Json -Depth 5 -Compress)) { throw 'Archived index mismatch.' }
    foreach ($record in $index.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $root $record.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.sha256) {
            throw 'Source evidence changed during export.'
        }
    }
    foreach ($hash in $seen) {
        $body = $zip.GetEntry('blobs/' + $hash).Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($body)).ToLowerInvariant() } finally { $body.Dispose() }
        if ($actual -ne $hash) { throw 'Archived evidence hash mismatch.' }
    }
} finally { $zip.Dispose() }
[pscustomobject]@{ Files = $files.Count; UniqueBlobs = $seen.Count; Bytes = (Get-Item -LiteralPath $destination).Length; SHA256 = (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() }
