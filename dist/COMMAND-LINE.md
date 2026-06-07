# Temp Profile Fixer Command Line

Run `TempProfileFixer.exe` from a command prompt. The GUI is opened only when no
command is supplied and will request administrator rights when needed. Rebuild,
delete, and registry-removal commands must be run from an elevated administrator
prompt. Help, list, diagnostics, and dry-run modes are non-destructive and can
run without pre-elevation.

If the EXE will not launch on a workstation, run `TempProfileFixer.cmd` from the
same folder. It checks for Microsoft .NET Framework 4.x Full and then starts the
EXE with the same arguments. The launcher uses the system `reg.exe` path
directly and checks both registry views so it can still work when the
workstation's `PATH` is damaged.

For the most reliable transfer to another computer, use
`TempProfileFixer-portable.zip` from the latest GitHub release:
https://github.com/SlimQuiggle/TempProfileFixer/releases/latest. Extract the ZIP
locally and run `Run-Diagnostics.cmd` before rebuilding a profile. That helper
keeps the window open so the diagnostics output can be read after a double-click
launch.
If Windows blocks the extracted files, run `Unblock-Package.cmd` only after
confirming the ZIP came from the trusted GitHub release. The helper uses
`Unblock-File` when available and falls back to clearing `Zone.Identifier`
download-block streams on older PowerShell versions. It uses system helper paths
directly so it can still run when the workstation's `PATH` is damaged.

## Profile selection

Use one of these selectors with commands that operate on a single profile:

```powershell
--profile SomeUser
--path C:\Users\SomeUser
--sid S-1-5-21-1111111111-2222222222-3333333333-1001
```

`--profile` is usually the simplest option. It selects a folder under the target
users root, which defaults to `C:\Users` locally or `\\COMPUTER\C$\Users` when
`--computer` is used.

## Local examples

List local profile folders and matched base SIDs:

```powershell
.\TempProfileFixer.exe list
```

Run compatibility diagnostics without changing profiles or registry keys:

```powershell
.\TempProfileFixer.exe doctor
```

`doctor` exits nonzero for failures or warnings so remote tools can detect a
machine that needs attention before a rebuild. A full `Win32_UserProfile` WMI
failure blocks profile actions because loaded state cannot be verified. A small
number of unreadable WMI rows is reported as a warning so the rest of the
inventory can still load and show the affected machine state.

Preview the rebuild plan without changing anything:

```powershell
.\TempProfileFixer.exe dry-run --profile SomeUser
```

Rebuild a local profile, skip the typed confirmation, and prompt for reboot:

```powershell
.\TempProfileFixer.exe rebuild --profile SomeUser --yes
```

Rebuild a local profile and start reboot automatically after success:

```powershell
.\TempProfileFixer.exe rebuild --profile SomeUser --yes --reboot
```

Delete a profile folder and matching ProfileList entries:

```powershell
.\TempProfileFixer.exe delete-profile --profile SomeUser --yes
```

Remove only matching registry entries without renaming or deleting the folder:

```powershell
.\TempProfileFixer.exe remove-registry --profile SomeUser --yes
```

Remove only matching `.bak` keys:

```powershell
.\TempProfileFixer.exe remove-bak --profile SomeUser --yes
```

## Remote targeting

Use `--computer PCNAME` to target another workstation from an elevated admin
prompt. This mode uses:

- `\\PCNAME\C$\Users` for profile folder rename/delete work
- remote `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList`
- remote WMI `Win32_UserProfile` to verify loaded and special profile state
- `shutdown.exe /m \\PCNAME` when `--reboot` is supplied

The account running the command must have admin rights on the target computer.
The target must allow admin share, Remote Registry, and WMI access. If the tool
cannot verify loaded profile state, the profile is blocked instead of modified.

List profiles on a remote workstation:

```powershell
.\TempProfileFixer.exe list --computer PC-1234
```

Run remote compatibility diagnostics:

```powershell
.\TempProfileFixer.exe doctor --computer PC-1234
```

Preview a remote rebuild:

```powershell
.\TempProfileFixer.exe dry-run --computer PC-1234 --profile SomeUser
```

Rebuild a remote profile and reboot the remote workstation after success:

```powershell
.\TempProfileFixer.exe rebuild --computer PC-1234 --profile SomeUser --yes --reboot
```

