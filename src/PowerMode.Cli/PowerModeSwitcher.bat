@echo off
chcp 65001 >nul
title PowerMode
setlocal
set "PM_ENGINE=%~dp0PowerMode.Engine.ps1"
if not exist "%PM_ENGINE%" (
    echo PowerMode.Engine.ps1 was not found next to PowerModeSwitcher.bat.
    exit /b 2
)
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PM_ENGINE%" %*
set "PM_EXIT=%ERRORLEVEL%"
if not "%PM_NO_PAUSE%"=="1" if not "%~1"=="" pause
exit /b %PM_EXIT%
