[CmdletBinding(DefaultParameterSetName='Directory')]
param(
    [Parameter(Mandatory,ParameterSetName='Directory')][string] $PublishDirectory,
    [Parameter(Mandatory,ParameterSetName='Archive')][string] $ArchivePath
)
$ErrorActionPreference = 'Stop'
$entries = if ($PSCmdlet.ParameterSetName -eq 'Directory') {
    @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | ForEach-Object FullName)
} else {
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try { @($archive.Entries | ForEach-Object FullName) }
    finally { $archive.Dispose() }
}
if ($PSCmdlet.ParameterSetName -eq 'Archive') {
    # Mirror ZipPluginSource's actual lookup: entries are case-insensitive,
    # but path separators are not normalized; .br is removed from the key.
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try {
        $manifestEntry = $archive.Entries | Where-Object { $_.Name.StartsWith('manifest.json', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (-not $manifestEntry) { throw 'Importer cannot find manifest.json in this archive.' }
        $manifestStream = $manifestEntry.Open()
        try {
            $readStream = if ($manifestEntry.Name.EndsWith('.br', [StringComparison]::OrdinalIgnoreCase)) { [IO.Compression.BrotliStream]::new($manifestStream,[IO.Compression.CompressionMode]::Decompress) } else { $manifestStream }
            $reader = [IO.StreamReader]::new($readStream)
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        } finally { $manifestStream.Dispose() }
        $prefix = [IO.Path]::GetDirectoryName($manifestEntry.FullName)
        $prefixLength = if ($prefix) { $prefix.Length } else { 0 }
        $map = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.Length -lt $prefixLength) { throw 'Archive entry shorter than importer manifest prefix.' }
            $key = $entry.FullName.Substring($prefixLength).TrimStart('\','/')
            if ($key.EndsWith('.br',[StringComparison]::OrdinalIgnoreCase)) { $key = $key.Substring(0,$key.Length - 3) }
            if (-not $map.ContainsKey($key)) { $map.Add($key, $entry) }
        }
        foreach ($asset in $manifest.Assets) {
            $entry = $null
            if (-not $map.TryGetValue([string]$asset.FilePath, [ref]$entry)) { throw "Importer cannot find manifest asset exactly: $($asset.FilePath)" }
            $source = $entry.Open()
            try {
                $decoded = if ($entry.Name.EndsWith('.br',[StringComparison]::OrdinalIgnoreCase)) { [IO.Compression.BrotliStream]::new($source,[IO.Compression.CompressionMode]::Decompress) } else { $source }
                try {
                    $md5 = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::MD5)
                    try {
                        $buffer = [byte[]]::new(65536); $length = 0L
                        while (($read = $decoded.Read($buffer, 0, $buffer.Length)) -gt 0) { $md5.AppendData($buffer, 0, $read); $length += $read }
                        $hash = [Convert]::ToBase64String($md5.GetHashAndReset())
                    } finally { $md5.Dispose() }
                    if ($length -ne $asset.Size -or $hash -ne $asset.FileHash) { throw "Importer asset size or hash mismatch: $($asset.FilePath)" }
                } finally { if ($decoded -ne $source) { $decoded.Dispose() } }
            } finally { $source.Dispose() }
        }
    } finally { $archive.Dispose() }
}
$forbidden = @($entries | Where-Object {
    [IO.Path]::GetFileName($_.Replace('\','/')) -match '^(NTEUpdate|NTEGame|NTEBrowser|NTELauncher|NTEGlobalUpdate|NTEGlobalGame|NTEGlobalBrowser|NTEGlobalLauncher|GlobalLoginHelper|Nte[A-Za-z0-9]*(?:Audit|Probe|Check|Broker|Harness|Verification))\.(exe|dll)$' -or
    [IO.Path]::GetFileName($_.Replace('\','/')) -match '^(preview-.*|startup-error.*|.*\.tmp|.*\.pdb(?:\.br)?)$'
})
if ($forbidden.Count) {
    throw ('Release content contains an official launcher, research executable, debug symbols or temporary diagnostic: ' + ($forbidden -join ', '))
}
foreach ($required in @('NTE.dll','LoginHost/Nte.LoginHost.exe','LoginHost/Nte.LoginHost.dll',
    'LoginHost/Nte.LoginHost.runtimeconfig.json','LoginHost/Assets/question-mascot.png',
    'LoginHost/coreclr.dll','LoginHost/hostfxr.dll','LoginHost/PresentationFramework.dll','LoginHost/ThirdParty/Collapse/LICENSE')) {
    if (-not @($entries | Where-Object { $_.Replace('\','/').EndsWith('/' + $required, [StringComparison]::OrdinalIgnoreCase) -or $_.Replace('\','/').Equals($required, [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw "Release content is missing the bundled login component: $required"
    }
}
$runtimeJson = if ($PSCmdlet.ParameterSetName -eq 'Directory') {
    Get-Content -LiteralPath (Join-Path $PublishDirectory 'LoginHost/Nte.LoginHost.runtimeconfig.json') -Raw
} else {
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try {
        $runtimeEntry = $archive.Entries | Where-Object {
            $runtimeName = $_.FullName.Replace('\','/')
            if ($runtimeName.EndsWith('.br', [StringComparison]::OrdinalIgnoreCase)) { $runtimeName = $runtimeName.Substring(0, $runtimeName.Length - 3) }
            $runtimeName.EndsWith('LoginHost/Nte.LoginHost.runtimeconfig.json', [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if (-not $runtimeEntry) { throw 'Missing login runtime configuration in archive.' }
        $source = $runtimeEntry.Open()
        try {
            $decoded = if ($runtimeEntry.Name.EndsWith('.br',[StringComparison]::OrdinalIgnoreCase)) { [IO.Compression.BrotliStream]::new($source,[IO.Compression.CompressionMode]::Decompress) } else { $source }
            $reader = [IO.StreamReader]::new($decoded)
            try { $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $source.Dispose() }
    } finally { $archive.Dispose() }
}
$runtime = ($runtimeJson | ConvertFrom-Json).runtimeOptions
if ($runtime.framework -or $runtime.frameworks -or -not @($runtime.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').Count) {
    throw 'Login host runtime configuration is framework-dependent; publish it with a full self-contained build.'
}
[pscustomobject]@{ passed = $true; checkedEntries = $entries.Count; excludesNteSelfUpdater = $true; excludesDebugSymbols = $true; bundledWpfLoginHost = $true; selfContained = $true }
