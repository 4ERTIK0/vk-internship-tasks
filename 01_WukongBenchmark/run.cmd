@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "bin\WukongRunner.exe" powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
if not exist "bin\WukongRunner.exe" exit /b 2
"bin\WukongRunner.exe" %*
set "result=%errorlevel%"
pause
exit /b %result%
