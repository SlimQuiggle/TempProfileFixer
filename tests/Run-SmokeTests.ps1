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

$manifestText = Get-Content -LiteralPath (Join-Path $repoRoot 'TempProfileFixer.exe.manifest') -Raw
if ($manifestText -notmatch 'requestedExecutionLevel level="asInvoker"') {
    throw 'The application manifest must allow non-destructive diagnostics to start without pre-elevation.'
}
if ($manifestText -match 'requireAdministrator') {
    throw 'The application manifest should not require UAC before help or diagnostics can run.'
}

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.cs' -File | Sort-Object Name)
$sourceText = ($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
if ($sourceText -notmatch 'Skipped unreadable ProfileList key') {
    throw 'ProfileList enumeration should report skipped unreadable subkeys instead of failing the whole scan.'
}
if ($sourceText -notmatch 'AddWarning\("ProfileList registry"') {
    throw 'Diagnostics should report partial ProfileList registry reads as warnings.'
}
if ($sourceText -notmatch 'Skipped unreadable Win32_UserProfile row') {
    throw 'Win32_UserProfile enumeration should report skipped unreadable rows instead of failing the whole state scan.'
}
if ($sourceText -notmatch 'AddWarning\("Win32_UserProfile"') {
    throw 'Diagnostics should report partial Win32_UserProfile reads as warnings.'
}
if ($sourceText -notmatch 'Win32_UserProfile query warning') {
    throw 'Profile inventory should surface partial Win32_UserProfile warnings.'
}
if ($sourceText -notmatch 'FileAttributes\.ReparsePoint') {
    throw 'Profile deletion should detect directory reparse points instead of recursing through them.'
}
if ($sourceText -match 'GetFiles\("\*", SearchOption\.AllDirectories\)') {
    throw 'Profile deletion should not recursively enumerate files with SearchOption.AllDirectories.'
}
if ($sourceText -match 'GetDirectories\("\*", SearchOption\.AllDirectories\)') {
    throw 'Profile deletion should not recursively enumerate directories with SearchOption.AllDirectories.'
}
if ($sourceText -notmatch 'SafeGetTempPath') {
    throw 'Diagnostic storage should tolerate a broken Temp environment path.'
}
if ($sourceText -match 'Path\.Combine\(Environment\.GetFolderPath\(Environment\.SpecialFolder\.System\)') {
    throw 'System tool lookup should not depend on a single unguarded GetFolderPath(System) call.'
}
if ($sourceText -notmatch 'IsFolderOnlyDeleteAllowedBlockReason') {
    throw 'Delete Profile should allow folder-only cleanup when the only block reason is a missing ProfileList SID.'
}
if ($sourceText -notmatch 'No matching ProfileList registry key; only the profile folder will be deleted') {
    throw 'Folder-only profile deletion should warn when no registry key is matched.'
}
if ($sourceText -notmatch 'GetCurrentProfilePath') {
    throw 'Inventory should protect the current admin profile by path even when SID matching is missing.'
}
if ($sourceText -notmatch 'BackgroundWorker') {
    throw 'GUI work should run through the background-work controller.'
}
if ($sourceText -notmatch 'AutoScaleMode\.Dpi') {
    throw 'GUI should remain DPI aware.'
}
if ($sourceText -notmatch 'RebootCountdownDialog' -or $sourceText -notmatch 'AbortReboot') {
    throw 'GUI should provide a cancelable reboot countdown.'
}
if ($sourceText -notmatch 'CreateBakRemovalPlan') {
    throw '.bak removal should use the centralized safety plan.'
}
if (Test-Path -LiteralPath (Join-Path $repoRoot 'src\TempProfileFixer.App.cs')) {
    throw 'The monolithic application source should remain split by subsystem.'
}
if ($sourceFiles.Count -lt 6) {
    throw 'Expected the application source to be split into subsystem files.'
}
$runMethodStart = $sourceText.IndexOf('public static int Run(string[] args)', [StringComparison]::Ordinal)
$helpCommandIndex = $sourceText.IndexOf('IsHelpCommand(command)', $runMethodStart, [StringComparison]::Ordinal)
$parseArgsIndex = $sourceText.IndexOf('ParsedArgs.Parse', $runMethodStart, [StringComparison]::Ordinal)
if ($runMethodStart -lt 0 -or $helpCommandIndex -lt 0 -or $parseArgsIndex -lt 0 -or $helpCommandIndex -gt $parseArgsIndex) {
    throw 'Command-line help should be printed before parsing target arguments.'
}
if ($sourceText -notmatch 'IsKnownCommand\(command\)') {
    throw 'Unknown commands should print usage before building a profile target.'
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
        @($sourceFiles.FullName + (Join-Path $obj 'GeneratedVersionInfo.cs'))
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

$versionOutput = & $exe version 2>&1
if ($LASTEXITCODE -ne 0 -or ($versionOutput -join "`n").Trim() -ne '0.2.0') {
    throw "version command did not report 0.2.0: $versionOutput"
}

$helpWithTargetOutput = & $exe help --computer 'PC-DOES-NOT-NEED-TO-EXIST' --users-root '\\PC-DOES-NOT-NEED-TO-EXIST\Z$\Users' 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "help command should ignore target arguments and print usage: $helpWithTargetOutput"
}

$unknownOutput = & $exe definitely-not-a-command --computer 'PC-DOES-NOT-NEED-TO-EXIST' 2>&1
$unknownExit = $LASTEXITCODE
if ($unknownExit -ne 1) {
    throw "unknown command returned an unexpected exit code ${unknownExit}: $unknownOutput"
}
if (($unknownOutput -join "`n") -notmatch 'Commands:') {
    throw 'unknown command did not print usage.'
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
if (($doctorOutput -join "`n") -notmatch 'Application version - 0.2.0') {
    throw 'doctor command did not report application version 0.2.0.'
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
$appDiagnosticsType = $assembly.GetType('TempProfileFixer.AppDiagnostics', $true)
$getSystemToolPathMethod = $appDiagnosticsType.GetMethod('GetSystemToolPath', [Reflection.BindingFlags] 'Public, Static')
$regToolPath = [string]$getSystemToolPathMethod.Invoke($null, @('reg.exe'))
if ([string]::IsNullOrWhiteSpace($regToolPath) -or (Split-Path -Leaf $regToolPath) -ne 'reg.exe') {
    throw "System tool resolver returned an invalid reg.exe path: $regToolPath"
}
if (-not (Test-Path -LiteralPath $regToolPath)) {
    throw "System tool resolver returned a reg.exe path that does not exist: $regToolPath"
}

$compatibilityReportType = $assembly.GetType('TempProfileFixer.CompatibilityReport', $true)
$warningReport = [Activator]::CreateInstance($compatibilityReportType)
$compatibilityReportType.GetMethod('AddWarning').Invoke($warningReport, @('ProfileList registry', 'Skipped unreadable ProfileList key(s): S-1-test'))
$warningReportText = $compatibilityReportType.GetMethod('ToDisplayText').Invoke($warningReport, @())
if ($compatibilityReportType.GetProperty('HasIssues').GetValue($warningReport, $null) -ne $true) {
    throw 'CompatibilityReport warnings should make doctor return a nonzero issue state.'
}
if ($compatibilityReportType.GetProperty('HasFailures').GetValue($warningReport, $null) -ne $false) {
    throw 'CompatibilityReport warnings should not be reported as failures.'
}
if ($warningReportText -notmatch 'completed with warnings') {
    throw 'CompatibilityReport warning summary should not say all checks passed.'
}

$profileServiceType = $assembly.GetType('TempProfileFixer.ProfileService', $true)
$profileRecordType = $assembly.GetType('TempProfileFixer.ProfileRecord', $true)
$createDeleteProfilePlanMethod = $profileServiceType.GetMethod('CreateDeleteProfilePlan', [Reflection.BindingFlags] 'Public, Static')
$stringListType = [System.Collections.Generic.List[string]]
$folderOnlyProfile = [Activator]::CreateInstance($profileRecordType)
$profileRecordType.GetProperty('FolderName').SetValue($folderOnlyProfile, 'Temp.NoSid', $null)
$profileRecordType.GetProperty('ProfilePath').SetValue($folderOnlyProfile, 'C:\Users\Temp.NoSid', $null)
$profileRecordType.GetProperty('RegistryRoot').SetValue($folderOnlyProfile, 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList', $null)
$profileRecordType.GetProperty('BaseSid').SetValue($folderOnlyProfile, '', $null)
$profileRecordType.GetProperty('NormalKeyNames').SetValue($folderOnlyProfile, (New-Object $stringListType), $null)
$profileRecordType.GetProperty('BakKeyNames').SetValue($folderOnlyProfile, (New-Object $stringListType), $null)
$folderOnlyBlockReasons = New-Object $stringListType
[void]$folderOnlyBlockReasons.Add('No matching ProfileList SID')
$profileRecordType.GetProperty('BlockReasons').SetValue($folderOnlyProfile, $folderOnlyBlockReasons, $null)
$profileRecordType.GetProperty('Warnings').SetValue($folderOnlyProfile, (New-Object $stringListType), $null)
$folderOnlyDeletePlan = $createDeleteProfilePlanMethod.Invoke($null, @($folderOnlyProfile))
$deletePlanType = $folderOnlyDeletePlan.GetType()
if ($deletePlanType.GetProperty('IsBlocked').GetValue($folderOnlyDeletePlan, $null) -ne $false) {
    throw 'Delete Profile should not be blocked for a folder-only temp profile with no matching SID.'
}
$folderOnlyDeletePlanText = $deletePlanType.GetMethod('ToDisplayText').Invoke($folderOnlyDeletePlan, @())
if ($folderOnlyDeletePlanText -notmatch 'No matching ProfileList key was found' -or $folderOnlyDeletePlanText -notmatch '\(none found\)') {
    throw 'Folder-only delete plan should clearly state that no ProfileList key was found.'
}

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

$parsedArgsType = $assembly.GetType('TempProfileFixer.ParsedArgs', $true)
$profileTargetType = $assembly.GetType('TempProfileFixer.ProfileTarget', $true)
$parseArgsMethod = $parsedArgsType.GetMethod('Parse', [Reflection.BindingFlags] 'Public, Static')
$fromParsedArgsMethod = $profileTargetType.GetMethod('FromParsedArgs', [Reflection.BindingFlags] 'Public, Static')
$profileImageRootProperty = $profileTargetType.GetProperty('ProfileImageRoot')
$usersRootProperty = $profileTargetType.GetProperty('UsersRoot')

[string[]]$localUsersRootArgs = @('--users-root', 'D:\Users')
$parsedLocalUsersRoot = $parseArgsMethod.Invoke($null, [object[]](,$localUsersRootArgs))
$localUsersRootTarget = $fromParsedArgsMethod.Invoke($null, [object[]]@($parsedLocalUsersRoot))
if ($profileImageRootProperty.GetValue($localUsersRootTarget, $null) -ne 'D:\Users') {
    throw 'Profile root was not inferred from local --users-root.'
}

[string[]]$remoteUsersRootArgs = @('--computer', 'PC-1234', '--users-root', '\\PC-1234\E$\Users')
$parsedRemoteUsersRoot = $parseArgsMethod.Invoke($null, [object[]](,$remoteUsersRootArgs))
$remoteUsersRootTarget = $fromParsedArgsMethod.Invoke($null, [object[]]@($parsedRemoteUsersRoot))
if ($profileImageRootProperty.GetValue($remoteUsersRootTarget, $null) -ne 'E:\Users') {
    throw 'Profile root was not inferred from remote admin-share --users-root.'
}
if ($usersRootProperty.GetValue($remoteUsersRootTarget, $null) -ne '\\PC-1234\E$\Users') {
    throw 'Remote users root changed unexpectedly while inferring profile root.'
}

[string[]]$explicitProfileRootArgs = @('--users-root', 'D:\Users', '--profile-root', 'E:\Profiles')
$parsedExplicitProfileRoot = $parseArgsMethod.Invoke($null, [object[]](,$explicitProfileRootArgs))
$explicitProfileRootTarget = $fromParsedArgsMethod.Invoke($null, [object[]]@($parsedExplicitProfileRoot))
if ($profileImageRootProperty.GetValue($explicitProfileRootTarget, $null) -ne 'E:\Profiles') {
    throw 'Explicit --profile-root should override --users-root inference.'
}

foreach ($packagedFile in @('README.md', 'COMMAND-LINE.md', 'TempProfileFixer.exe.config', 'TempProfileFixer.cmd', 'Run-Diagnostics.cmd', 'Unblock-Package.cmd', 'SHA256SUMS.txt', 'TempProfileFixer-portable.zip')) {
    $packagedPath = Join-Path $repoRoot (Join-Path 'dist' $packagedFile)
    if (-not (Test-Path -LiteralPath $packagedPath)) {
        throw "Expected packaged file is missing: $packagedPath"
    }
}

$standaloneRoot = Join-Path $obj 'standalone exe with spaces'
if (Test-Path -LiteralPath $standaloneRoot) {
    Remove-Item -LiteralPath $standaloneRoot -Recurse -Force
}
New-Item -Path $standaloneRoot -ItemType Directory -Force | Out-Null
$standaloneExe = Join-Path $standaloneRoot 'TempProfileFixer.exe'
Copy-Item -LiteralPath (Join-Path $repoRoot 'dist\TempProfileFixer.exe') -Destination $standaloneExe -Force
$standaloneHelpOutput = & $standaloneExe help 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "standalone EXE help failed without companion files: $standaloneHelpOutput"
}
if (($standaloneHelpOutput -join "`n") -notmatch 'TempProfileFixer\.exe') {
    throw 'standalone EXE did not print help output without companion files.'
}

$diagnosticsLauncherText = Get-Content -LiteralPath (Join-Path $repoRoot 'dist\Run-Diagnostics.cmd') -Raw
if ($diagnosticsLauncherText -notmatch 'failures or warnings') {
    throw 'Run-Diagnostics.cmd should describe nonzero doctor results as failures or warnings.'
}

$checksumText = Get-Content -LiteralPath (Join-Path $repoRoot 'dist\SHA256SUMS.txt') -Raw
foreach ($checksumFile in @('TempProfileFixer.exe', 'TempProfileFixer.exe.config', 'TempProfileFixer.cmd', 'Run-Diagnostics.cmd', 'Unblock-Package.cmd', 'README.md', 'COMMAND-LINE.md')) {
    if ($checksumText -notmatch [regex]::Escape($checksumFile)) {
        throw "Checksum file does not include $checksumFile."
    }
}
$checksumEntries = @{}
foreach ($line in ($checksumText -split "`r?`n")) {
    if ($line -match '^([0-9a-fA-F]{64})\s{2}(.+)$') {
        $checksumEntries[$Matches[2]] = $Matches[1].ToLowerInvariant()
    }
}
foreach ($checksumFile in $checksumEntries.Keys) {
    $checksumTarget = Join-Path $repoRoot (Join-Path 'dist' $checksumFile)
    $actualHash = (Get-FileHash -LiteralPath $checksumTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $checksumEntries[$checksumFile]) {
        throw "Checksum mismatch for $checksumFile."
    }
}

$unblockText = Get-Content -LiteralPath (Join-Path $repoRoot 'dist\Unblock-Package.cmd') -Raw
if ($unblockText -notmatch 'Unblock-File') {
    throw 'Unblock-Package.cmd no longer tries the standard Unblock-File path.'
}
if ($unblockText -notmatch 'Zone\.Identifier') {
    throw 'Unblock-Package.cmd does not include the older PowerShell Zone.Identifier fallback.'
}
if ($unblockText -notmatch '%SYSTEM32%\\choice\.exe') {
    throw 'Unblock-Package.cmd should use the absolute system choice.exe path for broken PATH environments.'
}
if ($unblockText -notmatch '%SYSTEM32%\\find\.exe') {
    throw 'Unblock-Package.cmd should use the absolute system find.exe path for broken PATH environments.'
}
if ($unblockText -notmatch '%SYSTEM32%\\WindowsPowerShell\\v1\.0\\powershell\.exe') {
    throw 'Unblock-Package.cmd should use the absolute system PowerShell path when available.'
}

$unblockPath = Join-Path $repoRoot 'dist\Unblock-Package.cmd'
$unblockEmptyPathOutput = & $env:ComSpec /d /c "set PATH=& echo Y|`"$unblockPath`"" 2>&1
$unblockEmptyPathExit = $LASTEXITCODE
if ($unblockEmptyPathExit -ne 0) {
    throw "Unblock-Package.cmd failed with PATH emptied: $unblockEmptyPathOutput"
}
if (($unblockEmptyPathOutput -join "`n") -notmatch 'Package files unblocked') {
    throw 'Unblock-Package.cmd did not report success with PATH emptied.'
}

$launcherText = Get-Content -LiteralPath (Join-Path $repoRoot 'dist\TempProfileFixer.cmd') -Raw
if ($launcherText -notmatch '%SystemRoot%\\System32\\reg\.exe') {
    throw 'TempProfileFixer.cmd should use the absolute system reg.exe path for broken PATH environments.'
}
if ($launcherText -notmatch '/reg:64' -or $launcherText -notmatch '/reg:32') {
    throw 'TempProfileFixer.cmd should check both .NET registry views.'
}

$launcherPath = Join-Path $repoRoot 'dist\TempProfileFixer.cmd'
$launcherHelpOutput = & $env:ComSpec /d /c "set PATH=& `"$launcherPath`" help" 2>&1
$launcherHelpExit = $LASTEXITCODE
if ($launcherHelpExit -ne 0) {
    throw "TempProfileFixer.cmd help failed with PATH emptied: $launcherHelpOutput"
}
if (($launcherHelpOutput -join "`n") -notmatch 'TempProfileFixer\.exe') {
    throw 'TempProfileFixer.cmd did not pass help through to the EXE when PATH was empty.'
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
