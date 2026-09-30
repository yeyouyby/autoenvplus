[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version,
    [string]$BuildCacheRoot,
    [string]$ArtifactsRoot,
    [string]$PortableRoot,
    [switch]$NoRestore,
    [switch]$RequireSignedPayload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$modulePath = Join-Path $PSScriptRoot 'AutoEnvPlus.Packaging.psm1'
$installerProject = Join-Path $repositoryRoot 'packaging\AutoEnvPlus.Installer\AutoEnvPlus.Installer.wixproj'
$installerLock = Join-Path $repositoryRoot 'packaging\WixToolset.lock.json'
$iconPath = Join-Path $repositoryRoot 'assets\branding\autoenvplus.ico'

Import-Module $modulePath -Force
$versionInfo = Get-AutoEnvPlusVersionInfo

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $versionInfo.ProductVersion
}
$Version = $Version.Trim()
if (-not [string]::Equals($Version, $versionInfo.ProductVersion, [System.StringComparison]::Ordinal)) {
    throw "Version must match Directory.Build.props ($($versionInfo.ProductVersion)); found $Version."
}

$artifactsRoot = if (-not [string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    [System.IO.Path]::GetFullPath($ArtifactsRoot)
}
elseif (-not [string]::IsNullOrWhiteSpace($env:AUTOENVPLUS_ARTIFACTS_ROOT)) {
    [System.IO.Path]::GetFullPath($env:AUTOENVPLUS_ARTIFACTS_ROOT)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
}

if ([string]::IsNullOrWhiteSpace($PortableRoot)) {
    $PortableRoot = Join-Path $artifactsRoot 'AutoEnvPlus-win-x64'
}
$PortableRoot = [System.IO.Path]::GetFullPath($PortableRoot)

$outputPath = Join-Path $artifactsRoot 'AutoEnvPlus-win-x64.msi'
$checksumPath = "$outputPath.sha256"
$stagingRoot = Join-Path $artifactsRoot '.staging\msi'
$generatedSource = Join-Path $stagingRoot 'AutoEnvPlus.Payload.wxs'
$buildOutput = Join-Path $stagingRoot 'build'
$intermediateOutput = Join-Path $stagingRoot 'obj'

function Assert-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $root = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI output must remain below ${root}: $fullPath"
    }
}

