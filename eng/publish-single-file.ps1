[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildCacheRoot,
    [string]$ArtifactsRoot,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\AutoEnvPlus.App\AutoEnvPlus.App.csproj'
$BuildCacheRoot = if (-not [string]::IsNullOrWhiteSpace($BuildCacheRoot)) {
    [System.IO.Path]::GetFullPath($BuildCacheRoot)
}
elseif (-not [string]::IsNullOrWhiteSpace($env:AUTOENVPLUS_BUILD_CACHE_ROOT)) {
    [System.IO.Path]::GetFullPath($env:AUTOENVPLUS_BUILD_CACHE_ROOT)
}
else {
    'D:\codex'
}
$ArtifactsRoot = if (-not [string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    [System.IO.Path]::GetFullPath($ArtifactsRoot)
}
elseif (-not [string]::IsNullOrWhiteSpace($env:AUTOENVPLUS_ARTIFACTS_ROOT)) {
    [System.IO.Path]::GetFullPath($env:AUTOENVPLUS_ARTIFACTS_ROOT)
}
else {
    Join-Path $repositoryRoot 'artifacts'
}
$stagingRoot = Join-Path $ArtifactsRoot '.staging\AutoEnvPlus-win-x64-single-file'
$cliStagingRoot = Join-Path $ArtifactsRoot '.staging\AutoEnvPlus.Cli-win-x64-single-file'
$assetPath = Join-Path $ArtifactsRoot 'AutoEnvPlus-win-x64.exe'
$hashPath = "$assetPath.sha256"
$manifestPath = "$assetPath.bundle-manifest.txt"
$bundleMapPath = Join-Path $stagingRoot 'bundle-inputs.txt'

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Child
    )

    $parentPath = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $childPath = [System.IO.Path]::GetFullPath($Child)
    $prefix = $parentPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $childPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must remain below $parentPath; found $childPath."
    }
}

function Remove-GeneratedPath {
    param([Parameter(Mandatory)][string]$Path)

    Assert-ChildPath -Parent $ArtifactsRoot -Child $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Set-BuildEnvironment {
    $env:AUTOENVPLUS_BUILD_CACHE_ROOT = $BuildCacheRoot
    $env:NUGET_PACKAGES = Join-Path $BuildCacheRoot '.nuget\packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $BuildCacheRoot '.nuget\v3-cache'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $BuildCacheRoot '.nuget\plugins-cache'
    $env:DOTNET_CLI_HOME = Join-Path $BuildCacheRoot '.dotnet-home'
    $env:TEMP = Join-Path $BuildCacheRoot 'tmp'
    $env:TMP = $env:TEMP
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    foreach ($path in @(
        $env:NUGET_PACKAGES,
        $env:NUGET_HTTP_CACHE_PATH,
        $env:NUGET_PLUGINS_CACHE_PATH,
        $env:DOTNET_CLI_HOME,
        $env:TEMP,
        $ArtifactsRoot
    )) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

Set-BuildEnvironment
Remove-GeneratedPath -Path $stagingRoot
Remove-GeneratedPath -Path $cliStagingRoot
foreach ($path in @($assetPath, $hashPath, $manifestPath)) {
    Remove-GeneratedPath -Path $path
}
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
New-Item -ItemType Directory -Path $cliStagingRoot -Force | Out-Null

$cliArguments = @(
    'publish',
    (Join-Path $repositoryRoot 'src\AutoEnvPlus.Cli\AutoEnvPlus.Cli.csproj'),
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishReadyToRun=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:CopyOutputSymbolsToPublishDirectory=false',
    '-o', $cliStagingRoot
)
if ($NoRestore) {
    $cliArguments += '--no-restore'
}

& dotnet @cliArguments
if ($LASTEXITCODE -ne 0) {
    throw "CLI dotnet publish exited with code $LASTEXITCODE."
}

$bundledCliPath = Join-Path $cliStagingRoot 'autoenvplus.exe'
$bundledShimPath = Join-Path $repositoryRoot "src\AutoEnvPlus.Shim\bin\$Configuration\win-x64\autoenvplus-shim.exe"
foreach ($companion in @($bundledCliPath, $bundledShimPath)) {
    if (-not (Test-Path -LiteralPath $companion -PathType Leaf)) {
        throw "Single-file companion was not produced: $companion"
    }
}

$arguments = @(
    'publish',
    $projectPath,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:Platform=x64',
    '-p:PublishSingleFile=true',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:EnableMsixTooling=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:PublishReadyToRun=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:CopyOutputSymbolsToPublishDirectory=false',
    "-p:AutoEnvPlusBundledCliPath=$bundledCliPath",
    "-p:AutoEnvPlusBundledShimPath=$bundledShimPath",
    "-p:AutoEnvPlusBundleManifestPath=$bundleMapPath",
    '-p:AutoEnvPlusSingleFileAssetName=AutoEnvPlus-win-x64',
    '-o', $stagingRoot
)
if ($NoRestore) {
    $arguments += '--no-restore'
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish exited with code $LASTEXITCODE."
}

$publishedFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse)
$publishedExecutable = Join-Path $stagingRoot 'AutoEnvPlus.App.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "The .NET bundler did not emit $publishedExecutable."
}
$unexpectedFiles = @($publishedFiles | Where-Object {
    $_.FullName -ne $publishedExecutable -and $_.FullName -ne $bundleMapPath
})
if ($unexpectedFiles.Count -ne 0) {
    $details = ($unexpectedFiles.FullName | Sort-Object) -join [Environment]::NewLine
    throw "Single-file staging contains external payloads that an end user would need to carry:`n$details"
}

