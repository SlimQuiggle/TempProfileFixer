@echo off
setlocal
cd /d "%~dp0"

echo Temp Profile Fixer diagnostics
echo ==============================
echo.
echo This check does not rename profiles, delete folders, or delete registry keys.
echo.

call "%~dp0TempProfileFixer.cmd" doctor
set "EXITCODE=%ERRORLEVEL%"

echo.
if "%EXITCODE%"=="0" (
    echo Diagnostics completed without reported failures.
) else (
    echo Diagnostics reported one or more failures.
    echo Review the messages above before rebuilding a profile on this computer.
)
echo.
echo Press any key to close this window.
pause >nul
exit /b %EXITCODE%
