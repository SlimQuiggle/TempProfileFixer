@echo off
setlocal

set "TPF_EXE=%~dp0TempProfileFixer.exe"
if not exist "%TPF_EXE%" (
    echo Temp Profile Fixer could not find TempProfileFixer.exe beside this launcher.
    echo Expected: %TPF_EXE%
    exit /b 2
)

set "REG_EXE=%SystemRoot%\System32\reg.exe"
if not exist "%REG_EXE%" set "REG_EXE=reg.exe"

call :detect_dotnet4
if not defined TPF_HAS_DOTNET4 (
    echo Temp Profile Fixer requires Microsoft .NET Framework 4.x Full.
    echo Install or enable .NET Framework 4.x on this computer, then run this tool again.
    exit /b 2
)

:run_tool
"%TPF_EXE%" %*
exit /b %ERRORLEVEL%

:detect_dotnet4
for %%V in ("" "/reg:64" "/reg:32") do (
    "%REG_EXE%" query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release %%~V >nul 2>nul
    if not errorlevel 1 set "TPF_HAS_DOTNET4=1"
    if defined TPF_HAS_DOTNET4 exit /b 0

    "%REG_EXE%" query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Install %%~V >nul 2>nul
    if not errorlevel 1 set "TPF_HAS_DOTNET4=1"
    if defined TPF_HAS_DOTNET4 exit /b 0
)
exit /b 0
