@echo off
setlocal
cd /d "%~dp0"
echo Building OpenCode Warp from %~dp0
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
