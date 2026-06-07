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

set "SYSTEM32=%SystemRoot%\System32"
if not exist "%SYSTEM32%\cmd.exe" set "SYSTEM32=%WINDIR%\System32"
set "CHOICE_EXE=%SYSTEM32%\choice.exe"
set "FIND_EXE=%SYSTEM32%\find.exe"
set "POWERSHELL_EXE=%SYSTEM32%\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%FIND_EXE%" set "FIND_EXE=find.exe"
if not exist "%POWERSHELL_EXE%" set "POWERSHELL_EXE=powershell.exe"

if not exist "%CHOICE_EXE%" goto prompt_text
"%CHOICE_EXE%" /C YN /N /M "Do you trust this extracted Temp Profile Fixer package? [Y/N] "
if errorlevel 2 (
    echo Cancelled.
    exit /b 1
)
goto trust_confirmed

:prompt_text
set "TPF_TRUST="
set /p TPF_TRUST=Do you trust this extracted Temp Profile Fixer package? [Y/N]:
if /I not "%TPF_TRUST%"=="Y" (
    echo Cancelled.
    exit /b 1
)

:trust_confirmed
set "TPF_ROOT=%CD%"
"%POWERSHELL_EXE%" -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $names=@('TempProfileFixer.exe','TempProfileFixer.exe.config','TempProfileFixer.cmd','Run-Diagnostics.cmd','Unblock-Package.cmd','README.md','COMMAND-LINE.md','SHA256SUMS.txt'); $unblock=Get-Command Unblock-File -ErrorAction SilentlyContinue; if ($unblock -eq $null) { exit 3 }; foreach ($name in $names) { $path=Join-Path $env:TPF_ROOT $name; if (Test-Path -LiteralPath $path) { & $unblock -LiteralPath $path } }; Write-Host 'Package files unblocked.'" 2>nul
if errorlevel 3 (
    echo.
    echo PowerShell Unblock-File is not available. Trying direct Zone.Identifier stream clearing...
    for %%F in (TempProfileFixer.exe TempProfileFixer.exe.config TempProfileFixer.cmd Run-Diagnostics.cmd Unblock-Package.cmd README.md COMMAND-LINE.md SHA256SUMS.txt) do (
        if exist "%TPF_ROOT%\%%F" (
            dir /r "%TPF_ROOT%\%%F" 2>nul | "%FIND_EXE%" ":Zone.Identifier" >nul
            if not errorlevel 1 (
                type nul > "%TPF_ROOT%\%%F:Zone.Identifier"
            )
        )
    )
    echo Package files unblocked by clearing Zone.Identifier streams when present.
) else if errorlevel 1 (
    echo.
    echo Unblock failed. Run this from an elevated prompt or unblock the files from Properties.
    exit /b 2
)

echo.
echo Done. Run Run-Diagnostics.cmd next.
exit /b 0
