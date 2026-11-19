[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $UpdateDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$destination = [IO.Path]::GetFullPath($UpdateDirectory)
if (Test-Path -LiteralPath $destination) { throw "Update directory already exists: $destination" }

$manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.MainLibraryName -ne 'NTE.dll') { throw 'Unexpected plugin manifest.' }

$payloadName = 'Nte.PluginPayload.zip'
$stage = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$nested = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    foreach ($asset in $manifest.Assets) {
        $path = [IO.Path]::GetFullPath([string]$asset.FilePath, $root)
        $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
        if ($relative -eq '..' -or $relative.StartsWith('../') -or
            [IO.Path]::IsPathRooted($asset.FilePath) -or -not $seen.Add($relative)) {
            throw "Invalid or duplicate manifest path: $($asset.FilePath)"
        }
        $file = Get-Item -LiteralPath $path
        $hash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $path -Algorithm MD5).Hash))
        if ($file.PSIsContainer -or $file.Length -ne $asset.Size -or $hash -ne $asset.FileHash) {
            throw "Manifest mismatch: $relative"
        }

        if ($relative.Contains('/')) {
            $nested.Add([pscustomobject]@{ Path = $path; Entry = $relative })
            continue
        }
        if ($relative -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $relative -in @($payloadName, 'manifest.json')) {
            throw "Unsupported Release asset name: $relative"
        }
        Copy-Item -LiteralPath $path -Destination (Join-Path $stage $relative)
    }
    if (-not $seen.Contains('NTE.dll') -or -not $seen.Contains('LoginHost/Nte.LoginHost.exe')) {
        throw 'Update assets must contain the plugin DLL and bundled login host.'
    }

    $zip = [IO.Compression.ZipFile]::Open((Join-Path $stage $payloadName), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $nested) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.Path, $file.Entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }

    $manifest.Assets = @(
        Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
            [ordered]@{
                FilePath = $_.Name
                Size = $_.Length
                FileHash = [Convert]::ToBase64String([Convert]::FromHexString(
                    (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash))
            }
        }
    )
    $manifest | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    [IO.Directory]::Move($stage, $destination)
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Get-ChildItem -LiteralPath $stage -File | Remove-Item -Force
        [IO.Directory]::Delete($stage)
    }
}

[pscustomobject]@{ UpdateDirectory = $destination; ReleaseAssets = $manifest.Assets.Count; PayloadEntries = $nested.Count }
