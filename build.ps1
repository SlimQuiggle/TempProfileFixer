[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputFile
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

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $outFile = Join-Path $dist 'TempProfileFixer.exe'
}
else {
    $outFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFile)
    $outDirectory = Split-Path -Parent $outFile
    if (-not [string]::IsNullOrWhiteSpace($outDirectory)) {
        New-Item -Path $outDirectory -ItemType Directory -Force | Out-Null
    }
}
$source = Join-Path $repoRoot 'src\TempProfileFixer.App.cs'
$manifest = Join-Path $repoRoot 'TempProfileFixer.exe.manifest'
$icon = Join-Path $repoRoot 'assets\TempProfileFixer.ico'
$png = Join-Path $repoRoot 'assets\TempProfileFixer.png'

if (-not (Test-Path -LiteralPath $icon) -or -not (Test-Path -LiteralPath $png)) {
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
    "/resource:$png,TempProfileFixer.Assets.TempProfileFixer.png",
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

$readme = Join-Path $repoRoot 'README.md'
$commandLineReadme = Join-Path $repoRoot 'COMMAND-LINE.md'
if ((Split-Path -Parent $outFile) -eq $dist) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $dist 'README.md') -Force
    Copy-Item -LiteralPath $commandLineReadme -Destination (Join-Path $dist 'COMMAND-LINE.md') -Force
}

Write-Host "Built $outFile"
