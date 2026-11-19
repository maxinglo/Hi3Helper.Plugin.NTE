[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Installer')][switch] $InstallerOnly,
    [Parameter(Mandatory, ParameterSetName = 'Feed')][switch] $UpdateFeed,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(\.\d+)?$')][string] $Tag,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $Commit,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]] $Files,
    [Parameter(ParameterSetName = 'Installer')][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $ReplaceInstallerCommit
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $env:GITEE_TOKEN) { throw 'GITEE_TOKEN is required to publish the Gitee mirror.' }
if ($env:GITEE_TOKEN -notmatch '^[A-Za-z0-9._~+/=-]+$') { throw 'GITEE_TOKEN has an invalid format.' }
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
function ConvertTo-PluginVersion([string] $Value) {
    $version = [version]$Value.TrimStart('v')
    [version]::new($version.Major, $version.Minor, $version.Build, [Math]::Max(0, $version.Revision))
}
$version = ConvertTo-PluginVersion $Tag
if ($InstallerOnly) {
    if ($assets.Count -ne 1) { throw 'The formal release must contain exactly one installer ZIP.' }
    $installer = $assets.Values | Select-Object -First 1
    if ($installer.Extension -ne '.zip') { throw 'The formal release attachment must be an installer ZIP.' }
    if ($ReplaceInstallerCommit -and $ReplaceInstallerCommit -eq $Commit) {
        throw 'ReplaceInstallerCommit must identify the previous installer release.'
    }
    $package = [IO.Compression.ZipFile]::OpenRead($installer.FullName)
    try {
        $entry = $package.GetEntry('manifest.json')
        if (-not $entry) { throw 'The installer ZIP has no manifest.json.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
    } finally { $package.Dispose() }
} else {
    if (-not $assets.ContainsKey('manifest.json') -or $assets['manifest.json'].Name -cne 'manifest.json') {
        throw 'Missing manifest.json in the update feed.'
    }
    $manifest = Get-Content -LiteralPath $assets['manifest.json'].FullName -Raw | ConvertFrom-Json
    if (-not $manifest.Assets.Count) { throw 'The update manifest has no assets.' }
    $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $manifest.Assets) {
        $assetName = [string]$asset.FilePath
        if ($assetName -eq 'manifest.json' -or -not $declared.Add($assetName) -or
            -not $assets.ContainsKey($assetName) -or $assets[$assetName].Name -cne $assetName) {
            throw "Invalid, duplicate or missing update attachment: $assetName"
        }
        $file = $assets[$assetName]
        $hash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $file.FullName -Algorithm MD5).Hash))
        if ($asset.Size -ne $file.Length -or $asset.FileHash -cne $hash) {
            throw "Update attachment does not match the manifest: $assetName"
        }
    }
    if ($assets.Count -ne $declared.Count + 1) { throw 'The update feed contains attachments absent from the manifest.' }
}
if ((ConvertTo-PluginVersion $manifest.PluginVersion) -ne $version) {
    throw "The manifest version does not match $Tag."
}

$gitUrl = "https://gitee.com/$Repository.git"
$apiUrl = "https://gitee.com/api/v5/repos/$Repository"
$headers = @{ Authorization = "Bearer $env:GITEE_TOKEN"; Accept = 'application/json' }
$source = "https://github.com/$env:GITHUB_REPOSITORY"
$installerTitle = "$(Split-Path $env:GITHUB_REPOSITORY -Leaf) $Tag"
$installerNotes = "GitHub release: $source/releases/tag/$Tag`n`nSource commit: $Commit"

function Invoke-GiteeApi {
    param(
        [string] $Resource,
        [string] $Method = 'Get',
        [hashtable] $Body,
        [switch] $AllowMissing
    )
    $request = @{ Uri = "$apiUrl/$Resource"; Headers = $headers; Method = $Method;
        TimeoutSec = 300; SkipHttpErrorCheck = $true }
    if ($Body) {
        $request.ContentType = 'application/json'
        $request.Body = $Body | ConvertTo-Json -Compress
    }
    $response = Invoke-WebRequest @request
    $status = [int]$response.StatusCode
    if ($status -eq 404 -and $AllowMissing) { return }
    if ($status -lt 200 -or $status -ge 300) { throw "Gitee API $Method $Resource failed (HTTP $status)." }
    if ($response.Content) { $response.Content | ConvertFrom-Json }
}

function Get-DownloadUrl([string] $ReleaseTag, [string] $Name) {
    "https://gitee.com/$Repository/releases/download/$ReleaseTag/$([Uri]::EscapeDataString($Name))"
}

