[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\branding\source'),
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\branding\generated')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$themes = [ordered]@{
    light = Join-Path $SourceRoot 'foxmouse-mark-color.png'
    dark = Join-Path $SourceRoot 'foxmouse-mark-dark.png'
    'high-contrast-black' = Join-Path $SourceRoot 'foxmouse-mark-mono-black.png'
    'high-contrast-white' = Join-Path $SourceRoot 'foxmouse-mark-mono-white.png'
}

function Save-ResizedPng {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][int]$Size
    )

    $sourceImage = [Drawing.Bitmap]::new($Source)
    try {
        $bitmap = [Drawing.Bitmap]::new(
            $Size,
            $Size,
            [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.Clear([Drawing.Color]::Transparent)
                $attributes = [Drawing.Imaging.ImageAttributes]::new()
                try {
                    $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
                    $graphics.DrawImage(
                        $sourceImage,
                        [Drawing.Rectangle]::new(0, 0, $Size, $Size),
                        0,
                        0,
                        $sourceImage.Width,
                        $sourceImage.Height,
                        [Drawing.GraphicsUnit]::Pixel,
                        $attributes)
                }
                finally {
                    $attributes.Dispose()
                }
            }
            finally {
                $graphics.Dispose()
            }

            $directory = Split-Path -Parent $Destination
            [void][IO.Directory]::CreateDirectory($directory)
            $bitmap.Save($Destination, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $sourceImage.Dispose()
    }
}

function Write-PngIcon {
    param(
        [Parameter(Mandatory)][string[]]$PngPaths,
        [Parameter(Mandatory)][string]$Destination
    )

    $images = [Collections.Generic.List[byte[]]]::new()
    foreach ($pngPath in $PngPaths) {
        $images.Add([IO.File]::ReadAllBytes($pngPath))
    }
    $stream = [IO.File]::Open(
        $Destination,
        [IO.FileMode]::Create,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$PngPaths.Count)
        $offset = 6 + (16 * $PngPaths.Count)
        for ($index = 0; $index -lt $PngPaths.Count; $index++) {
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
        $stream.Dispose()
    }
}

$null = [IO.Directory]::CreateDirectory($OutputRoot)
foreach ($theme in $themes.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $theme.Value -PathType Leaf)) {
        throw "Brand source is missing: $($theme.Value)"
    }

    $themeRoot = Join-Path $OutputRoot "icons\$($theme.Key)"
    $pngPaths = foreach ($size in $sizes) {
        $destination = Join-Path $themeRoot "foxmouse-$size.png"
        $optimizedSource = Join-Path $SourceRoot "icons\foxmouse-icon-$size.png"
        $source = if ($theme.Key -eq 'light' -and (Test-Path -LiteralPath $optimizedSource -PathType Leaf)) {
            $optimizedSource
        }
        else {
            $theme.Value
        }
        Save-ResizedPng -Source $source -Destination $destination -Size $size
        $destination
    }

    $iconName = switch ($theme.Key) {
        light { 'FoxMouse.ico' }
        dark { 'FoxMouse.Dark.ico' }
        'high-contrast-black' { 'FoxMouse.HighContrast.Black.ico' }
        'high-contrast-white' { 'FoxMouse.HighContrast.White.ico' }
    }
    Write-PngIcon -PngPaths $pngPaths -Destination (Join-Path $OutputRoot $iconName)
}

$files = @(
    Get-ChildItem -LiteralPath (Split-Path -Parent $OutputRoot) -File -Recurse |
        Where-Object { $_.Name -ne 'branding-manifest.json' } |
        Sort-Object FullName
)
$brandingRoot = Split-Path -Parent $OutputRoot
$records = foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($brandingRoot, $file.FullName).Replace('\', '/')
    $record = [ordered]@{
        path = $relative
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($file.Extension -eq '.png') {
        $image = [Drawing.Image]::FromFile($file.FullName)
        try {
            $record.width = $image.Width
            $record.height = $image.Height
        }
        finally {
            $image.Dispose()
        }
    }
    [pscustomobject]$record
}

$manifest = [ordered]@{
    schema = 'foxmouse.branding/1'
    product = 'FoxMouse'
    source = 'user-selected-logo-kit'
    colors = [ordered]@{
        orange = '#F26A2E'
        nearBlack = '#1B1B1B'
        darkForeground = '#FFFFFF'
    }
    iconSizes = $sizes
    files = $records
}
$manifestPath = Join-Path $brandingRoot 'branding-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "FoxMouse branding generated: $manifestPath"
