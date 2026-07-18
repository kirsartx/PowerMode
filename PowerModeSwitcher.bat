@echo off
call "%~dp0src\PowerMode.Cli\PowerModeSwitcher.bat" %*
exit /b %ERRORLEVEL%
