Set-StrictMode -Version Latest

function ConvertTo-AutoEnvPlusPackageVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    if ($Version -notmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
        throw "MSIX version must contain exactly four numeric components: $Version"
    }

    foreach ($component in $Version.Split('.')) {
        if ([int]$component -gt 65535) {
            throw "Each MSIX version component must be between 0 and 65535: $Version"
        }
    }

    return $Version
}

function Assert-AutoEnvPlusPackageName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    if ($Name.Length -lt 3 -or $Name.Length -gt 50 -or
        $Name -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])$') {
        throw 'MSIX package name must be 3-50 characters, use only letters, numbers, periods, and hyphens, and start and end with a letter or number.'
    }
}

function ConvertTo-AutoEnvPlusHttpsUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$ParameterName,
        [string]$RequiredExtension
    )

    $uri = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne [System.Uri]::UriSchemeHttps -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw "$ParameterName must be an absolute HTTPS URI without credentials or a fragment."
    }

    if (-not [string]::IsNullOrWhiteSpace($RequiredExtension) -and
        -not $uri.AbsolutePath.EndsWith($RequiredExtension, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$ParameterName path must end with $RequiredExtension."
    }

    return $uri.AbsoluteUri
}

function ConvertTo-AutoEnvPlusTimestampUri {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Value)

    $uri = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$uri) -or
        ($uri.Scheme -ne [System.Uri]::UriSchemeHttp -and $uri.Scheme -ne [System.Uri]::UriSchemeHttps) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw 'TimestampUri must be an absolute HTTP or HTTPS RFC 3161 endpoint without credentials or a fragment.'
    }

    return $uri.AbsoluteUri
}

function Write-AutoEnvPlusXmlDocument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Xml.XmlDocument]$Document,
        [Parameter(Mandatory)][string]$Path
    )

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace

    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function New-AutoEnvPlusAppxManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TemplatePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$Publisher,
        [Parameter(Mandatory)][string]$PublisherDisplayName,
        [Parameter(Mandatory)][string]$PackageVersion
    )

    Assert-AutoEnvPlusPackageName -Name $PackageName
    $PackageVersion = ConvertTo-AutoEnvPlusPackageVersion -Version $PackageVersion
    if ([string]::IsNullOrWhiteSpace($Publisher)) {
        throw 'Publisher cannot be empty.'
    }
    if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
        throw 'PublisherDisplayName cannot be empty.'
    }

    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $false
    $document.Load($TemplatePath)
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $namespaceManager.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

    $identity = $document.SelectSingleNode('/m:Package/m:Identity', $namespaceManager)
    $publisherNode = $document.SelectSingleNode('/m:Package/m:Properties/m:PublisherDisplayName', $namespaceManager)
    if ($null -eq $identity -or $null -eq $publisherNode) {
        throw 'The AppxManifest template is missing its identity or publisher display name.'
    }

    $identity.SetAttribute('Name', $PackageName)
    $identity.SetAttribute('Publisher', $Publisher)
    $identity.SetAttribute('Version', $PackageVersion)
    $identity.SetAttribute('ProcessorArchitecture', 'x64')
    $publisherNode.InnerText = $PublisherDisplayName
    Write-AutoEnvPlusXmlDocument -Document $document -Path $OutputPath
}

function New-AutoEnvPlusAppInstaller {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TemplatePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$Publisher,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$PackageUri,
        [Parameter(Mandatory)][string]$AppInstallerUri
    )

    Assert-AutoEnvPlusPackageName -Name $PackageName
    $PackageVersion = ConvertTo-AutoEnvPlusPackageVersion -Version $PackageVersion
    $PackageUri = ConvertTo-AutoEnvPlusHttpsUri -Value $PackageUri -ParameterName 'PackageUri' -RequiredExtension '.msix'
    $AppInstallerUri = ConvertTo-AutoEnvPlusHttpsUri -Value $AppInstallerUri -ParameterName 'AppInstallerUri' -RequiredExtension '.appinstaller'

    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $false
    $document.Load($TemplatePath)
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $namespaceManager.AddNamespace('a', 'http://schemas.microsoft.com/appx/appinstaller/2018')

    $appInstaller = $document.SelectSingleNode('/a:AppInstaller', $namespaceManager)
    $mainPackage = $document.SelectSingleNode('/a:AppInstaller/a:MainPackage', $namespaceManager)
    if ($null -eq $appInstaller -or $null -eq $mainPackage) {
        throw 'The AppInstaller template is missing AppInstaller or MainPackage.'
    }

    $appInstaller.SetAttribute('Uri', $AppInstallerUri)
    $appInstaller.SetAttribute('Version', $PackageVersion)
    $mainPackage.SetAttribute('Name', $PackageName)
    $mainPackage.SetAttribute('Publisher', $Publisher)
    $mainPackage.SetAttribute('Version', $PackageVersion)
    $mainPackage.SetAttribute('ProcessorArchitecture', 'x64')
    $mainPackage.SetAttribute('Uri', $PackageUri)
    Write-AutoEnvPlusXmlDocument -Document $document -Path $OutputPath
}

