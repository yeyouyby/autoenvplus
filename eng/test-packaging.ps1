[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $repositoryRoot 'artifacts\.staging\packaging-tests'
$modulePath = Join-Path $PSScriptRoot 'AutoEnvPlus.Packaging.psm1'
$manifestTemplate = Join-Path $repositoryRoot 'packaging\AppxManifest.xml'
$appInstallerTemplate = Join-Path $repositoryRoot 'packaging\AutoEnvPlus.appinstaller'
$appInstallerSchema = Join-Path $repositoryRoot 'packaging\AutoEnvPlus.AppInstallerProfile.xsd'
$mainWindowPath = Join-Path $repositoryRoot 'src\AutoEnvPlus.App\MainWindow.xaml'
$applicationManifestPath = Join-Path $repositoryRoot 'src\AutoEnvPlus.App\app.manifest'
$script:AssertionCount = 0

Import-Module $modulePath -Force
$versionInfo = Get-AutoEnvPlusVersionInfo
$originalPfxPassword = $env:AUTOENVPLUS_PFX_PASSWORD
$originalPublisher = $env:AUTOENVPLUS_PUBLISHER
$originalTimestampUri = $env:AUTOENVPLUS_TIMESTAMP_URI

function Assert-Equal {
    param(
        [Parameter(Mandatory)][AllowEmptyString()]$Actual,
        [Parameter(Mandatory)][AllowEmptyString()]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Actual -ne $Expected) {
        throw "$Label failed. Expected '$Expected', found '$Actual'."
    }
    $script:AssertionCount++
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessageFragment
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$MessageFragment*") {
            throw "Expected error containing '$MessageFragment', found '$($_.Exception.Message)'."
        }
        $script:AssertionCount++
        return
    }
    throw "Expected action to fail with '$MessageFragment'."
}

function New-TamperedAppInstaller {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$OldValue,
        [Parameter(Mandatory)][string]$NewValue
    )

    $content = [System.IO.File]::ReadAllText($appInstallerPath)
    if (-not $content.Contains($OldValue)) {
        throw "Tamper fixture '$Name' could not find its source value."
    }
    $tamperedPath = Join-Path $testRoot "$Name.appinstaller"
    [System.IO.File]::WriteAllText(
        $tamperedPath,
        $content.Replace($OldValue, $NewValue),
        (New-Object System.Text.UTF8Encoding($false)))
    return $tamperedPath
}

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

