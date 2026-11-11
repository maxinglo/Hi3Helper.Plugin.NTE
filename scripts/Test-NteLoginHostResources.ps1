[CmdletBinding()]
param([Parameter(Mandatory)][string] $HostDirectory)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $HostDirectory).Path
$executable = Join-Path $directory 'Nte.LoginHost.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw "Missing login host: $executable" }
# Resource-only mode executes in the published Native AOT binary without a
# renderer, account access, or network requests. Loading its former DLL via
# reflection would neither work nor verify the deployed executable.
$process = Start-Process -FilePath $executable -ArgumentList '--verify-resources' -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Native login resource verification failed ($($process.ExitCode))." }
[pscustomobject]@{ passed = $true; cultures = 9; publishedExecutable = $true; accountRequests = 0 }
