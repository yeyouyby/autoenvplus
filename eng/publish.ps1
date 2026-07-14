[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoArchive
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$outputRoot = Join-Path $artifactsRoot 'AutoEnvPlus-win-x64'
$cliStage = Join-Path $artifactsRoot '.staging\AutoEnvPlus.Cli-win-x64'
$archivePath = Join-Path $artifactsRoot 'AutoEnvPlus-win-x64.zip'
$appBuildRoot = Join-Path $repositoryRoot "src\AutoEnvPlus.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64"

function Assert-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $fullArtifactsRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish output must remain inside $fullArtifactsRoot."
    }
}

function Remove-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    Assert-ArtifactPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Remove-ArtifactPath -Path $outputRoot
Remove-ArtifactPath -Path $cliStage
if (Test-Path -LiteralPath $archivePath) {
    Assert-ArtifactPath -Path $archivePath
    Remove-Item -LiteralPath $archivePath -Force
}

try {
    Invoke-DotNet -Arguments @(
        'publish',
        (Join-Path $repositoryRoot 'src\AutoEnvPlus.Cli\AutoEnvPlus.Cli.csproj'),
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $cliStage
    )

    Invoke-DotNet -Arguments @(
        'build',
        (Join-Path $repositoryRoot 'src\AutoEnvPlus.App\AutoEnvPlus.App.csproj'),
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:Platform=x64',
        '-p:PublishReadyToRun=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $appBuildRoot '*') -Destination $outputRoot -Recurse -Force

    foreach ($staleCliFile in @(
        'autoenvplus.deps.json',
        'autoenvplus.dll',
        'autoenvplus.exe',
        'autoenvplus.runtimeconfig.json'
    )) {
        $stalePath = Join-Path $outputRoot $staleCliFile
        if (Test-Path -LiteralPath $stalePath) {
            Remove-Item -LiteralPath $stalePath -Force
        }
    }

    $cliOutput = Join-Path $outputRoot 'cli'
    if (Test-Path -LiteralPath $cliOutput) {
        Remove-Item -LiteralPath $cliOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Path $cliOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $cliStage 'autoenvplus.exe') -Destination $cliOutput
    $nativeShim = Join-Path $repositoryRoot "src\AutoEnvPlus.Shim\bin\$Configuration\win-x64\autoenvplus-shim.exe"
    Copy-Item -LiteralPath $nativeShim -Destination $cliOutput

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $outputRoot
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $outputRoot
    $licensesOutput = Join-Path $outputRoot 'third_party\licenses'
    New-Item -ItemType Directory -Path $licensesOutput -Force | Out-Null
    Copy-Item -Path (Join-Path $repositoryRoot 'third_party\licenses\*') -Destination $licensesOutput -Force

    $requiredFiles = @(
        (Join-Path $outputRoot 'AutoEnvPlus.App.exe'),
        (Join-Path $outputRoot 'AutoEnvPlus.App.pri'),
        (Join-Path $outputRoot 'App.xbf'),
        (Join-Path $outputRoot 'Pages\DashboardPage.xbf'),
        (Join-Path $outputRoot 'coreclr.dll'),
        (Join-Path $outputRoot 'hostfxr.dll'),
        (Join-Path $outputRoot 'LICENSE'),
        (Join-Path $outputRoot 'THIRD-PARTY-NOTICES.md'),
        (Join-Path $licensesOutput 'Sigstore.Net-Apache-2.0.txt'),
        (Join-Path $cliOutput 'autoenvplus.exe'),
        (Join-Path $cliOutput 'autoenvplus-shim.exe')
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Publish output is incomplete: $requiredFile"
        }
    }

    $checksumPath = Join-Path $outputRoot 'SHA256SUMS.txt'
    $checksumLines = Get-ChildItem -LiteralPath $outputRoot -File -Recurse |
        Where-Object FullName -ne $checksumPath |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($outputRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash *$relativePath"
        }
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding UTF8

    if (-not $NoArchive) {
        Compress-Archive -Path (Join-Path $outputRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath ($archivePath + '.sha256') -Value "$archiveHash *$(Split-Path $archivePath -Leaf)" -Encoding ASCII
    }

    $publishedFiles = Get-ChildItem -LiteralPath $outputRoot -File -Recurse
    $publishedBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
    Write-Host "Published $($publishedFiles.Count) files ($([math]::Round($publishedBytes / 1MB, 2)) MB)."
    Write-Host "Portable layout: $outputRoot"
    if (-not $NoArchive) {
        Write-Host "Archive: $archivePath"
    }
}
finally {
    Remove-ArtifactPath -Path $cliStage
}
