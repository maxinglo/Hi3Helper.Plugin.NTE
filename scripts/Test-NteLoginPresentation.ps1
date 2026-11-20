[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HostDirectory,
    [string] $HarnessExecutable
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$source = (Resolve-Path -LiteralPath $HostDirectory).Path
$harness = if ($HarnessExecutable) { (Resolve-Path -LiteralPath $HarnessExecutable).Path }
    else { Join-Path $repository 'tools/NteLoginPresentationHarness/bin/Release/net11.0-windows/NteLoginPresentationHarness.exe' }
if (-not (Test-Path -LiteralPath $harness)) { throw 'Build tools/NteLoginPresentationHarness before running the presentation fixture.' }
$directory = Join-Path $repository ('artifacts/login-host/presentation-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$preview = Join-Path $directory 'host-preview'
New-Item -ItemType Directory -Path $preview | Out-Null
# Never write fixture diagnostics to a published bundle, or use real accounts.
Get-ChildItem -LiteralPath $source | Where-Object { $_.Name -notlike 'preview-*' -and $_.Name -notlike 'startup-error*' } |
    Copy-Item -Destination $preview -Recurse
$executable = Join-Path $preview 'Nte.LoginHost.exe'
$layoutPath = Join-Path $preview 'preview-layout.json'
$sdkPath = Join-Path $preview 'preview-sdk.json'
$game = $null
$hostProcess = $null
$report = [ordered]@{ passed = $false; syntheticOnly = $true; ownerCommandsSent = 0; accountRequests = 0; checks = @(); recoveries = @() }
function Wait-Condition([scriptblock] $Condition, [int] $Seconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Stationary-owner presentation condition timed out.'
}
function Read-Layout {
    if (-not (Test-Path -LiteralPath $layoutPath)) { return $null }
    try { return Get-Content -LiteralPath $layoutPath -Raw | ConvertFrom-Json }
    catch [System.IO.IOException] { return $null }
    catch [System.ArgumentException] { return $null }
}
function Inspect-Window([int] $ProcessId) {
    $json = & $harness inspect $ProcessId
    if ($LASTEXITCODE -ne 0) { throw 'Window inspection failed.' }
    return $json | ConvertFrom-Json
}
function Send-SdkLogin {
    & $harness notify-login $sdkPath $hostProcess.Id
    if ($LASTEXITCODE -ne 0) { throw 'The simulated SDK login notification failed.' }
}
function Same-Client($First, $Second) {
    return $First.left -eq $Second.left -and $First.top -eq $Second.top -and
        $First.width -eq $Second.width -and $First.height -eq $Second.height
}
function Has-CompleteLayout($Layout) {
    return $null -ne $Layout -and $Layout.visible -and $Layout.aboveGame -and $Layout.xamlWidth -gt 0 -and $Layout.xamlHeight -gt 0 -and
        $Layout.formWidth -gt 0 -and $Layout.formDesiredHeight -gt 0 -and
        $Layout.formDesiredHeight -le $Layout.formAvailableHeight + 1 -and $Layout.mascotReady
}
function Assert-Presented {
    $layout = Read-Layout
    if (-not (Has-CompleteLayout $layout) -or $layout.hostPid -ne $hostProcess.Id -or $layout.gamePid -ne $game.Id) {
        throw 'The visible native surface does not contain a fully laid-out login card.'
    }
    $owner = Inspect-Window $game.Id
    $surface = Inspect-Window $hostProcess.Id
    if (-not $surface.visible -or $surface.nativeChild -or -not $surface.topmost -or
        $owner.topmost -ne $initialOwner.topmost -or
        $surface.ownerWindow -ne $owner.window -or -not (Same-Client $owner $surface)) {
        throw 'The login cover must be promoted and aligned without changing the owner.'
    }
    $stack = & $harness inspect-stack $game.Id $hostProcess.Id | ConvertFrom-Json
    $ownerOrder = @($stack.windows | Where-Object window -EQ $owner.window)[0].order
    $surfaceOrder = @($stack.windows | Where-Object window -EQ $surface.window)[0].order
    if ($surfaceOrder -ge $ownerOrder) { throw 'The visible cover is behind the owner in the actual native Z order.' }
    if (-not (Same-Client $initialOwner $owner) -or $initialOwner.window -ne $owner.window) {
        throw 'The owner moved or was recreated during the stationary presentation test.'
    }
    return $layout
}
try {
    $background = Join-Path $repository 'docs/design/references/game-login-night.png'
    $game = Start-Process -FilePath $harness -ArgumentList @('window', ('"' + $directory + '"'), ('"' + $background + '"')) -WindowStyle Normal -PassThru -RedirectStandardError (Join-Path $directory 'fixture-error.txt')
    Wait-Condition {
        if ($game.HasExited) { throw "The owner fixture exited before opening (exit $($game.ExitCode))." }
        return Test-Path -LiteralPath (Join-Path $directory 'ready.json')
    }
    $initialOwner = Inspect-Window $game.Id
    $report.owner = $initialOwner
    # In particular: no restore, move, resize, activate or mouse input is sent
    # to the owner after launching the host. Keep production owned-popup styles.
    $hostProcess = Start-Process -FilePath $executable -ArgumentList @('--preview-owned-pid', $game.Id) -WindowStyle Hidden -PassThru
    Wait-Condition { Test-Path -LiteralPath $sdkPath }
    if ((Inspect-Window $hostProcess.Id).visible) { throw 'The panel appeared before the login request.' }
    $foregroundBeforeLogin = (& $harness inspect-stack $game.Id | ConvertFrom-Json).foreground
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Send-SdkLogin
    # Loaded-priority measure callbacks finish after the native window becomes
    # visible. Wait for the complete card, not its intermediate first measure.
    Wait-Condition { return Has-CompleteLayout (Read-Layout) }
    $report.firstAppearanceMs = $clock.ElapsedMilliseconds
    $report.initialLayout = Assert-Presented
    if ($report.initialLayout.sdkLoginNotifications -ne 1) { throw 'The initial SDK notification was not observed.' }
    $foregroundAfterLogin = (& $harness inspect-stack $game.Id | ConvertFrom-Json).foreground
    if ($foregroundBeforeLogin -ne $foregroundAfterLogin) { throw 'Showing the login panel changed foreground focus.' }
    $report.checks += 'initial-sdk-login-shows-panel-without-changing-focus'
    $report.checks += 'first-layout-complete-and-native-client-aligned'
    $report.checks += 'owned-popup-promoted-without-changing-game-layer'
    foreach ($action in @('defer-presentation', 'defer-native-presentation', 'defer-z-order', 'repeat-sdk-login')) {
        $id = [Guid]::NewGuid().ToString('N')
        $temporary = Join-Path $preview 'preview-command.tmp'
        $command = if ($action -eq 'repeat-sdk-login') { 'defer-z-order' } else { $action }
        [IO.File]::WriteAllText($temporary, (@{ id = $id; action = $command } | ConvertTo-Json))
        [IO.File]::Move($temporary, (Join-Path $preview 'preview-command.json'), $true)
        $resultPath = Join-Path $preview 'preview-action-result.json'
        Wait-Condition {
            if (-not (Test-Path -LiteralPath $resultPath)) { return $false }
            try { $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json }
            catch { return $false }
            if ($result.id -ne $id) { return $false }
            if (-not $result.passed) { throw "Preview action failed: $($result.error)" }
            return $true
        }
        $hidden = Read-Layout
        if ($command -eq 'defer-z-order') {
            if (-not $hidden.visible -or $hidden.aboveGame) { throw 'The Z-order fixture must leave a visible cover below its owner.' }
        }
        elseif ($hidden.visible) { throw 'Deferred presentation did not hide the cover before recovery.' }
        $clock.Restart()
        if ($action -eq 'repeat-sdk-login') { Send-SdkLogin }
        Wait-Condition {
            $layout = Read-Layout
            return (Has-CompleteLayout $layout) -and
                ($action -ne 'repeat-sdk-login' -or $layout.sdkLoginNotifications -eq 2)
        } -Seconds 4
        $report.recoveries += [ordered]@{ action = $action; milliseconds = $clock.ElapsedMilliseconds }
        $null = Assert-Presented
        $report.checks += ($action + '-recovered-without-owner-input')
    }
    $report.checks += 'owner-handle-and-client-unchanged-throughout'
    $report.passed = $true
}
catch {
    $report.error = $_.Exception.Message
    throw
}
finally {
    foreach ($owned in @($hostProcess, $game)) {
        if ($null -ne $owned -and -not $owned.HasExited) { Stop-Process -Id $owned.Id -ErrorAction SilentlyContinue }
    }
    $reportPath = Join-Path $directory 'presentation-report.json'
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 12))
}
[pscustomobject]@{ Passed = $report.passed; Checks = $report.checks.Count; FirstAppearanceMs = $report.firstAppearanceMs;
    Recoveries = $report.recoveries; Report = $reportPath }
