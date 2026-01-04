@echo off
echo Building ProcRipper v3.0.0...
echo.

REM Build the solution (now contains ProcRipper + ProcRipperConfig)
dotnet build "ProcRipper v3.0.0.sln" --configuration Release
if %errorlevel% neq 0 (
    echo.
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo Build successful!
echo Output folder (shared):
echo   .\Bin\Release\net8.0-windows10.0.17763.0\
echo.
echo Executables:
echo   - ProcRipper.exe       (runtime console + tray)
echo   - ProcRipperConfig.exe (config editor)
echo.
pause
