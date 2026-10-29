[CmdletBinding()]
param([Parameter(Mandatory)][string] $ArchivePath)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$archivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
$directory = Join-Path $repository ('artifacts/login-host/import-simulation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$destination = Join-Path $directory 'Hi3Helper.Plugin.NTE'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
& (Join-Path $PSScriptRoot 'Test-NtePackageContent.ps1') -ArchivePath $archivePath | Out-Host
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $manifestEntry = $archive.Entries | Where-Object { $_.Name -eq 'manifest.json' } | Select-Object -First 1
    if (-not $manifestEntry) { throw 'Simulation expects an uncompressed root manifest.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $map = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) { if (-not $map.ContainsKey($entry.FullName)) { $map.Add($entry.FullName, $entry) } }
    foreach ($asset in @($manifest.Assets) + @([pscustomobject]@{ FilePath = 'manifest.json' })) {
        $entry = $null
        if (-not $map.TryGetValue([string]$asset.FilePath, [ref]$entry)) { throw "Importer cannot find asset: $($asset.FilePath)" }
        $path = [IO.Path]::GetFullPath((Join-Path $destination $asset.FilePath))
        if (-not $path.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Import would escape the staging directory.' }
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        $input = $entry.Open()
        try { $output = [IO.File]::Create($path); try { $input.CopyTo($output) } finally { $output.Dispose() } }
        finally { $input.Dispose() }
    }
} finally { $archive.Dispose() }
& (Join-Path $PSScriptRoot 'Test-NtePackageContent.ps1') -PublishDirectory $destination | Out-Host
& (Join-Path $PSScriptRoot 'Test-NativePlugin.ps1') -Path (Join-Path $destination 'NTE.dll') -ExpectedVersion $manifest.PluginVersion
$result = [ordered]@{ passed = $true; fixture = $true; archive = $archivePath; output = $destination;
    manifestAssetsCopied = @($manifest.Assets).Count; nativePluginLoaded = $true; installedPluginModified = $false;
    archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'result.json') -Encoding utf8
$result | ConvertTo-Json