function New-AutoEnvPlusRoundedRectanglePath {
    param(
        [Parameter(Mandatory)][float]$X,
        [Parameter(Mandatory)][float]$Y,
        [Parameter(Mandatory)][float]$Width,
        [Parameter(Mandatory)][float]$Height,
        [Parameter(Mandatory)][float]$Radius
    )

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-AutoEnvPlusBrandAssets {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutputDirectory)

    Add-Type -AssemblyName System.Drawing
    [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

    $specifications = @(
        [pscustomobject]@{ Name = 'StoreLogo.png'; Width = 50; Height = 50 },
        [pscustomobject]@{ Name = 'Square44x44Logo.png'; Width = 44; Height = 44 },
        [pscustomobject]@{ Name = 'Square150x150Logo.png'; Width = 150; Height = 150 },
        [pscustomobject]@{ Name = 'Wide310x150Logo.png'; Width = 310; Height = 150 },
        [pscustomobject]@{ Name = 'Square310x310Logo.png'; Width = 310; Height = 310 },
        [pscustomobject]@{ Name = 'SplashScreen.png'; Width = 620; Height = 300 }
    )

    foreach ($specification in $specifications) {
        $bitmap = New-Object System.Drawing.Bitmap(
            $specification.Width,
            $specification.Height,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::FromArgb(255, 15, 23, 42))

            $shortSide = [math]::Min($specification.Width, $specification.Height)
            $iconSize = $shortSide * 0.72
            $iconX = ($specification.Width - $iconSize) / 2
            $iconY = ($specification.Height - $iconSize) / 2
            $cornerRadius = $iconSize * 0.22
            $iconPath = New-AutoEnvPlusRoundedRectanglePath -X $iconX -Y $iconY -Width $iconSize -Height $iconSize -Radius $cornerRadius
            $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
            try {
                $graphics.FillPath($accentBrush, $iconPath)
            }
            finally {
                $accentBrush.Dispose()
                $iconPath.Dispose()
            }

            $strokeWidth = [math]::Max(2.0, $iconSize * 0.075)
            $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $strokeWidth)
            try {
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                $left = $iconX + ($iconSize * 0.24)
                $apexX = $iconX + ($iconSize * 0.43)
                $right = $iconX + ($iconSize * 0.62)
                $top = $iconY + ($iconSize * 0.24)
                $bottom = $iconY + ($iconSize * 0.76)
                $graphics.DrawLine($pen, $left, $bottom, $apexX, $top)
                $graphics.DrawLine($pen, $apexX, $top, $right, $bottom)
                $graphics.DrawLine($pen, $left + ($iconSize * 0.08), $iconY + ($iconSize * 0.57), $right - ($iconSize * 0.08), $iconY + ($iconSize * 0.57))
                $plusX = $iconX + ($iconSize * 0.72)
                $plusY = $iconY + ($iconSize * 0.51)
                $plusRadius = $iconSize * 0.11
                $graphics.DrawLine($pen, $plusX - $plusRadius, $plusY, $plusX + $plusRadius, $plusY)
                $graphics.DrawLine($pen, $plusX, $plusY - $plusRadius, $plusX, $plusY + $plusRadius)
            }
            finally {
                $pen.Dispose()
            }

            $assetPath = Join-Path $OutputDirectory $specification.Name
            $bitmap.Save($assetPath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}

function Get-AutoEnvPlusSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Password
    )

    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Path, $Password, $flags)
}

function Assert-AutoEnvPlusSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][string]$Publisher
    )

    if (-not $Certificate.HasPrivateKey) {
        throw 'The signing certificate does not contain a private key.'
    }
    if (-not [string]::Equals($Certificate.Subject, $Publisher, [System.StringComparison]::Ordinal)) {
        throw "Publisher must exactly match the signing certificate subject. Expected '$($Certificate.Subject)'."
    }

    $now = [System.DateTime]::UtcNow
    if ($now -lt $Certificate.NotBefore.ToUniversalTime() -or $now -gt $Certificate.NotAfter.ToUniversalTime()) {
        throw 'The signing certificate is not currently valid.'
    }

    $enhancedKeyUsage = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1
    if ($null -eq $enhancedKeyUsage) {
        throw 'The signing certificate must declare the code-signing extended key usage.'
    }
    $parsedEnhancedKeyUsage = New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
        $enhancedKeyUsage,
        $enhancedKeyUsage.Critical)
    if (-not ($parsedEnhancedKeyUsage.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw 'The signing certificate must allow code signing.'
    }

    $keyUsage = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.15' } |
        Select-Object -First 1
    if ($null -eq $keyUsage) {
        throw 'The signing certificate must declare digital-signature key usage.'
    }
    $parsedKeyUsage = New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
        $keyUsage,
        $keyUsage.Critical)
    if (($parsedKeyUsage.KeyUsages -band [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -eq 0) {
        throw 'The signing certificate must allow digital signatures.'
    }

    $basicConstraints = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
        Select-Object -First 1
    if ($null -ne $basicConstraints) {
        $parsedBasicConstraints = New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
            $basicConstraints,
            $basicConstraints.Critical)
        if ($parsedBasicConstraints.CertificateAuthority) {
            throw 'The signing certificate must not be a certificate authority.'
        }
    }
}

