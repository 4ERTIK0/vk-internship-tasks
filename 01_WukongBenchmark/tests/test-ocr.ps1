param([string]$Tesseract = 'C:\Program Files\Tesseract-OCR\tesseract.exe')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Tesseract)) { throw 'Pass -Tesseract with the path to installed Tesseract 5.' }
& (Join-Path $PSScriptRoot '..\build.ps1') -Test
& (Join-Path $PSScriptRoot 'make-ocr-fixture.ps1')
$fixture = Join-Path $PSScriptRoot 'fixtures\synthetic-result'
& $Tesseract "$fixture.png" $fixture -l eng --psm 11 tsv
if ($LASTEXITCODE -ne 0) { throw 'OCR failed.' }
& (Join-Path $PSScriptRoot '..\bin\Tests.exe') "$fixture.tsv"
if ($LASTEXITCODE -ne 0) { throw 'OCR parsing test failed.' }
