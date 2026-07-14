[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [Parameter(Mandatory)]
    [string]$PackageVersion,
    [string]$PackageName = 'yeyouyby.AutoEnvPlus',
    [string]$Publisher,
    [string]$PublisherDisplayName = 'yeyouyby',
    [Parameter(Mandatory)]
    [string]$PackageUri,
    [Parameter(Mandatory)]
    [string]$AppInstallerUri,
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [switch]$DevelopmentCertificate,
    [string]$TimestampUri,
    [string]$MakeAppxPath,
    [string]$SignToolPath,
    [string]$OpenSslPath,
    [string]$BuildCacheRoot,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$outputRoot = Join-Path $artifactsRoot (Join-Path 'msix' $PackageVersion)
$stagingRoot = Join-Path $artifactsRoot '.staging\AutoEnvPlus-msix'
$packageStagingRoot = Join-Path $stagingRoot 'package'
$verificationRoot = Join-Path $stagingRoot 'verify'
$certificateStagingRoot = Join-Path $stagingRoot 'certificate'
$portableRoot = Join-Path $artifactsRoot 'AutoEnvPlus-win-x64'
$modulePath = Join-Path $PSScriptRoot 'AutoEnvPlus.Packaging.psm1'
$manifestTemplate = Join-Path $repositoryRoot 'packaging\AppxManifest.xml'
$appInstallerTemplate = Join-Path $repositoryRoot 'packaging\AutoEnvPlus.appinstaller'
$msixPath = Join-Path $outputRoot 'AutoEnvPlus-win-x64.msix'
$appInstallerPath = Join-Path $outputRoot 'AutoEnvPlus.appinstaller'
$developmentCertificatePath = Join-Path $outputRoot 'AutoEnvPlus-development.cer'
$releaseMetadataPath = Join-Path $outputRoot 'AutoEnvPlus-release.json'

Import-Module $modulePath -Force

function Assert-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $fullArtifactsRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Packaging output must remain inside $fullArtifactsRoot."
    }
}

function Remove-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    Assert-ArtifactPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-ExternalTool {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$QuietOnSuccess
    )

    if ($QuietOnSuccess) {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            $output | ForEach-Object { Write-Host $_ }
            throw "$(Split-Path $FilePath -Leaf) exited with code $exitCode."
        }
        $output | Select-Object -Last 4 | ForEach-Object { Write-Host $_ }
    }
    else {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$(Split-Path $FilePath -Leaf) exited with code $LASTEXITCODE."
        }
    }
}

function ConvertFrom-SecurePassword {
    param([Parameter(Mandatory)][Security.SecureString]$Value)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function New-RandomPassword {
    $bytes = New-Object byte[] 36
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
    }
    finally {
        $random.Dispose()
    }
    return [Convert]::ToBase64String($bytes)
}

function Resolve-OpenSsl {
    if (-not [string]::IsNullOrWhiteSpace($OpenSslPath)) {
        return [System.IO.Path]::GetFullPath($OpenSslPath)
    }

    $command = Get-Command openssl.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        'C:\Program Files\Git\mingw64\bin\openssl.exe',
        'C:\Program Files\Git\usr\bin\openssl.exe'
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    throw 'OpenSSL is required for -DevelopmentCertificate. Pass -OpenSslPath explicitly.'
}

function New-DevelopmentSigningCertificate {
    param(
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$PfxPath
    )

    $openSsl = Resolve-OpenSsl
    if (-not (Test-Path -LiteralPath $openSsl -PathType Leaf)) {
        throw "OpenSSL was not found: $openSsl"
    }

    New-Item -ItemType Directory -Path $certificateStagingRoot -Force | Out-Null
    $keyPath = Join-Path $certificateStagingRoot 'development.key.pem'
    $certificatePemPath = Join-Path $certificateStagingRoot 'development.cert.pem'
    Invoke-ExternalTool -FilePath $openSsl -Arguments @(
        'req', '-quiet', '-x509', '-newkey', 'rsa:3072', '-sha256', '-nodes', '-days', '30',
        '-subj', '/CN=AutoEnvPlus Development',
        '-addext', 'basicConstraints=critical,CA:FALSE',
        '-addext', 'keyUsage=critical,digitalSignature',
        '-addext', 'extendedKeyUsage=codeSigning',
        '-keyout', $keyPath,
        '-out', $certificatePemPath
    )
    Invoke-ExternalTool -FilePath $openSsl -Arguments @(
        'pkcs12', '-export',
        '-name', 'AutoEnvPlus Development',
        '-inkey', $keyPath,
        '-in', $certificatePemPath,
        '-out', $PfxPath,
        '-passout', "pass:$Password"
    )
}