function Resolve-AutoEnvPlusWindowsSdkTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('makeappx.exe', 'signtool.exe')][string]$ToolName,
        [Parameter(Mandatory)][string]$NuGetPackagesPath
    )

    $packageRoot = Join-Path $NuGetPackagesPath 'microsoft.windows.sdk.buildtools'
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "Microsoft.Windows.SDK.BuildTools is not available under $NuGetPackagesPath. Restore the solution first."
    }

    $tool = Get-ChildItem -LiteralPath $packageRoot -Directory |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
        ForEach-Object {
            Get-ChildItem -LiteralPath (Join-Path $_.FullName 'bin') -Filter $ToolName -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
        } |
        Where-Object { $null -ne $_ } |
        Select-Object -First 1

    if ($null -eq $tool) {
        throw "$ToolName was not found in Microsoft.Windows.SDK.BuildTools under $NuGetPackagesPath."
    }
    return $tool.FullName
}

function Get-AutoEnvPlusMsixIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ManifestPath)

    $document = New-Object System.Xml.XmlDocument
    $document.Load($ManifestPath)
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $namespaceManager.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $document.SelectSingleNode('/m:Package/m:Identity', $namespaceManager)
    if ($null -eq $identity) {
        throw "Package manifest does not contain an identity: $ManifestPath"
    }

    return [pscustomobject]@{
        Name = $identity.GetAttribute('Name')
        Publisher = $identity.GetAttribute('Publisher')
        Version = $identity.GetAttribute('Version')
        ProcessorArchitecture = $identity.GetAttribute('ProcessorArchitecture')
    }
}

function Get-AutoEnvPlusAppInstallerIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $namespaceManager.AddNamespace('a', 'http://schemas.microsoft.com/appx/appinstaller/2018')
    $appInstaller = $document.SelectSingleNode('/a:AppInstaller', $namespaceManager)
    $mainPackage = $document.SelectSingleNode('/a:AppInstaller/a:MainPackage', $namespaceManager)
    if ($null -eq $appInstaller -or $null -eq $mainPackage) {
        throw "AppInstaller does not contain AppInstaller and MainPackage elements: $Path"
    }

    return [pscustomobject]@{
        AppInstallerUri = $appInstaller.GetAttribute('Uri')
        AppInstallerVersion = $appInstaller.GetAttribute('Version')
        Name = $mainPackage.GetAttribute('Name')
        Publisher = $mainPackage.GetAttribute('Publisher')
        Version = $mainPackage.GetAttribute('Version')
        ProcessorArchitecture = $mainPackage.GetAttribute('ProcessorArchitecture')
        PackageUri = $mainPackage.GetAttribute('Uri')
    }
}

function Test-AutoEnvPlusMsixSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedThumbprint
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.Security

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $signatureEntry = $archive.GetEntry('AppxSignature.p7x')
        if ($null -eq $signatureEntry) {
            throw 'MSIX does not contain AppxSignature.p7x.'
        }
        $memory = New-Object System.IO.MemoryStream
        try {
            $stream = $signatureEntry.Open()
            try {
                $stream.CopyTo($memory)
            }
            finally {
                $stream.Dispose()
            }
            $signatureBytes = $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    if ($signatureBytes.Length -lt 5 -or
        [System.Text.Encoding]::ASCII.GetString($signatureBytes, 0, 4) -ne 'PKCX') {
        throw 'MSIX signature does not use the expected PKCX SignedCms envelope.'
    }

    $cmsBytes = New-Object byte[] ($signatureBytes.Length - 4)
    [System.Array]::Copy($signatureBytes, 4, $cmsBytes, 0, $cmsBytes.Length)
    $signedCms = New-Object System.Security.Cryptography.Pkcs.SignedCms
    $signedCms.Decode($cmsBytes)
    $signedCms.CheckSignature($true)
    if ($signedCms.SignerInfos.Count -ne 1) {
        throw 'MSIX must contain exactly one primary signer.'
    }

    $signerCertificate = $signedCms.SignerInfos[0].Certificate
    if ($null -eq $signerCertificate -or
        -not [string]::Equals($signerCertificate.Thumbprint, $ExpectedThumbprint, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'MSIX signer thumbprint does not match the selected signing certificate.'
    }
}

Export-ModuleMember -Function @(
    'Assert-AutoEnvPlusPackageName',
    'Assert-AutoEnvPlusSigningCertificate',
    'ConvertTo-AutoEnvPlusHttpsUri',
    'ConvertTo-AutoEnvPlusPackageVersion',
    'ConvertTo-AutoEnvPlusTimestampUri',
    'Get-AutoEnvPlusAppInstallerIdentity',
    'Get-AutoEnvPlusMsixIdentity',
    'Get-AutoEnvPlusSigningCertificate',
    'New-AutoEnvPlusAppInstaller',
    'New-AutoEnvPlusAppxManifest',
    'New-AutoEnvPlusBrandAssets',
    'Resolve-AutoEnvPlusWindowsSdkTool',
    'Test-AutoEnvPlusMsixSignature'
)
