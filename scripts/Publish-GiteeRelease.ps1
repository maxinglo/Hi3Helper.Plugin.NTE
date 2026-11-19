[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(\.\d+)?$')][string] $Tag,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $Commit,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]] $Files
)

$ErrorActionPreference = 'Stop'
if (-not $env:GITEE_TOKEN) { throw 'GITEE_TOKEN is required to publish the Gitee mirror.' }
if ($env:GITHUB_REPOSITORY -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'GITHUB_REPOSITORY must identify the source repository.'
}

$assets = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in $Files) {
    $file = Get-Item -LiteralPath $path
    if ($file -isnot [IO.FileInfo] -or $file.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw "Unsupported release attachment path: $path"
    }
    if ($file.Length -gt 100MB) { throw "Gitee attachment exceeds 100 MiB: $($file.Name)" }
    if (-not $assets.TryAdd($file.Name, $file)) { throw "Duplicate attachment name: $($file.Name)" }
}
if (-not $assets.ContainsKey('manifest.json') -or $assets['manifest.json'].Name -cne 'manifest.json') {
    throw 'Missing manifest.json in release attachments.'
}
$manifest = Get-Content -LiteralPath $assets['manifest.json'].FullName -Raw | ConvertFrom-Json
if (-not $manifest.Assets.Count) { throw 'The update manifest has no assets.' }
foreach ($asset in $manifest.Assets) {
    $assetName = [string]$asset.FilePath
    if (-not $assets.ContainsKey($assetName) -or $assets[$assetName].Name -cne $assetName) {
        throw "Missing release attachment referenced in the update manifest: $assetName"
    }
}

$gitUrl = "https://gitee.com/$Repository.git"
$apiUrl = "https://gitee.com/api/v5/repos/$Repository"
$downloadUrl = "https://gitee.com/$Repository/releases/download/$Tag"
$headers = @{ Authorization = "Bearer $env:GITEE_TOKEN"; Accept = 'application/json' }
$name = "$(Split-Path $env:GITHUB_REPOSITORY -Leaf) $Tag"
$source = "https://github.com/$env:GITHUB_REPOSITORY/releases/tag/$Tag"
$body = "GitHub release: $source`n`nSource commit: $Commit"

function Invoke-GiteeApi {
    param(
        [string] $Resource,
        [string] $Method = 'Get',
        [hashtable] $Body,
        [hashtable] $Form,
        [switch] $AllowMissing
    )
    $request = @{ Uri = "$apiUrl/$Resource"; Headers = $headers; Method = $Method;
        TimeoutSec = 300; SkipHttpErrorCheck = $true }
    if ($Body) {
        $request.ContentType = 'application/json'
        $request.Body = $Body | ConvertTo-Json -Compress
    }
    if ($Form) { $request.Form = $Form }
    $response = Invoke-WebRequest @request
    $status = [int]$response.StatusCode
    if ($status -eq 404 -and $AllowMissing) { return }
    if ($status -lt 200 -or $status -ge 300) { throw "Gitee API $Method $Resource failed (HTTP $status)." }
    if ($response.Content) { $response.Content | ConvertFrom-Json }
}

function Confirm-PublishedFile([IO.FileInfo] $File, [long] $RemoteSize) {
    if ($RemoteSize -ne $File.Length) { throw "Gitee attachment has a different size: $($File.Name)" }
    $download = [IO.Path]::GetTempFileName()
    try {
        $url = "$downloadUrl/$([Uri]::EscapeDataString($File.Name))"
        Invoke-WebRequest -Uri $url -OutFile $download -TimeoutSec 300 -MaximumRetryCount 2 -RetryIntervalSec 2
        $downloadHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
        if ($downloadHash -ne (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash) {
            throw "Gitee attachment has different content: $($File.Name)"
        }
    } finally {
        Remove-Item -LiteralPath $download -Force
    }
}

$release = Invoke-GiteeApi "releases/tags/$Tag" -AllowMissing
if ($release -and ($release.target_commitish -ne $Commit -or ($release.prerelease -and $release.body -ne $body))) {
    throw "Gitee release $Tag already exists and does not match this mirror publication."
}
if (-not $release -or $release.prerelease) {
    $latest = Invoke-GiteeApi 'releases/latest' -AllowMissing
    if ($latest.tag_name -match '^v\d+\.\d+\.\d+(\.\d+)?$' -and
        [version]$latest.tag_name.Substring(1) -gt [version]$Tag.Substring(1)) {
        throw "Gitee already has a newer stable release ($($latest.tag_name))."
    }
}

# Use Git's normal fast-forward and tag protections. Credentials stay in the
# environment and askpass never supplies the token to another host.
$resolvedCommit = git rev-parse --verify "$Commit^{commit}"
if ($LASTEXITCODE -ne 0 -or $resolvedCommit -ne $Commit) { throw "Unknown source commit: $Commit" }
$askPass = [IO.Path]::GetTempFileName()
$previousAskPass = $env:GIT_ASKPASS
$previousPrompt = $env:GIT_TERMINAL_PROMPT
try {
    $promptScript = @'
#!/bin/sh
case "$1" in
    "Password for "*@gitee.com*) printf '%s\n' "$GITEE_TOKEN" ;;
    *) exit 1 ;;
