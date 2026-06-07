@echo off
setlocal

reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>nul
if errorlevel 1 (
    echo Temp Profile Fixer requires Microsoft .NET Framework 4.x Full.
    echo Install or enable .NET Framework 4.x on this computer, then run this tool again.
    exit /b 2
)

"%~dp0TempProfileFixer.exe" %*
exit /b %ERRORLEVEL%
