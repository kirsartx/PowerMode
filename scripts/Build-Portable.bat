@echo off
chcp 65001 >nul
title Build PowerMode Portable
setlocal

for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "OUTPUT=%ROOT%\dist\PowerMode-win-x64"

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Portable.ps1" -Root "%ROOT%" -CreateZip
if errorlevel 1 (
    echo.
    echo PowerMode 便携版构建失败。
    pause
    exit /b 1
)

echo.
echo PowerMode 便携版已生成：
echo %OUTPUT%
echo.
pause
