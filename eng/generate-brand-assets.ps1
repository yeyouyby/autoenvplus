[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
else {
    $RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
}

function New-RoundedRectanglePath {
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

function New-BrandBitmap {
    param([Parameter(Mandatory)][ValidateRange(16, 4096)][int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $scale = $Size / 512.0

        $backgroundPath = New-RoundedRectanglePath `
            -X ([float](24 * $scale)) `
            -Y ([float](24 * $scale)) `
            -Width ([float](464 * $scale)) `
            -Height ([float](464 * $scale)) `
            -Radius ([float](104 * $scale))
        $backgroundBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(255, 23, 26, 33))
        try {
            $graphics.FillPath($backgroundBrush, $backgroundPath)
        }
        finally {
            $backgroundBrush.Dispose()
            $backgroundPath.Dispose()
        }

        $bluePen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(255, 85, 167, 255),
            [float](44 * $scale))
        $greenPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(255, 66, 211, 146),
            [float](44 * $scale))
        $amberPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(255, 246, 196, 83),
            [float](38 * $scale))
        $whitePen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::White,
            [float](32 * $scale))
        try {
            foreach ($pen in @($bluePen, $greenPen, $amberPen, $whitePen)) {
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            }

            $graphics.DrawLine($bluePen, 130 * $scale, 368 * $scale, 234 * $scale, 132 * $scale)
            $graphics.DrawLine($greenPen, 234 * $scale, 132 * $scale, 338 * $scale, 368 * $scale)
            $graphics.DrawLine($amberPen, 169 * $scale, 282 * $scale, 300 * $scale, 282 * $scale)
            $graphics.DrawLine($whitePen, 342 * $scale, 198 * $scale, 422 * $scale, 198 * $scale)
            $graphics.DrawLine($whitePen, 382 * $scale, 158 * $scale, 382 * $scale, 238 * $scale)
        }
        finally {
            $bluePen.Dispose()
            $greenPen.Dispose()
            $amberPen.Dispose()
            $whitePen.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Convert-BitmapToPngBytes {
    param([Parameter(Mandatory)][System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Convert-BitmapToIconDibBytes {
    param([Parameter(Mandatory)][System.Drawing.Bitmap]$Bitmap)

    $width = $Bitmap.Width
    $height = $Bitmap.Height
    $andStride = [int]([math]::Ceiling($width / 32.0) * 4)
    $imageBytes = [int](($width * $height * 4) + ($andStride * $height))
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint32]40)
        $writer.Write([int32]$width)
        $writer.Write([int32]($height * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32]$imageBytes)
        $writer.Write([int32]0)
        $writer.Write([int32]0)
        $writer.Write([uint32]0)
        $writer.Write([uint32]0)

        for ($y = $height - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $width; $x++) {
                $color = $Bitmap.GetPixel($x, $y)
                $writer.Write([byte]$color.B)
                $writer.Write([byte]$color.G)
                $writer.Write([byte]$color.R)
                $writer.Write([byte]$color.A)
            }
        }

        $writer.Write((New-Object byte[] ($andStride * $height)))
        return ,$stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-MultiSizeIcon {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int[]]$Sizes
    )

    $images = foreach ($size in $Sizes) {
        $bitmap = New-BrandBitmap -Size $size
        try {
            [pscustomobject]@{
                Size = $size
                Bytes = Convert-BitmapToIconDibBytes -Bitmap $bitmap
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $stream = New-Object System.IO.FileStream(
        $Path,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)
        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }

        foreach ($image in $images) {
            $writer.Write([byte[]]$image.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$outputDirectory = Join-Path $RepositoryRoot 'assets\branding'
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$logoPath = Join-Path $outputDirectory 'autoenvplus-logo.png'
$iconPath = Join-Path $outputDirectory 'autoenvplus.ico'

$logo = New-BrandBitmap -Size 512
try {
    $logo.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $logo.Dispose()
}

Write-MultiSizeIcon -Path $iconPath -Sizes @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Write-Host "Brand PNG: $logoPath"
Write-Host "Application icon: $iconPath"
