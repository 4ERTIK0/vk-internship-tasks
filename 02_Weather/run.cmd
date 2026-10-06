@echo off
chcp 65001 >nul
cd /d "%~dp0"
python weather.py %*
set "result=%errorlevel%"
pause
exit /b %result%
