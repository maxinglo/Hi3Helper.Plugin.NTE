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
function Find-PackageEntry([string] $RelativePath) {
    @($entries | Where-Object {
        $name = $_.Replace('\','/')
        if ($name.EndsWith('.br', [StringComparison]::OrdinalIgnoreCase)) { $name = $name.Substring(0, $name.Length - 3) }
        $name.EndsWith('/' + $RelativePath, [StringComparison]::OrdinalIgnoreCase) -or $name.Equals($RelativePath, [StringComparison]::OrdinalIgnoreCase)
    })
}
foreach ($required in @('NTE.dll','LoginHost/Nte.LoginHost.exe','LoginHost/libSkiaSharp.dll',
    'LoginHost/libHarfBuzzSharp.dll','LoginHost/av_libglesv2.dll','LoginHost/Assets/question-mascot.png',
    'LoginHost/Assets/Brand/google-g.png','LoginHost/Assets/Brand/facebook-primary.png','LoginHost/ThirdParty/Collapse/LICENSE') +
    @('zh-CN','zh-TW','ja','ko','de','fr','ru','es' | ForEach-Object { "LoginHost/$_/Nte.LoginHost.resources.dll" })) {
    if (@(Find-PackageEntry $required).Count -ne 1) { throw "Release content is missing or duplicates a login component: $required" }
}
foreach ($legacy in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','PresentationFramework.dll','PresentationCore.dll',
    'WindowsBase.dll','Nte.LoginHost.dll','Nte.LoginHost.runtimeconfig.json','System.Private.CoreLib.dll')) {
    if (@(Find-PackageEntry "LoginHost/$legacy").Count) { throw "Native AOT login package contains a managed runtime/host: $legacy" }
}
[byte[]] $executable = if ($PSCmdlet.ParameterSetName -eq 'Directory') {
    ,([IO.File]::ReadAllBytes((Join-Path $PublishDirectory 'LoginHost/Nte.LoginHost.exe')))
} else {
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try {
        $entryName = @(Find-PackageEntry 'LoginHost/Nte.LoginHost.exe')[0]
        $entry = $archive.GetEntry($entryName)
        $source = $entry.Open()
        try {
            $decoded = if ($entry.Name.EndsWith('.br', [StringComparison]::OrdinalIgnoreCase)) { [IO.Compression.BrotliStream]::new($source, [IO.Compression.CompressionMode]::Decompress) } else { $source }
            try {
                $memory = [IO.MemoryStream]::new()
                try { $decoded.CopyTo($memory); ,($memory.ToArray()) } finally { $memory.Dispose() }
            } finally { if ($decoded -ne $source) { $decoded.Dispose() } }
        } finally { $source.Dispose() }
    } finally { $archive.Dispose() }
}
if ($executable.Length -lt 1024 -or [BitConverter]::ToUInt16($executable, 0) -ne 0x5A4D) { throw 'Invalid native login executable.' }
$pe = [BitConverter]::ToInt32($executable, 0x3C)
if ($pe -lt 0 -or $pe + 264 -gt $executable.Length -or [BitConverter]::ToUInt32($executable, $pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($executable, $pe + 4) -ne 0x8664 -or [BitConverter]::ToUInt16($executable, $pe + 24) -ne 0x20B) { throw 'Login host must be a Windows x64 PE32+ executable.' }
# A managed PE has an IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR. Native AOT has no CLR header.
if ([BitConverter]::ToUInt64($executable, $pe + 24 + 112 + 14 * 8) -ne 0) { throw 'Login host still contains a CLR header.' }
[pscustomobject]@{ passed = $true; checkedEntries = $entries.Count; excludesNteSelfUpdater = $true; excludesDebugSymbols = $true; bundledNativeAotLoginHost = $true; selfContained = $true; clrHeader = $false }