function Test-PublishedFile([IO.FileInfo] $File, $Attachment, [string] $ReleaseTag) {
    if ($Attachment.name -cne $File.Name -or $Attachment.size -ne $File.Length) { return $false }
    $download = [IO.Path]::GetTempFileName()
    try {
        $url = Get-DownloadUrl $ReleaseTag $File.Name
        Invoke-WebRequest -Uri $url -OutFile $download -TimeoutSec 300 -MaximumRetryCount 2 -RetryIntervalSec 2
        $downloadHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
        $downloadHash -eq (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash
    } finally {
        Remove-Item -LiteralPath $download -Force
    }
}

function Get-Attachments([string] $Resource) {
    $result = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($attachment in @(Invoke-GiteeApi $Resource)) {
        if ($attachment.id -notmatch '^[1-9]\d*$' -or -not $result.TryAdd($attachment.name, $attachment)) {
            throw "Invalid or duplicate Gitee attachment: $($attachment.name)"
        }
    }
    return ,$result
}

function Publish-Attachment([string] $Resource, [IO.FileInfo] $File, [string] $ReleaseTag) {
    $curl = if ($IsWindows) { 'curl.exe' } else { 'curl' }
    $formPath = $File.FullName.Replace('\', '/').Replace('"', '\"')
    $form = 'file=@"' + $formPath + '"'
    $curlConfig = 'header = "Authorization: Bearer ' + $env:GITEE_TOKEN + '"'
    $responsePath = [IO.Path]::GetTempFileName()
    try {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            Write-Host "Uploading Gitee attachment $($File.Name) ($($File.Length) bytes), attempt $attempt."
            $metrics = $curlConfig | & $curl --disable --config - --http1.1 --silent --show-error `
                --connect-timeout 30 --max-time 900 --speed-time 120 --speed-limit 1024 `
                --header 'Expect:' --header 'Accept: application/json' --form $form `
                --output $responsePath --write-out '%{http_code} %{size_upload} %{time_total}' "$apiUrl/$Resource"
            $exitCode = $LASTEXITCODE
            $status, $uploadedBytes, $duration = $metrics -split ' ', 3
            Write-Host "Gitee upload result: exit $exitCode, HTTP $status, sent $uploadedBytes bytes in $duration seconds."
            if ($exitCode -eq 0 -and $status -match '^2\d\d$') {
                $uploaded = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json
                if (-not (Test-PublishedFile $File $uploaded $ReleaseTag)) {
                    throw "Gitee attachment differs from the source: $($File.Name)"
                }
                Write-Host "Verified Gitee attachment: $($File.Name)"
                return
            }

            # A timed-out request may have committed the attachment server-side.
            $existing = Get-Attachments $Resource
            if ($existing.ContainsKey($File.Name)) {
                if (-not (Test-PublishedFile $File $existing[$File.Name] $ReleaseTag)) {
                    throw "Gitee attachment differs from the source after an interrupted upload: $($File.Name)"
                }
                Write-Host "Verified Gitee attachment after interrupted upload: $($File.Name)"
                return
            }
            if ($attempt -eq 3 -or ($exitCode -eq 0 -and $status -match '^4\d\d$' -and $status -notin @('408', '429'))) {
                throw "Could not upload $($File.Name) to Gitee (curl exit $exitCode, HTTP $status)."
            }
            Start-Sleep -Seconds (5 * $attempt)
        }
    } finally {
        Remove-Item -LiteralPath $responsePath -Force
    }
}

function Get-MirrorRefs {
    $remoteRefs = git @gitOptions ls-remote $gitUrl $branchRef $tagRef "$tagRef^{}"
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the Gitee mirror refs.' }
    $refs = @{}
    foreach ($line in $remoteRefs) {
        $sha, $ref = $line -split '\s+', 2
        $refs[$ref] = $sha
    }
    return $refs
}

function Sync-MirrorSource($Release) {
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
        $refs = Get-MirrorRefs
        $remoteCommit = $refs["$tagRef^{}"] ?? $refs[$tagRef]
        $replaceTag = $ReplaceInstallerCommit -and $remoteCommit -eq $ReplaceInstallerCommit
        $replaceRelease = $Release -and $Release.target_commitish -ne $Commit
        if ($remoteCommit -and $remoteCommit -ne $Commit -and -not $replaceTag) {
            throw "Gitee tag $Tag points to another commit."
        }
        if ($replaceRelease -and (-not $replaceTag -or $Release.target_commitish -ne $ReplaceInstallerCommit)) {
            throw "Gitee release $Tag and its tag do not both match the expected previous commit."
        }
        if ($Release -and -not $replaceRelease -and $replaceTag) {
            throw "Gitee release $Tag already records the new commit, but its tag still points to the previous commit."
        }
        if ($refs[$branchRef] -and $refs[$branchRef] -ne $Commit) {
            git merge-base --is-ancestor $Commit $refs[$branchRef] 2>$null
            $branchIncludesCommit = $LASTEXITCODE -eq 0
        } else {
            $branchIncludesCommit = $refs[$branchRef] -eq $Commit
        }
        if (-not $branchIncludesCommit) {
            git @gitOptions push --no-follow-tags $gitUrl "${Commit}:$branchRef" | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Could not fast-forward the Gitee mirror main branch.' }
        }

        if ($replaceRelease) {
            Write-Host "Replacing Gitee release $Tag from expected commit $ReplaceInstallerCommit."
            [void](Invoke-GiteeApi "releases/$($Release.id)" -Method Delete)
            $Release = $null
            # Account for a deleted tag without transferring a lease to a different object.
            $afterDelete = Get-MirrorRefs
            if ($afterDelete[$tagRef] -and $afterDelete[$tagRef] -ne $refs[$tagRef]) {
                throw "Gitee tag $Tag changed while replacing the previous release."
            }
            if (-not $afterDelete[$tagRef]) {
                $replaceTag = $false
                $remoteCommit = $null
            }
        }

        if ($replaceTag) {
            $lease = "--force-with-lease=${tagRef}:$($refs[$tagRef])"
            git @gitOptions push --no-follow-tags $lease $gitUrl "${Commit}:$tagRef" | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Could not replace Gitee tag $Tag with its exact lease." }
        } elseif (-not $remoteCommit) {
            git @gitOptions push --no-follow-tags $gitUrl "${Commit}:$tagRef" | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Could not create Gitee tag $Tag." }
        }
        $publishedRefs = Get-MirrorRefs
        if (($publishedRefs["$tagRef^{}"] ?? $publishedRefs[$tagRef]) -ne $Commit) {
            throw "Gitee tag $Tag does not point to source commit $Commit."
        }
        return $Release
    } finally {
        $env:GIT_ASKPASS = $previousAskPass
        $env:GIT_TERMINAL_PROMPT = $previousPrompt
        Remove-Item -LiteralPath $askPass -Force
    }
}

function New-MirrorRelease([string] $ReleaseTag, [string] $Title, [string] $Notes) {
    $created = Invoke-GiteeApi 'releases' -Method Post -Body @{
        tag_name = $ReleaseTag; target_commitish = $Commit; name = $Title; body = $Notes; prerelease = $true
    }
    if (-not $created.id -or $created.tag_name -ne $ReleaseTag -or
        $created.target_commitish -ne $Commit -or $created.prerelease -ne $true) {
        throw "The created Gitee release $ReleaseTag does not match source commit $Commit."
    }
    return $created
}

function Publish-InstallerRelease {
    $release = Invoke-GiteeApi "releases/tags/$Tag" -AllowMissing
    if ($release -and $release.target_commitish -ne $Commit -and
        (-not $ReplaceInstallerCommit -or $release.target_commitish -ne $ReplaceInstallerCommit)) {
        throw "Gitee release $Tag already exists at another commit."
    }
    if ($release -and $release.target_commitish -eq $Commit -and
        $release.prerelease -and $release.body -ne $installerNotes) {
        throw "Gitee prerelease $Tag was not prepared by this workflow."
    }
    if (-not $release -or $release.prerelease) {
        # The REST latest endpoint includes previews and may return the update feed.
        $releases = @(Invoke-GiteeApi 'releases?direction=desc&per_page=100')
        foreach ($candidate in $releases) {
            if (-not $candidate.prerelease -and $candidate.tag_name -match '^v\d+\.\d+\.\d+(\.\d+)?$' -and
                (ConvertTo-PluginVersion $candidate.tag_name) -gt $version) {
                throw "Gitee already has a newer stable release ($($candidate.tag_name))."
            }
        }
    }

    $release = Sync-MirrorSource $release
    if (-not $release) { $release = New-MirrorRelease $Tag $installerTitle $installerNotes }
    $attachments = "releases/$($release.id)/attach_files"
    $existing = Get-Attachments $attachments
    if ($existing.Count -gt 1 -or ($existing.Count -eq 1 -and -not $existing.ContainsKey($installer.Name))) {
        throw "Gitee release $Tag already has attachments other than $($installer.Name)."
    }
    if ($existing.Count -eq 0) {
        Publish-Attachment $attachments $installer $Tag
    } elseif (-not (Test-PublishedFile $installer $existing[$installer.Name] $Tag)) {
        throw "Gitee release $Tag already has a different $($installer.Name)."
    }

    if ($release.prerelease) {
        $published = Invoke-GiteeApi "releases/$($release.id)" -Method Patch -Body @{
            tag_name = $Tag; name = $installerTitle; body = $installerNotes; prerelease = $false
        }
        if ($published.tag_name -ne $Tag -or $published.prerelease -ne $false) {
            throw "Could not promote Gitee release $Tag."
        }
    }
    Write-Host "Verified installer release: https://gitee.com/$Repository/releases/tag/$Tag"
}

function Publish-CollapseUpdateFeed {
    $release = Invoke-GiteeApi "releases/tags/$Tag" -AllowMissing
    if (-not $release -or $release.target_commitish -ne $Commit -or $release.prerelease -ne $false) {
        throw "Publish the Gitee installer release for $Tag at $Commit before updating the feed."
    }

    $feedTag = 'collapse-update'
    $title = 'Collapse automatic updates'
    $marker = "Collapse update feed for $source"
    $notes = "$marker`n`nVersion: $Tag`nSource commit: $Commit"
    $feed = Invoke-GiteeApi "releases/tags/$feedTag" -AllowMissing
    if ($feed) {
        $pattern = '\A' + [regex]::Escape($marker) + '\r?\n\r?\nVersion: (v\d+\.\d+\.\d+(?:\.\d+)?)\r?\nSource commit: ([a-fA-F0-9]{40})\s*\z'
        $current = [regex]::Match($feed.body, $pattern)
        if (-not $current.Success -or $feed.prerelease -ne $true) {
            throw "Gitee release $feedTag was not prepared by this workflow."
        }
        $currentVersion = ConvertTo-PluginVersion $current.Groups[1].Value
        if ($currentVersion -gt $version -or ($currentVersion -eq $version -and $current.Groups[2].Value -ne $Commit)) {
            throw "Refusing to replace update feed from $($current.Groups[1].Value) at $($current.Groups[2].Value)."
        }
    } else {
        $feed = New-MirrorRelease $feedTag $title $notes
    }

    $attachments = "releases/$($feed.id)/attach_files"
    $existing = Get-Attachments $attachments
    $reuse = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($assetName in $assets.Keys) {
        if ($existing.ContainsKey($assetName) -and (Test-PublishedFile $assets[$assetName] $existing[$assetName] $feedTag)) {
            [void]$reuse.Add($assetName)
        }
    }

    # Pin the intended version before withdrawing its predecessor, so an older
    # rerun cannot roll back a publication interrupted midway through.
    if ($feed.body -ne $notes) {
        $updated = Invoke-GiteeApi "releases/$($feed.id)" -Method Patch -Body @{
            tag_name = $feedTag; name = $title; body = $notes; prerelease = $true
        }
        if ($updated.tag_name -ne $feedTag -or $updated.prerelease -ne $true -or $updated.body -ne $notes) {
            throw 'Could not record the intended Collapse update feed version.'
        }
    }

    if ($reuse.Count -ne $assets.Count -or $existing.Count -ne $assets.Count) {
        # Withdraw discovery before changing any component. Fresh requests
        # should receive a 404 until the full new version is available.
        if ($existing.ContainsKey('manifest.json')) {
            [void](Invoke-GiteeApi "$attachments/$($existing['manifest.json'].id)" -Method Delete)
            $manifestUrl = Get-DownloadUrl $feedTag 'manifest.json'
            $withdrawn = Invoke-WebRequest -Uri $manifestUrl -Method Head -SkipHttpErrorCheck -TimeoutSec 30
            if ([int]$withdrawn.StatusCode -ne 404) { throw 'The previous update manifest is still available.' }
            [void]$existing.Remove('manifest.json')
            [void]$reuse.Remove('manifest.json')
        }
        foreach ($attachment in $existing.Values) {
            if (-not $reuse.Contains($attachment.name)) {
                [void](Invoke-GiteeApi "$attachments/$($attachment.id)" -Method Delete)
            }
        }
        foreach ($assetName in $assets.Keys | Where-Object { $_ -ne 'manifest.json' -and -not $reuse.Contains($_) } | Sort-Object) {
            Publish-Attachment $attachments $assets[$assetName] $feedTag
        }
        Publish-Attachment $attachments $assets['manifest.json'] $feedTag
    }

    $manifestUrl = Get-DownloadUrl $feedTag 'manifest.json'
    Write-Host "Verified Collapse update feed for ${Tag}: $manifestUrl"
}

if ($InstallerOnly) { Publish-InstallerRelease }
else { Publish-CollapseUpdateFeed }