if (-not (Test-Path -LiteralPath $bundleMapPath -PathType Leaf)) {
    throw "The SDK bundle input manifest was not produced at $bundleMapPath."
}
$bundleMap = Get-Content -LiteralPath $bundleMapPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$requiredEntries = @(
    'AutoEnvPlus.App.dll',
    'AutoEnvPlus.Core.dll',
    'AutoEnvPlus.App.pri',
    'AutoEnvPlus-win-x64.pri',
    'Microsoft.ui.xaml.dll',
    'Microsoft.WindowsAppRuntime.dll',
    'System.Private.CoreLib.dll',
    'cli/autoenvplus.exe',
    'cli/autoenvplus-shim.exe'
)
foreach ($requiredEntry in $requiredEntries) {
    $normalizedEntry = $requiredEntry.Replace('\', '/')
    if (-not ($bundleMap | Where-Object {
        $parts = $_ -split '\|', 3
        $targetPath = $parts[0].Replace('\', '/')
        $sourcePath = $parts[1].Replace('\', '/')
        $targetPath.Equals($normalizedEntry, [System.StringComparison]::OrdinalIgnoreCase) -or
            $targetPath.EndsWith('/' + $normalizedEntry, [System.StringComparison]::OrdinalIgnoreCase) -or
            $sourcePath.EndsWith('/' + $normalizedEntry, [System.StringComparison]::OrdinalIgnoreCase)
    })) {
        throw "SDK bundle input manifest is missing required entry: $requiredEntry"
    }
}

Move-Item -LiteralPath $publishedExecutable -Destination $assetPath
[System.IO.File]::WriteAllLines(
    $manifestPath,
    $bundleMap,
    (New-Object System.Text.UTF8Encoding($false)))
$hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$hash *$(Split-Path $assetPath -Leaf)" -Encoding ASCII
Remove-GeneratedPath -Path $stagingRoot
Remove-GeneratedPath -Path $cliStagingRoot

$asset = Get-Item -LiteralPath $assetPath
Write-Host "Published a true .NET/Windows App SDK single-file executable."
Write-Host "Asset: $assetPath"
Write-Host "Size: $($asset.Length) bytes ($([math]::Round($asset.Length / 1MB, 2)) MiB)"
Write-Host "SHA-256: $hash"
Write-Host "SDK bundle input manifest: $manifestPath"
