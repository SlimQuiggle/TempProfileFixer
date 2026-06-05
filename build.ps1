[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$framework64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'

if (Test-Path -LiteralPath $framework64) {
    $csc = $framework64
}
elseif (Test-Path -LiteralPath $framework32) {
    $csc = $framework32
}
else {
    throw 'Could not find the .NET Framework C# compiler under C:\Windows\Microsoft.NET.'
}

$dist = Join-Path $repoRoot 'dist'
New-Item -Path $dist -ItemType Directory -Force | Out-Null

$outFile = Join-Path $dist 'TempProfileFixer.exe'
$source = Join-Path $repoRoot 'src\TempProfileFixer.App.cs'
$manifest = Join-Path $repoRoot 'TempProfileFixer.exe.manifest'
$icon = Join-Path $repoRoot 'assets\TempProfileFixer.ico'

if (-not (Test-Path -LiteralPath $icon)) {
    & (Join-Path $repoRoot 'tools\Generate-Icon.ps1')
}

$args = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    "/out:$outFile",
    "/win32manifest:$manifest",
    "/win32icon:$icon",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Management.dll',
    '/reference:System.Windows.Forms.dll',
    $source
)

if ($Configuration -ieq 'Debug') {
    $args = $args | Where-Object { $_ -ne '/optimize+' }
    $args += '/debug+'
}

& $csc @args
if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE."
}

Write-Host "Built $outFile"
