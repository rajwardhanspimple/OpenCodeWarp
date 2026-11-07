@echo off
setlocal
cd /d "%~dp0"
if not exist "dist\OpenCodeWarp.exe" (
    echo OpenCodeWarp.exe was not found in the dist folder.
    echo Build it first with: powershell -ExecutionPolicy Bypass -File build.ps1
    exit /b 1
)
start "" "dist\OpenCodeWarp.exe"