function Remove-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    Assert-ArtifactPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to remove a reparse point from the artifact tree: $Path"
        }
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Set-BuildCacheEnvironment {
    if ([string]::IsNullOrWhiteSpace($BuildCacheRoot)) {
        if (-not [string]::IsNullOrWhiteSpace($env:AUTOENVPLUS_BUILD_CACHE_ROOT)) {
            $script:BuildCacheRoot = [System.IO.Path]::GetFullPath($env:AUTOENVPLUS_BUILD_CACHE_ROOT)
        }
        elseif ([System.IO.Path]::GetPathRoot($repositoryRoot) -eq 'D:\') {
            $script:BuildCacheRoot = 'D:\codex'
        }
        else {
            $script:BuildCacheRoot = Join-Path $repositoryRoot '.build-cache'
        }
    }
    else {
        $script:BuildCacheRoot = [System.IO.Path]::GetFullPath($BuildCacheRoot)
    }

    $env:AUTOENVPLUS_BUILD_CACHE_ROOT = $script:BuildCacheRoot
    $env:NUGET_PACKAGES = Join-Path $script:BuildCacheRoot '.nuget\packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $script:BuildCacheRoot '.nuget\v3-cache'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $script:BuildCacheRoot '.nuget\plugins-cache'
    $env:DOTNET_CLI_HOME = Join-Path $script:BuildCacheRoot '.dotnet'
    $env:TEMP = Join-Path $script:BuildCacheRoot 'tmp'
    $env:TMP = $env:TEMP
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    foreach ($path in @(
        $env:NUGET_PACKAGES,
        $env:NUGET_HTTP_CACHE_PATH,
        $env:NUGET_PLUGINS_CACHE_PATH,
        $env:DOTNET_CLI_HOME,
        $env:TEMP
    )) {
        [System.IO.Directory]::CreateDirectory($path) | Out-Null
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function ConvertTo-XmlText {
    param([AllowEmptyString()][string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function Get-Sha1Hex {
    param([Parameter(Mandatory)][string]$Value)
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $bytes = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
        return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha1.Dispose()
    }
}

function New-DeterministicGuid {
    param(
        [Parameter(Mandatory)][Guid]$Namespace,
        [Parameter(Mandatory)][string]$Name
    )

    # RFC 4122 UUIDv5; normalize Guid's mixed-endian byte representation first.
    $namespaceBytes = $Namespace.ToByteArray()
    [Array]::Reverse($namespaceBytes, 0, 4)
    [Array]::Reverse($namespaceBytes, 4, 2)
    [Array]::Reverse($namespaceBytes, 6, 2)
    $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($Name)
    $inputBytes = New-Object byte[] ($namespaceBytes.Length + $nameBytes.Length)
    [Array]::Copy($namespaceBytes, 0, $inputBytes, 0, $namespaceBytes.Length)
    [Array]::Copy($nameBytes, 0, $inputBytes, $namespaceBytes.Length, $nameBytes.Length)

    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $hash = $sha1.ComputeHash($inputBytes)
    }
    finally {
        $sha1.Dispose()
    }
    $hash[6] = ($hash[6] -band 0x0f) -bor 0x50
    $hash[8] = ($hash[8] -band 0x3f) -bor 0x80
    $guidBytes = New-Object byte[] 16
    [Array]::Copy($hash, $guidBytes, 16)
    [Array]::Reverse($guidBytes, 0, 4)
    [Array]::Reverse($guidBytes, 4, 2)
    [Array]::Reverse($guidBytes, 6, 2)
    return [Guid]::new($guidBytes)
}

function Assert-NoReparsePoint {
    param([Parameter(Mandatory)][string]$Path)

    $root = [System.IO.Path]::GetFullPath($PortableRoot).TrimEnd('\', '/')
    $currentPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $rootPrefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $currentPath.Equals($root, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $currentPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Portable payload path escaped its root: $Path"
    }

    while ($true) {
        $current = Get-Item -LiteralPath $currentPath -Force
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Portable payload contains a reparse point: $currentPath"
        }
        if ($currentPath.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $parent = [System.IO.Path]::GetDirectoryName($currentPath)
        if ([string]::IsNullOrWhiteSpace($parent)) {
            throw "Portable payload path escaped its root: $Path"
        }
        $currentPath = $parent.TrimEnd('\', '/')
    }
}

function Get-RelativePayloadPath {
    param([Parameter(Mandatory)][string]$FullPath)

    $root = [System.IO.Path]::GetFullPath($PortableRoot).TrimEnd('\', '/')
    $full = [System.IO.Path]::GetFullPath($FullPath)
    $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Portable payload path escaped its root: $FullPath"
    }
    $relative = $full.Substring($prefix.Length)
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [System.IO.Path]::IsPathRooted($relative) -or
        $relative -match '^(?:\.\.(?:[\\/]|$))') {
        throw "Portable payload path is invalid: $FullPath"
    }
    return $relative.Replace('/', '\')
}

function Get-PayloadFiles {
    Assert-NoReparsePoint -Path $PortableRoot
    $directories = @(Get-ChildItem -LiteralPath $PortableRoot -Directory -Recurse -Force)
    foreach ($directory in $directories) {
        Assert-NoReparsePoint -Path $directory.FullName
    }
    $files = @(Get-ChildItem -LiteralPath $PortableRoot -File -Recurse -Force)
    if ($files.Count -eq 0) {
        throw "Portable payload is empty: $PortableRoot"
    }
    if ($files.Count -gt 20000) {
        throw "Portable payload contains too many files ($($files.Count)); refusing to generate an oversized MSI."
    }

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $result = foreach ($file in $files) {
        Assert-NoReparsePoint -Path $file.FullName
        $relative = Get-RelativePayloadPath -FullPath $file.FullName
        if (-not $seen.Add($relative)) {
            throw "Portable payload has duplicate case-insensitive path: $relative"
        }
        [pscustomobject]@{
            FullPath = $file.FullName
            RelativePath = $relative
            DirectoryPath = [System.IO.Path]::GetDirectoryName($relative)
            Name = $file.Name
            Length = $file.Length
        }
    }
    return @($result | Sort-Object RelativePath)
}

function Assert-PayloadRequiredFiles {
    param([Parameter(Mandatory)][object[]]$Files)

    $paths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Files) { [void]$paths.Add($file.RelativePath) }
    foreach ($required in @(
        'AutoEnvPlus.App.exe',
        'AutoEnvPlus.App.dll',
        'AutoEnvPlus.Core.dll',
        'AutoEnvPlus.App.pri',
        'App.xbf',
        'LICENSE',
        'THIRD-PARTY-NOTICES.md',
        'cli\autoenvplus.exe',
        'cli\autoenvplus-shim.exe'
    )) {
        if (-not $paths.Contains($required)) {
            throw "Portable payload is missing required file: $required"
        }
    }
    $licenseFiles = @($Files | Where-Object { $_.RelativePath -like 'third_party\licenses\*' })
    if ($licenseFiles.Count -eq 0) {
        throw 'Portable payload must contain third_party\licenses files.'
    }
}

function Assert-PayloadManifest {
    param([Parameter(Mandatory)][object[]]$Files)

    $manifestPath = Join-Path $PortableRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Portable payload is missing SHA256SUMS.txt: $manifestPath"
    }
    Assert-NoReparsePoint -Path $manifestPath
    $listed = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [System.IO.File]::ReadAllLines($manifestPath, [System.Text.Encoding]::UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64}) \*(?<path>.+)$') {
            throw "Invalid SHA256SUMS.txt line: $line"
        }
        $relative = $Matches.path.Trim().Replace('/', '\')
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative -match '^(?:\.\.(?:[\\/]|$))') {
            throw "SHA256SUMS.txt contains an escaping path: $relative"
        }
        $full = [System.IO.Path]::GetFullPath((Join-Path $PortableRoot $relative))
        $rootPrefix = $PortableRoot.TrimEnd('\') + '\'
        if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "SHA256SUMS.txt references a missing or escaping file: $relative"
        }
        if ($relative -ieq 'SHA256SUMS.txt' -or -not $listed.Add($relative)) {
            throw "SHA256SUMS.txt contains a duplicate or self-referential path: $relative"
        }
        $actual = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if (-not [string]::Equals($actual, $Matches.hash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "SHA256SUMS.txt hash mismatch for $relative."
        }
    }

    $actualPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Files) {
        if ($file.RelativePath -ine 'SHA256SUMS.txt') {
            [void]$actualPaths.Add($file.RelativePath)
        }
    }
    foreach ($path in $actualPaths) {
        if (-not $listed.Contains($path)) {
            throw "SHA256SUMS.txt does not cover payload file: $path"
        }
    }
    foreach ($path in $listed) {
        if (-not $actualPaths.Contains($path)) {
            throw "SHA256SUMS.txt contains an untracked path: $path"
        }
    }
    return (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PayloadSignatureState {
    param([Parameter(Mandatory)][object[]]$Files)

    $results = @{}
    foreach ($relative in @(
        'AutoEnvPlus.App.exe',
        'AutoEnvPlus.App.dll',
        'AutoEnvPlus.Core.dll',
        'cli\autoenvplus.exe',
        'cli\autoenvplus-shim.exe'
    )) {
        $file = $Files | Where-Object RelativePath -eq $relative | Select-Object -First 1
        if ($null -eq $file) { continue }
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullPath
        $results[$relative] = [string]$signature.Status
        if ($RequireSignedPayload -and $signature.Status -ne 'Valid') {
            throw "Signed payload was required, but $relative has Authenticode status '$($signature.Status)'."
        }
    }
    return $results
}

function New-PayloadWixSource {
    param(
        [Parameter(Mandatory)][object[]]$Files,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $directoryId = @{'INSTALLFOLDER' = 'INSTALLFOLDER'}
    $directoryDisplayName = @{}
    foreach ($file in $Files) {
        $dir = $file.DirectoryPath
        if ([string]::IsNullOrEmpty($dir)) { continue }
        $parts = $dir.Split('\')
        $current = ''
        foreach ($part in $parts) {
            $current = if ([string]::IsNullOrEmpty($current)) { $part } else { "$current\$part" }
            if (-not $directoryId.ContainsKey($current)) {
                $directoryId[$current] = 'Dir_' + (Get-Sha1Hex -Value "directory|$current").Substring(0, 20)
                $directoryDisplayName[$current] = $part
            }
        }
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')

    function Add-DirectoryXml {
        param([string]$Parent, [int]$Indent)
        $prefix = if ([string]::IsNullOrEmpty($Parent)) { '' } else { "$Parent\" }
        $children = @($directoryDisplayName.Keys | Where-Object {
            $key = $_
            $key.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -and
                $key.Substring($prefix.Length).IndexOf('\') -lt 0
        } | Sort-Object)
        foreach ($child in $children) {
            $name = $directoryDisplayName[$child]
            $spaces = ' ' * $Indent
            [void]$builder.AppendLine(('{0}<Directory Id="{1}" Name="{2}">' -f
                $spaces,
                $directoryId[$child],
                (ConvertTo-XmlText $name)))
            Add-DirectoryXml -Parent $child -Indent ($Indent + 2)
            [void]$builder.AppendLine("$spaces</Directory>")
        }
    }
    Add-DirectoryXml -Parent '' -Indent 6
    [void]$builder.AppendLine('    </DirectoryRef>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <ComponentGroup Id="PayloadComponents">')

    foreach ($file in @($Files | Sort-Object RelativePath)) {
        $dir = [string]$file.DirectoryPath
        $dirId = if ([string]::IsNullOrEmpty($dir)) { 'INSTALLFOLDER' } else { $directoryId[$dir] }
        $componentId = 'Payload_' + (Get-Sha1Hex -Value "component|$($file.RelativePath)").Substring(0, 20)
        $componentGuid = (New-DeterministicGuid `
            -Namespace ([Guid]'D7D5C3EA-5D6B-5D9B-8F12-7649E1B13A0D') `
            -Name "component|x64|$($file.RelativePath)").ToString('B').ToUpperInvariant()
        $fileId = 'File_' + (Get-Sha1Hex -Value "file|$($file.RelativePath)").Substring(0, 24)
        $source = "`$(var.PortableRoot)\$($file.RelativePath)"
        $sourceXml = ConvertTo-XmlText -Value $source
        $registryKey = "Software\AutoEnvPlus\Installer\Components\$componentId"
        $registryValue = ConvertTo-XmlText -Value $file.RelativePath
        [void]$builder.AppendLine(('      <Component Id="{0}" Directory="{1}" Guid="{2}">' -f
            $componentId,
            $dirId,
            $componentGuid))
        [void]$builder.AppendLine(('        <File Id="{0}" Source="{1}" Checksum="yes" />' -f
            $fileId,
            $sourceXml))
        [void]$builder.AppendLine(('        <RegistryKey Root="HKCU" Key="{0}">' -f $registryKey))
        [void]$builder.AppendLine(('          <RegistryValue Name="PayloadFile" Type="string" Value="{0}" KeyPath="yes" />' -f
            $registryValue))
        [void]$builder.AppendLine('        </RegistryKey>')
        [void]$builder.AppendLine('      </Component>')
    }
    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <ComponentGroup Id="PayloadDirectoryCleanupComponents">')
    foreach ($dir in @($directoryDisplayName.Keys | Sort-Object)) {
        $dirId = $directoryId[$dir]
        $componentId = 'Cleanup_' + (Get-Sha1Hex -Value "cleanup|$dir").Substring(0, 20)
        $componentGuid = (New-DeterministicGuid `
            -Namespace ([Guid]'176E6BA7-83EA-5202-BFAF-EB82D78655D4') `
            -Name "cleanup|x64|$dir").ToString('B').ToUpperInvariant()
        $removeId = 'Remove_' + (Get-Sha1Hex -Value "remove|$dir").Substring(0, 20)
        $registryKey = "Software\AutoEnvPlus\Installer\Directories\$componentId"
        $registryValue = ConvertTo-XmlText -Value $dir
        [void]$builder.AppendLine(('      <Component Id="{0}" Directory="{1}" Guid="{2}">' -f
            $componentId,
            $dirId,
            $componentGuid))
        [void]$builder.AppendLine(('        <RemoveFolder Id="{0}" Directory="{1}" On="uninstall" />' -f
            $removeId,
            $dirId))
        [void]$builder.AppendLine(('        <RegistryKey Root="HKCU" Key="{0}">' -f $registryKey))
        [void]$builder.AppendLine(('          <RegistryValue Name="PayloadDirectory" Type="string" Value="{0}" KeyPath="yes" />' -f
            $registryValue))
        [void]$builder.AppendLine('        </RegistryKey>')
        [void]$builder.AppendLine('      </Component>')
    }
    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')

    [System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}

function Get-MsiOutput {
    param([Parameter(Mandatory)][string]$Root)
    $candidates = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Filter '*.msi' | Where-Object Name -eq 'AutoEnvPlus-win-x64.msi')
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one WiX MSI output named AutoEnvPlus-win-x64.msi below $Root; found $($candidates.Count)."
    }
    return $candidates[0].FullName
}

Set-BuildCacheEnvironment
if (-not (Test-Path -LiteralPath $installerProject -PathType Leaf)) { throw "Installer project not found: $installerProject" }
if (-not (Test-Path -LiteralPath $installerLock -PathType Leaf)) { throw "WiX lock file not found: $installerLock" }
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw "Installer icon not found: $iconPath" }
if (-not (Test-Path -LiteralPath $PortableRoot -PathType Container)) { throw "Portable payload root not found: $PortableRoot" }

$lock = Get-Content -LiteralPath $installerLock -Raw -Encoding UTF8 | ConvertFrom-Json
if ($lock.packageId -ne 'WixToolset.Sdk' -or $lock.version -ne '4.0.6' -or
    $lock.sha256 -ne '029C37C6490A810F61BCD375DD661ACE04C328640A9DADAEF1E7149BC14FF0F6') {
    throw 'WixToolset.lock.json is not the expected pinned WiX Toolset 4.0.6 lock.'
}

$files = Get-PayloadFiles
Assert-PayloadRequiredFiles -Files $files
$payloadHash = Assert-PayloadManifest -Files $files
$signatureState = Get-PayloadSignatureState -Files $files

$upgradeCode = [Guid]'2D82A0C5-6E8A-5B8C-9F1A-54A693A70B6D'
$productCode = New-DeterministicGuid -Namespace ([Guid]'A8B9C65C-3E96-5E2A-8D09-7C2F5F6DBF2A') -Name "AutoEnvPlus|x64|$Version"

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Remove-ArtifactPath -Path $outputPath
Remove-ArtifactPath -Path $checksumPath
Remove-ArtifactPath -Path $stagingRoot
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
New-PayloadWixSource -Files $files -OutputPath $generatedSource

$msiSourceArguments = @(
    'restore', $installerProject,
    '--locked-mode',
    '-p:RestorePackagesWithLockFile=true',
    '-p:Platform=x64'
)
if (-not $NoRestore) {
    Invoke-DotNet -Arguments $msiSourceArguments
}

$packageCache = Join-Path $env:NUGET_PACKAGES 'wixtoolset.sdk\4.0.6'
$cachedPackage = Join-Path $packageCache 'wixtoolset.sdk.4.0.6.nupkg'
if (-not (Test-Path -LiteralPath $cachedPackage -PathType Leaf)) {
    throw "Locked WiX package was not found in the configured NuGet cache: $cachedPackage"
}
$cachedHash = (Get-FileHash -LiteralPath $cachedPackage -Algorithm SHA256).Hash
if (-not [string]::Equals($cachedHash, $lock.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Cached WiX package hash mismatch. Expected $($lock.sha256), found $cachedHash."
}

$buildArguments = @(
    'build', $installerProject,
    '-c', $Configuration,
    '--no-restore',
    '--nologo',
    '-p:Platform=x64',
    "-p:OutputPath=$buildOutput\",
    "-p:IntermediateOutputPath=$intermediateOutput\",
    "-p:GeneratedWixSource=$generatedSource",
    "-p:ProductCode=$($productCode.ToString('B').ToUpperInvariant())",
    "-p:UpgradeCode=$($upgradeCode.ToString('B').ToUpperInvariant())",
    "-p:MsiVersion=$Version",
    "-p:PackageVersion=$($versionInfo.PackageVersion)",
    "-p:RepositoryUrl=$($versionInfo.RepositoryUrl)",
    "-p:PayloadHash=$payloadHash",
    "-p:IconPath=$iconPath",
    "-p:PortableRoot=$PortableRoot"
)

try {
    Invoke-DotNet -Arguments $buildArguments
    $builtMsi = Get-MsiOutput -Root $buildOutput
    Copy-Item -LiteralPath $builtMsi -Destination $outputPath -Force
    $hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($checksumPath, "$hash *AutoEnvPlus-win-x64.msi`r`n", [System.Text.Encoding]::ASCII)
    Write-Host "MSI: $outputPath"
    Write-Host "MSI SHA256: $hash"
    Write-Host "ProductCode: $($productCode.ToString('B').ToUpperInvariant())"
    Write-Host "UpgradeCode: $($upgradeCode.ToString('B').ToUpperInvariant())"
    Write-Host "Payload SHA256SUMS hash: $payloadHash"
    $signatureSummary = (($signatureState.GetEnumerator() |
        Sort-Object Key |
        ForEach-Object { '{0}={1}' -f $_.Key, $_.Value }) -join ', ')
    Write-Host "Payload signatures: $signatureSummary"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-ArtifactPath -Path $stagingRoot
    }
}
