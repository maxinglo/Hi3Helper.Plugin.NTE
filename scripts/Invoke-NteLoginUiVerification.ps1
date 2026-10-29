[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SessionDirectory,
    [Parameter(Mandatory)][ValidateSet('state','invoke','toggle','fill','select','focus','key','scenario','capture','close','resize','prompt-probe')][string] $Action,
    [string] $Node,
    [long] $Revision,
    [string] $Value,
    [ValidateSet('Enter','Escape','Up','Down','Left','Right','Tab','Space')][string] $Key,
    [Nullable[int]] $Index,
    [Nullable[bool]] $Checked,
    [Nullable[int]] $AccountCount,
    [ValidateSet('legacy','missing','legacy-profile')][string] $ProfileVariant,
    [Nullable[bool]] $Busy,
    [Nullable[bool]] $FirstUseNotice,
    [string] $Notice,
    [string] $Name,
    [ValidateSet('success','cancel','failure')][string] $Outcome = 'success',
    [bool] $InitiallyTopmost = $false,
    [ValidateSet('night','day','sunset')][string] $Theme = 'night',
    [ValidateRange(640,3840)][int] $Width = 1180,
    [ValidateRange(360,2160)][int] $Height = 730
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$directory = (Resolve-Path -LiteralPath $SessionDirectory).Path
if (-not $directory.StartsWith((Join-Path $repository 'artifacts') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Session must stay under artifacts.' }
if ($Action -eq 'resize') {
    [IO.File]::WriteAllText((Join-Path $directory 'fixture/command.json'),(@{action='resize';width=$Width;height=$Height;sequence=[Guid]::NewGuid().ToString('N')} | ConvertTo-Json))
    return [pscustomobject]@{ok=$true;requestedWidth=$Width;requestedHeight=$Height;note='Observe state to verify resize completion.'}
}
$request = [ordered]@{ action=$Action }
foreach ($entry in @{node='Node';revision='Revision';value='Value';key='Key';index='Index';checked='Checked';accountCount='AccountCount';profileVariant='ProfileVariant';busy='Busy';firstUseNotice='FirstUseNotice';notice='Notice';name='Name'}.GetEnumerator()) {
    if ($PSBoundParameters.ContainsKey($entry.Value)) { $request[$entry.Key] = $PSBoundParameters[$entry.Value] }
}
if ($Action -eq 'capture') { $request.theme=$Theme }
if ($Action -eq 'prompt-probe') { $request.outcome=$Outcome; $request.initiallyTopmost=$InitiallyTopmost }
$requestPath = Join-Path $directory ('request-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($requestPath,($request | ConvertTo-Json -Depth 4))
    $tool = Join-Path $repository 'tools/NteLoginUiVerification/bin/x64/Release/net10.0-windows/NteLoginUiVerification.exe'
    $output = & $tool call $directory $requestPath
    $exitCode = $LASTEXITCODE
    $result = $output | ConvertFrom-Json
    if ($exitCode -notin @(0,2)) { throw 'Verification transport or setup failed.' }
    $result
}
finally { if (Test-Path -LiteralPath $requestPath) { Remove-Item -LiteralPath $requestPath } }
