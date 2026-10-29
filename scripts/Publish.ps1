[CmdletBinding()]
param(
    [ValidateSet('ReleasePublish-O1', 'ReleasePublish-O2', 'DebugPublish',
        'ReleaseNoReflectionPublish-O1', 'ReleaseNoReflectionPublish-O2', 'DebugNoReflectionPublish')]
    [string] $Profile = 'ReleasePublish-O1',
    [string] $Version,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $OutputName,
    [switch] $Package
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$configuration = if ($Profile.StartsWith('Debug')) { 'Debug' } else { 'Release' }
if ($Profile.Contains('NoReflection')) { $configuration += 'NoReflection' }
$effectiveOutputName = if ($OutputName) { $OutputName } else { $Profile }
$outputDirectory = Join-Path $repositoryRoot "artifacts/publish/$effectiveOutputName"
$project = Join-Path $repositoryRoot 'src/Hi3Helper.Plugin.NTE/Hi3Helper.Plugin.NTE.csproj'
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw 'Version must contain three or four numeric components.'
}

Push-Location $repositoryRoot
try {
    # Clean only this profile's generated directory, so old manifests cannot enter a new package.
    $publishRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/publish'))
    $resolvedOutput = [System.IO.Path]::GetFullPath($outputDirectory)
    if (-not $resolvedOutput.StartsWith($publishRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory is outside the publish root: $resolvedOutput"
    }
    if (Test-Path -LiteralPath $resolvedOutput) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $publishArguments = @('publish', $project, '-c', $configuration, '-r', 'win-x64',
        '-p:Platform=x64', "-p:PublishProfile=$Profile", '-p:RestoreLockedMode=true',
        '-p:NuGetAudit=false', '-p:CopyOutputSymbolsToPublishDirectory=false', '--nologo', '-o', $outputDirectory)
    if ($Version) { $publishArguments += "-p:Version=$Version" }
    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed ($LASTEXITCODE)." }

    # The plugin only hands off launch; WPF, SDK transport and authentication
    # run in this separate, self-contained process beside the native library.
    $loginProject = Join-Path $repositoryRoot 'src/Hi3Helper.Plugin.NTE.LoginHost/Hi3Helper.Plugin.NTE.LoginHost.csproj'
    $loginOutput = Join-Path $outputDirectory 'LoginHost'
    dotnet publish $loginProject -c Release -r win-x64 -p:Platform=x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:CopyOutputSymbolsToPublishDirectory=false `
        -p:RestoreLockedMode=true -p:NuGetAudit=false --nologo -o $loginOutput
    if ($LASTEXITCODE -ne 0) { throw "Bundled WPF login host publish failed ($LASTEXITCODE)." }

    & (Join-Path $PSScriptRoot 'Test-NteLoginHostResources.ps1') -HostDirectory $loginOutput | Out-Host

    & (Join-Path $PSScriptRoot 'Test-NtePackageContent.ps1') -PublishDirectory $outputDirectory | Out-Host
    & (Join-Path $PSScriptRoot 'Test-NativePlugin.ps1') -Path (Join-Path $outputDirectory 'NTE.dll') -ExpectedVersion $Version
    & (Join-Path $PSScriptRoot 'Test-NativeIntegrity.ps1') -PluginPath (Join-Path $outputDirectory 'NTE.dll')
    if ($Package) {
        $indexer = Join-Path $repositoryRoot 'Indexer.exe'
        if (-not (Test-Path -LiteralPath $indexer)) { throw "Missing release indexer: $indexer" }
        $packageDirectory = Join-Path $repositoryRoot 'artifacts/packages'
        New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
        $effectiveVersion = if ($Version) { $Version } else { ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
        $packageName = 'NTE_{0}_{1}.zip' -f $effectiveVersion, $effectiveOutputName
        & $indexer $outputDirectory
        if ($LASTEXITCODE -ne 0) { throw "Indexing failed ($LASTEXITCODE)." }
        # Preserve manifest path separators in ZIP entry names: the Collapse
        # importer compares FullName to manifest FilePath without normalization.
        & (Join-Path $PSScriptRoot 'New-NteImportPackage.ps1') -PublishDirectory $outputDirectory -ArchivePath (Join-Path $packageDirectory $packageName) | Out-Host
    }
    Write-Host "Output: $outputDirectory"
}
finally {
    Pop-Location
}
