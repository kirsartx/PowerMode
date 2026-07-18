@echo off
call "%~dp0scripts\Build-Portable.bat" %*
exit /b %ERRORLEVEL%
