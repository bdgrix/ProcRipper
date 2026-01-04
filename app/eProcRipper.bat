@echo off
cd /d "%~dp0"


start "" "ProcRipper v3.0.0.exe"

timeout /t 5 /nobreak >nul
taskkill /im "ProcRipper v3.0.0.exe" /f >nul 2>&1

exit /b