function Invoke-SignTool {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$PfxPath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Password,
        [string]$Rfc3161TimestampUri
    )

    if ($Password.Contains("`r") -or $Password.Contains("`n")) {
        throw 'Certificate password cannot contain line breaks.'
    }

    $responsePath = Join-Path $certificateStagingRoot ([System.IO.Path]::GetRandomFileName() + '.rsp')
    $arguments = @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $Password)
    if (-not [string]::IsNullOrWhiteSpace($Rfc3161TimestampUri)) {
        $arguments += @('/tr', $Rfc3161TimestampUri, '/td', 'SHA256')
    }
    $arguments += $Path

    [System.IO.File]::WriteAllLines(
        $responsePath,
        $arguments,
        (New-Object System.Text.UTF8Encoding($false)))
    try {
        Invoke-ExternalTool -FilePath $SignToolPath -Arguments @("@$responsePath")
    }
    finally {
        if (Test-Path -LiteralPath $responsePath) {
            Remove-Item -LiteralPath $responsePath -Force
        }
    }
}

function Assert-IdentityValue {
    param(
        [Parameter(Mandatory)][string]$Label,
        [AllowEmptyString()][string]$Actual,
        [AllowEmptyString()][string]$Expected
    )

    if (-not [string]::Equals($Actual, $Expected, [System.StringComparison]::Ordinal)) {
        throw "$Label mismatch. Expected '$Expected', found '$Actual'."
    }
}

function Assert-AuthenticodeSigner {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedThumbprint,
        [switch]$AllowUntrustedRoot
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate) {
        throw "No Authenticode signer was found for $Path. Status: $($signature.Status)"
    }
    if (-not [string]::Equals(
        $signature.SignerCertificate.Thumbprint,
        $ExpectedThumbprint,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Authenticode signer does not match the selected certificate: $Path"
    }
    if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::HashMismatch -or
        $signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Authenticode verification failed for $Path. Status: $($signature.Status)"
    }
    if (-not $AllowUntrustedRoot -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode trust verification failed for $Path. Status: $($signature.Status); $($signature.StatusMessage)"
    }
}

