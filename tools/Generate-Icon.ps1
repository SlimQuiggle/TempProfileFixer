[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
    $OutputDirectory = Join-Path $repoRoot 'assets'
}

function New-TempProfileFixerIconBitmap {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Size
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 256.0
    function S([float]$value) { return [int][Math]::Round($value * $scale) }
    function Pen([System.Drawing.Color]$color, [float]$width) {
        $pen = New-Object System.Drawing.Pen($color, [Math]::Max(1.0, $width * $scale))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        return $pen
    }

    $ink = [System.Drawing.Color]::FromArgb(25, 25, 25)
    $accent = [System.Drawing.Color]::FromArgb(36, 115, 216)
    $line = Pen $ink 13
    $thin = Pen $ink 8
    $blue = Pen $accent 11

    try {
        # Goofy head outline.
        $graphics.DrawEllipse($line, (S 48), (S 42), (S 150), (S 160))
        $graphics.DrawArc($thin, (S 34), (S 96), (S 40), (S 46), 95, 205)
        $graphics.DrawArc($thin, (S 180), (S 96), (S 40), (S 46), -115, 205)

        # Loose hair squiggles.
        $graphics.DrawBezier($thin, (S 78), (S 52), (S 72), (S 28), (S 108), (S 34), (S 96), (S 58))
        $graphics.DrawBezier($thin, (S 108), (S 45), (S 115), (S 22), (S 148), (S 36), (S 130), (S 60))

        # Goofy face.
        $graphics.DrawEllipse($thin, (S 83), (S 94), (S 20), (S 28))
        $graphics.FillEllipse([System.Drawing.Brushes]::Black, (S 91), (S 104), (S 7), (S 7))
        $graphics.DrawArc($thin, (S 132), (S 93), (S 28), (S 28), 20, 320)
        $graphics.DrawArc($thin, (S 91), (S 134), (S 64), (S 36), 10, 145)
        $graphics.DrawLine($thin, (S 117), (S 118), (S 108), (S 138))

        # Bandage / profile patch on the cheek.
        $graphics.DrawLine($blue, (S 64), (S 142), (S 105), (S 154))
        $graphics.DrawLine($blue, (S 72), (S 132), (S 97), (S 164))

        # Little wrench crossing the head to imply repair/fix.
        $graphics.DrawLine($line, (S 157), (S 186), (S 218), (S 126))
        $graphics.DrawArc($line, (S 202), (S 101), (S 42), (S 42), 124, 225)
        $graphics.DrawLine($line, (S 200), (S 126), (S 226), (S 152))
        $graphics.DrawEllipse($line, (S 145), (S 181), (S 22), (S 22))
    }
    finally {
        $line.Dispose()
        $thin.Dispose()
        $blue.Dispose()
        $graphics.Dispose()
    }

    return $bitmap
}

function Save-Png {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [string]$Path
    )

    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Save-Ico {
    param(
        [int[]]$Sizes,
        [string]$Path
    )

    $images = New-Object System.Collections.Generic.List[object]
    foreach ($size in $Sizes) {
        $bitmap = New-TempProfileFixerIconBitmap -Size $size
        $stream = New-Object System.IO.MemoryStream
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add([pscustomobject]@{
                Size = $size
                Data = $stream.ToArray()
            })
        }
        finally {
            $stream.Dispose()
            $bitmap.Dispose()
        }
    }

    $file = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = New-Object System.IO.BinaryWriter($file)
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
            $writer.Write([UInt32]$image.Data.Length)
            $writer.Write([UInt32]$offset)
            $offset += $image.Data.Length
        }

        foreach ($image in $images) {
            $writer.Write($image.Data)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null

$pngPath = Join-Path $OutputDirectory 'TempProfileFixer.png'
$icoPath = Join-Path $OutputDirectory 'TempProfileFixer.ico'

$preview = New-TempProfileFixerIconBitmap -Size 256
try {
    Save-Png -Bitmap $preview -Path $pngPath
}
finally {
    $preview.Dispose()
}

Save-Ico -Sizes @(256, 128, 64, 48, 32, 16) -Path $icoPath

Write-Host "Wrote $pngPath"
Write-Host "Wrote $icoPath"
