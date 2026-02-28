@echo off
chcp 65001 > nul
echo ========================================
echo TradingBot Setup.exe Build Script
echo ========================================
echo.

REM Get version input
set /p VERSION="Enter version (e.g., 1.0.0): "

if "%VERSION%"=="" (
    echo Error: Version is required.
    pause
    exit /b 1
)

echo.
echo [1/4] Checking Velopack tool...
where vpk >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo Velopack tool not found. Installing...
    dotnet tool install -g vpk
    if %ERRORLEVEL% neq 0 (
        echo Error: Failed to install Velopack
        pause
        exit /b 1
    )
)
echo Velopack tool found

echo.
echo [2/4] Publishing application...
dotnet publish TradingBot\TradingBot.csproj -c Release -r win-x64 --self-contained -o .\publish
if %ERRORLEVEL% neq 0 (
    echo Error: Publishing failed
    pause
    exit /b 1
)
echo Publishing completed

echo.
echo [3/4] Packaging with Velopack...
vpk pack -u TradingBot -v %VERSION% -p .\publish -e TradingBot.exe
if %ERRORLEVEL% neq 0 (
    echo Error: Packaging failed
    pause
    exit /b 1
)
echo Packaging completed

echo.
echo [4/4] Cleaning up...
echo.
echo ========================================
echo Build Completed!
echo ========================================
echo.
echo Generated files in Releases folder:
dir Releases\*Setup.exe /b 2>nul
echo.
echo You can install the application using:
echo   Releases\TradingBot-win-Setup.exe
echo.
pause
