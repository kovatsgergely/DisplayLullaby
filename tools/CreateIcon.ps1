param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Assets\DisplayLullaby.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconImageBytes {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / 24.0
        $blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)

        try {
            $background = New-RoundedRectanglePath -X (2 * $scale) -Y (2 * $scale) -Width (20 * $scale) -Height (20 * $scale) -Radius (5 * $scale)
            try {
                $graphics.FillPath($blue, $background)
            }
            finally {
                $background.Dispose()
            }

            $outer = New-RoundedRectanglePath -X (4 * $scale) -Y (4 * $scale) -Width (16 * $scale) -Height (12 * $scale) -Radius (1.5 * $scale)
            try {
                $graphics.FillPath($white, $outer)
            }
            finally {
                $outer.Dispose()
            }

            $graphics.FillRectangle($blue, 6 * $scale, 6 * $scale, 12 * $scale, 7.5 * $scale)
            $graphics.FillRectangle($white, 10 * $scale, 15 * $scale, 4 * $scale, 3 * $scale)
            $graphics.FillRectangle($white, 7 * $scale, 18 * $scale, 10 * $scale, 2 * $scale)
        }
        finally {
            $blue.Dispose()
            $white.Dispose()
        }

        $xorLength = $Size * $Size * 4
        $maskStride = [int]([Math]::Ceiling($Size / 32.0) * 4)
        $maskLength = $maskStride * $Size
        $stream = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new($stream)

        try {
            $writer.Write([UInt32]40)
            $writer.Write([Int32]$Size)
            $writer.Write([Int32]($Size * 2))
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]0)
            $writer.Write([UInt32]$xorLength)
            $writer.Write([Int32]0)
            $writer.Write([Int32]0)
            $writer.Write([UInt32]0)
            $writer.Write([UInt32]0)

            for ($y = $Size - 1; $y -ge 0; $y--) {
                for ($x = 0; $x -lt $Size; $x++) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    $writer.Write([byte]$pixel.B)
                    $writer.Write([byte]$pixel.G)
                    $writer.Write([byte]$pixel.R)
                    $writer.Write([byte]$pixel.A)
                }
            }

            $writer.Write([byte[]]::new($maskLength))
            $writer.Flush()
            return ,([byte[]]$stream.ToArray())
        }
        finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Bytes = New-IconImageBytes -Size $size
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$file = [System.IO.File]::Create($OutputPath)
$writer = [System.IO.BinaryWriter]::new($file)

try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $writer.Write([byte]($(if ($image.Size -eq 256) { 0 } else { $image.Size })))
        $writer.Write([byte]($(if ($image.Size -eq 256) { 0 } else { $image.Size })))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Created $OutputPath"
