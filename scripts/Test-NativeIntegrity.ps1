[CmdletBinding()]
param([Parameter(Mandatory)][string] $PluginPath)
$ErrorActionPreference = 'Stop'
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$directory = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/native-integrity-test/' + [Guid]::NewGuid().ToString('N'))
$game = Join-Path $directory 'game'
New-Item -ItemType Directory -Path $game -Force | Out-Null
$expected = @{'good.pak'='GOOD';'bad.pak'='FIX!';'missing.pak'='ADDED'}
$elements = foreach ($name in $expected.Keys) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($expected[$name])
    $hash = [Convert]::ToHexString([Security.Cryptography.MD5]::HashData($bytes))
    '<Res filename="{0}" filesize="{1}" md5="{2}"/>' -f $name,$bytes.Length,$hash
}
$manifest = Join-Path $directory 'manifest.xml'
[IO.File]::WriteAllText($manifest, ('<ResList version="1.4.17">' + ($elements -join '') + '</ResList>'))
[IO.File]::WriteAllText((Join-Path $game 'good.pak'),'GOOD')
[IO.File]::WriteAllText((Join-Path $game 'bad.pak'),'BAD!')
$report = Join-Path $directory 'report.json'
$result = & (Join-Path $PSScriptRoot 'Check-NteIntegrity.ps1') -GameRoot $game -PluginPath $plugin -ManifestPath $manifest -ReportPath $report -Quiet
if ($result.Completed -ne $true -or $result.Healthy -ne $false -or $result.Issues.Count -ne 2) { throw 'Native integrity check did not detect the damaged fixture.' }
foreach ($name in $expected.Keys) { [IO.File]::WriteAllText((Join-Path $game $name),$expected[$name]) }
$result = & (Join-Path $PSScriptRoot 'Check-NteIntegrity.ps1') -GameRoot $game -PluginPath $plugin -ManifestPath $manifest -ReportPath $report -Quiet
if ($result.Healthy -ne $true -or $result.CheckedFiles -ne 3) { throw 'Native integrity check did not verify the healthy fixture.' }
Write-Host 'Native integrity ABI and report serialization OK (damaged and healthy fixtures).'
