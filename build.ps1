[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputFile,
    [string]$Version = '0.2.0'
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
$obj = Join-Path $repoRoot 'obj'
New-Item -Path $obj -ItemType Directory -Force | Out-Null

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use major.minor.patch format; received '$Version'."
}
$assemblyVersion = "$Version.0"
$generatedVersionSource = Join-Path $obj 'GeneratedVersionInfo.cs'
$versionSource = @(
    'using System.Reflection;',
    '[assembly: AssemblyTitle("Temp Profile Fixer")]',
    '[assembly: AssemblyProduct("Temp Profile Fixer")]',
    '[assembly: AssemblyCompany("Flex3Designs")]',
    "[assembly: AssemblyVersion(`"$assemblyVersion`")]",
    "[assembly: AssemblyFileVersion(`"$assemblyVersion`")]",
    "[assembly: AssemblyInformationalVersion(`"$Version`")]"
)
Set-Content -LiteralPath $generatedVersionSource -Value $versionSource -Encoding UTF8

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
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
$sources += $generatedVersionSource
$manifest = Join-Path $repoRoot 'TempProfileFixer.exe.manifest'
$appConfig = Join-Path $repoRoot 'TempProfileFixer.exe.config'
$launcher = Join-Path $repoRoot 'TempProfileFixer.cmd'
$diagnosticsLauncher = Join-Path $repoRoot 'Run-Diagnostics.cmd'
$unblockLauncher = Join-Path $repoRoot 'Unblock-Package.cmd'
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
    $sources
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
    Remove-Item -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $dist 'TempProfileFixer-portable.zip') -Force -ErrorAction SilentlyContinue

    Copy-Item -LiteralPath $readme -Destination (Join-Path $dist 'README.md') -Force
    Copy-Item -LiteralPath $commandLineReadme -Destination (Join-Path $dist 'COMMAND-LINE.md') -Force
    Copy-Item -LiteralPath $appConfig -Destination (Join-Path $dist 'TempProfileFixer.exe.config') -Force
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $dist 'TempProfileFixer.cmd') -Force
    Copy-Item -LiteralPath $diagnosticsLauncher -Destination (Join-Path $dist 'Run-Diagnostics.cmd') -Force
    Copy-Item -LiteralPath $unblockLauncher -Destination (Join-Path $dist 'Unblock-Package.cmd') -Force

    $packageFiles = @(
        'TempProfileFixer.exe',
        'TempProfileFixer.exe.config',
        'TempProfileFixer.cmd',
        'Run-Diagnostics.cmd',
        'Unblock-Package.cmd',
        'README.md',
        'COMMAND-LINE.md'
    )
    $checksumPath = Join-Path $dist 'SHA256SUMS.txt'
    $checksums = foreach ($fileName in $packageFiles) {
        $filePath = Join-Path $dist $fileName
        $hash = Get-FileHash -LiteralPath $filePath -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $fileName
    }
    Set-Content -LiteralPath $checksumPath -Value $checksums -Encoding ASCII

    $zipPath = Join-Path $dist 'TempProfileFixer-portable.zip'
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    $archivePaths = ($packageFiles + @('SHA256SUMS.txt')) | ForEach-Object { Join-Path $dist $_ }
    Compress-Archive -LiteralPath $archivePaths -DestinationPath $zipPath -Force
}

Write-Host "Built $outFile"
