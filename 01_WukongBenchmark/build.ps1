param([switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found. Windows 10/11 x64 with .NET Framework 4.8 is required.' }
$binDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $binDirectory | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$refs = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Management.dll','/r:System.Web.Extensions.dll')
& $compiler /nologo /utf8output /codepage:65001 /langversion:5 /target:exe /platform:x64 /optimize+ /warn:4 /warnaserror+ "/out:$binDirectory\WukongRunner.exe" $refs $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App.config') -Destination (Join-Path $binDirectory 'WukongRunner.exe.config') -Force
if ($Test) {
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' | ForEach-Object FullName)
    & $compiler /nologo /utf8output /codepage:65001 /langversion:5 /target:exe /platform:x64 /warn:4 /warnaserror+ /main:VkBenchmark.Tests "/out:$binDirectory\Tests.exe" $refs $sourceFiles $testFiles
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & "$binDirectory\Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
Write-Host "Built: $binDirectory\WukongRunner.exe"
