@echo off
setlocal

reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>nul
if not errorlevel 1 goto run_tool

reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Install >nul 2>nul
if not errorlevel 1 goto run_tool

if errorlevel 1 (
    echo Temp Profile Fixer requires Microsoft .NET Framework 4.x Full.
    echo Install or enable .NET Framework 4.x on this computer, then run this tool again.
    exit /b 2
)

:run_tool
"%~dp0TempProfileFixer.exe" %*
exit /b %ERRORLEVEL%
