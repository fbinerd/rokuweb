Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$extensionSource = @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

public static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using (var path = new GraphicsPath())
        {
            float diameter = radius * 2f;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            graphics.FillPath(brush, path);
        }
    }
}
"@

Add-Type -TypeDefinition $extensionSource -ReferencedAssemblies System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$imagesDir = Join-Path $repoRoot "images"

if (-not (Test-Path $imagesDir)) {
    New-Item -ItemType Directory -Path $imagesDir | Out-Null
}

function New-BrandImage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [int]$Width,

        [Parameter(Mandatory = $true)]
        [int]$Height,

        [Parameter(Mandatory = $true)]
        [string]$TopHex,

        [Parameter(Mandatory = $true)]
        [string]$BottomHex,

        [Parameter(Mandatory = $true)]
        [string]$AccentHex,

        [Parameter(Mandatory = $true)]
        [string]$TitleText,

        [Parameter(Mandatory = $true)]
        [string]$SubtitleText,

        [Parameter(Mandatory = $true)]
        [float]$TitleSize,

        [Parameter(Mandatory = $true)]
        [float]$SubtitleSize
    )

    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $bounds = New-Object System.Drawing.Rectangle 0, 0, $Width, $Height
    $top = [System.Drawing.ColorTranslator]::FromHtml($TopHex)
    $bottom = [System.Drawing.ColorTranslator]::FromHtml($BottomHex)
    $accent = [System.Drawing.ColorTranslator]::FromHtml($AccentHex)
    $titleColor = [System.Drawing.Color]::FromArgb(255, 247, 250, 252)
    $subtitleColor = [System.Drawing.Color]::FromArgb(220, 191, 219, 254)

    $backgroundBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $bounds, $top, $bottom, 45
    $graphics.FillRectangle($backgroundBrush, $bounds)

    $accentBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(52, $accent.R, $accent.G, $accent.B))
    $accentBrushStrong = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(88, $accent.R, $accent.G, $accent.B))
    $graphics.FillEllipse($accentBrush, [int]($Width * 0.50), [int](-$Height * 0.12), [int]($Width * 0.58), [int]($Height * 0.88))
    $graphics.FillEllipse($accentBrush, [int](-$Width * 0.10), [int]($Height * 0.55), [int]($Width * 0.42), [int]($Height * 0.48))

    $pillBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(80, 255, 255, 255))
    $pillBounds = New-Object System.Drawing.RectangleF ([single]($Width * 0.08)), ([single]($Height * 0.16)), ([single]($Width * 0.21)), ([single]([Math]::Max(7, $Height * 0.05)))
    [GraphicsExtensions]::FillRoundedRectangle($graphics, $pillBrush, $pillBounds, [single]([Math]::Max(6, $Height * 0.025)))

    $sparkBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(180, $accent.R, $accent.G, $accent.B))
    $graphics.FillEllipse($sparkBrush, [int]($Width * 0.82), [int]($Height * 0.18), [int]($Width * 0.06), [int]($Width * 0.06))
    $graphics.FillEllipse($accentBrushStrong, [int]($Width * 0.86), [int]($Height * 0.62), [int]($Width * 0.03), [int]($Width * 0.03))

    $titleFont = New-Object System.Drawing.Font("Segoe UI", [float]$TitleSize, [System.Drawing.FontStyle]::Bold)
    $subtitleFont = New-Object System.Drawing.Font("Segoe UI", [float]$SubtitleSize, [System.Drawing.FontStyle]::Regular)
    $titleBrush = New-Object System.Drawing.SolidBrush $titleColor
    $subtitleBrush = New-Object System.Drawing.SolidBrush $subtitleColor
    $stringFormat = New-Object System.Drawing.StringFormat
    $stringFormat.Alignment = [System.Drawing.StringAlignment]::Near
    $stringFormat.LineAlignment = [System.Drawing.StringAlignment]::Near

    $titleBounds = New-Object System.Drawing.RectangleF ([single]($Width * 0.08)), ([single]($Height * 0.28)), ([single]($Width * 0.72)), ([single]($Height * 0.30))
    $subtitleBounds = New-Object System.Drawing.RectangleF ([single]($Width * 0.08)), ([single]($Height * 0.67)), ([single]($Width * 0.72)), ([single]($Height * 0.16))
    $graphics.DrawString($TitleText, $titleFont, $titleBrush, $titleBounds, $stringFormat)
    $graphics.DrawString($SubtitleText, $subtitleFont, $subtitleBrush, $subtitleBounds, $stringFormat)

    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)

    $stringFormat.Dispose()
    $titleBrush.Dispose()
    $subtitleBrush.Dispose()
    $pillBrush.Dispose()
    $sparkBrush.Dispose()
    $accentBrush.Dispose()
    $accentBrushStrong.Dispose()
    $backgroundBrush.Dispose()
    $titleFont.Dispose()
    $subtitleFont.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

New-BrandImage `
    -Path (Join-Path $imagesDir "mm_icon_focus_hd.png") `
    -Width 336 `
    -Height 210 `
    -TopHex "#0F172A" `
    -BottomHex "#123C5A" `
    -AccentHex "#38BDF8" `
    -TitleText "SUPER" `
    -SubtitleText "PAINEL TV" `
    -TitleSize 58 `
    -SubtitleSize 26

New-BrandImage `
    -Path (Join-Path $imagesDir "mm_icon_focus_sd.png") `
    -Width 290 `
    -Height 174 `
    -TopHex "#0F172A" `
    -BottomHex "#123C5A" `
    -AccentHex "#38BDF8" `
    -TitleText "SUPER" `
    -SubtitleText "PAINEL TV" `
    -TitleSize 47 `
    -SubtitleSize 21

New-BrandImage `
    -Path (Join-Path $imagesDir "mm_icon_side_hd.png") `
    -Width 108 `
    -Height 69 `
    -TopHex "#0F172A" `
    -BottomHex "#123C5A" `
    -AccentHex "#38BDF8" `
    -TitleText "SUPER" `
    -SubtitleText "TV" `
    -TitleSize 20 `
    -SubtitleSize 11

New-BrandImage `
    -Path (Join-Path $imagesDir "mm_icon_side_sd.png") `
    -Width 88 `
    -Height 56 `
    -TopHex "#0F172A" `
    -BottomHex "#123C5A" `
    -AccentHex "#38BDF8" `
    -TitleText "SUPER" `
    -SubtitleText "TV" `
    -TitleSize 15 `
    -SubtitleSize 9

New-BrandImage `
    -Path (Join-Path $imagesDir "splash_screen_hd.png") `
    -Width 1280 `
    -Height 720 `
    -TopHex "#0B1120" `
    -BottomHex "#0F3B5B" `
    -AccentHex "#38BDF8" `
    -TitleText "SUPER" `
    -SubtitleText "PAINEL TV" `
    -TitleSize 148 `
    -SubtitleSize 54

New-BrandImage `
    -Path (Join-Path $imagesDir "splash_screen_sd.png") `
    -Width 720 `
    -Height 480 `
    -TopHex "#0B1120" `
    -BottomHex "#0F3B5B" `
    -AccentHex "#38BDF8" `
    -TitleText "SUPER" `
    -SubtitleText "PAINEL TV" `
    -TitleSize 102 `
    -SubtitleSize 38
