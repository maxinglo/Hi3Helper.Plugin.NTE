[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('nte','stella-sora')][string] $Plugin,
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(\.\d+)?$')][string] $Tag,
    [Parameter(Mandatory)][string] $Installer,
    [Parameter(Mandatory)][string] $UpdateDirectory
)

$ErrorActionPreference = 'Stop'
$region = 'cn-shenzhen'
$prefix = "collapse-plugins/$Plugin"
$destination = "oss://maxing-long/$prefix"
$mirror = "https://plugins.freedorm.net/$prefix"
$package = Get-Item -LiteralPath $Installer
if ($package.PSIsContainer -or $package.Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.zip$') {
    throw 'The installer must be a ZIP with a safe download filename.'
}
$manifestFile = Get-Item -LiteralPath (Join-Path $UpdateDirectory 'manifest.json')
$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
$version = [version]$Tag.Substring(1)
$expectedVersion = [version]::new($version.Major, $version.Minor, $version.Build, [Math]::Max(0, $version.Revision))
if ([version]$manifest.PluginVersion -ne $expectedVersion) { throw 'Update manifest version differs from the release tag.' }

$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$files = foreach ($asset in $manifest.Assets) {
    $name = [string]$asset.FilePath
    if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $name -eq 'manifest.json' -or -not $names.Add($name)) {
        throw "Invalid or duplicate manifest file: $name"
    }
    $file = Get-Item -LiteralPath (Join-Path $UpdateDirectory $name)
    $hash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $file.FullName -Algorithm MD5).Hash))
    if ($file.PSIsContainer -or $file.Length -ne $asset.Size -or $hash -cne $asset.FileHash) {
        throw "Manifest size or hash mismatch: $name"
    }
    $file
}
if (-not $manifest.MainLibraryName -or -not $names.Contains($manifest.MainLibraryName)) {
    throw 'Update manifest does not list its main library.'
}

$current = Invoke-WebRequest "$mirror/collapse-update/manifest.json" -TimeoutSec 30 -SkipHttpErrorCheck
switch ([int]$current.StatusCode) {
    200 {
        $published = $current.Content | ConvertFrom-Json
        if ([version]$published.PluginVersion -gt $expectedVersion) { throw 'The OSS feed already advertises a newer version.' }
    }
    404 {}
    default { throw "ESA update feed preflight returned HTTP $($current.StatusCode)." }
}

function Publish-Object([IO.FileInfo] $File, [string] $Key, [string] $ContentType, [string] $CacheControl) {
    aliyun oss cp $File.FullName "$destination/$Key" --region $region --endpoint "https://oss-$region.aliyuncs.com" --force `
        --meta "Content-Type:${ContentType}#Cache-Control:${CacheControl}"
    if ($LASTEXITCODE -ne 0) { throw "OSS upload failed: $Key" }

    $download = [IO.Path]::GetTempFileName()
    try {
        Invoke-WebRequest "$mirror/$Key" -OutFile $download -TimeoutSec 120 -MaximumRetryCount 2 -RetryIntervalSec 2
        if ((Get-Item -LiteralPath $download).Length -ne $File.Length -or
            (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash) {
            throw "ESA returned different content: $Key"
        }
    } finally {
        Remove-Item -LiteralPath $download -Force
    }
    Write-Host "Verified through ESA: $Key"
}

Publish-Object $package "releases/$Tag/$($package.Name)" 'application/zip' 'public,max-age=86400'
# Collapse requests flat paths, so the fixed update objects must bypass caching.
foreach ($file in $files) {
    Publish-Object $file "collapse-update/$($file.Name)" 'application/octet-stream' 'no-store'
}
Publish-Object $manifestFile 'collapse-update/manifest.json' 'application/json' 'no-store'
