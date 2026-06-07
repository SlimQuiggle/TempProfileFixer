@echo off
setlocal
cd /d "%~dp0"

echo Temp Profile Fixer package unblock
echo ==================================
echo.
echo Use this only when you downloaded the official package from a trusted source
echo and Windows is blocking the EXE, CMD, or config files.
echo.
echo This only removes the Windows download block from files in this folder.
echo It does not modify profiles, delete folders, or edit registry keys.
echo.

choice /C YN /N /M "Do you trust this extracted Temp Profile Fixer package? [Y/N] "
if errorlevel 2 (
    echo Cancelled.
    exit /b 1
)

set "TPF_ROOT=%CD%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $names=@('TempProfileFixer.exe','TempProfileFixer.exe.config','TempProfileFixer.cmd','Run-Diagnostics.cmd','Unblock-Package.cmd','README.md','COMMAND-LINE.md','SHA256SUMS.txt'); foreach ($name in $names) { $path=Join-Path $env:TPF_ROOT $name; if (Test-Path -LiteralPath $path) { Unblock-File -LiteralPath $path } }; Write-Host 'Package files unblocked.'"
if errorlevel 1 (
    echo.
    echo Unblock failed. Run this from an elevated prompt or unblock the files from Properties.
    exit /b 2
)

echo.
echo Done. Run Run-Diagnostics.cmd next.
exit /b 0
