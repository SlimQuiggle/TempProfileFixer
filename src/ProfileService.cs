using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TempProfileFixer
{
    internal static class ProfileService
    {
        public const string ProfileListRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        public const string ProfileListRegPath = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        private static readonly Regex OldProfileRegex = new Regex(@"\.old(\.?\d{8}-\d{6}(\.\d+)?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly IProfileMutationAdapter MutationAdapter = new WindowsProfileMutationAdapter();
        private static int runSequence;

        public static List<ProfileRecord> GetProfiles(string usersRoot)
        {
            return GetProfiles(ProfileTarget.Local(usersRoot));
        }

        public static List<ProfileRecord> GetProfiles(ProfileTarget target)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            List<FolderRecord> folders = GetProfileFolders(target);
            string registryError = null;
            List<ProfileListEntry> entries;
            try
            {
                string registryWarning;
                entries = GetProfileListEntries(target, out registryWarning);
                if (!String.IsNullOrWhiteSpace(registryWarning))
                {
                    registryError = registryWarning;
                }
            }
            catch (Exception ex)
            {
                entries = new List<ProfileListEntry>();
                registryError = ex.Message;
            }
            string stateError;
            string stateWarning;
            List<UserProfileState> states = GetUserProfileStates(target.ComputerName, out stateError, out stateWarning);
            string currentSid = target.IsRemote ? String.Empty : GetCurrentSid();
            string currentProfilePath = target.IsRemote ? String.Empty : GetCurrentProfilePath();
            return BuildInventory(folders, entries, states, currentSid, currentProfilePath, stateError, stateWarning, target.RegistryPath, registryError);
        }

        public static CompatibilityReport RunDiagnostics(ProfileTarget target)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            CompatibilityReport report = new CompatibilityReport { Target = target.DisplayName };
            report.AddOk("Application version", AppVersion.Current);
            report.AddOk("Executable", Application.ExecutablePath);
            report.AddOk("Operating system", Environment.OSVersion.VersionString);
            report.AddOk(".NET runtime", Environment.Version.ToString());
            report.AddOk("Process architecture", (Environment.Is64BitProcess ? "64-bit process" : "32-bit process") + " on " + (Environment.Is64BitOperatingSystem ? "64-bit Windows" : "32-bit Windows"));
            report.AddOk("Configured users root", target.UsersRoot);
            report.AddOk("Configured registry profile root", target.ProfileImageRoot);

            if (IsAdministrator())
            {
                report.AddOk("Administrator", "Running elevated.");
            }
            else
            {
                report.AddFailure("Administrator", "Not elevated. Run TempProfileFixer.exe as administrator.");
            }

            string dataDirectory = AppDiagnostics.GetDataDirectory();
            if (AppDiagnostics.TryEnsureWritableDirectory(dataDirectory))
            {
                report.AddOk("Writable data folder", dataDirectory);
            }
            else
            {
                report.AddFailure("Writable data folder", "Could not write to " + dataDirectory + ".");
            }

            CheckFileExists(report, "reg.exe", AppDiagnostics.GetSystemToolPath("reg.exe"));
            CheckFileExists(report, "shutdown.exe", AppDiagnostics.GetSystemToolPath("shutdown.exe"));

            try
            {
                if (!Directory.Exists(target.UsersRoot))
                {
                    report.AddFailure("Users root", target.UsersRoot + " was not found.");
                }
                else
                {
                    int folderCount = new DirectoryInfo(target.UsersRoot).GetDirectories().Length;
                    report.AddOk("Users root", target.UsersRoot + " exists; " + folderCount + " folder(s) found.");
                }
            }
            catch (Exception ex)
            {
                report.AddFailure("Users root", ex.Message);
            }

            try
            {
                string registryWarning;
                List<ProfileListEntry> entries = GetProfileListEntries(target, out registryWarning);
                if (String.IsNullOrWhiteSpace(registryWarning))
                {
                    report.AddOk("ProfileList registry", target.RegistryPath + " readable; " + entries.Count + " key(s) found.");
                }
                else
                {
                    report.AddWarning("ProfileList registry", target.RegistryPath + " readable with warning; " + entries.Count + " key(s) read. " + registryWarning);
                }
            }
            catch (Exception ex)
            {
                report.AddFailure("ProfileList registry", ex.Message);
            }

            try
            {
                string stateError;
                string stateWarning;
                List<UserProfileState> states = GetUserProfileStates(target.ComputerName, out stateError, out stateWarning);
                if (String.IsNullOrWhiteSpace(stateError))
                {
                    if (String.IsNullOrWhiteSpace(stateWarning))
                    {
                        report.AddOk("Win32_UserProfile", states.Count + " profile state record(s) found.");
                    }
                    else
                    {
                        report.AddWarning("Win32_UserProfile", states.Count + " profile state record(s) read. " + stateWarning);
                    }
                }
                else
                {
                    report.AddFailure("Win32_UserProfile", stateError);
                }
            }
            catch (Exception ex)
            {
                report.AddFailure("Win32_UserProfile", ex.Message);
            }

            if (target.IsRemote)
            {
                report.AddOk("Remote target", "Using " + target.UsersRoot + " and " + target.RegistryPath + ".");
            }

            return report;
        }

        public static ProfileRecord FindProfileByPath(string path, string usersRoot)
        {
            return FindProfileByPath(path, ProfileTarget.Local(usersRoot));
        }

        public static ProfileRecord FindProfileByPath(string path, ProfileTarget target)
        {
            string normalized = NormalizePath(path);
            List<ProfileRecord> matches = GetProfiles(target)
                .Where(p => p.NormalizedPath == normalized || p.ActualNormalizedPath == normalized)
                .ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException("Expected exactly one profile match for '" + path + "'; found " + matches.Count + ".");
            }

            return matches[0];
        }

        public static ProfileRecord FindProfileBySid(string sid, ProfileTarget target)
        {
            string baseSid = GetBaseSid(sid);
            List<ProfileRecord> matches = GetProfiles(target)
                .Where(p => String.Equals(p.BaseSid, baseSid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException("Expected exactly one profile match for SID '" + sid + "'; found " + matches.Count + ".");
            }

            return matches[0];
        }

        public static RebuildPlan CreateRebuildPlan(ProfileRecord profile, DateTime now)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            List<string> keys = new List<string>();
            keys.AddRange(profile.NormalKeyNames);
            keys.AddRange(profile.BakKeyNames);
            keys = keys.Where(k => !String.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            return new RebuildPlan
            {
                FolderName = profile.FolderName,
                ProfilePath = profile.ProfilePath,
                RenameTo = GetUniqueOldPath(profile.ProfilePath, now),
                BaseSid = profile.BaseSid,
                RegistryRoot = profile.RegistryRoot,
                RegistryKeyNames = keys,
                IsBlocked = profile.IsBlocked,
                BlockReasons = new List<string>(profile.BlockReasons),
                Warnings = new List<string>(profile.Warnings)
            };
        }

        public static RegistryRemovalPlan CreateRegistryRemovalPlan(ProfileRecord profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            List<string> keys = new List<string>();
            keys.AddRange(profile.NormalKeyNames ?? new List<string>());
            keys.AddRange(profile.BakKeyNames ?? new List<string>());
            keys = keys.Where(k => !String.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            List<string> blockReasons = new List<string>(profile.BlockReasons ?? new List<string>());
            if (keys.Count == 0)
            {
                blockReasons.Add("No matching ProfileList registry key");
            }

            return new RegistryRemovalPlan
            {
                FolderName = profile.FolderName,
                ProfilePath = profile.ProfilePath,
                BaseSid = profile.BaseSid,
                RegistryRoot = profile.RegistryRoot,
                RegistryKeyNames = keys,
                IsBlocked = blockReasons.Count > 0,
                BlockReasons = blockReasons,
                Warnings = new List<string>(profile.Warnings ?? new List<string>())
            };
        }

        public static DeleteProfilePlan CreateDeleteProfilePlan(ProfileRecord profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            List<string> keys = new List<string>();
            keys.AddRange(profile.NormalKeyNames ?? new List<string>());
            keys.AddRange(profile.BakKeyNames ?? new List<string>());
            keys = keys.Where(k => !String.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            List<string> blockReasons = new List<string>(profile.BlockReasons ?? new List<string>());
            List<string> warnings = new List<string>(profile.Warnings ?? new List<string>());
            blockReasons.RemoveAll(IsFolderOnlyDeleteAllowedBlockReason);
            if (keys.Count == 0)
            {
                warnings.Add("No matching ProfileList registry key; only the profile folder will be deleted.");
            }

            return new DeleteProfilePlan
            {
                FolderName = profile.FolderName,
                ProfilePath = profile.ProfilePath,
                BaseSid = profile.BaseSid,
                RegistryRoot = profile.RegistryRoot,
                RegistryKeyNames = keys,
                IsBlocked = blockReasons.Count > 0,
                BlockReasons = blockReasons,
                Warnings = warnings
            };
        }

        public static BakRemovalPlan CreateBakRemovalPlan(ProfileRecord profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            List<string> keys = (profile.BakKeyNames ?? new List<string>())
                .Where(k => !String.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> blockReasons = new List<string>(profile.BlockReasons ?? new List<string>());
            if (keys.Count == 0)
            {
                blockReasons.Add("No matching .bak ProfileList key");
            }

            return new BakRemovalPlan
            {
                FolderName = profile.FolderName,
                ProfilePath = profile.ProfilePath,
                BaseSid = profile.BaseSid,
                RegistryRoot = profile.RegistryRoot,
                RegistryKeyNames = keys,
                IsBlocked = blockReasons.Count > 0,
                BlockReasons = blockReasons,
                Warnings = new List<string>(profile.Warnings ?? new List<string>())
            };
        }

        private static bool IsFolderOnlyDeleteAllowedBlockReason(string reason)
        {
            return String.Equals(reason, "No matching ProfileList SID", StringComparison.OrdinalIgnoreCase);
        }

        public static RebuildResult RebuildProfile(string profilePath, string usersRoot)
        {
            return RebuildProfile(profilePath, ProfileTarget.Local(usersRoot));
        }

        public static RebuildResult RebuildProfile(string profilePath, ProfileTarget target)
        {
            ProfileRecord profile = FindProfileByPath(profilePath, target);
            RebuildPlan plan = CreateRebuildPlan(profile, DateTime.Now);
            if (plan.IsBlocked)
            {
                throw new InvalidOperationException("Profile rebuild is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
            }

            string runId = CreateRunId();
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName);
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + ".log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting rebuild for " + profile.ProfilePath);
            WriteLog(logPath, "Base SID: " + profile.BaseSid);
            WriteLog(logPath, "Rename target: " + plan.RenameTo);

            string phase = "exporting registry backups";
            List<string> completedActions = new List<string>();
            try
            {
                ExportAndValidateRegistryKeys(plan.RegistryKeyNames, backupDirectory, target.ComputerName, logPath, MutationAdapter);

                phase = "renaming the profile folder";
                WriteLog(logPath, "Renaming " + plan.ProfilePath + " to " + plan.RenameTo);
                MutationAdapter.MoveDirectory(plan.ProfilePath, plan.RenameTo);
                completedActions.Add("Renamed profile folder to " + plan.RenameTo);

                phase = "removing ProfileList registry keys";
                RemoveRegistryKeys(plan.RegistryKeyNames, target.ComputerName, logPath, MutationAdapter, completedActions);

                WriteLog(logPath, "Rebuild completed successfully.");

                return new RebuildResult
                {
                    ProfilePath = plan.ProfilePath,
                    RenamedTo = plan.RenameTo,
                    RegistryRoot = plan.RegistryRoot,
                    RemovedKeys = new List<string>(plan.RegistryKeyNames),
                    BackupDirectory = backupDirectory,
                    LogPath = logPath,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "FAILED: " + ex.Message);
                throw CreateOperationFailure("Profile rebuild", phase, backupDirectory, logPath, plan.RenameTo, completedActions, ex);
            }
        }

        public static RegistryRemovalResult RemoveRegistryEntries(string profilePath, string usersRoot)
        {
            return RemoveRegistryEntries(profilePath, ProfileTarget.Local(usersRoot));
        }

        public static RegistryRemovalResult RemoveRegistryEntries(string profilePath, ProfileTarget target)
        {
            ProfileRecord profile = FindProfileByPath(profilePath, target);
            RegistryRemovalPlan plan = CreateRegistryRemovalPlan(profile);
            if (plan.IsBlocked)
            {
                throw new InvalidOperationException("Registry removal is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
            }

            string runId = CreateRunId();
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName + "-registry");
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + "-registry.log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting registry entry removal for " + profile.ProfilePath);
            string phase = "exporting registry backups";
            List<string> completedActions = new List<string>();
            try
            {
                ExportAndValidateRegistryKeys(plan.RegistryKeyNames, backupDirectory, target.ComputerName, logPath, MutationAdapter);

                phase = "removing ProfileList registry keys";
                RemoveRegistryKeys(plan.RegistryKeyNames, target.ComputerName, logPath, MutationAdapter, completedActions);

                WriteLog(logPath, "Registry entry removal completed successfully.");

                return new RegistryRemovalResult
                {
                    ProfilePath = profile.ProfilePath,
                    RegistryRoot = plan.RegistryRoot,
                    RemovedKeys = new List<string>(plan.RegistryKeyNames),
                    BackupDirectory = backupDirectory,
                    LogPath = logPath,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "FAILED: " + ex.Message);
                throw CreateOperationFailure("Registry removal", phase, backupDirectory, logPath, null, completedActions, ex);
            }
        }

        public static DeleteProfileResult DeleteProfile(string profilePath, string usersRoot)
        {
            return DeleteProfile(profilePath, ProfileTarget.Local(usersRoot));
        }

        public static DeleteProfileResult DeleteProfile(string profilePath, ProfileTarget target)
        {
            ProfileRecord profile = FindProfileByPath(profilePath, target);
            DeleteProfilePlan plan = CreateDeleteProfilePlan(profile);
            if (plan.IsBlocked)
            {
                throw new InvalidOperationException("Profile deletion is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
            }

            string runId = CreateRunId();
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName + "-delete");
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + "-delete.log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting profile deletion for " + profile.ProfilePath);
            WriteLog(logPath, "Base SID: " + profile.BaseSid);

            string phase = "exporting registry backups";
            List<string> completedActions = new List<string>();
            try
            {
                ExportAndValidateRegistryKeys(plan.RegistryKeyNames, backupDirectory, target.ComputerName, logPath, MutationAdapter);

                phase = "deleting the profile folder";
                WriteLog(logPath, "Deleting profile folder " + plan.ProfilePath);
                MutationAdapter.DeleteDirectoryTree(plan.ProfilePath, logPath);
                completedActions.Add("Deleted profile folder " + plan.ProfilePath);

                phase = "removing ProfileList registry keys";
                RemoveRegistryKeys(plan.RegistryKeyNames, target.ComputerName, logPath, MutationAdapter, completedActions);

                WriteLog(logPath, "Profile deletion completed successfully.");

                return new DeleteProfileResult
                {
                    ProfilePath = plan.ProfilePath,
                    RegistryRoot = plan.RegistryRoot,
                    RemovedKeys = new List<string>(plan.RegistryKeyNames),
                    BackupDirectory = backupDirectory,
                    LogPath = logPath,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "FAILED: " + ex.Message);
                throw CreateOperationFailure("Profile deletion", phase, backupDirectory, logPath, null, completedActions, ex);
            }
        }

        public static BakRemovalResult RemoveBakKeys(string profilePath, string usersRoot)
        {
            return RemoveBakKeys(profilePath, ProfileTarget.Local(usersRoot));
        }

        public static BakRemovalResult RemoveBakKeys(string profilePath, ProfileTarget target)
        {
            ProfileRecord profile = FindProfileByPath(profilePath, target);
            BakRemovalPlan plan = CreateBakRemovalPlan(profile);
            if (plan.IsBlocked)
            {
                throw new InvalidOperationException(".bak removal is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
            }

            string runId = CreateRunId();
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName + "-bak");
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + "-bak.log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting .bak removal for " + profile.ProfilePath);
            string phase = "exporting registry backups";
            List<string> completedActions = new List<string>();
            try
            {
                ExportAndValidateRegistryKeys(plan.RegistryKeyNames, backupDirectory, target.ComputerName, logPath, MutationAdapter);

                phase = "removing .bak ProfileList registry keys";
                RemoveRegistryKeys(plan.RegistryKeyNames, target.ComputerName, logPath, MutationAdapter, completedActions);

                WriteLog(logPath, ".bak removal completed successfully.");

                return new BakRemovalResult
                {
                    ProfilePath = profile.ProfilePath,
                    RegistryRoot = profile.RegistryRoot,
                    RemovedKeys = new List<string>(plan.RegistryKeyNames),
                    BackupDirectory = backupDirectory,
                    LogPath = logPath,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "FAILED: " + ex.Message);
                throw CreateOperationFailure(".bak removal", phase, backupDirectory, logPath, null, completedActions, ex);
            }
        }

        public static void RebootComputer()
        {
            RebootComputer(null);
        }

        public static void RebootComputer(string computerName)
        {
            ScheduleReboot(computerName, 0);
        }

        public static void ScheduleReboot(string computerName, int delaySeconds)
        {
            if (delaySeconds < 0)
            {
                throw new ArgumentOutOfRangeException("delaySeconds");
            }

            string target = IsRemoteComputer(computerName) ? " /m \\\\" + computerName.Trim().TrimStart('\\') : String.Empty;
            RunShutdown("/r" + target + " /t " + delaySeconds + " /c \"Temp Profile Fixer requested reboot after profile rebuild.\"");
        }

        public static void AbortReboot(string computerName)
        {
            string target = IsRemoteComputer(computerName) ? " /m \\\\" + computerName.Trim().TrimStart('\\') : String.Empty;
            RunShutdown("/a" + target);
        }

        private static void RunShutdown(string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = AppDiagnostics.GetSystemToolPath("shutdown.exe");
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Could not start shutdown.exe.");
                }
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("shutdown.exe did not complete within 15 seconds.");
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("shutdown.exe failed with exit code " + process.ExitCode + ".");
                }
            }
        }

        public static Icon LoadApplicationIcon()
        {
            try
            {
                string executable = Application.ExecutablePath;
                if (!String.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                {
                    return Icon.ExtractAssociatedIcon(executable);
                }
            }
            catch
            {
            }

            return (Icon)SystemIcons.Application.Clone();
        }

        public static Image LoadHeaderImage()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("TempProfileFixer.Assets.TempProfileFixer.png"))
                {
                    if (stream != null)
                    {
                        return Image.FromStream(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException("Header image load failed", ex);
            }

            using (Icon icon = LoadApplicationIcon())
            {
                return icon.ToBitmap();
            }
        }

        public static void OpenRegistryForProfile(ProfileRecord profile)
        {
            if (profile == null || !profile.HasRegistryEntries)
            {
                throw new InvalidOperationException("The selected profile has no matching ProfileList registry entry.");
            }

            string keyName = (profile.NormalKeyNames ?? new List<string>()).FirstOrDefault();
            if (String.IsNullOrWhiteSpace(keyName))
            {
                keyName = (profile.BakKeyNames ?? new List<string>()).FirstOrDefault();
            }

            string lastKey = @"Computer\HKEY_LOCAL_MACHINE\" + ProfileListRegistryPath + "\\" + keyName;
            using (RegistryKey regedit = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
            {
                if (regedit != null)
                {
                    regedit.SetValue("LastKey", lastKey, RegistryValueKind.String);
                }
            }

            Process.Start("regedit.exe");
        }

        public static bool IsAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static string GetCurrentSid()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User == null ? String.Empty : identity.User.Value;
        }

        public static string GetCurrentProfilePath()
        {
            try
            {
                return NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            catch
            {
                return String.Empty;
            }
        }

        public static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return String.Empty;
            }

            string expanded = Environment.ExpandEnvironmentVariables(path.Trim()).Replace('/', '\\');
            try
            {
                expanded = Path.GetFullPath(expanded);
            }
            catch
            {
            }

            return expanded.TrimEnd('\\').ToUpperInvariant();
        }

        public static string NormalizeRegistryProfilePath(string profileImagePath, string profileImageRoot)
        {
            if (String.IsNullOrWhiteSpace(profileImagePath))
            {
                return String.Empty;
            }

            string path = profileImagePath.Trim().Replace('/', '\\');
            const string systemDriveToken = "%SystemDrive%";
            if (path.StartsWith(systemDriveToken, StringComparison.OrdinalIgnoreCase))
            {
                string root = GetDriveRoot(profileImageRoot);
                if (!String.IsNullOrWhiteSpace(root))
                {
                    string suffix = path.Substring(systemDriveToken.Length).TrimStart('\\');
                    path = String.IsNullOrWhiteSpace(suffix)
                        ? root
                        : Path.Combine(root, suffix);
                }
            }

            return NormalizePath(path);
        }

        public static string GetBaseSid(string sidOrKeyName)
        {
            if (sidOrKeyName != null && sidOrKeyName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                return sidOrKeyName.Substring(0, sidOrKeyName.Length - 4);
            }

            return sidOrKeyName ?? String.Empty;
        }

        public static string GetProfileListRegPath(string computerName)
        {
            return IsRemoteComputer(computerName)
                ? @"\\" + computerName.Trim().TrimStart('\\') + @"\HKLM\" + ProfileListRegistryPath
                : ProfileListRegPath;
        }

        public static bool IsRemoteComputer(string computerName)
        {
            if (String.IsNullOrWhiteSpace(computerName))
            {
                return false;
            }

            string normalized = computerName.Trim().TrimStart('\\').TrimEnd('\\');
            return !String.Equals(normalized, ".", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(normalized, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }

        private static void CheckFileExists(CompatibilityReport report, string name, string path)
        {
            if (File.Exists(path))
            {
                report.AddOk(name, path);
            }
            else
            {
                report.AddFailure(name, path + " was not found.");
            }
        }

        private static List<FolderRecord> GetProfileFolders(ProfileTarget target)
        {
            if (!Directory.Exists(target.UsersRoot))
            {
                throw new DirectoryNotFoundException("Users root '" + target.UsersRoot + "' was not found.");
            }

            HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "All Users",
                "Default",
                "Default User",
                "defaultuser0",
                "LocalService",
                "NetworkService",
                "Public"
            };

            return new DirectoryInfo(target.UsersRoot)
                .GetDirectories()
                .Where(d => !excluded.Contains(d.Name) && !OldProfileRegex.IsMatch(d.Name))
                .OrderBy(d => d.Name)
                .Select(d => new FolderRecord
                {
                    Name = d.Name,
                    FullName = d.FullName,
                    ActualNormalizedPath = NormalizePath(d.FullName),
                    NormalizedPath = NormalizePath(target.GetLogicalPathForFolder(d.Name))
                })
                .ToList();
        }

        private static List<ProfileListEntry> GetProfileListEntries(ProfileTarget target)
        {
            string warning;
            return GetProfileListEntries(target, out warning);
        }

        private static List<ProfileListEntry> GetProfileListEntries(ProfileTarget target, out string warning)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            return GetProfileListEntries(target.ComputerName, target.ProfileImageRoot, out warning);
        }

        private static List<ProfileListEntry> GetProfileListEntries(string computerName, string profileImageRoot, out string warning)
        {
            List<ProfileListEntry> entries = new List<ProfileListEntry>();
            List<string> skippedKeys = new List<string>();
            warning = null;
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using (RegistryKey localMachine = OpenLocalMachine(computerName, view))
            using (RegistryKey profileList = localMachine.OpenSubKey(ProfileListRegistryPath, false))
            {
                if (profileList == null)
                {
                    throw new InvalidOperationException(GetProfileListRegPath(computerName) + " was not found.");
                }

                foreach (string keyName in profileList.GetSubKeyNames().OrderBy(n => n))
                {
                    try
                    {
                        using (RegistryKey key = profileList.OpenSubKey(keyName, false))
                        {
                            if (key == null)
                            {
                                skippedKeys.Add(keyName + " (could not open)");
                                continue;
                            }

                            string imagePath = Convert.ToString(key.GetValue("ProfileImagePath", String.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames));
                            entries.Add(new ProfileListEntry
                            {
                                KeyName = keyName,
                                BaseSid = GetBaseSid(keyName),
                                IsBak = keyName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase),
                                ProfileImagePath = imagePath,
                                NormalizedProfilePath = NormalizeRegistryProfilePath(imagePath, profileImageRoot),
                                State = key.GetValue("State"),
                                RefCount = key.GetValue("RefCount")
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        skippedKeys.Add(keyName + " (" + ex.Message + ")");
                    }
                }
            }

            if (skippedKeys.Count > 0)
            {
                warning = "Skipped unreadable ProfileList key(s): " + String.Join("; ", skippedKeys.ToArray());
            }

            return entries;
        }

        private static List<UserProfileState> GetUserProfileStates(string computerName, out string error)
        {
            string warning;
            return GetUserProfileStates(computerName, out error, out warning);
        }

        private static List<UserProfileState> GetUserProfileStates(string computerName, out string error, out string warning)
        {
            List<UserProfileState> states = new List<UserProfileState>();
            List<string> skippedRows = new List<string>();
            error = null;
            warning = null;

            try
            {
                ObjectQuery query = new ObjectQuery("SELECT SID, LocalPath, Loaded, Special FROM Win32_UserProfile");
                ManagementObjectSearcher searcher;
                if (IsRemoteComputer(computerName))
                {
                    ManagementScope scope = new ManagementScope("\\\\" + computerName.Trim().TrimStart('\\') + "\\root\\cimv2");
                    scope.Connect();
                    searcher = new ManagementObjectSearcher(scope, query);
                }
                else
                {
                    searcher = new ManagementObjectSearcher(query);
                }

                using (searcher)
                using (ManagementObjectCollection results = searcher.Get())
                {
                    int rowNumber = 0;
                    foreach (ManagementObject profile in results)
                    {
                        rowNumber++;
                        using (profile)
                        {
                            try
                            {
                                string sid = Convert.ToString(profile["SID"]);
                                string localPath = Convert.ToString(profile["LocalPath"]);
                                states.Add(new UserProfileState
                                {
                                    Sid = sid,
                                    LocalPath = localPath,
                                    NormalizedPath = NormalizePath(localPath),
                                    Loaded = Convert.ToBoolean(profile["Loaded"]),
                                    Special = Convert.ToBoolean(profile["Special"])
                                });
                            }
                            catch (Exception ex)
                            {
                                skippedRows.Add(DescribeUserProfileRow(profile, rowNumber) + " (" + ex.Message + ")");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (skippedRows.Count > 0)
            {
                warning = "Skipped unreadable Win32_UserProfile row(s): " + String.Join("; ", skippedRows.ToArray());
            }

            return states;
        }

        private static string DescribeUserProfileRow(ManagementObject profile, int rowNumber)
        {
            string sid = TryGetManagementString(profile, "SID");
            if (!String.IsNullOrWhiteSpace(sid))
            {
                return sid;
            }

            string localPath = TryGetManagementString(profile, "LocalPath");
            if (!String.IsNullOrWhiteSpace(localPath))
            {
                return localPath;
            }

            return "row " + rowNumber;
        }

        private static string TryGetManagementString(ManagementBaseObject instance, string propertyName)
        {
            try
            {
                return Convert.ToString(instance[propertyName]);
            }
            catch
            {
                return String.Empty;
            }
        }

        internal static List<ProfileRecord> BuildInventory(
            List<FolderRecord> folders,
            List<ProfileListEntry> entries,
            List<UserProfileState> states,
            string currentSid,
            string currentProfilePath,
            string stateError,
            string stateWarning,
            string registryRoot,
            string registryError)
        {
            Dictionary<string, List<UserProfileState>> statesBySid = states
                .Where(s => !String.IsNullOrWhiteSpace(s.Sid))
                .GroupBy(s => s.Sid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            Dictionary<string, List<UserProfileState>> statesByPath = states
                .Where(s => !String.IsNullOrWhiteSpace(s.NormalizedPath))
                .GroupBy(s => s.NormalizedPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            List<ProfileRecord> records = new List<ProfileRecord>();

            foreach (FolderRecord folder in folders)
            {
                List<ProfileListEntry> matchingEntries = entries
                    .Where(e => String.Equals(e.NormalizedProfilePath, folder.NormalizedPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                List<string> baseSids = matchingEntries
                    .Select(e => e.BaseSid)
                    .Where(s => !String.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string baseSid = baseSids.Count == 1 ? baseSids[0] : String.Empty;
                List<ProfileListEntry> normalEntries = new List<ProfileListEntry>();
                List<ProfileListEntry> bakEntries = new List<ProfileListEntry>();
                if (!String.IsNullOrWhiteSpace(baseSid))
                {
                    normalEntries = entries.Where(e => String.Equals(e.BaseSid, baseSid, StringComparison.OrdinalIgnoreCase) && !e.IsBak).ToList();
                    bakEntries = entries.Where(e => String.Equals(e.BaseSid, baseSid, StringComparison.OrdinalIgnoreCase) && e.IsBak).ToList();
                }

                UserProfileState state = null;
                if (!String.IsNullOrWhiteSpace(baseSid) && statesBySid.ContainsKey(baseSid))
                {
                    state = AggregateProfileState(statesBySid[baseSid]);
                }
                else if (statesByPath.ContainsKey(folder.NormalizedPath))
                {
                    state = AggregateProfileState(statesByPath[folder.NormalizedPath]);
                }

                bool loaded = state != null && state.Loaded;
                bool special = state != null && state.Special;
                List<string> blockReasons = new List<string>();
                List<string> warnings = new List<string>();

                if (!Directory.Exists(folder.FullName))
                {
                    blockReasons.Add("Folder missing");
                }
                if (!String.IsNullOrWhiteSpace(registryError))
                {
                    blockReasons.Add("Could not read ProfileList registry");
                    warnings.Add("ProfileList query failed: " + registryError);
                }
                if (baseSids.Count == 0)
                {
                    blockReasons.Add("No matching ProfileList SID");
                }
                else if (baseSids.Count > 1)
                {
                    blockReasons.Add("Multiple SIDs match this folder");
                }
                if (!String.IsNullOrWhiteSpace(baseSid) && String.Equals(baseSid, currentSid, StringComparison.OrdinalIgnoreCase))
                {
                    blockReasons.Add("Current admin profile");
                }
                else if (!String.IsNullOrWhiteSpace(currentProfilePath) && String.Equals(folder.NormalizedPath, currentProfilePath, StringComparison.OrdinalIgnoreCase))
                {
                    blockReasons.Add("Current admin profile");
                }
                if (loaded)
                {
                    blockReasons.Add("Profile is loaded");
                }
                if (special)
                {
                    blockReasons.Add("Special/system profile");
                }
                if (normalEntries.Count > 1)
                {
                    blockReasons.Add("Multiple normal ProfileList keys");
                }
                if (!String.IsNullOrWhiteSpace(stateError))
                {
                    blockReasons.Add("Could not verify loaded profile state");
                    warnings.Add("Win32_UserProfile query failed: " + stateError);
                }
                if (!String.IsNullOrWhiteSpace(stateWarning))
                {
                    blockReasons.Add("Could not fully verify loaded profile state");
                    warnings.Add("Win32_UserProfile query warning: " + stateWarning);
                }
                if (bakEntries.Count > 0)
                {
                    warnings.Add("Matching .bak ProfileList key will be removed during rebuild");
                }
                if (normalEntries.Count == 0 && bakEntries.Count > 0)
                {
                    warnings.Add("Only .bak key is present");
                }
                if (normalEntries.Count > 0 && normalEntries.All(e => !String.Equals(e.NormalizedProfilePath, folder.NormalizedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add("Normal ProfileList key points to a different path");
                }
                if (bakEntries.Count > 0 && bakEntries.All(e => !String.Equals(e.NormalizedProfilePath, folder.NormalizedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add(".bak ProfileList key points to a different path");
                }

                string status = "Ready";
                if (blockReasons.Count > 0)
                {
                    status = "Blocked: " + String.Join("; ", blockReasons.ToArray());
                }
                else if (warnings.Count > 0)
                {
                    status = "Warning: " + String.Join("; ", warnings.ToArray());
                }

                records.Add(new ProfileRecord
                {
                    FolderName = folder.Name,
                    ProfilePath = folder.FullName,
                    NormalizedPath = folder.NormalizedPath,
                    ActualNormalizedPath = folder.ActualNormalizedPath,
                    RegistryRoot = registryRoot,
                    BaseSid = baseSid,
                    MatchingSidCount = baseSids.Count,
                    NormalKeyNames = normalEntries.Select(e => e.KeyName).ToList(),
                    BakKeyNames = bakEntries.Select(e => e.KeyName).ToList(),
                    NormalKeyPresent = normalEntries.Count > 0,
                    BakKeyPresent = bakEntries.Count > 0,
                    Loaded = loaded,
                    Special = special,
                    IsBlocked = blockReasons.Count > 0,
                    BlockReasons = blockReasons,
                    Warnings = warnings,
                    Status = status
                });
            }

            return records;
        }

        private static UserProfileState AggregateProfileState(IEnumerable<UserProfileState> matchingStates)
        {
            List<UserProfileState> matches = new List<UserProfileState>(matchingStates ?? Enumerable.Empty<UserProfileState>());
            UserProfileState first = matches.FirstOrDefault();
            if (first == null)
            {
                return null;
            }

            return new UserProfileState
            {
                Sid = first.Sid,
                LocalPath = first.LocalPath,
                NormalizedPath = first.NormalizedPath,
                Loaded = matches.Any(s => s.Loaded),
                Special = matches.Any(s => s.Special)
            };
        }

        private static string GetUniqueOldPath(string profilePath, DateTime now)
        {
            string timestamp = now.ToString("yyyyMMdd-HHmmss");
            string timestamped = profilePath + ".old" + timestamp;
            if (!Directory.Exists(timestamped) && !File.Exists(timestamped))
            {
                return timestamped;
            }

            for (int i = 2; i < 1000; i++)
            {
                string candidate = timestamped + "." + i;
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("Could not find an available .old path for '" + profilePath + "'.");
        }

        private static RegistryKey OpenLocalMachine(string computerName, RegistryView view)
        {
            return IsRemoteComputer(computerName)
                ? RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, computerName.Trim().TrimStart('\\'), view)
                : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        }

        private static string GetDriveRoot(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return String.Empty;
            }

            string trimmed = path.Trim().Replace('/', '\\');
            if (trimmed.Length >= 2 && Char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            {
                return Char.ToUpperInvariant(trimmed[0]) + @":\";
            }

            Match adminShareMatch = Regex.Match(trimmed, @"^\\\\[^\\]+\\([A-Za-z])\$($|\\)");
            if (adminShareMatch.Success)
            {
                return Char.ToUpperInvariant(adminShareMatch.Groups[1].Value[0]) + @":\";
            }

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(trimmed);
                string root = Path.GetPathRoot(expanded);
                if (!String.IsNullOrWhiteSpace(root) && root.Length >= 2 && Char.IsLetter(root[0]) && root[1] == ':')
                {
                    return Char.ToUpperInvariant(root[0]) + @":\";
                }
            }
            catch
            {
            }

            return String.Empty;
        }

        internal static string CreateRunId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-p" + Process.GetCurrentProcess().Id + "-" + Interlocked.Increment(ref runSequence);
        }

        internal static void ExportAndValidateRegistryKeys(
            IEnumerable<string> keyNames,
            string backupDirectory,
            string computerName,
            string logPath,
            IProfileMutationAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException("adapter");
            }

            List<string> destinations = new List<string>();
            foreach (string keyName in keyNames ?? Enumerable.Empty<string>())
            {
                string destination = Path.Combine(backupDirectory, keyName + ".reg");
                WriteLog(logPath, "Exporting ProfileList key " + keyName + " to " + destination);
                adapter.ExportRegistryKey(keyName, destination, computerName);
                destinations.Add(destination);
            }

            foreach (string destination in destinations)
            {
                ValidateRegistryBackup(destination);
                WriteLog(logPath, "Validated registry backup " + destination);
            }
        }

        internal static void ValidateRegistryBackup(string path)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("Registry backup was not created: " + path);
            }

            FileInfo info = new FileInfo(path);
            if (info.Length == 0)
            {
                throw new InvalidOperationException("Registry backup is empty: " + path);
            }

            string text = File.ReadAllText(path);
            if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("Windows Registry Editor Version", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Registry backup has an invalid header: " + path);
            }
        }

        internal static void RemoveRegistryKeys(
            IEnumerable<string> keyNames,
            string computerName,
            string logPath,
            IProfileMutationAdapter adapter,
            List<string> completedActions)
        {
            foreach (string keyName in keyNames ?? Enumerable.Empty<string>())
            {
                WriteLog(logPath, "Removing ProfileList key " + keyName);
                adapter.RemoveRegistryKey(keyName, computerName);
                if (completedActions != null)
                {
                    completedActions.Add("Removed registry key " + keyName);
                }
            }
        }

        internal static ProfileOperationException CreateOperationFailure(
            string operation,
            string phase,
            string backupDirectory,
            string logPath,
            string renamedPath,
            List<string> completedActions,
            Exception innerException)
        {
            return new ProfileOperationException(
                operation,
                phase,
                backupDirectory,
                logPath,
                renamedPath,
                completedActions,
                innerException);
        }

        internal static void ExportRegistryKey(string keyName, string destinationPath, string computerName)
        {
            string regPath = GetProfileListRegPath(computerName) + "\\" + keyName;
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = AppDiagnostics.GetSystemToolPath("reg.exe");
            startInfo.Arguments = "export \"" + regPath + "\" \"" + destinationPath + "\" /y";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = Process.Start(startInfo);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("reg.exe export failed for '" + regPath + "' with exit code " + process.ExitCode + ".");
            }
        }

        internal static void RemoveRegistryKey(string keyName, string computerName)
        {
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using (RegistryKey localMachine = OpenLocalMachine(computerName, view))
            using (RegistryKey profileList = localMachine.OpenSubKey(ProfileListRegistryPath, true))
            {
                if (profileList == null)
                {
                    throw new InvalidOperationException(GetProfileListRegPath(computerName) + " was not found.");
                }

                if (profileList.GetSubKeyNames().Any(k => String.Equals(k, keyName, StringComparison.OrdinalIgnoreCase)))
                {
                    profileList.DeleteSubKeyTree(keyName, false);
                }
            }
        }

        internal static void DeleteDirectoryTree(string directoryPath, string logPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            DirectoryInfo root = new DirectoryInfo(directoryPath);
            if (IsDirectoryReparsePoint(root))
            {
                DeleteDirectoryReparsePoint(root, logPath);
                return;
            }

            DeleteDirectoryContents(root, logPath);
            ClearDirectoryAttributes(root, logPath);
            DeleteEmptyDirectory(root, logPath);
        }

        private static void DeleteDirectoryContents(DirectoryInfo directory, string logPath)
        {
            foreach (FileInfo file in GetFilesForDelete(directory))
            {
                DeleteFileForProfileCleanup(file, logPath);
            }

            foreach (DirectoryInfo child in GetDirectoriesForDelete(directory))
            {
                if (IsDirectoryReparsePoint(child))
                {
                    DeleteDirectoryReparsePoint(child, logPath);
                    continue;
                }

                DeleteDirectoryContents(child, logPath);
                ClearDirectoryAttributes(child, logPath);
                DeleteEmptyDirectory(child, logPath);
            }
        }

        private static FileInfo[] GetFilesForDelete(DirectoryInfo directory)
        {
            try
            {
                return directory.GetFiles();
            }
            catch (Exception ex)
            {
                throw new IOException("Could not list files in '" + directory.FullName + "': " + ex.Message, ex);
            }
        }

        private static DirectoryInfo[] GetDirectoriesForDelete(DirectoryInfo directory)
        {
            try
            {
                return directory.GetDirectories();
            }
            catch (Exception ex)
            {
                throw new IOException("Could not list folders in '" + directory.FullName + "': " + ex.Message, ex);
            }
        }

        private static void DeleteFileForProfileCleanup(FileInfo file, string logPath)
        {
            try
            {
                if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    file.Attributes = file.Attributes & ~FileAttributes.ReadOnly;
                }

                file.Delete();
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "Failed to delete file " + file.FullName + ": " + ex.Message);
                throw new IOException("Could not delete file '" + file.FullName + "': " + ex.Message, ex);
            }
        }

        private static void DeleteDirectoryReparsePoint(DirectoryInfo directory, string logPath)
        {
            try
            {
                WriteLog(logPath, "Deleting directory reparse point " + directory.FullName);
                ClearDirectoryAttributes(directory, logPath);
                directory.Delete(false);
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "Failed to delete directory reparse point " + directory.FullName + ": " + ex.Message);
                throw new IOException("Could not delete directory reparse point '" + directory.FullName + "': " + ex.Message, ex);
            }
        }

        private static void DeleteEmptyDirectory(DirectoryInfo directory, string logPath)
        {
            try
            {
                directory.Delete(false);
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "Failed to delete folder " + directory.FullName + ": " + ex.Message);
                throw new IOException("Could not delete folder '" + directory.FullName + "': " + ex.Message, ex);
            }
        }

        private static void ClearDirectoryAttributes(DirectoryInfo directory, string logPath)
        {
            try
            {
                directory.Attributes = directory.Attributes &
                    ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "Failed to clear folder attributes " + directory.FullName + ": " + ex.Message);
                throw new IOException("Could not clear folder attributes for '" + directory.FullName + "': " + ex.Message, ex);
            }
        }

        private static bool IsDirectoryReparsePoint(DirectoryInfo directory)
        {
            return (directory.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }

        private static string GetAppDirectory()
        {
            return AppDiagnostics.GetDataDirectory();
        }

        private static string SafeFileName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return "profile";
            }

            StringBuilder builder = new StringBuilder();
            foreach (char c in value)
            {
                if (Char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('_');
                }
            }

            return builder.ToString();
        }

        private static void WriteLog(string logPath, string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine;
            File.AppendAllText(logPath, line, Encoding.UTF8);
        }
    }

}