function Write-HashSidecar {
    param([Parameter(Mandatory)][string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        ($Path + '.sha256'),
        "$hash *$(Split-Path $Path -Leaf)`r`n",
        [System.Text.Encoding]::ASCII)
    return $hash
}

function Set-BuildCacheEnvironment {
    if ([string]::IsNullOrWhiteSpace($BuildCacheRoot)) {
        if (-not [string]::IsNullOrWhiteSpace($env:AUTOENVPLUS_BUILD_CACHE_ROOT)) {
            $script:BuildCacheRoot = $env:AUTOENVPLUS_BUILD_CACHE_ROOT
        }
        elseif ([System.IO.Path]::GetPathRoot($repositoryRoot) -eq 'D:\') {
            $script:BuildCacheRoot = 'D:\codex'
        }
        else {
            $script:BuildCacheRoot = Join-Path $repositoryRoot '.build-cache'
        }
    }

    $script:BuildCacheRoot = [System.IO.Path]::GetFullPath($script:BuildCacheRoot)
    $env:NUGET_PACKAGES = Join-Path $script:BuildCacheRoot '.nuget\packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $script:BuildCacheRoot '.nuget\v3-cache'
    $env:DOTNET_CLI_HOME = Join-Path $script:BuildCacheRoot '.dotnet'
    $env:TEMP = Join-Path $script:BuildCacheRoot 'tmp'
    $env:TMP = $env:TEMP
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    foreach ($path in @($env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:DOTNET_CLI_HOME, $env:TEMP)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

$PackageVersion = ConvertTo-AutoEnvPlusPackageVersion -Version $PackageVersion
Assert-AutoEnvPlusPackageName -Name $PackageName
$PackageUri = ConvertTo-AutoEnvPlusHttpsUri -Value $PackageUri -ParameterName 'PackageUri' -RequiredExtension '.msix'
$AppInstallerUri = ConvertTo-AutoEnvPlusHttpsUri -Value $AppInstallerUri -ParameterName 'AppInstallerUri' -RequiredExtension '.appinstaller'
if (-not [string]::IsNullOrWhiteSpace($TimestampUri)) {
    $TimestampUri = ConvertTo-AutoEnvPlusTimestampUri -Value $TimestampUri
}
if ($DevelopmentCertificate -and -not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    throw 'DevelopmentCertificate and CertificatePath are mutually exclusive.'
}
if (-not $DevelopmentCertificate -and [string]::IsNullOrWhiteSpace($CertificatePath)) {
    throw 'Production MSIX publication requires -CertificatePath. Use -DevelopmentCertificate only for local validation.'
}
if (-not $DevelopmentCertificate -and [string]::IsNullOrWhiteSpace($Publisher)) {
    throw 'Production MSIX publication requires -Publisher, exactly matching the PFX certificate subject.'
}

Set-BuildCacheEnvironment
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Remove-ArtifactPath -Path $outputRoot
Remove-ArtifactPath -Path $stagingRoot
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $packageStagingRoot -Force | Out-Null
New-Item -ItemType Directory -Path $certificateStagingRoot -Force | Out-Null

$certificate = $null
$plainTextPassword = $null
$effectiveCertificatePath = $null
$developmentPfxPath = Join-Path $certificateStagingRoot 'AutoEnvPlus-development.pfx'
try {
    if ($DevelopmentCertificate) {
        $plainTextPassword = New-RandomPassword
        New-DevelopmentSigningCertificate -Password $plainTextPassword -PfxPath $developmentPfxPath
        $effectiveCertificatePath = $developmentPfxPath
        $certificate = Get-AutoEnvPlusSigningCertificate -Path $effectiveCertificatePath -Password $plainTextPassword
        $Publisher = $certificate.Subject
    }
    else {
        $effectiveCertificatePath = [System.IO.Path]::GetFullPath($CertificatePath)
        if (-not (Test-Path -LiteralPath $effectiveCertificatePath -PathType Leaf)) {
            throw "Signing certificate was not found: $effectiveCertificatePath"
        }
        if ($null -ne $CertificatePassword) {
            $plainTextPassword = ConvertFrom-SecurePassword -Value $CertificatePassword
        }
        elseif ($null -ne $env:AUTOENVPLUS_PFX_PASSWORD) {
            $plainTextPassword = $env:AUTOENVPLUS_PFX_PASSWORD
        }
        else {
            throw 'Provide -CertificatePassword or set AUTOENVPLUS_PFX_PASSWORD for the production PFX.'
        }
        $certificate = Get-AutoEnvPlusSigningCertificate -Path $effectiveCertificatePath -Password $plainTextPassword
    }

    Assert-AutoEnvPlusSigningCertificate -Certificate $certificate -Publisher $Publisher

    if ([string]::IsNullOrWhiteSpace($MakeAppxPath)) {
        $MakeAppxPath = Resolve-AutoEnvPlusWindowsSdkTool -ToolName 'makeappx.exe' -NuGetPackagesPath $env:NUGET_PACKAGES
    }
    if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
        $SignToolPath = Resolve-AutoEnvPlusWindowsSdkTool -ToolName 'signtool.exe' -NuGetPackagesPath $env:NUGET_PACKAGES
    }
    foreach ($toolPath in @($MakeAppxPath, $SignToolPath)) {
        if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
            throw "Required Windows SDK tool was not found: $toolPath"
        }
    }

    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration -Version $PackageVersion -NoArchive
    }
    if (-not (Test-Path -LiteralPath $portableRoot -PathType Container)) {
        throw "Portable publish layout was not found: $portableRoot"
    }

    Copy-Item -Path (Join-Path $portableRoot '*') -Destination $packageStagingRoot -Recurse -Force
    $manifestParameters = @{
        TemplatePath = $manifestTemplate
        OutputPath = Join-Path $packageStagingRoot 'AppxManifest.xml'
        PackageName = $PackageName
        Publisher = $Publisher
        PublisherDisplayName = $PublisherDisplayName
        PackageVersion = $PackageVersion
    }
    New-AutoEnvPlusAppxManifest @manifestParameters
    New-AutoEnvPlusBrandAssets -OutputDirectory (Join-Path $packageStagingRoot 'Assets')

    $checksumPath = Join-Path $packageStagingRoot 'SHA256SUMS.txt'
    $checksumLines = Get-ChildItem -LiteralPath $packageStagingRoot -File -Recurse |
        Where-Object FullName -ne $checksumPath |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($packageStagingRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash *$relativePath"
        }
    [System.IO.File]::WriteAllLines(
        $checksumPath,
        $checksumLines,
        (New-Object System.Text.UTF8Encoding($false)))

    $appInstallerParameters = @{
        TemplatePath = $appInstallerTemplate
        OutputPath = $appInstallerPath
        PackageName = $PackageName
        Publisher = $Publisher
        PackageVersion = $PackageVersion
        PackageUri = $PackageUri
        AppInstallerUri = $AppInstallerUri
    }
    New-AutoEnvPlusAppInstaller @appInstallerParameters

    Invoke-ExternalTool -FilePath $MakeAppxPath -QuietOnSuccess -Arguments @(
        'pack', '/o', '/d', $packageStagingRoot, '/p', $msixPath
    )
    Invoke-SignTool -Path $msixPath -PfxPath $effectiveCertificatePath -Password $plainTextPassword -Rfc3161TimestampUri $TimestampUri

    Test-AutoEnvPlusMsixSignature -Path $msixPath -ExpectedThumbprint $certificate.Thumbprint
    Assert-AuthenticodeSigner -Path $msixPath -ExpectedThumbprint $certificate.Thumbprint -AllowUntrustedRoot:$DevelopmentCertificate
    if (-not $DevelopmentCertificate) {
        Invoke-ExternalTool -FilePath $SignToolPath -Arguments @('verify', '/pa', '/all', '/v', $msixPath)
    }

    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    Invoke-ExternalTool -FilePath $MakeAppxPath -QuietOnSuccess -Arguments @(
        'unpack', '/o', '/p', $msixPath, '/d', $verificationRoot
    )
    $packageIdentity = Get-AutoEnvPlusMsixIdentity -ManifestPath (Join-Path $verificationRoot 'AppxManifest.xml')
    Assert-IdentityValue -Label 'MSIX package name' -Actual $packageIdentity.Name -Expected $PackageName
    Assert-IdentityValue -Label 'MSIX publisher' -Actual $packageIdentity.Publisher -Expected $Publisher
    Assert-IdentityValue -Label 'MSIX version' -Actual $packageIdentity.Version -Expected $PackageVersion
    Assert-IdentityValue -Label 'MSIX architecture' -Actual $packageIdentity.ProcessorArchitecture -Expected 'x64'

    $appInstallerIdentity = Get-AutoEnvPlusAppInstallerIdentity -Path $appInstallerPath
    Assert-IdentityValue -Label 'AppInstaller package name' -Actual $appInstallerIdentity.Name -Expected $PackageName
    Assert-IdentityValue -Label 'AppInstaller publisher' -Actual $appInstallerIdentity.Publisher -Expected $Publisher
    Assert-IdentityValue -Label 'AppInstaller package version' -Actual $appInstallerIdentity.Version -Expected $PackageVersion
    Assert-IdentityValue -Label 'AppInstaller metadata version' -Actual $appInstallerIdentity.AppInstallerVersion -Expected $PackageVersion
    Assert-IdentityValue -Label 'AppInstaller architecture' -Actual $appInstallerIdentity.ProcessorArchitecture -Expected 'x64'
    Assert-IdentityValue -Label 'AppInstaller package URI' -Actual $appInstallerIdentity.PackageUri -Expected $PackageUri
    Assert-IdentityValue -Label 'AppInstaller URI' -Actual $appInstallerIdentity.AppInstallerUri -Expected $AppInstallerUri

    if ($DevelopmentCertificate) {
        [System.IO.File]::WriteAllBytes(
            $developmentCertificatePath,
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }

    $msixHash = Write-HashSidecar -Path $msixPath
    $appInstallerHash = Write-HashSidecar -Path $appInstallerPath
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $certificateSha256 = [BitConverter]::ToString(
            $sha256.ComputeHash($certificate.RawData)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    $metadata = [ordered]@{
        schemaVersion = 1
        packageName = $PackageName
        packageVersion = $PackageVersion
        architecture = 'x64'
        publisher = $Publisher
        publisherDisplayName = $PublisherDisplayName
        packageUri = $PackageUri
        appInstallerUri = $AppInstallerUri
        packageFile = (Split-Path $msixPath -Leaf)
        packageSha256 = $msixHash
        appInstallerFile = (Split-Path $appInstallerPath -Leaf)
        appInstallerSha256 = $appInstallerHash
        appInstallerSignature = 'not-supported-by-windows-app-installer'
        certificateThumbprintSha1 = $certificate.Thumbprint.ToLowerInvariant()
        certificateSha256 = $certificateSha256
        certificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
        developmentCertificate = [bool]$DevelopmentCertificate
        timestampUri = $TimestampUri
        createdAtUtc = [DateTime]::UtcNow.ToString('O')
    }
    [System.IO.File]::WriteAllText(
        $releaseMetadataPath,
        ($metadata | ConvertTo-Json -Depth 4) + "`r`n",
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "Signed MSIX: $msixPath"
    Write-Host "AppInstaller metadata: $appInstallerPath"
    Write-Host "Publisher: $Publisher"
    Write-Host "Certificate SHA-256: $certificateSha256"
    if ($DevelopmentCertificate) {
        Write-Warning 'The package uses an ephemeral development certificate. It is not production-trusted and was not added to any Windows certificate store.'
        Write-Host "Development public certificate: $developmentCertificatePath"
    }
}
finally {
    if ($null -ne $certificate) {
        $certificate.Dispose()
    }
    $plainTextPassword = $null
    Remove-ArtifactPath -Path $stagingRoot
}
