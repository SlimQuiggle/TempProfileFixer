# Temp Profile Fixer

Temp Profile Fixer is a standalone Windows admin EXE for rebuilding a local
Windows user profile and cleaning the matching temporary-profile registry state
in one workflow.

The tool lists profile folders from `C:\Users`, relates each folder to its base
SID under:

```text
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList
```

For a selected profile, it exports the matching registry keys, renames the
profile folder to `.old`, then removes the matching normal SID key and any
matching `.bak` key. After the rebuild it asks whether to reboot. The target user
should sign in after the next reboot so Windows creates a fresh profile.

The EXE uses an elevation manifest, so Windows prompts for administrator rights
when it starts.

## Build

Build the standalone executable with the .NET Framework compiler included with
Windows:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
```

The output is:

```text
dist\TempProfileFixer.exe
```

## Run

Open the GUI:

```powershell
.\dist\TempProfileFixer.exe
```

Right-click a profile row for actions:

- dry run / show plan
- rebuild profile
- remove `.bak` key(s)
- copy profile path
- copy base SID
- open profile folder

## Command line

List detected local profile folders and matched SIDs:

```powershell
.\dist\TempProfileFixer.exe list
```

Preview the exact rename and registry actions for a profile folder:

```powershell
.\dist\TempProfileFixer.exe dry-run --path C:\Users\SomeUser
```

Rebuild a profile from an elevated command prompt:

```powershell
.\dist\TempProfileFixer.exe rebuild --path C:\Users\SomeUser
```

Skip the typed confirmation when automating:

```powershell
.\dist\TempProfileFixer.exe rebuild --path C:\Users\SomeUser --yes
```

Remove only the matching `.bak` key(s):

```powershell
.\dist\TempProfileFixer.exe remove-bak --path C:\Users\SomeUser
```

## Safety behavior

The app blocks rebuilds for profiles that are:

- the currently running admin profile
- currently loaded
- special/system profiles
- missing a matching ProfileList SID
- matched to multiple SIDs
- matched to multiple normal ProfileList keys
- unable to verify loaded state through `Win32_UserProfile`

The app does not collect the target user's password and does not try to create a
fake interactive sign-in. Windows creates the fresh profile when the target user
signs in normally after the next reboot.

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
powershell.exe -ExecutionPolicy Bypass -File .\tests\Run-SmokeTests.ps1
```
