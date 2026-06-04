# Temp Profile Fixer

Temp Profile Fixer is a Windows PowerShell 5.1 admin tool for rebuilding a local
Windows user profile and cleaning the matching temporary-profile registry state
in one workflow.

The tool lists profile folders from `C:\Users`, relates each folder to its base
SID under:

```text
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList
```

For a selected safe profile, it exports the matching registry keys, renames the
profile folder to `.old`, then removes the matching normal SID key and matching
`.bak` key. The next real sign-in for that user lets Windows create a fresh
profile without needing the admin to manually inspect ProfileList.

## Run

Open an elevated PowerShell prompt from this folder:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\TempProfileFixer.ps1
```

The GUI will relaunch itself elevated if it is started without administrator
rights.

## Non-destructive commands

List detected local profile folders and matched SIDs:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\TempProfileFixer.ps1 -ListOnly
```

Preview the exact rename and registry actions for a profile folder:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\TempProfileFixer.ps1 -DryRun -Path C:\Users\SomeUser
```

## Safety behavior

The app blocks rebuilds for profiles that are:

- the currently running admin profile
- currently loaded
- special/system profiles
- direct members of the local Administrators group, when detectable
- missing a matching ProfileList SID
- matched to multiple SIDs
- matched to multiple normal ProfileList keys

The app does not collect the target user's password and does not try to create a
fake interactive sign-in. Windows creates the fresh profile when the target user
signs in normally after the rebuild completes.

## Backups and logs

Before registry keys are deleted, the app exports matching ProfileList keys to:

```text
backups\<timestamp>-<profile-folder>\
```

Each rebuild writes a log to:

```text
logs\<timestamp>-<profile-folder>.log
```

The old profile folder is preserved as `C:\Users\<name>.old`. If that path
already exists, a timestamped `.old.<yyyyMMdd-HHmmss>` suffix is used instead.

## Validation

Run the lightweight tests:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tests\Run-Tests.ps1
```

Syntax-check the scripts:

```powershell
powershell.exe -NoProfile -Command "[scriptblock]::Create((Get-Content .\TempProfileFixer.ps1 -Raw)) > `$null; [scriptblock]::Create((Get-Content .\src\TempProfileFixer.Core.psm1 -Raw)) > `$null; [scriptblock]::Create((Get-Content .\tests\Run-Tests.ps1 -Raw)) > `$null"
```
