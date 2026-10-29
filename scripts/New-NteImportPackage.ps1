[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $ArchivePath
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\','/')
$archivePath = [IO.Path]::GetFullPath($ArchivePath)
if (Test-Path -LiteralPath $archivePath) { throw "Archive already exists: $archivePath" }
$manifestPath = Join-Path $root 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.MainLibraryName -ne 'NTE.dll' -or $manifest.PluginStandardVersion -ne '0.1.6.0') { throw 'Unexpected plugin manifest.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$assets = @($manifest.Assets) + @([pscustomobject]@{ FilePath = 'manifest.json'; FileHash = $null; Size = $null })
foreach ($asset in $assets) {
    $name = [string]$asset.FilePath
    if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or -not $seen.Add($name)) { throw "Invalid or duplicate manifest path: $name" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $name))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Asset escapes package directory: $name" }
    $file = Get-Item -LiteralPath $path
    if ($null -ne $asset.Size) {
        $hash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $path -Algorithm MD5).Hash))
        if ($file.Length -ne $asset.Size -or $hash -ne $asset.FileHash) { throw "Manifest mismatch: $name" }
    }
}
New-Item -ItemType Directory -Path (Split-Path $archivePath -Parent) -Force | Out-Null
$temporary = $archivePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $fileStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($asset in $assets) {
                # The installed importer indexes entry.FullName verbatim. Keep
                # precisely the separator style recorded in the manifest.
                $name = [string]$asset.FilePath
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $input = [IO.File]::OpenRead((Join-Path $root $name))
                try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } }
                finally { $input.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $fileStream.Dispose() }
    & (Join-Path $PSScriptRoot 'Test-NtePackageContent.ps1') -ArchivePath $temporary | Out-Host
    [IO.File]::Move($temporary, $archivePath)
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
[pscustomobject]@{ Archive = $archivePath; ManifestAssets = @($manifest.Assets).Count; ImporterPathsVerified = $true; SHA256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash }
