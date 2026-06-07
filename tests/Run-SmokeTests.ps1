[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$obj = Join-Path $repoRoot 'obj'
New-Item -Path $obj -ItemType Directory -Force | Out-Null
$manifestExe = Join-Path $obj 'TempProfileFixer.ManifestSmoke.exe'
$exe = $manifestExe

& (Join-Path $repoRoot 'build.ps1') -OutputFile $manifestExe

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Expected EXE was not built: $exe"
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $framework64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $framework32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    if (Test-Path -LiteralPath $framework64) {
        $csc = $framework64
    }
    elseif (Test-Path -LiteralPath $framework32) {
        $csc = $framework32
    }
    else {
        throw 'Could not find csc.exe for smoke-test build.'
    }

    $smokeExe = Join-Path $obj 'TempProfileFixer.Smoke.exe'
    & $csc /nologo /target:exe /platform:anycpu /optimize+ "/out:$smokeExe" `
        "/resource:$(Join-Path $repoRoot 'assets\TempProfileFixer.png'),TempProfileFixer.Assets.TempProfileFixer.png" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Management.dll `
        /reference:System.Windows.Forms.dll `
        (Join-Path $repoRoot 'src\TempProfileFixer.App.cs')
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke-test compile failed with exit code $LASTEXITCODE."
    }

    $exe = $smokeExe
}

$listOutput = & $exe list 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "list command failed: $listOutput"
}
if (($listOutput -join "`n") -notmatch 'ProfilePath') {
    throw 'list command did not print the expected header.'
}

$helpOutput = & $exe help 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "help command failed: $helpOutput"
}
if (($helpOutput -join "`n") -notmatch 'delete-profile') {
    throw 'help command did not advertise delete-profile.'
}
if (($helpOutput -join "`n") -notmatch '--computer') {
    throw 'help command did not advertise remote computer targeting.'
}
if (($helpOutput -join "`n") -notmatch '--profile') {
    throw 'help command did not advertise profile-name targeting.'
}
if (($helpOutput -join "`n") -notmatch 'doctor') {
    throw 'help command did not advertise diagnostics.'
}

$doctorOutput = & $exe doctor 2>&1
$doctorExit = $LASTEXITCODE
if ($doctorExit -ne 0 -and $doctorExit -ne 1) {
    throw "doctor command returned an unexpected exit code ${doctorExit}: $doctorOutput"
}
if (($doctorOutput -join "`n") -notmatch 'compatibility diagnostics') {
    throw 'doctor command did not print the expected diagnostics heading.'
}
if (($doctorOutput -join "`n") -notmatch 'ProfileList registry') {
    throw 'doctor command did not check ProfileList registry access.'
}
if (($doctorOutput -join "`n") -match 'C:Users') {
    throw 'doctor command used a drive-relative C:Users path instead of C:\Users.'
}

$dryRunOutput = & $exe dry-run --path $env:USERPROFILE 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "dry-run command failed: $dryRunOutput"
}
if (($dryRunOutput -join "`n") -notmatch 'Blocked') {
    throw 'dry-run for the current profile should be blocked.'
}
if (($dryRunOutput -join "`n") -notmatch '\.old\d{8}-\d{6}') {
    throw 'dry-run did not show the expected .old<date> rename target.'
}

$profileName = Split-Path -Leaf $env:USERPROFILE
$profileDryRunOutput = & $exe dry-run --profile $profileName 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "dry-run by profile name failed: $profileDryRunOutput"
}
if (($profileDryRunOutput -join "`n") -notmatch 'Blocked') {
    throw 'dry-run by profile name for the current profile should be blocked.'
}

$assembly = [Reflection.Assembly]::LoadFrom($exe)
$profileServiceType = $assembly.GetType('TempProfileFixer.ProfileService', $true)
$normalizeRegistryMethod = $profileServiceType.GetMethod(
    'NormalizeRegistryProfilePath',
    [Reflection.BindingFlags] 'Public, Static')
if ($null -eq $normalizeRegistryMethod) {
    throw 'ProfileService.NormalizeRegistryProfilePath was not found.'
}
$normalizedRemoteRegistryPath = $normalizeRegistryMethod.Invoke($null, @('%SystemDrive%\Users\RemoteUser', 'D:\Users'))
if ($normalizedRemoteRegistryPath -ne 'D:\USERS\REMOTEUSER') {
    throw "Registry ProfileImagePath normalization used the wrong system drive: $normalizedRemoteRegistryPath"
}
$normalizedAdminShareRegistryPath = $normalizeRegistryMethod.Invoke($null, @('%SystemDrive%\Users\RemoteUser', '\\PC-1234\E$\Users'))
if ($normalizedAdminShareRegistryPath -ne 'E:\USERS\REMOTEUSER') {
    throw "Registry ProfileImagePath normalization did not infer drive from admin share: $normalizedAdminShareRegistryPath"
}
$normalizedLiteralRegistryPath = $normalizeRegistryMethod.Invoke($null, @('E:\Profiles\RemoteUser', 'D:\Users'))
if ($normalizedLiteralRegistryPath -ne 'E:\PROFILES\REMOTEUSER') {
    throw "Literal registry ProfileImagePath normalization changed unexpectedly: $normalizedLiteralRegistryPath"
}

foreach ($packagedFile in @('README.md', 'COMMAND-LINE.md', 'TempProfileFixer.exe.config', 'TempProfileFixer.cmd', 'Run-Diagnostics.cmd', 'Unblock-Package.cmd', 'SHA256SUMS.txt', 'TempProfileFixer-portable.zip')) {
    $packagedPath = Join-Path $repoRoot (Join-Path 'dist' $packagedFile)
    if (-not (Test-Path -LiteralPath $packagedPath)) {
        throw "Expected packaged file is missing: $packagedPath"
    }
}

$checksumText = Get-Content -LiteralPath (Join-Path $repoRoot 'dist\SHA256SUMS.txt') -Raw
foreach ($checksumFile in @('TempProfileFixer.exe', 'TempProfileFixer.exe.config', 'TempProfileFixer.cmd', 'Run-Diagnostics.cmd', 'Unblock-Package.cmd', 'README.md', 'COMMAND-LINE.md')) {
    if ($checksumText -notmatch [regex]::Escape($checksumFile)) {
        throw "Checksum file does not include $checksumFile."
    }
}

$unblockText = Get-Content -LiteralPath (Join-Path $repoRoot 'dist\Unblock-Package.cmd') -Raw
if ($unblockText -notmatch 'Unblock-File') {
    throw 'Unblock-Package.cmd no longer tries the standard Unblock-File path.'
}
if ($unblockText -notmatch 'Zone\.Identifier') {
    throw 'Unblock-Package.cmd does not include the older PowerShell Zone.Identifier fallback.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $repoRoot 'dist\TempProfileFixer-portable.zip'
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $zipNames = @($zip.Entries | ForEach-Object { $_.FullName })
    foreach ($zipFile in @('TempProfileFixer.exe', 'TempProfileFixer.exe.config', 'TempProfileFixer.cmd', 'Run-Diagnostics.cmd', 'Unblock-Package.cmd', 'README.md', 'COMMAND-LINE.md', 'SHA256SUMS.txt')) {
        if ($zipNames -notcontains $zipFile) {
            throw "Portable ZIP does not include $zipFile."
        }
    }
}
finally {
    $zip.Dispose()
}

$extractRoot = Join-Path $obj 'portable extract with spaces'
if (Test-Path -LiteralPath $extractRoot) {
    Remove-Item -LiteralPath $extractRoot -Recurse -Force
}
New-Item -Path $extractRoot -ItemType Directory -Force | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
foreach ($zipFile in @('TempProfileFixer.exe', 'TempProfileFixer.exe.config', 'TempProfileFixer.cmd', 'Run-Diagnostics.cmd', 'Unblock-Package.cmd', 'README.md', 'COMMAND-LINE.md', 'SHA256SUMS.txt')) {
    $extractedPath = Join-Path $extractRoot $zipFile
    if (-not (Test-Path -LiteralPath $extractedPath)) {
        throw "Portable ZIP extraction did not produce $zipFile."
    }
}

Write-Host 'Smoke tests passed.'
