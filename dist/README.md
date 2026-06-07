# Temp Profile Fixer

[![CI](https://github.com/SlimQuiggle/TempProfileFixer/actions/workflows/ci.yml/badge.svg)](https://github.com/SlimQuiggle/TempProfileFixer/actions/workflows/ci.yml)

Temp Profile Fixer is a standalone Windows admin EXE for rebuilding a local
Windows user profile and cleaning the matching temporary-profile registry state
in one workflow.

The tool lists profile folders from `C:\Users`, relates each folder to its base
SID under:

```text
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList
```

For a selected profile, it exports the matching registry keys, renames the
profile folder to `.old<date>`, then removes the matching normal SID key and any
matching `.bak` key. After the rebuild it asks whether to reboot. The target user
should sign in after the next reboot so Windows creates a fresh profile.

The EXE uses an elevation manifest, so Windows prompts for administrator rights
when it starts.

If the app fails to open or fails on a specific workstation, run the
non-destructive diagnostics command from an elevated prompt:

```powershell
.\dist\TempProfileFixer.exe doctor
```

For another computer, use the latest GitHub release ZIP:
[TempProfileFixer-portable.zip](https://github.com/SlimQuiggle/TempProfileFixer/releases/latest).

## Build

Build the standalone executable with the .NET Framework compiler included with
Windows:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
```

The output is:

```text
dist\TempProfileFixer.exe
dist\TempProfileFixer.cmd
dist\Run-Diagnostics.cmd
dist\Unblock-Package.cmd
dist\TempProfileFixer.exe.config
dist\README.md
dist\COMMAND-LINE.md
dist\SHA256SUMS.txt
dist\TempProfileFixer-portable.zip
```

For another computer, copy `dist\TempProfileFixer-portable.zip` or download the
latest release ZIP, extract it locally, and run `Run-Diagnostics.cmd` first. The
ZIP keeps the EXE, runtime config, launchers, docs, and checksums together.

## Run

Open the GUI:

```powershell
.\dist\TempProfileFixer.exe
```

If a workstation will not launch the EXE directly, try the packaged launcher:

```powershell
.\dist\TempProfileFixer.cmd
```

The launcher checks for Microsoft .NET Framework 4.x Full before starting the
EXE, which gives a clearer error on older or stripped-down Windows builds.
For a double-clickable compatibility check that keeps the window open, use
`Run-Diagnostics.cmd`.

If Windows reports that the downloaded files are blocked, run
`Unblock-Package.cmd` after confirming the ZIP came from the official GitHub
release. It only removes the Windows download block from files in the extracted
tool folder.

Use the visible `Help / FAQ` button in the header, or `Help` > `Help / FAQ` in
the menu bar, for a formatted overview of each button, right-click actions,
blocked-profile reasons, reboot behavior, command-line usage, and backup/log
locations.

Right-click a profile row for actions:

- rebuild profile
- remove registry entry
- open profile path
- copy SID
- open to registry
- refresh

The large top-left `Rebuild Profile` button runs the full rebuild for the
highlighted profile: rename the folder to `.old<date>`, export/delete matching
ProfileList registry entries, then start the reboot after the rebuild succeeds.
It greys out when the highlighted profile is locked, loaded, current, or
otherwise blocked.

The red top `Delete Profile` button permanently deletes the highlighted profile
folder and removes its matching ProfileList registry entries. It uses the same
blocked-profile safety checks as rebuild and greys out for locked profiles.

## Command line

Run these commands from an elevated prompt. You can select a profile by
`--profile`, `--path`, or `--sid`.

List detected local profile folders and matched SIDs:

```powershell
.\dist\TempProfileFixer.exe list
```

Run compatibility diagnostics without changing profiles or registry keys:

```powershell
.\dist\TempProfileFixer.exe doctor
```

Preview the exact rename and registry actions for a profile folder:

```powershell
.\dist\TempProfileFixer.exe dry-run --path C:\Users\SomeUser
```

The same preview using the profile folder name:

```powershell
.\dist\TempProfileFixer.exe dry-run --profile SomeUser
```

Rebuild a profile from an elevated command prompt. The GUI starts reboot after a
successful rebuild; the CLI prompts unless `--reboot` is passed.

```powershell
.\dist\TempProfileFixer.exe rebuild --profile SomeUser
```

Skip the typed confirmation when automating:

```powershell
.\dist\TempProfileFixer.exe rebuild --profile SomeUser --yes
```

Rebuild and start the reboot automatically after success:

```powershell
.\dist\TempProfileFixer.exe rebuild --profile SomeUser --yes --reboot
```

Permanently delete a profile folder and matching ProfileList registry entries:

```powershell
.\dist\TempProfileFixer.exe delete-profile --profile SomeUser
```

Remove only the matching `.bak` key(s):

```powershell
.\dist\TempProfileFixer.exe remove-bak --profile SomeUser
```

Remove all matching ProfileList registry entries for a profile without renaming
the profile folder:

```powershell
.\dist\TempProfileFixer.exe remove-registry --profile SomeUser
```

Remote targeting is available with `--computer`. It uses the target computer's
admin share for profile folders, Remote Registry for `HKLM\...\ProfileList`, and
WMI to verify loaded/special profile state. The account running the command must
have admin rights on the target, and the target must allow those remote admin
access paths.

```powershell
.\dist\TempProfileFixer.exe doctor --computer PC-1234
.\dist\TempProfileFixer.exe list --computer PC-1234
.\dist\TempProfileFixer.exe dry-run --computer PC-1234 --profile SomeUser
.\dist\TempProfileFixer.exe rebuild --computer PC-1234 --profile SomeUser --yes --reboot
```

If the target uses a non-standard profile root, override both the filesystem
root and the registry-matching root:

```powershell
.\dist\TempProfileFixer.exe rebuild --computer PC-1234 --users-root \\PC-1234\D$\Users --profile-root D:\Users --profile SomeUser --yes
```

See `COMMAND-LINE.md` beside the EXE for a fuller command reference.

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

Delete-profile runs write registry backups under `backups\<timestamp>-<profile-folder>-delete\`
and logs under `logs\<timestamp>-<profile-folder>-delete.log`.

The tool writes under the first writable data folder it can use. It tries the
EXE folder first, then `C:\ProgramData\TempProfileFixer`, then the current user's
Temp folder. This avoids failures when the EXE is launched from a read-only share
or restricted folder.

The old profile folder is preserved as `C:\Users\<name>.old<yyyyMMdd-HHmmss>`.
If that path already exists, a numeric suffix is added.

## Troubleshooting

Run diagnostics first:

```powershell
.\dist\TempProfileFixer.exe doctor
```

The diagnostic report checks:

- administrator elevation
- readable `C:\Users` or remote users root
- readable `HKLM\...\ProfileList`
- `Win32_UserProfile` WMI access
- `reg.exe` and `shutdown.exe`
- writable backup/log storage

If the EXE was downloaded from GitHub and Windows blocks it, open the file
properties and use `Unblock`, or run it from an elevated PowerShell prompt after
confirming the file is trusted.
You can also run `Unblock-Package.cmd` from the extracted release folder after
confirming the package is trusted.

Temp Profile Fixer is built for the .NET Framework 4 runtime. Supported Windows
10/11 and current Windows Server builds normally include it. Older or stripped
down systems may need .NET Framework 4.x Full installed or enabled first.

## Validation

Run the lightweight tests:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tests\Run-SmokeTests.ps1
```

The public GitHub repository also runs the build and smoke tests on
`windows-latest` through GitHub Actions.
