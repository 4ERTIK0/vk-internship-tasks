param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'fixtures'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$bitmap = New-Object System.Drawing.Bitmap 1280,720
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$font = New-Object System.Drawing.Font 'Arial',24
$title = New-Object System.Drawing.Font 'Arial',18
try {
    $graphics.Clear([System.Drawing.Color]::FromArgb(20,25,30))
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawString('SYNTHETIC OCR TEST - NOT A BENCHMARK RESULT', $title, [System.Drawing.Brushes]::Gold, 90,70)
    $labels = @('Average FPS','Minimum FPS','Maximum FPS')
    $values = @('60.5','40','90')
    for ($i=0; $i -lt 3; $i++) {
        $y = 200 + $i*80
        $graphics.DrawString($labels[$i], $font, [System.Drawing.Brushes]::White, 150,$y)
        $graphics.DrawString($values[$i], $font, [System.Drawing.Brushes]::White, 480,$y)
    }
    $bitmap.Save((Join-Path $OutputDirectory 'synthetic-result.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally { $title.Dispose(); $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
