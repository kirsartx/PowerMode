@echo off
chcp 65001 >nul
title PowerMode WinUI
setlocal

for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "SOURCE=%ROOT%\src\PowerMode.App"
set "CLI=%ROOT%\src\PowerMode.Cli\PowerModeSwitcher.bat"
set "README=%ROOT%\README.md"
set "OUTPUT=%ROOT%\dist\PowerMode-win-x64"
set "APP=%OUTPUT%\App\PowerMode.exe"
set "STAMP=%OUTPUT%\build-info.json"
set "NEED_BUILD=0"

if not exist "%APP%" set "NEED_BUILD=1"
if not exist "%STAMP%" set "NEED_BUILD=1"
if exist "%APP%" if exist "%STAMP%" for /f %%I in ('powershell -NoProfile -Command "$stamp=(Get-Item -LiteralPath '%STAMP%').LastWriteTimeUtc; $inputs=@(Get-ChildItem -LiteralPath '%SOURCE%' -Recurse -File -Include *.cs,*.xaml,*.csproj,*.manifest); $inputs=@($inputs.Where({$_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*'})); $inputs+=Get-Item -LiteralPath '%CLI%','%README%'; $newer=$false; foreach($file in $inputs){if($file.LastWriteTimeUtc -gt $stamp){$newer=$true;break}}; if($newer){'1'}else{'0'}"') do set "NEED_BUILD=%%I"

if "%NEED_BUILD%"=="1" (
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Portable.ps1" -Root "%ROOT%"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)

start "" "%APP%"
exit /b 0
