[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [Parameter(Mandatory)][string] $UpdateDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string] $Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $Commit
)

$ErrorActionPreference = 'Stop'
$tag = 'collapse-update'
$manifestName = 'manifest.json'
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required for the GitHub update feed.' }

$root = (Resolve-Path -LiteralPath $UpdateDirectory).Path
$manifestFile = Get-Item -LiteralPath (Join-Path $root $manifestName)
$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
$expectedVersion = [version]$Version
if ($expectedVersion.Revision -lt 0) {
    $expectedVersion = [version]::new($expectedVersion.Major, $expectedVersion.Minor, $expectedVersion.Build, 0)
}
if ([version]$manifest.PluginVersion -ne $expectedVersion) { throw 'Update manifest version does not match the installer tag.' }

$files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($asset in $manifest.Assets) {
    $name = [string]$asset.FilePath
    if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or $name.EndsWith('.') -or
        $name -eq $manifestName -or $files.ContainsKey($name)) {
        throw "Invalid or duplicate update asset name: $name"
    }
    $file = Get-Item -LiteralPath (Join-Path $root $name)
    $md5 = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $file.FullName -Algorithm MD5).Hash))
    if ($file.PSIsContainer -or $file.Length -ne $asset.Size -or $md5 -cne $asset.FileHash) {
        throw "Update manifest size or hash mismatch: $name"
    }
    $files.Add($name, $file)
}
if (-not $manifest.MainLibraryName -or -not $files.ContainsKey($manifest.MainLibraryName)) {
    throw 'The update manifest does not list the plugin DLL.'
}
$files.Add($manifestName, $manifestFile)

$expectedDigests = @{}
foreach ($file in $files.Values) {
    $expectedDigests[$file.Name] = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}

$api = "https://api.github.com/repos/$Repository/releases"
$request = @{
    Headers = @{
        Authorization = "Bearer $env:GH_TOKEN"
        Accept = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    TimeoutSec = 120
}
$releaseBody = @"
Automatic updates for Collapse Launcher.

Installer: [v$Version](https://github.com/$Repository/releases/tag/v$Version)

<!-- collapse-feed-version:$expectedVersion -->
"@
$release = Invoke-RestMethod @request -Uri "$api/tags/$tag" -SkipHttpErrorCheck -StatusCodeVariable status
if ($status -eq 404) {
    $create = @{
        tag_name = $tag
        target_commitish = $Commit
        name = 'Collapse plugin update feed'
        body = $releaseBody
        prerelease = $true
        make_latest = 'false'
    } | ConvertTo-Json
    $release = Invoke-RestMethod @request -Method Post -Uri $api -ContentType 'application/json' -Body $create
}
elseif ($status -ne 200) {
    throw "Could not look up the GitHub update feed (HTTP $status)."
}

if (-not $release.prerelease -or $release.draft -or $release.immutable) {
    throw "The $tag tag must be a mutable, published GitHub prerelease."
}
if ($release.body -notmatch '<!-- collapse-feed-version:([0-9.]+) -->') {
    throw "The existing $tag release is missing its managed update feed marker."
}
if ([version]$Matches[1] -gt $expectedVersion) {
    throw "The GitHub update feed already targets a newer version than $expectedVersion."
}
$releaseId = [long]$release.id
if ($releaseId -le 0) { throw 'Invalid GitHub update feed release ID.' }

function Test-ExpectedAsset($Asset) {
    $files.ContainsKey([string]$Asset.name) -and
        $Asset.name -ceq $files[$Asset.name].Name -and
        $Asset.state -eq 'uploaded' -and
        $Asset.size -eq $files[$Asset.name].Length -and
        $Asset.digest -eq $expectedDigests[$Asset.name]
}

function Send-UpdateAsset([IO.FileInfo] $File) {
    $name = [Uri]::EscapeDataString($File.Name)
    $upload = "https://uploads.github.com/repos/$Repository/releases/$releaseId/assets?name=$name"
    $contentType = if ($File.Name -eq $manifestName) { 'application/json' } else { 'application/octet-stream' }
    $asset = Invoke-RestMethod @request -Method Post -Uri $upload -ContentType $contentType -InFile $File.FullName
    if (-not (Test-ExpectedAsset $asset)) { throw "GitHub returned a size or digest mismatch for $($File.Name)." }
}

$assets = Invoke-RestMethod @request -Uri "$api/$releaseId/assets?per_page=100"
$matchingAssets = @($assets | Where-Object { Test-ExpectedAsset $_ })
if (@($assets).Count -eq $files.Count -and $matchingAssets.Count -eq $files.Count) {
    Write-Host "GitHub update feed $expectedVersion already contains the expected assets."
    return
}

if ($release.body -ne $releaseBody) {
    $patch = @{ body = $releaseBody } | ConvertTo-Json
    $null = Invoke-RestMethod @request -Method Patch -Uri "$api/$releaseId" -ContentType 'application/json' -Body $patch
}

# Withdraw the previous manifest before changing bytes at its stable attachment paths.
foreach ($asset in $assets | Where-Object name -EQ $manifestName) {
    $null = Invoke-RestMethod @request -Method Delete -Uri "$api/assets/$([long]$asset.id)"
}
foreach ($asset in $assets | Where-Object name -NE $manifestName) {
    if (-not (Test-ExpectedAsset $asset)) {
        $null = Invoke-RestMethod @request -Method Delete -Uri "$api/assets/$([long]$asset.id)"
    }
}
foreach ($file in $files.Values | Where-Object Name -NE $manifestName) {
    if ($file.Name -cnotin $matchingAssets.name) { Send-UpdateAsset $file }
}

$payload = Invoke-RestMethod @request -Uri "$api/$releaseId/assets?per_page=100"
if (@($payload).Count -ne ($files.Count - 1) -or @($payload | Where-Object { -not (Test-ExpectedAsset $_) }).Count) {
    throw 'GitHub update files do not match the manifest; leaving the feed unadvertised.'
}
Send-UpdateAsset $manifestFile
Write-Host "GitHub update feed published for $expectedVersion."
