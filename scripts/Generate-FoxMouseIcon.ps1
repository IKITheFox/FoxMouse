[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\FoxMouse.App\Assets\FoxMouse.ico'),
    [string]$PreviewPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\assets\foxmouse-icon-256.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = New-Object 'System.Collections.Generic.List[byte[]]'

function New-FoxMouseBitmap {
    param([Parameter(Mandatory)][int]$Size)

    $bitmap = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)

        $scale = $Size / 64.0
        $orange = [Drawing.Color]::FromArgb(255, 226, 82, 35)
        $deepOrange = [Drawing.Color]::FromArgb(255, 177, 50, 18)
        $ink = [Drawing.Color]::FromArgb(255, 48, 48, 48)

        $headPath = New-Object Drawing.Drawing2D.GraphicsPath
        $headPath.AddPolygon([Drawing.PointF[]]@(
            [Drawing.PointF]::new(8 * $scale, 22 * $scale),
            [Drawing.PointF]::new(11 * $scale, 5 * $scale),
            [Drawing.PointF]::new(25 * $scale, 14 * $scale),
            [Drawing.PointF]::new(39 * $scale, 14 * $scale),
            [Drawing.PointF]::new(53 * $scale, 5 * $scale),
            [Drawing.PointF]::new(56 * $scale, 22 * $scale),
            [Drawing.PointF]::new(53 * $scale, 43 * $scale),
            [Drawing.PointF]::new(32 * $scale, 58 * $scale),
            [Drawing.PointF]::new(11 * $scale, 43 * $scale)
        ))
        try {
            $headBrush = New-Object Drawing.SolidBrush $orange
            $headPen = New-Object Drawing.Pen $deepOrange, ([Math]::Max(1.0, 2.0 * $scale))
            try {
                $headPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                $graphics.FillPath($headBrush, $headPath)
                $graphics.DrawPath($headPen, $headPath)
            }
            finally {
                $headBrush.Dispose()
                $headPen.Dispose()
            }
        }
        finally {
            $headPath.Dispose()
        }

        $faceBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(245, 255, 255, 255))
        try {
            $graphics.FillEllipse($faceBrush, 18 * $scale, 21 * $scale, 28 * $scale, 28 * $scale)
        }
        finally {
            $faceBrush.Dispose()
        }

        $cursorPath = New-Object Drawing.Drawing2D.GraphicsPath
        $cursorPath.AddPolygon([Drawing.PointF[]]@(
            [Drawing.PointF]::new(25 * $scale, 18 * $scale),
            [Drawing.PointF]::new(25 * $scale, 45 * $scale),
            [Drawing.PointF]::new(31 * $scale, 39 * $scale),
            [Drawing.PointF]::new(37 * $scale, 51 * $scale),
            [Drawing.PointF]::new(44 * $scale, 47 * $scale),
            [Drawing.PointF]::new(38 * $scale, 36 * $scale),
            [Drawing.PointF]::new(48 * $scale, 35 * $scale)
        ))
        try {
            $cursorBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
            $cursorPen = New-Object Drawing.Pen $ink, ([Math]::Max(1.0, 1.8 * $scale))
            try {
                $cursorPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                $graphics.FillPath($cursorBrush, $cursorPath)
                $graphics.DrawPath($cursorPen, $cursorPath)
            }
            finally {
                $cursorBrush.Dispose()
                $cursorPen.Dispose()
            }
        }
        finally {
            $cursorPath.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

foreach ($size in $sizes) {
    $bitmap = New-FoxMouseBitmap -Size $size
    try {
        $stream = New-Object IO.MemoryStream
        try {
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $images.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
        }

        if ($size -eq 256) {
            $previewDirectory = Split-Path -Parent $PreviewPath
            [void][IO.Directory]::CreateDirectory($previewDirectory)
            $bitmap.Save($PreviewPath, [Drawing.Imaging.ImageFormat]::Png)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
[void][IO.Directory]::CreateDirectory($outputDirectory)
$file = [IO.File]::Open($OutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = New-Object IO.BinaryWriter $file
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $bytes = $images[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $bytes.Length
    }

    foreach ($bytes in $images) {
        $writer.Write($bytes)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "FoxMouse icon generated: $OutputPath"