Run by explicit path. With `--computer`, a `C:\Users\...` path is mapped to the
target admin share for filesystem work:

```powershell
.\TempProfileFixer.exe rebuild --computer PC-1234 --path C:\Users\SomeUser --yes
```

Target a non-standard users root. The registry `ProfileImagePath` root is
inferred from `--users-root` unless `--profile-root` is supplied explicitly:

```powershell
.\TempProfileFixer.exe rebuild --computer PC-1234 --users-root \\PC-1234\D$\Users --profile SomeUser --yes
```

## Remote execution tools

You can also copy the EXE to a workstation and run it there through your normal
remote management tool. In that case, omit `--computer` because the process is
already running on the target machine.

Example PsExec-style local-on-target run:

```powershell
psexec \\PC-1234 -h C:\Temp\TempProfileFixer.exe rebuild --profile SomeUser --yes --reboot
```

Example PowerShell remoting run after copying the EXE to the target:

```powershell
Invoke-Command -ComputerName PC-1234 -ScriptBlock {
    C:\Temp\TempProfileFixer.exe rebuild --profile SomeUser --yes --reboot
}
```

## Commands and switches

```text
TempProfileFixer.exe list [--computer PCNAME] [--users-root PATH] [--profile-root PATH]
TempProfileFixer.exe doctor [--computer PCNAME] [--users-root PATH] [--profile-root PATH]
TempProfileFixer.exe dry-run (--profile NAME | --path PATH | --sid SID) [--computer PCNAME]
TempProfileFixer.exe rebuild (--profile NAME | --path PATH | --sid SID) [--computer PCNAME] [--yes] [--reboot] [--no-reboot-prompt]
TempProfileFixer.exe delete-profile (--profile NAME | --path PATH | --sid SID) [--computer PCNAME] [--yes]
TempProfileFixer.exe remove-registry (--profile NAME | --path PATH | --sid SID) [--computer PCNAME] [--yes]
TempProfileFixer.exe remove-bak (--profile NAME | --path PATH | --sid SID) [--computer PCNAME] [--yes]
```

Useful switches:

- `--yes`: skip the typed confirmation for automation.
- `--reboot`: start reboot automatically after a successful rebuild.
- `--no-reboot-prompt`: do not prompt for reboot after rebuild.
- `--computer PCNAME`: target a remote workstation from the current machine.
- `--users-root PATH`: override the profile folder root used for filesystem work. When `--profile-root` is omitted, the registry matching root is inferred from this path.
- `--profile-root PATH`: explicitly override the root used to match registry `ProfileImagePath`.
- `--system-drive D:`: use a different default drive when building default roots.

## Safety behavior

Rebuild and delete actions are blocked when the selected profile is current,
loaded, special/system, missing a matching ProfileList SID, matched ambiguously,
or loaded state cannot be verified. Registry keys are exported before deletion.

## Troubleshooting startup failures

Run this first on a computer where the tool fails. Elevated diagnostics are most
complete, but the command can also run unelevated so prerequisite failures are
visible instead of being hidden behind startup elevation:

```powershell
.\TempProfileFixer.exe doctor
```

For an interactive double-click check, run:

```powershell
.\Run-Diagnostics.cmd
```

The report checks elevation, configured users/profile roots, users-root access,
ProfileList registry access, skipped or unreadable ProfileList subkeys, WMI
profile state, skipped or unreadable WMI rows, `reg.exe`, `shutdown.exe`, and
writable data storage. Backup, log, and diagnostic files are written under the
first writable location the tool can use: the EXE folder,
`C:\ProgramData\TempProfileFixer`, or the current user's Temp folder.

If Windows blocks a downloaded EXE, open the file properties and use `Unblock`,
or run it from an elevated PowerShell prompt after confirming the file is trusted.
From the portable ZIP, `Unblock-Package.cmd` performs the same unblock operation
for the tool files in that extracted folder after an explicit trust prompt. On
older PowerShell versions, it removes the `Zone.Identifier` download-block
stream data directly instead of requiring `Unblock-File`. It uses system helper
paths directly so a damaged `PATH` does not stop the unblock helper first.

Temp Profile Fixer targets the .NET Framework 4 runtime. Older or stripped-down
Windows systems may need .NET Framework 4.x Full installed or enabled before the
EXE can start.
