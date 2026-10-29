[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'DebugNoReflection', 'ReleaseNoReflection')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore Hi3Helper.Plugin.NTE.slnx --locked-mode -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "Restore failed ($LASTEXITCODE)." }
    dotnet build Hi3Helper.Plugin.NTE.slnx -c $Configuration -p:Platform=x64 --no-restore --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
    $testResults = Join-Path (Get-Location) "artifacts/test-results/$Configuration"
    New-Item -ItemType Directory -Path $testResults -Force | Out-Null
    $testRunner = Join-Path (Get-Location) "tests/Hi3Helper.Plugin.NTE.Tests/bin/x64/$Configuration/net10.0/Hi3Helper.Plugin.NTE.Tests.exe"
    & $testRunner -result-xml (Join-Path $testResults 'results.xml') -maxThreads 2
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}
