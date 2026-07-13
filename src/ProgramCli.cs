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
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception exception = e.ExceptionObject as Exception;
                if (exception != null)
                {
                    AppDiagnostics.LogException("Unhandled application exception", exception);
                }
            };

            try
            {
                if (args.Length > 0)
                {
                    return CommandLine.Run(args);
                }

                NativeMethods.FreeConsole();
                if (!ProfileService.IsAdministrator())
                {
                    if (TryRelaunchElevated(args))
                    {
                        return 0;
                    }

                    MessageBox.Show("Temp Profile Fixer must be run as administrator.", "Temp Profile Fixer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    ShowGuiException(e.Exception);
                };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                string logPath = AppDiagnostics.LogException("Startup failure", ex);
                if (args.Length > 0)
                {
                    Console.Error.WriteLine(ex.Message);
                    Console.Error.WriteLine("Diagnostic log: " + logPath);
                    if (args.Any(a => String.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase) || String.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.Error.WriteLine(ex.ToString());
                    }
                }
                else
                {
                    MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + "Diagnostic log: " + logPath, "Temp Profile Fixer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return 1;
            }
        }

        private static void ShowGuiException(Exception ex)
        {
            string logPath = AppDiagnostics.LogException("User interface error", ex);
            MessageBox.Show(
                ex.Message + Environment.NewLine + Environment.NewLine + "Diagnostic log: " + logPath,
                "Temp Profile Fixer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private static bool TryRelaunchElevated(string[] args)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = Application.ExecutablePath;
                startInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                startInfo.Arguments = JoinArguments(args);
                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException("Elevation relaunch failed", ex);
                return false;
            }
        }

        private static string JoinArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return String.Empty;
            }

            return String.Join(" ", args.Select(QuoteArgument).ToArray());
        }

        private static string QuoteArgument(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();
    }

    internal static class AppDiagnostics
    {
        public static string GetDataDirectory()
        {
            List<string> candidates = new List<string>();
            AddPathCandidate(candidates, SafeGetBaseDirectory());

            string commonAppData = SafeGetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            AddPathCandidate(candidates, SafeCombine(commonAppData, "TempProfileFixer"));

            string tempPath = SafeGetTempPath();
            AddPathCandidate(candidates, SafeCombine(tempPath, "TempProfileFixer"));
            AddPathCandidate(candidates, SafeGetCurrentDirectory());

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TryEnsureWritableDirectory(candidate))
                {
                    return candidate;
                }
            }

            foreach (string candidate in candidates.Where(c => !String.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }

            return ".";
        }

        public static string GetLogDirectory()
        {
            string logDirectory = Path.Combine(GetDataDirectory(), "logs");
            Directory.CreateDirectory(logDirectory);
            return logDirectory;
        }

        public static string LogException(string context, Exception ex)
        {
            try
            {
                string logDirectory = GetLogDirectory();
                string logPath = Path.Combine(logDirectory, "diagnostic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("Temp Profile Fixer diagnostic log");
                builder.AppendLine("Context: " + context);
                builder.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                builder.AppendLine("Machine: " + SafeDiagnosticValue(delegate { return Environment.MachineName; }));
                builder.AppendLine("User: " + SafeDiagnosticValue(delegate { return Environment.UserDomainName + "\\" + Environment.UserName; }));
                builder.AppendLine("OS: " + SafeDiagnosticValue(delegate { return Environment.OSVersion.VersionString; }));
                builder.AppendLine(".NET: " + SafeDiagnosticValue(delegate { return Environment.Version.ToString(); }));
                builder.AppendLine("64-bit OS: " + SafeDiagnosticValue(delegate { return Environment.Is64BitOperatingSystem.ToString(); }));
                builder.AppendLine("64-bit process: " + SafeDiagnosticValue(delegate { return Environment.Is64BitProcess.ToString(); }));
                builder.AppendLine("Executable: " + SafeDiagnosticValue(delegate { return Application.ExecutablePath; }));
                builder.AppendLine();
                builder.AppendLine(ex.ToString());
                File.WriteAllText(logPath, builder.ToString(), Encoding.UTF8);
                return logPath;
            }
            catch
            {
                return "(failed to write diagnostic log)";
            }
        }

        public static bool TryEnsureWritableDirectory(string directoryPath)
        {
            try
            {
                Directory.CreateDirectory(directoryPath);
                string probe = Path.Combine(directoryPath, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "test");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string GetSystemToolPath(string fileName)
        {
            List<string> candidates = new List<string>();
            AddPathCandidate(candidates, SafeCombine(SafeGetFolderPath(Environment.SpecialFolder.System), fileName));
            AddPathCandidate(candidates, SafeCombine(SafeCombine(SafeGetEnvironmentVariable("SystemRoot"), "System32"), fileName));
            AddPathCandidate(candidates, SafeCombine(SafeCombine(SafeGetEnvironmentVariable("WINDIR"), "System32"), fileName));

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!String.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!String.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            return fileName;
        }

        private static void AddPathCandidate(List<string> candidates, string path)
        {
            if (!String.IsNullOrWhiteSpace(path))
            {
                candidates.Add(path);
            }
        }

        private static string SafeGetBaseDirectory()
        {
            try
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string SafeGetCurrentDirectory()
        {
            try
            {
                return Directory.GetCurrentDirectory();
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string SafeGetTempPath()
        {
            try
            {
                return Path.GetTempPath();
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string SafeGetFolderPath(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string SafeGetEnvironmentVariable(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name);
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string SafeCombine(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right))
            {
                return String.Empty;
            }

            try
            {
                return Path.Combine(left, right);
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string SafeDiagnosticValue(Func<string> valueFactory)
        {
            try
            {
                string value = valueFactory();
                return String.IsNullOrWhiteSpace(value) ? "(unavailable)" : value;
            }
            catch (Exception ex)
            {
                return "(unavailable: " + ex.GetType().Name + ")";
            }
        }
    }

    internal static class CommandLine
    {
        public static int Run(string[] args)
        {
            string command = args[0].Trim().ToLowerInvariant();

            if (command == "version" || command == "--version" || command == "-v")
            {
                Console.WriteLine(AppVersion.Current);
                return 0;
            }

            if (IsHelpCommand(command))
            {
                PrintUsage();
                return 0;
            }

            if (!IsKnownCommand(command))
            {
                PrintUsage();
                return 1;
            }

            ParsedArgs parsed = ParsedArgs.Parse(args.Skip(1).ToArray());
            ProfileTarget target = ProfileTarget.FromParsedArgs(parsed);

            if (command == "list")
            {
                ListProfiles(target);
                return 0;
            }

            if (command == "doctor" || command == "diagnostics" || command == "check")
            {
                CompatibilityReport report = ProfileService.RunDiagnostics(target);
                Console.WriteLine(report.ToDisplayText());
                return report.HasIssues ? 1 : 0;
            }

            if (RequiresAdministrator(command) && !ProfileService.IsAdministrator())
            {
                throw new InvalidOperationException("This command must be run from an elevated administrator prompt.");
            }

            if (command == "dry-run" || command == "plan")
            {
                ProfileRecord profile = GetProfileForCommand(parsed, target, "dry-run");
                RebuildPlan plan = ProfileService.CreateRebuildPlan(profile, DateTime.Now);
                Console.WriteLine(plan.ToDisplayText());
                return 0;
            }

            if (command == "rebuild" || command == "rebuild-profile")
            {
                ProfileRecord profile = GetProfileForCommand(parsed, target, "rebuild");
                RebuildPlan plan = ProfileService.CreateRebuildPlan(profile, DateTime.Now);
                Console.WriteLine(plan.ToDisplayText());

                if (plan.IsBlocked)
                {
                    throw new InvalidOperationException("Rebuild is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
                }

                if (!parsed.HasFlag("yes") && !ConfirmTyped("Type REBUILD to rename the profile folder and delete the listed ProfileList keys: ", "REBUILD"))
                {
                    Console.WriteLine("Cancelled.");
                    return 2;
                }

                RebuildResult result = ProfileService.RebuildProfile(profile.ProfilePath, target);
                Console.WriteLine(result.ToDisplayText());
                PromptForReboot(parsed, target);
                return 0;
            }

            if (command == "remove-registry" || command == "remove-reg" || command == "delete-registry")
            {
                ProfileRecord profile = GetProfileForCommand(parsed, target, "remove-registry");
                RegistryRemovalPlan plan = ProfileService.CreateRegistryRemovalPlan(profile);
                Console.WriteLine(plan.ToDisplayText());

                if (plan.IsBlocked)
                {
                    throw new InvalidOperationException("Registry removal is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
                }

                if (!parsed.HasFlag("yes") && !ConfirmTyped("Type REMOVEREGISTRY to delete the listed ProfileList key(s): ", "REMOVEREGISTRY"))
                {
                    Console.WriteLine("Cancelled.");
                    return 2;
                }

                RegistryRemovalResult result = ProfileService.RemoveRegistryEntries(profile.ProfilePath, target);
                Console.WriteLine(result.ToDisplayText());
                return 0;
            }

            if (command == "delete-profile" || command == "delete")
            {
                ProfileRecord profile = GetProfileForCommand(parsed, target, "delete-profile");
                DeleteProfilePlan plan = ProfileService.CreateDeleteProfilePlan(profile);
                Console.WriteLine(plan.ToDisplayText());

                if (plan.IsBlocked)
                {
                    throw new InvalidOperationException("Profile deletion is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
                }

                if (!parsed.HasFlag("yes") && !ConfirmTyped("Type DELETEPROFILE to permanently delete the profile folder and listed ProfileList key(s): ", "DELETEPROFILE"))
                {
                    Console.WriteLine("Cancelled.");
                    return 2;
                }

                DeleteProfileResult result = ProfileService.DeleteProfile(profile.ProfilePath, target);
                Console.WriteLine(result.ToDisplayText());
                return 0;
            }

            if (command == "remove-bak" || command == "fix-bak")
            {
                ProfileRecord profile = GetProfileForCommand(parsed, target, "remove-bak");
                BakRemovalPlan plan = ProfileService.CreateBakRemovalPlan(profile);
                Console.WriteLine(plan.ToDisplayText());
                if (plan.RegistryKeyNames.Count == 0)
                {
                    Console.WriteLine("No .bak ProfileList key was found for " + profile.ProfilePath + ".");
                    return 0;
                }
                if (plan.IsBlocked)
                {
                    throw new InvalidOperationException(".bak removal is blocked: " + String.Join("; ", plan.BlockReasons.ToArray()));
                }

                if (!parsed.HasFlag("yes") && !ConfirmTyped("Type REMOVEBAK to delete the listed .bak key(s): ", "REMOVEBAK"))
                {
                    Console.WriteLine("Cancelled.");
                    return 2;
                }

                BakRemovalResult result = ProfileService.RemoveBakKeys(profile.ProfilePath, target);
                Console.WriteLine(result.ToDisplayText());
                return 0;
            }

            PrintUsage();
            return 1;
        }

        private static bool IsHelpCommand(string command)
        {
            return command == "help" ||
                command == "--help" ||
                command == "-h" ||
                command == "/?";
        }

        private static bool IsKnownCommand(string command)
        {
            return command == "list" ||
                command == "doctor" ||
                command == "diagnostics" ||
                command == "check" ||
                command == "dry-run" ||
                command == "plan" ||
                command == "rebuild" ||
                command == "rebuild-profile" ||
                command == "remove-registry" ||
                command == "remove-reg" ||
                command == "delete-registry" ||
                command == "delete-profile" ||
                command == "delete" ||
                command == "remove-bak" ||
                command == "fix-bak" ||
                command == "version" ||
                command == "--version" ||
                command == "-v";
        }

        private static ProfileRecord GetProfileForCommand(ParsedArgs parsed, ProfileTarget target, string commandName)
        {
            string sid = parsed.GetSid();
            if (!String.IsNullOrWhiteSpace(sid))
            {
                return ProfileService.FindProfileBySid(sid, target);
            }

            string path = target.ResolveProfilePath(parsed);
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(commandName + " requires --profile <folder>, --path C:\\Users\\<name>, or --sid <baseSid>.");
            }

            return ProfileService.FindProfileByPath(path, target);
        }

        private static void ListProfiles(ProfileTarget target)
        {
            List<ProfileRecord> profiles = ProfileService.GetProfiles(target);
            Console.WriteLine("Target: " + target.DisplayName);
            Console.WriteLine("{0,-22} {1,-34} {2,-48} {3,-7} {4,-7} {5}", "Folder", "ProfilePath", "BaseSid", "Loaded", ".bak", "Status");
            Console.WriteLine(new String('-', 140));
            foreach (ProfileRecord profile in profiles)
            {
                Console.WriteLine(
                    "{0,-22} {1,-34} {2,-48} {3,-7} {4,-7} {5}",
                    Truncate(profile.FolderName, 21),
                    Truncate(profile.ProfilePath, 33),
                    Truncate(profile.BaseSid, 47),
                    profile.Loaded ? "Yes" : "No",
                    profile.BakKeyPresent ? "Yes" : "No",
                    profile.Status);
            }
        }

        private static string Truncate(string value, int max)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? String.Empty;
            }

            return value.Substring(0, max - 3) + "...";
        }

        private static bool ConfirmTyped(string prompt, string expected)
        {
            Console.Write(prompt);
            string input = Console.ReadLine();
            return String.Equals(input, expected, StringComparison.Ordinal);
        }

        private static void PromptForReboot(ParsedArgs parsed, ProfileTarget target)
        {
            if (parsed.HasFlag("no-reboot-prompt") || parsed.HasFlag("no-reboot"))
            {
                return;
            }

            if (parsed.HasFlag("reboot") || parsed.HasFlag("restart"))
            {
                ProfileService.RebootComputer(target.ComputerName);
                return;
            }

            Console.Write("Rebuild complete. Reboot " + target.DisplayName + " now? [y/N]: ");
            string answer = Console.ReadLine();
            if (String.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                ProfileService.RebootComputer(target.ComputerName);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("TempProfileFixer.exe");
            Console.WriteLine();
            Console.WriteLine("GUI:");
            Console.WriteLine("  TempProfileFixer.exe");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  TempProfileFixer.exe version");
            Console.WriteLine("  TempProfileFixer.exe list [--computer PCNAME] [--users-root C:\\Users]");
            Console.WriteLine("  TempProfileFixer.exe doctor [--computer PCNAME] [--users-root C:\\Users]");
            Console.WriteLine("  TempProfileFixer.exe dry-run (--profile SomeUser | --path C:\\Users\\SomeUser | --sid S-1-...) [--computer PCNAME]");
            Console.WriteLine("  TempProfileFixer.exe rebuild (--profile SomeUser | --path C:\\Users\\SomeUser | --sid S-1-...) [--computer PCNAME] [--yes] [--reboot] [--no-reboot-prompt]");
            Console.WriteLine("  TempProfileFixer.exe delete-profile (--profile SomeUser | --path C:\\Users\\SomeUser | --sid S-1-...) [--computer PCNAME] [--yes]");
            Console.WriteLine("  TempProfileFixer.exe remove-registry (--profile SomeUser | --path C:\\Users\\SomeUser | --sid S-1-...) [--computer PCNAME] [--yes]");
            Console.WriteLine("  TempProfileFixer.exe remove-bak (--profile SomeUser | --path C:\\Users\\SomeUser | --sid S-1-...) [--computer PCNAME] [--yes]");
            Console.WriteLine();
            Console.WriteLine("Targeting:");
            Console.WriteLine("  --profile SomeUser       Selects C:\\Users\\SomeUser under the target users root.");
            Console.WriteLine("  --computer PCNAME        Uses \\\\PCNAME\\C$\\Users plus remote HKLM ProfileList access.");
            Console.WriteLine("  --users-root PATH        Overrides the filesystem users root.");
            Console.WriteLine("  --profile-root PATH      Overrides the ProfileImagePath root used for registry matching; inferred from --users-root when omitted.");
            Console.WriteLine();
            Console.WriteLine("rebuild renames the profile folder to .old<date>, exports/deletes the matching normal SID key");
            Console.WriteLine("and matching .bak key, then prompts for reboot.");
        }

        private static bool RequiresAdministrator(string command)
        {
            return command == "rebuild" ||
                command == "rebuild-profile" ||
                command == "remove-registry" ||
                command == "remove-reg" ||
                command == "delete-registry" ||
                command == "delete-profile" ||
                command == "delete" ||
                command == "remove-bak" ||
                command == "fix-bak";
        }
    }

    internal sealed class ParsedArgs
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> positional = new List<string>();

        public static ParsedArgs Parse(string[] args)
        {
            ParsedArgs parsed = new ParsedArgs();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    string token = arg.Substring(2);
                    string key;
                    string value;
                    SplitOption(token, out key, out value);
                    if (value == null && i + 1 < args.Length && !IsOption(args[i + 1]))
                    {
                        value = args[++i];
                    }

                    if (value == null)
                    {
                        parsed.flags.Add(key);
                    }
                    else
                    {
                        parsed.values[key] = value;
                    }
                }
                else if (arg.StartsWith("/", StringComparison.Ordinal) && !LooksLikePath(arg))
                {
                    string token = arg.Substring(1);
                    string key;
                    string value;
                    SplitOption(token, out key, out value);
                    if (value == null)
                    {
                        parsed.flags.Add(key);
                    }
                    else
                    {
                        parsed.values[key] = value;
                    }
                }
                else
                {
                    parsed.positional.Add(arg);
                }
            }

            return parsed;
        }

        public bool HasFlag(string key)
        {
            return flags.Contains(key);
        }

        public string GetValue(string key, string defaultValue)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : defaultValue;
        }

        public bool HasValue(string key)
        {
            return values.ContainsKey(key);
        }

        public string GetFirstValue(params string[] keys)
        {
            foreach (string key in keys)
            {
                string value;
                if (values.TryGetValue(key, out value))
                {
                    return value;
                }
            }

            return null;
        }

        public string GetPath()
        {
            string value;
            if (values.TryGetValue("path", out value) || values.TryGetValue("p", out value))
            {
                return value;
            }

            return positional.Count > 0 ? positional[0] : null;
        }

        public string GetProfileName()
        {
            return GetFirstValue("profile", "user", "username", "folder");
        }

        public string GetSid()
        {
            return GetFirstValue("sid", "base-sid");
        }

        private static bool IsOption(string value)
        {
            return value.StartsWith("--", StringComparison.Ordinal) ||
                (value.StartsWith("/", StringComparison.Ordinal) && !LooksLikePath(value));
        }

        private static bool LooksLikePath(string value)
        {
            return value.Length >= 3 && Char.IsLetter(value[0]) && value[1] == ':' &&
                (value[2] == '\\' || value[2] == '/');
        }

        private static void SplitOption(string token, out string key, out string value)
        {
            int equals = token.IndexOf('=');
            int colon = token.IndexOf(':');
            int index = -1;
            if (equals >= 0 && colon >= 0)
            {
                index = Math.Min(equals, colon);
            }
            else if (equals >= 0)
            {
                index = equals;
            }
            else if (colon >= 0)
            {
                index = colon;
            }

            if (index < 0)
            {
                key = token;
                value = null;
            }
            else
            {
                key = token.Substring(0, index);
                value = token.Substring(index + 1).Trim('"');
            }
        }
    }

    internal sealed class ProfileTarget
    {
        public string ComputerName { get; private set; }
        public string UsersRoot { get; private set; }
        public string ProfileImageRoot { get; private set; }

        public bool IsRemote
        {
            get { return !String.IsNullOrWhiteSpace(ComputerName); }
        }

        public string DisplayName
        {
            get { return IsRemote ? "\\\\" + ComputerName : "local computer"; }
        }

        public string RegistryPath
        {
            get { return ProfileService.GetProfileListRegPath(ComputerName); }
        }

        public static ProfileTarget FromParsedArgs(ParsedArgs parsed)
        {
            string computer = NormalizeComputerName(parsed.GetFirstValue("computer", "target", "remote-computer"));
            string systemDrive = parsed.GetValue("system-drive", Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
            if (String.IsNullOrWhiteSpace(systemDrive))
            {
                systemDrive = "C:";
            }

            string defaultProfileRoot = Path.Combine(EnsureDriveRoot(systemDrive), "Users");
            bool hasExplicitProfileRoot = parsed.HasValue("profile-root");
            string profileRoot = parsed.GetValue("profile-root", defaultProfileRoot);
            string usersRoot = parsed.GetValue("users-root", null);
            if (String.IsNullOrWhiteSpace(usersRoot))
            {
                usersRoot = String.IsNullOrWhiteSpace(computer) ? profileRoot : ToRemoteAdminShare(computer, profileRoot);
            }
            else
            {
                if (!hasExplicitProfileRoot)
                {
                    profileRoot = InferProfileImageRootFromUsersRoot(usersRoot, defaultProfileRoot);
                }

                if (!String.IsNullOrWhiteSpace(computer))
                {
                    usersRoot = ConvertLogicalRootToRemote(computer, usersRoot);
                }
            }

            return new ProfileTarget
            {
                ComputerName = computer,
                UsersRoot = usersRoot,
                ProfileImageRoot = profileRoot
            };
        }

        public static ProfileTarget Local(string usersRoot)
        {
            string root = String.IsNullOrWhiteSpace(usersRoot)
                ? Path.Combine(EnsureDriveRoot(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"), "Users")
                : usersRoot;
            return new ProfileTarget
            {
                ComputerName = null,
                UsersRoot = root,
                ProfileImageRoot = root
            };
        }

        public string ResolveProfilePath(ParsedArgs parsed)
        {
            string profileName = parsed.GetProfileName();
            if (!String.IsNullOrWhiteSpace(profileName))
            {
                return Path.Combine(UsersRoot, profileName);
            }

            string path = parsed.GetPath();
            if (String.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (!LooksLikeDrivePath(path) && !path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return Path.Combine(UsersRoot, path);
            }

            if (IsRemote)
            {
                return ConvertLogicalPathToActual(path);
            }

            return path;
        }

        public string GetLogicalPathForFolder(string folderName)
        {
            return Path.Combine(ProfileImageRoot, folderName);
        }

        private string ConvertLogicalPathToActual(string path)
        {
            string trimmed = path.Trim();
            string normalizedProfileRoot = ProfileService.NormalizePath(ProfileImageRoot);
            string normalizedInput = ProfileService.NormalizePath(trimmed);
            if (!String.IsNullOrWhiteSpace(normalizedProfileRoot) &&
                normalizedInput.StartsWith(normalizedProfileRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = trimmed.Substring(ProfileImageRoot.TrimEnd('\\').Length).TrimStart('\\');
                return Path.Combine(UsersRoot, suffix);
            }

            return ConvertLogicalRootToRemote(ComputerName, trimmed);
        }

        private static string ConvertLogicalRootToRemote(string computer, string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return path;
            }

            if (LooksLikeDrivePath(path))
            {
                return ToRemoteAdminShare(computer, path);
            }

            return path;
        }

        private static string InferProfileImageRootFromUsersRoot(string usersRoot, string defaultProfileRoot)
        {
            if (String.IsNullOrWhiteSpace(usersRoot))
            {
                return defaultProfileRoot;
            }

            string root = usersRoot.Trim().Replace('/', '\\');
            Match adminShareMatch = Regex.Match(root, @"^\\\\[^\\]+\\([A-Za-z])\$($|\\(.*)$)");
            if (adminShareMatch.Success)
            {
                string suffix = adminShareMatch.Groups[3].Success ? adminShareMatch.Groups[3].Value : String.Empty;
                return Char.ToUpperInvariant(adminShareMatch.Groups[1].Value[0]) + @":\" + suffix.TrimStart('\\');
            }

            if (LooksLikeDrivePath(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return root;
            }

            string driveRoot = EnsureDriveRoot(Path.GetPathRoot(defaultProfileRoot));
            return Path.Combine(driveRoot, root);
        }

        private static string ToRemoteAdminShare(string computer, string localPath)
        {
            string full = localPath.Replace('/', '\\');
            if (!LooksLikeDrivePath(full))
            {
                return full;
            }

            string drive = Char.ToUpperInvariant(full[0]).ToString();
            string remainder = full.Substring(2).TrimStart('\\');
            return @"\\" + computer + "\\" + drive + "$" + (String.IsNullOrWhiteSpace(remainder) ? String.Empty : "\\" + remainder);
        }

        private static string EnsureDriveRoot(string systemDrive)
        {
            string drive = String.IsNullOrWhiteSpace(systemDrive) ? "C:" : systemDrive.Trim().Replace('/', '\\').TrimEnd('\\');
            if (drive.Length == 2 && Char.IsLetter(drive[0]) && drive[1] == ':')
            {
                return drive + "\\";
            }

            return drive;
        }

        private static bool LooksLikeDrivePath(string value)
        {
            return value != null && value.Length >= 2 && Char.IsLetter(value[0]) && value[1] == ':';
        }

        private static string NormalizeComputerName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string computer = value.Trim().TrimStart('\\').TrimEnd('\\');
            if (String.Equals(computer, ".", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(computer, "localhost", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(computer, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return computer;
        }
    }

}
