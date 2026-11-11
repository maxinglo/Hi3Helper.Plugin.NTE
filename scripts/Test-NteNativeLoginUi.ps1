[CmdletBinding()]
param([Parameter(Mandatory)][string] $HostDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$directory = (Resolve-Path -LiteralPath $HostDirectory).Path
$executable = Join-Path $directory 'Nte.LoginHost.exe'
$reportDirectory = Join-Path $repository ('artifacts/login-host/native-ui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $reportDirectory | Out-Null
$process = Start-Process -FilePath $executable -ArgumentList @('--verify-ui', ('"' + $reportDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
$reportPath = Join-Path $reportDirectory 'native-ui-report.json'
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Native login UI verification did not produce a report (exit $($process.ExitCode))." }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Native login UI verification failed: $($report.error)" }
[pscustomobject]@{ Passed = $true; Checks = $report.count; SyntheticOnly = $report.syntheticOnly;
    AccountRequests = $report.accountRequests; Directory = $reportDirectory; Report = $reportPath }
