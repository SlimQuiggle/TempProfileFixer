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
    $accent = [System.Drawing.Color]::FromArgb(34, 110, 210)
    $line = Pen $ink 12
    $thin = Pen $ink 7
    $blue = Pen $accent 9

    try {
        # Simple outline head with ears.
        $graphics.DrawEllipse($line, (S 58), (S 58), (S 140), (S 146))
        $graphics.DrawArc($thin, (S 43), (S 112), (S 34), (S 42), 92, 205)
        $graphics.DrawArc($thin, (S 179), (S 112), (S 34), (S 42), -117, 205)

        # Minimal goofy face.
        $graphics.DrawEllipse($thin, (S 91), (S 111), (S 16), (S 22))
        $graphics.DrawEllipse($thin, (S 145), (S 111), (S 16), (S 22))
        $graphics.DrawArc($thin, (S 99), (S 142), (S 58), (S 34), 15, 150)

        # Tiny "fixed here" patch on the forehead.
        $graphics.DrawLine($blue, (S 107), (S 82), (S 145), (S 82))
        $graphics.DrawLine($blue, (S 126), (S 67), (S 126), (S 97))

        # Hammer angled down toward the head.
        $graphics.DrawLine($line, (S 162), (S 57), (S 218), (S 111))
        $graphics.DrawLine($line, (S 184), (S 32), (S 224), (S 72))
        $graphics.DrawLine($line, (S 170), (S 46), (S 198), (S 18))
        $graphics.DrawLine($line, (S 211), (S 86), (S 239), (S 58))

        # Impact marks.
        $graphics.DrawLine($thin, (S 151), (S 50), (S 142), (S 33))
        $graphics.DrawLine($thin, (S 142), (S 62), (S 124), (S 56))
        $graphics.DrawLine($thin, (S 166), (S 40), (S 170), (S 22))
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
