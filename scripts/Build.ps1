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
}
finally {
    Pop-Location
}
