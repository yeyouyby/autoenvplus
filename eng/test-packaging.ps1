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
$script:AssertionCount = 0

Import-Module $modulePath -Force

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

    $assetsPath = Join-Path $testRoot 'Assets'
    New-AutoEnvPlusBrandAssets -OutputDirectory $assetsPath
    Add-Type -AssemblyName System.Drawing
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

    Assert-Throws {
        $publishParameters = @{
            PackageVersion = '99.0.0.0'
            PackageUri = 'https://example.test/AutoEnvPlus-win-x64.msix'
            AppInstallerUri = 'https://example.test/AutoEnvPlus.appinstaller'
        }
        & (Join-Path $PSScriptRoot 'publish-msix.ps1') @publishParameters
    } 'Production MSIX publication requires -CertificatePath'

    Write-Host "Packaging tests passed: $script:AssertionCount assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