try {
    Assert-Equal $versionInfo.ProductVersion '0.0.1' 'Authoritative product version'
    Assert-Equal $versionInfo.PackageVersion '0.0.1.0' 'Authoritative MSIX version'
    Assert-Equal $versionInfo.ReleaseTag 'v0.0.1' 'Authoritative release tag'
    Assert-Equal $versionInfo.ReleaseStage 'preview' 'Authoritative release stage'
    Assert-Equal $versionInfo.PackageUri `
        'https://github.com/yeyouyby/autoenvplus/releases/download/v0.0.1/AutoEnvPlus-win-x64.msix' `
        'Authoritative package URI'
    Assert-Equal $versionInfo.AppInstallerUri `
        'https://github.com/yeyouyby/autoenvplus/releases/latest/download/AutoEnvPlus.appinstaller' `
        'Authoritative AppInstaller URI'

    $mainWindowVersionMatches = [regex]::Matches(
        [System.IO.File]::ReadAllText($mainWindowPath),
        '(?<![A-Za-z0-9])v[0-9]+\.[0-9]+\.[0-9]+(?![A-Za-z0-9.])')
    Assert-Equal $mainWindowVersionMatches.Count 0 'Main window has no hard-coded display version'
    $mainWindowCode = [System.IO.File]::ReadAllText(
        (Join-Path $repositoryRoot 'src\AutoEnvPlus.App\MainWindow.xaml.cs'))
    Assert-Equal $mainWindowCode.Contains('ProductIdentityPresentationPolicy.FromAssembly') `
        $true `
        'Main window uses assembly product identity'

    $applicationManifestVersionMatches = [regex]::Matches(
        [System.IO.File]::ReadAllText($applicationManifestPath),
        '<assemblyIdentity\s+[^>]*version="([^"]+)"')
    Assert-Equal $applicationManifestVersionMatches.Count 1 'Application manifest version occurrence count'
    Assert-Equal $applicationManifestVersionMatches[0].Groups[1].Value `
        $versionInfo.PackageVersion `
        'Application manifest version'

    $templateManifestIdentity = Get-AutoEnvPlusMsixIdentity -ManifestPath $manifestTemplate
    Assert-Equal $templateManifestIdentity.Version $versionInfo.PackageVersion 'Template manifest version'
    $templateAppInstallerIdentity = Get-AutoEnvPlusAppInstallerIdentity `
        -Path $appInstallerTemplate `
        -SchemaPath $appInstallerSchema
    Assert-Equal $templateAppInstallerIdentity.Version $versionInfo.PackageVersion 'Template AppInstaller package version'
    Assert-Equal $templateAppInstallerIdentity.AppInstallerVersion $versionInfo.PackageVersion 'Template AppInstaller metadata version'
    Assert-Equal $templateAppInstallerIdentity.PackageUri $versionInfo.PackageUri 'Template package URI'
    Assert-Equal $templateAppInstallerIdentity.AppInstallerUri $versionInfo.AppInstallerUri 'Template AppInstaller URI'

    Assert-Equal (ConvertTo-AutoEnvPlusPackageVersion -Version '1.2.3.4') '1.2.3.4' 'Four-part package version'
    Assert-Throws { ConvertTo-AutoEnvPlusPackageVersion -Version '1.2.3' } 'exactly four numeric components'
    Assert-Throws { ConvertTo-AutoEnvPlusPackageVersion -Version '1.2.3.65536' } 'between 0 and 65535'
    Assert-Throws { ConvertTo-AutoEnvPlusPackageVersion -Version '01.2.3.4' } 'exactly four numeric components'

    Assert-AutoEnvPlusPackageName -Name 'yeyouyby.AutoEnvPlus'
    Assert-Throws { Assert-AutoEnvPlusPackageName -Name '..\escape' } 'MSIX package name'
    Assert-Throws { Assert-AutoEnvPlusPackageName -Name 'ab' } 'MSIX package name'

    $packageUriParameters = @{
        Value = 'https://github.com/yeyouyby/autoenvplus/releases/download/v1.2.3/AutoEnvPlus-win-x64.msix'
        ParameterName = 'PackageUri'
        RequiredExtension = '.msix'
    }
    $packageUri = ConvertTo-AutoEnvPlusHttpsUri @packageUriParameters
    Assert-Equal $packageUri 'https://github.com/yeyouyby/autoenvplus/releases/download/v1.2.3/AutoEnvPlus-win-x64.msix' 'Package URI'
    Assert-Throws {
        ConvertTo-AutoEnvPlusHttpsUri -Value 'http://example.test/app.msix' -ParameterName 'PackageUri' -RequiredExtension '.msix'
    } 'absolute HTTPS URI'
    Assert-Throws {
        ConvertTo-AutoEnvPlusHttpsUri -Value 'https://example.test/app.zip' -ParameterName 'PackageUri' -RequiredExtension '.msix'
    } 'path must end with .msix'
    Assert-Throws {
        ConvertTo-AutoEnvPlusHttpsUri -Value 'https://example.test/app.msix#fragment' -ParameterName 'PackageUri' -RequiredExtension '.msix'
    } 'without credentials or a fragment'

    $manifestPath = Join-Path $testRoot 'AppxManifest.xml'
    $publisher = 'CN=AutoEnvPlus & Packaging Tests'
    $manifestParameters = @{
        TemplatePath = $manifestTemplate
        OutputPath = $manifestPath
        PackageName = 'yeyouyby.AutoEnvPlus.Tests'
        Publisher = $publisher
        PublisherDisplayName = 'AutoEnvPlus & Contributors'
        PackageVersion = '1.2.3.4'
    }
    New-AutoEnvPlusAppxManifest @manifestParameters
    $manifestIdentity = Get-AutoEnvPlusMsixIdentity -ManifestPath $manifestPath
    Assert-Equal $manifestIdentity.Name 'yeyouyby.AutoEnvPlus.Tests' 'Manifest package name'
    Assert-Equal $manifestIdentity.Publisher $publisher 'Manifest publisher XML escaping'
    Assert-Equal $manifestIdentity.Version '1.2.3.4' 'Manifest version'
    Assert-Equal $manifestIdentity.ProcessorArchitecture 'x64' 'Manifest architecture'

    $appInstallerPath = Join-Path $testRoot 'AutoEnvPlus.appinstaller'
    $appInstallerParameters = @{
        TemplatePath = $appInstallerTemplate
        OutputPath = $appInstallerPath
        PackageName = 'yeyouyby.AutoEnvPlus.Tests'
        Publisher = $publisher
        PackageVersion = '1.2.3.4'
        PackageUri = $packageUri
        AppInstallerUri = 'https://github.com/yeyouyby/autoenvplus/releases/latest/download/AutoEnvPlus.appinstaller'
        SchemaPath = $appInstallerSchema
    }
    New-AutoEnvPlusAppInstaller @appInstallerParameters
    $appInstallerIdentity = Get-AutoEnvPlusAppInstallerIdentity -Path $appInstallerPath
    Assert-Equal $appInstallerIdentity.Name $manifestIdentity.Name 'AppInstaller package name binding'
    Assert-Equal $appInstallerIdentity.Publisher $manifestIdentity.Publisher 'AppInstaller publisher binding'
    Assert-Equal $appInstallerIdentity.Version $manifestIdentity.Version 'AppInstaller version binding'
    Assert-Equal $appInstallerIdentity.ProcessorArchitecture $manifestIdentity.ProcessorArchitecture 'AppInstaller architecture binding'
    Assert-Equal $appInstallerIdentity.PackageUri $packageUri 'AppInstaller package URI binding'

    $strictAppInstallerParameters = @{
        SchemaPath = $appInstallerSchema
        ExpectedPackageName = 'yeyouyby.AutoEnvPlus.Tests'
        ExpectedPublisher = $publisher
        ExpectedPackageVersion = '1.2.3.4'
        ExpectedPackageUri = $packageUri
        ExpectedAppInstallerUri = 'https://github.com/yeyouyby/autoenvplus/releases/latest/download/AutoEnvPlus.appinstaller'
    }
    $strictIdentity = Assert-AutoEnvPlusAppInstaller -Path $appInstallerPath @strictAppInstallerParameters
    Assert-Equal $strictIdentity.Name 'yeyouyby.AutoEnvPlus.Tests' 'Strict AppInstaller profile validation'

    $downgradePath = New-TamperedAppInstaller -Name 'force-downgrade' `
        -OldValue '<ForceUpdateFromAnyVersion>false</ForceUpdateFromAnyVersion>' `
        -NewValue '<ForceUpdateFromAnyVersion>true</ForceUpdateFromAnyVersion>'
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $downgradePath @strictAppInstallerParameters
    } 'schema validation failed'

    $nonCanonicalBooleanPath = New-TamperedAppInstaller -Name 'noncanonical-boolean' `
        -OldValue '<ForceUpdateFromAnyVersion>false</ForceUpdateFromAnyVersion>' `
        -NewValue '<ForceUpdateFromAnyVersion>0</ForceUpdateFromAnyVersion>'
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $nonCanonicalBooleanPath @strictAppInstallerParameters
    } 'schema validation failed'

    $unknownElementPath = New-TamperedAppInstaller -Name 'unknown-element' `
        -OldValue '<AutomaticBackgroundTask />' `
        -NewValue '<AutomaticBackgroundTask /><Unexpected />'
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $unknownElementPath @strictAppInstallerParameters
    } 'schema validation failed'

    $duplicateMainPackage = '<MainPackage Name="yeyouyby.AutoEnvPlus.Tests" ' +
        'Publisher="CN=AutoEnvPlus &amp; Packaging Tests" Version="1.2.3.4" ' +
        'ProcessorArchitecture="x64" Uri="' + $packageUri + '" />'
    $duplicateMainPackagePath = New-TamperedAppInstaller -Name 'duplicate-main-package' `
        -OldValue '<UpdateSettings>' `
        -NewValue "$duplicateMainPackage`r`n  <UpdateSettings>"
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $duplicateMainPackagePath @strictAppInstallerParameters
    } 'schema validation failed'

    $unknownAttributePath = New-TamperedAppInstaller -Name 'unknown-attribute' `
        -OldValue 'ProcessorArchitecture="x64"' `
        -NewValue 'ProcessorArchitecture="x64" Extra="value"'
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $unknownAttributePath @strictAppInstallerParameters
    } 'schema validation failed'

    $namespacePath = New-TamperedAppInstaller -Name 'namespace-downgrade' `
        -OldValue 'http://schemas.microsoft.com/appx/appinstaller/2018' `
        -NewValue 'http://schemas.microsoft.com/appx/appinstaller/2017'
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $namespacePath @strictAppInstallerParameters
    } 'schema validation failed'

    $packageUriTamperPath = New-TamperedAppInstaller -Name 'package-uri' `
        -OldValue $packageUri `
        -NewValue 'https://example.test/AutoEnvPlus-win-x64.msix'
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $packageUriTamperPath @strictAppInstallerParameters
    } 'AppInstaller package URI mismatch'

    $doctypePath = New-TamperedAppInstaller -Name 'doctype' `
        -OldValue '?>' `
        -NewValue "?>`r`n<!DOCTYPE AppInstaller [<!ENTITY publisher SYSTEM 'file:///C:/Windows/win.ini'>]>"
    Assert-Throws {
        Assert-AutoEnvPlusAppInstaller -Path $doctypePath @strictAppInstallerParameters
    } 'could not be parsed safely'

    Add-Type -AssemblyName System.Drawing
    $authoritativeBrandPng = Join-Path $repositoryRoot 'assets\branding\autoenvplus-logo.png'
    $authoritativeIcon = Join-Path $repositoryRoot 'assets\branding\autoenvplus.ico'
    $brandImage = [System.Drawing.Image]::FromFile($authoritativeBrandPng)
    try {
        Assert-Equal $brandImage.Width 512 'Authoritative brand PNG width'
        Assert-Equal $brandImage.Height 512 'Authoritative brand PNG height'
        Assert-Equal $brandImage.RawFormat.Guid ([System.Drawing.Imaging.ImageFormat]::Png.Guid) `
            'Authoritative brand image format'
    }
    finally {
        $brandImage.Dispose()
    }

    $expectedIconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $iconStream = New-Object System.IO.FileStream(
        $authoritativeIcon,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    $iconReader = New-Object System.IO.BinaryReader($iconStream)
    try {
        Assert-Equal $iconReader.ReadUInt16() 0 'ICO reserved header'
        Assert-Equal $iconReader.ReadUInt16() 1 'ICO resource type'
        Assert-Equal $iconReader.ReadUInt16() $expectedIconSizes.Count 'ICO frame count'
        $iconEntries = @()
        foreach ($expectedSize in $expectedIconSizes) {
            $encodedWidth = $iconReader.ReadByte()
            $encodedHeight = $iconReader.ReadByte()
            $actualWidth = if ($encodedWidth -eq 0) { 256 } else { [int]$encodedWidth }
            $actualHeight = if ($encodedHeight -eq 0) { 256 } else { [int]$encodedHeight }
            Assert-Equal $actualWidth $expectedSize "ICO $expectedSize frame width"
            Assert-Equal $actualHeight $expectedSize "ICO $expectedSize frame height"
            Assert-Equal $iconReader.ReadByte() 0 "ICO $expectedSize palette size"
            Assert-Equal $iconReader.ReadByte() 0 "ICO $expectedSize reserved byte"
            Assert-Equal $iconReader.ReadUInt16() 1 "ICO $expectedSize color planes"
            Assert-Equal $iconReader.ReadUInt16() 32 "ICO $expectedSize bit depth"
            $iconEntries += [pscustomobject]@{
                Size = $expectedSize
                ByteCount = $iconReader.ReadUInt32()
                Offset = $iconReader.ReadUInt32()
            }
        }

        foreach ($entry in $iconEntries) {
            if (($entry.Offset + $entry.ByteCount) -gt $iconStream.Length) {
                throw "ICO $($entry.Size) frame extends beyond the icon file."
            }
            $script:AssertionCount++
            $iconStream.Position = $entry.Offset
            Assert-Equal $iconReader.ReadUInt32() 40 "ICO $($entry.Size) DIB header size"
            Assert-Equal $iconReader.ReadInt32() $entry.Size "ICO $($entry.Size) DIB width"
            Assert-Equal $iconReader.ReadInt32() ($entry.Size * 2) "ICO $($entry.Size) DIB doubled height"
        }
    }
    finally {
        $iconReader.Dispose()
        $iconStream.Dispose()
    }

    $assetsPath = Join-Path $testRoot 'Assets'
    New-AutoEnvPlusBrandAssets -OutputDirectory $assetsPath
    $expectedDimensions = [ordered]@{
        'StoreLogo.png' = @(50, 50)
        'Square44x44Logo.png' = @(44, 44)
        'Square150x150Logo.png' = @(150, 150)
        'Wide310x150Logo.png' = @(310, 150)
        'Square310x310Logo.png' = @(310, 310)
        'SplashScreen.png' = @(620, 300)
    }
    foreach ($assetName in $expectedDimensions.Keys) {
        $assetPath = Join-Path $assetsPath $assetName
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "Brand asset was not generated: $assetName"
        }
        $image = [System.Drawing.Image]::FromFile($assetPath)
        try {
            Assert-Equal $image.Width $expectedDimensions[$assetName][0] "$assetName width"
            Assert-Equal $image.Height $expectedDimensions[$assetName][1] "$assetName height"
        }
        finally {
            $image.Dispose()
        }
    }

    $invalidBrandPath = Join-Path $testRoot 'invalid-brand-source.png'
    $invalidBrand = New-Object System.Drawing.Bitmap(16, 16)
    try {
        $invalidBrand.Save($invalidBrandPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $invalidBrand.Dispose()
    }
    Assert-Throws {
        New-AutoEnvPlusBrandAssets `
            -OutputDirectory (Join-Path $testRoot 'invalid-brand-assets') `
            -SourcePath $invalidBrandPath
    } 'must be exactly 512 by 512 pixels'

    $portableCleanupRoot = Join-Path $testRoot 'portable-cleanup'
    New-Item -ItemType Directory -Path $portableCleanupRoot -Force | Out-Null
    $portableArchive = Join-Path $portableCleanupRoot 'AutoEnvPlus-win-x64-portable.zip'
    $portableSidecar = "$portableArchive.sha256"
    [System.IO.File]::WriteAllText($portableArchive, 'archive')
    [System.IO.File]::WriteAllText($portableSidecar, 'stale checksum')
    Clear-AutoEnvPlusPortableArchiveArtifacts -ArtifactsRoot $portableCleanupRoot
    Assert-Equal (Test-Path -LiteralPath $portableArchive) $false 'Portable archive cleanup removes ZIP'
    Assert-Equal (Test-Path -LiteralPath $portableSidecar) $false 'Portable archive cleanup removes checksum sidecar'
    [System.IO.File]::WriteAllText($portableSidecar, 'orphaned checksum')
    Clear-AutoEnvPlusPortableArchiveArtifacts -ArtifactsRoot $portableCleanupRoot
    Assert-Equal (Test-Path -LiteralPath $portableSidecar) $false 'Portable archive cleanup removes orphaned checksum sidecar'
    Assert-Throws {
        Clear-AutoEnvPlusPortableArchiveArtifacts `
            -ArtifactsRoot $portableCleanupRoot `
            -ArchiveName '..\outside.zip'
    } 'leaf .zip file name'

    foreach ($scriptName in @('publish-single-file.ps1', 'publish-msi.ps1')) {
        $tokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot $scriptName),
            [ref]$tokens,
            [ref]$parseErrors) | Out-Null
        Assert-Equal $parseErrors.Count 0 "$scriptName PowerShell parse errors"
    }

    $singleFileScript = [System.IO.File]::ReadAllText(
        (Join-Path $PSScriptRoot 'publish-single-file.ps1'))
    foreach ($requiredText in @(
        'AutoEnvPlus-win-x64.exe',
        '-p:PublishSingleFile=true',
        '-p:WindowsAppSDKSelfContained=true',
        '-p:IncludeAllContentForSelfExtract=true',
        'AutoEnvPlusBundledCliPath',
        'AutoEnvPlusBundledShimPath',
        'AutoEnvPlusSingleFileAssetName',
        'AutoEnvPlus-win-x64.pri',
        'cli/autoenvplus.exe',
        'cli/autoenvplus-shim.exe'
    )) {
        Assert-Equal $singleFileScript.Contains($requiredText) $true `
            "Single-file contract contains $requiredText"
    }

    $portableScript = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'publish.ps1'))
    Assert-Equal $portableScript.Contains('AutoEnvPlus-win-x64-portable.zip') $true `
        'Portable publish uses the authoritative archive name'

    $wixLock = Get-Content `
        -LiteralPath (Join-Path $repositoryRoot 'packaging\WixToolset.lock.json') `
        -Raw `
        -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal $wixLock.packageId 'WixToolset.Sdk' 'Pinned WiX package ID'
    Assert-Equal $wixLock.version '4.0.6' 'Pinned WiX version'
    Assert-Equal $wixLock.sha256 `
        '029C37C6490A810F61BCD375DD661ACE04C328640A9DADAEF1E7149BC14FF0F6' `
        'Pinned WiX package SHA-256'
    Assert-Equal $wixLock.license 'Microsoft Reciprocal License (MS-RL)' 'Pinned WiX license'

    $wixProject = [System.IO.File]::ReadAllText(
        (Join-Path $repositoryRoot 'packaging\AutoEnvPlus.Installer\AutoEnvPlus.Installer.wixproj'))
    Assert-Equal $wixProject.Contains('WixToolset.Sdk/4.0.6') $true 'WiX project version'
    Assert-Equal $wixProject.Contains('ICE03;ICE91') $true 'Documented WiX ICE suppressions'

    $wixSource = [System.IO.File]::ReadAllText(
        (Join-Path $repositoryRoot 'packaging\AutoEnvPlus.Installer\AutoEnvPlus.Installer.wxs'))
    foreach ($requiredText in @(
        'Scope="perUser"',
        '<MajorUpgrade',
        'PayloadDirectoryCleanupComponents',
        'InstallFolderCleanupComponent',
        'RemoveInstallFolder',
        'StartMenuShortcutComponent'
    )) {
        Assert-Equal $wixSource.Contains($requiredText) $true "MSI source contains $requiredText"
    }

    $msiScript = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'publish-msi.ps1'))
    foreach ($requiredText in @(
        'AutoEnvPlus-win-x64.msi',
        'RequireSignedPayload',
        'PayloadDirectoryCleanupComponents',
        'WixToolset.Sdk',
        '4.0.6'
    )) {
        Assert-Equal $msiScript.Contains($requiredText) $true "MSI publish contract contains $requiredText"
    }

    $releaseWorkflow = [System.IO.File]::ReadAllText(
        (Join-Path $repositoryRoot '.github\workflows\release.yml'))
    Assert-Equal $releaseWorkflow.Contains(
        'SignPath/github-action-submit-signing-request@b9d91eadd323de506c0c81cf0c7fe7438f3360fd') `
        $true `
        'Release workflow pins SignPath action'
    Assert-Equal $releaseWorkflow.Contains('SIGNPATH_EXPECTED_SIGNER_THUMBPRINT') $true `
        'Release workflow pins the expected signer certificate'
    Assert-Equal $releaseWorkflow.Contains('-RequireSignedPayload') $true `
        'MSI release requires signed embedded payload'

    $workflowFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github\workflows') -Filter '*.yml' |
            Sort-Object Name)
    Assert-Equal $workflowFiles.Count 4 'Repository ships exactly four workflow files'
    foreach ($workflowFile in $workflowFiles) {
        $workflowContent = [System.IO.File]::ReadAllText($workflowFile.FullName)
        $usesLines = @(
            [regex]::Matches($workflowContent, '(?m)^\s*uses:\s*(?<ref>\S+)\s*(?:#\s*(?<version>\S+))?\s*$'))
        Assert-Equal ($usesLines.Count -gt 0) $true `
            "$($workflowFile.Name) references at least one action"
        foreach ($usesLine in $usesLines) {
            $ref = $usesLine.Groups['ref'].Value
            Assert-Equal ($ref -match '^[^@/]+/[^@]+@[0-9a-f]{40}$') $true `
                "$($workflowFile.Name) pins action '$ref' to a full commit SHA"
            Assert-Equal (-not [string]::IsNullOrWhiteSpace($usesLine.Groups['version'].Value)) $true `
                "$($workflowFile.Name) documents the version for action '$ref'"
        }
    }

    $dependabotConfig = [System.IO.File]::ReadAllText(
        (Join-Path $repositoryRoot '.github\dependabot.yml'))
    foreach ($requiredEcosystem in @('github-actions', 'nuget')) {
        Assert-Equal $dependabotConfig.Contains("package-ecosystem: $requiredEcosystem") $true `
            "Dependabot covers the $requiredEcosystem ecosystem"
    }

    $env:AUTOENVPLUS_PFX_PASSWORD = 'packaging-test-password'
    $env:AUTOENVPLUS_PUBLISHER = 'CN=AutoEnvPlus Packaging Test'
    $env:AUTOENVPLUS_TIMESTAMP_URI = 'https://timestamp.example.test/rfc3161'
    Assert-Throws {
        & (Join-Path $PSScriptRoot 'publish-msix.ps1')
    } 'Production MSIX publication requires -CertificatePath'
    Assert-Equal ([string]::IsNullOrEmpty($env:AUTOENVPLUS_PFX_PASSWORD)) $true `
        'MSIX script clears password environment'
    Assert-Equal ([string]::IsNullOrEmpty($env:AUTOENVPLUS_PUBLISHER)) $true `
        'MSIX script clears publisher environment'
    Assert-Equal ([string]::IsNullOrEmpty($env:AUTOENVPLUS_TIMESTAMP_URI)) $true `
        'MSIX script clears timestamp environment'
    Assert-Throws {
        & (Join-Path $PSScriptRoot 'publish-msix.ps1') -PackageVersion '99.0.0.0'
    } 'PackageVersion must match Directory.Build.props'

    Write-Host "Packaging tests passed: $script:AssertionCount assertions."
}
finally {
    $env:AUTOENVPLUS_PFX_PASSWORD = $originalPfxPassword
    $env:AUTOENVPLUS_PUBLISHER = $originalPublisher
    $env:AUTOENVPLUS_TIMESTAMP_URI = $originalTimestampUri
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