esac
'@
    [IO.File]::WriteAllText($askPass, $promptScript.Replace("`r`n", "`n") + "`n")
    if (-not $IsWindows) {
        chmod 700 $askPass
        if ($LASTEXITCODE -ne 0) { throw 'Could not set permissions on the temporary Git askpass helper.' }
    }
    $env:GIT_ASKPASS = $askPass
    $env:GIT_TERMINAL_PROMPT = '0'
    $username = if ($env:GITEE_USER) { $env:GITEE_USER } else { ($Repository -split '/', 2)[0] }
    $gitOptions = @('-c', 'credential.helper=', '-c', "credential.username=$username")
    $tagRef = "refs/tags/$Tag"
    $branchRef = 'refs/heads/main'
    $remoteRefs = git @gitOptions ls-remote $gitUrl $branchRef $tagRef "$tagRef^{}"
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the Gitee mirror refs.' }
    $refs = @{}
    foreach ($line in $remoteRefs) {
        $sha, $ref = $line -split '\s+', 2
        $refs[$ref] = $sha
    }
    $remoteCommit = $refs["$tagRef^{}"] ?? $refs[$tagRef]
    if ($remoteCommit -and $remoteCommit -ne $Commit) { throw "Gitee tag $Tag points to another commit." }
    if ($refs[$branchRef] -and $refs[$branchRef] -ne $Commit) {
        git merge-base --is-ancestor $Commit $refs[$branchRef] 2>$null
        $branchIncludesCommit = $LASTEXITCODE -eq 0
    } else {
        $branchIncludesCommit = $refs[$branchRef] -eq $Commit
    }
    $updates = @()
    if (-not $branchIncludesCommit) { $updates += "${Commit}:$branchRef" }
    if (-not $remoteCommit) { $updates += "${Commit}:$tagRef" }
    if ($updates.Count) {
        git @gitOptions push --no-follow-tags $gitUrl @updates
        if ($LASTEXITCODE -ne 0) { throw 'Could not fast-forward the Gitee mirror or create its release tag.' }
    }
} finally {
    $env:GIT_ASKPASS = $previousAskPass
    $env:GIT_TERMINAL_PROMPT = $previousPrompt
    Remove-Item -LiteralPath $askPass -Force
}

$releaseBody = @{ tag_name = $Tag; target_commitish = $Commit; name = $name; body = $body; prerelease = $true }
if (-not $release) {
    $release = Invoke-GiteeApi 'releases' -Method Post -Body $releaseBody
    if (-not $release.id -or $release.target_commitish -ne $Commit) {
        throw "The created Gitee release does not match source commit $Commit."
    }
}

$attachments = "releases/$($release.id)/attach_files"
$existingNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($attachment in @(Invoke-GiteeApi $attachments)) {
    if (-not $assets.ContainsKey($attachment.name)) { continue }
    if (-not $existingNames.Add($attachment.name)) { throw "Duplicate Gitee attachment: $($attachment.name)" }
    Confirm-PublishedFile -File $assets[$attachment.name] -RemoteSize $attachment.size
}

# Collapse starts looking for the component files as soon as it sees a manifest.
$publishOrder = @($assets.Keys | Where-Object { $_ -ne 'manifest.json' } | Sort-Object) + @('manifest.json')
foreach ($assetName in $publishOrder) {
    if ($existingNames.Contains($assetName)) { continue }
    $uploaded = Invoke-GiteeApi $attachments -Method Post -Form @{ file = $assets[$assetName] }
    if ($uploaded.name -cne $assetName) { throw "Gitee renamed the uploaded attachment: $assetName" }
    Confirm-PublishedFile -File $assets[$assetName] -RemoteSize $uploaded.size
    Write-Host "Verified Gitee attachment: $assetName"
}

if ($release.prerelease) {
    $releaseBody.prerelease = $false
    $published = Invoke-GiteeApi "releases/$($release.id)" -Method Patch -Body $releaseBody
    if ($published.tag_name -ne $Tag -or $published.prerelease) { throw "Could not promote Gitee release $Tag." }
}
Write-Host "Verified Gitee release: https://gitee.com/$Repository/releases/tag/$Tag"
