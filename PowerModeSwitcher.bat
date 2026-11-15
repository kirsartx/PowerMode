@echo off
setlocal DisableDelayedExpansion
set "CLI=%~dp0src\PowerMode.Cli\PowerModeSwitcher.bat"
if not exist "%CLI%" (
    echo PowerMode CLI was not found: "%CLI%" 1>&2
    exit /b 2
)
call "%CLI%" %*
exit /b %ERRORLEVEL%
