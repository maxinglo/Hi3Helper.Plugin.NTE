[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HostDirectory,
    [ValidateSet('CN','Global','Taiwan')][string] $Region = 'Global',
    [ValidateSet('en','zh-CN','zh-TW','ja','ko','de','fr','ru','es')][string] $Language = 'zh-CN',
    [switch] $NoBuild,
    [switch] $Visible
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$hostDirectory = (Resolve-Path -LiteralPath $HostDirectory).Path
if (-not $hostDirectory.StartsWith((Join-Path $repository 'artifacts') + '\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a test or published host under this repository artifacts.'
}
$verificationProject = Join-Path $repository 'tools/NteLoginUiVerification/NteLoginUiVerification.csproj'
$fixtureProject = Join-Path $repository 'tools/NteLoginHostHarness/NteLoginHostHarness.csproj'
if (-not $NoBuild) {
    dotnet build $verificationProject -c Release -p:Platform=x64 -p:NuGetAudit=false --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'UI verification build failed.' }
    dotnet build $fixtureProject -c Release -p:NuGetAudit=false --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'UI fixture build failed.' }
}
$directory = Join-Path $repository ('artifacts/login-host/ux-verification-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$fixtureDirectory = Join-Path $directory 'fixture'
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
$fixtureExe = Join-Path $repository 'tools/NteLoginHostHarness/bin/Release/net10.0-windows/NteLoginHostHarness.exe'
$verificationExe = Join-Path $repository 'tools/NteLoginUiVerification/bin/x64/Release/net10.0-windows/NteLoginUiVerification.exe'
$quoteArgument = { param([string] $Value) '"' + $Value + '"' }
$fixtureMode = if ($Visible) { 'window' } else { 'window-offscreen' }
$fixtureWindowStyle = if ($Visible) { 'Normal' } else { 'Hidden' }
$fixture = Start-Process -FilePath $fixtureExe -ArgumentList @($fixtureMode,(& $quoteArgument $fixtureDirectory),(& $quoteArgument (Join-Path $repository 'tests/Fixtures/UI/game-login-night.png'))) `
    -WindowStyle $fixtureWindowStyle -PassThru -WorkingDirectory $repository -RedirectStandardOutput (Join-Path $directory 'fixture.stdout.log') -RedirectStandardError (Join-Path $directory 'fixture.stderr.log')
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath (Join-Path $fixtureDirectory 'ready.json'))) {
        if ($fixture.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Synthetic fixture did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    $verification = Start-Process -FilePath $verificationExe -ArgumentList @('serve',(& $quoteArgument $hostDirectory),(& $quoteArgument $directory),[string]$fixture.Id,$Region,$Language) `
        -WindowStyle Hidden -PassThru -WorkingDirectory $repository -RedirectStandardOutput (Join-Path $directory 'verification.stdout.log') -RedirectStandardError (Join-Path $directory 'verification.stderr.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath (Join-Path $directory 'endpoint.json'))) {
        if ($verification.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw "Verification did not become ready. See $directory" }
        Start-Sleep -Milliseconds 100
    }
    [ordered]@{ SessionDirectory=$directory; FixturePid=$fixture.Id; VerificationPid=$verification.Id; Region=$Region; Language=$Language; SyntheticOnly=$true; AuthenticationRequests=0 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'session.json')
    Get-Content -LiteralPath (Join-Path $directory 'session.json') -Raw | ConvertFrom-Json
}
catch {
    [IO.File]::WriteAllText((Join-Path $fixtureDirectory 'command.json'),'{"action":"close"}')
    throw
}
