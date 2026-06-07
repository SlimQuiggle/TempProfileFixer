using Microsoft.Win32;
using System;
using System.Collections.Generic;
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
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (!String.IsNullOrWhiteSpace(baseDirectory))
            {
                candidates.Add(baseDirectory);
            }

            string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!String.IsNullOrWhiteSpace(commonAppData))
            {
                candidates.Add(Path.Combine(commonAppData, "TempProfileFixer"));
            }

            candidates.Add(Path.Combine(Path.GetTempPath(), "TempProfileFixer"));

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TryEnsureWritableDirectory(candidate))
                {
                    return candidate;
                }
            }

            return Path.GetTempPath();
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
                builder.AppendLine("Machine: " + Environment.MachineName);
                builder.AppendLine("User: " + Environment.UserDomainName + "\\" + Environment.UserName);
                builder.AppendLine("OS: " + Environment.OSVersion.VersionString);
                builder.AppendLine(".NET: " + Environment.Version);
                builder.AppendLine("64-bit OS: " + Environment.Is64BitOperatingSystem);
                builder.AppendLine("64-bit process: " + Environment.Is64BitProcess);
                builder.AppendLine("Executable: " + Application.ExecutablePath);
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
    }

    internal static class CommandLine
    {
        public static int Run(string[] args)
        {
            string command = args[0].Trim().ToLowerInvariant();
            ParsedArgs parsed = ParsedArgs.Parse(args.Skip(1).ToArray());
            ProfileTarget target = ProfileTarget.FromParsedArgs(parsed);

            if (command == "help" || command == "--help" || command == "-h" || command == "/?")
            {
                PrintUsage();
                return 0;
            }

            if (command == "list")
            {
                ListProfiles(target);
                return 0;
            }

            if (command == "doctor" || command == "diagnostics" || command == "check")
            {
                CompatibilityReport report = ProfileService.RunDiagnostics(target);
                Console.WriteLine(report.ToDisplayText());
                return report.HasFailures ? 1 : 0;
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
                if (!profile.BakKeyPresent)
                {
                    Console.WriteLine("No .bak ProfileList key was found for " + profile.ProfilePath + ".");
                    return 0;
                }

                Console.WriteLine("The following .bak ProfileList key(s) will be exported and deleted:");
                foreach (string keyName in profile.BakKeyNames)
                {
                    Console.WriteLine("  " + profile.RegistryRoot + "\\" + keyName);
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

    internal sealed class MainForm : Form
    {
        private readonly string usersRoot;
        private readonly DataGridView grid;
        private readonly Label statusLabel;
        private readonly Button rebuildButton;
        private readonly Button deleteProfileButton;
        private readonly Button helpButton;
        private readonly ToolStripMenuItem rebuildMenuItem;
        private readonly ToolStripMenuItem removeRegistryMenuItem;
        private readonly ToolStripMenuItem copySidMenuItem;
        private readonly ToolStripMenuItem openProfilePathMenuItem;
        private readonly ToolStripMenuItem openRegistryMenuItem;
        private readonly NotifyIcon trayIcon;
        private readonly Icon appIcon;
        private readonly Image headerImage;
        private List<ProfileRecord> profiles = new List<ProfileRecord>();

        public MainForm()
        {
            usersRoot = ProfileTarget.Local(null).UsersRoot;

            Text = "Temp Profile Fixer";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1220, 700);
            MinimumSize = new Size(1040, 560);
            appIcon = ProfileService.LoadApplicationIcon();
            if (appIcon != null)
            {
                Icon = appIcon;
            }
            headerImage = ProfileService.LoadHeaderImage();

            trayIcon = CreateTrayIcon();

            MenuStrip menuStrip = new MenuStrip();
            menuStrip.Dock = DockStyle.Top;
            ToolStripMenuItem helpMenu = new ToolStripMenuItem("Help");
            helpMenu.DropDownItems.Add("Help / FAQ", null, delegate { ShowHelpDialog(); });
            menuStrip.Items.Add(helpMenu);
            MainMenuStrip = menuStrip;

            Panel topPanel = new Panel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 122;
            topPanel.Padding = new Padding(12, 10, 12, 8);

            PictureBox headerIcon = new PictureBox();
            headerIcon.Image = headerImage;
            headerIcon.SizeMode = PictureBoxSizeMode.Zoom;
            headerIcon.Location = new Point(14, 10);
            headerIcon.Size = new Size(58, 58);

            Label titleLabel = new Label();
            titleLabel.Text = "Temp Profile Fixer";
            titleLabel.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(84, 13);

            Label subtitleLabel = new Label();
            subtitleLabel.Text = "Rename a local profile to .old<date>, delete matching ProfileList keys, then reboot.";
            subtitleLabel.AutoSize = true;
            subtitleLabel.Location = new Point(86, 43);

            Button refreshButton = new Button();
            refreshButton.Text = "Refresh";
            refreshButton.Width = 96;
            refreshButton.Height = 30;
            refreshButton.Location = new Point(704, 78);
            refreshButton.Click += delegate { RefreshProfiles(); };

            rebuildButton = new Button();
            rebuildButton.Text = "Rebuild Profile";
            rebuildButton.Width = 204;
            rebuildButton.Height = 38;
            rebuildButton.Location = new Point(84, 74);
            rebuildButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            rebuildButton.BackColor = Color.FromArgb(36, 115, 216);
            rebuildButton.ForeColor = Color.White;
            rebuildButton.FlatStyle = FlatStyle.Flat;
            rebuildButton.FlatAppearance.BorderSize = 0;
            rebuildButton.UseVisualStyleBackColor = false;
            rebuildButton.Click += delegate { RebuildSelectedProfileAndReboot(); };

            deleteProfileButton = new Button();
            deleteProfileButton.Text = "Delete Profile";
            deleteProfileButton.Width = 204;
            deleteProfileButton.Height = 38;
            deleteProfileButton.Location = new Point(304, 74);
            deleteProfileButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            deleteProfileButton.BackColor = Color.FromArgb(196, 48, 43);
            deleteProfileButton.ForeColor = Color.White;
            deleteProfileButton.FlatStyle = FlatStyle.Flat;
            deleteProfileButton.FlatAppearance.BorderSize = 0;
            deleteProfileButton.UseVisualStyleBackColor = false;
            deleteProfileButton.Click += delegate { DeleteSelectedProfile(); };

            helpButton = new Button();
            helpButton.Text = "Help / FAQ";
            helpButton.Width = 148;
            helpButton.Height = 38;
            helpButton.Location = new Point(524, 74);
            helpButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            helpButton.BackColor = Color.FromArgb(38, 132, 104);
            helpButton.ForeColor = Color.White;
            helpButton.FlatStyle = FlatStyle.Flat;
            helpButton.FlatAppearance.BorderSize = 0;
            helpButton.UseVisualStyleBackColor = false;
            helpButton.Cursor = Cursors.Hand;
            helpButton.Click += delegate { ShowHelpDialog(); };

            topPanel.Controls.Add(headerIcon);
            topPanel.Controls.Add(titleLabel);
            topPanel.Controls.Add(subtitleLabel);
            topPanel.Controls.Add(refreshButton);
            topPanel.Controls.Add(rebuildButton);
            topPanel.Controls.Add(deleteProfileButton);
            topPanel.Controls.Add(helpButton);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.BackgroundColor = Color.White;
            grid.CellMouseDown += GridCellMouseDown;
            grid.SelectionChanged += delegate { UpdateActionState(); };
            grid.CellDoubleClick += delegate { ShowSelectedPlan(); };
            AddColumn("FolderName", "Folder", 130);
            AddColumn("ProfilePath", "Path", 245);
            AddColumn("BaseSid", "Base SID", 300);
            AddColumn("NormalKey", "Normal", 64);
            AddColumn("BakKey", ".bak", 56);
            AddColumn("Loaded", "Loaded", 68);
            AddColumn("Special", "Special", 68);
            AddColumn("Status", "Status", 270);

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            rebuildMenuItem = new ToolStripMenuItem("Rebuild Profile", null, delegate { RebuildSelectedProfileAndReboot(); });
            removeRegistryMenuItem = new ToolStripMenuItem("Remove Registry Entry", null, delegate { RemoveSelectedRegistryEntries(); });
            openProfilePathMenuItem = new ToolStripMenuItem("Open Profile Path", null, delegate { OpenSelectedProfilePath(); });
            copySidMenuItem = new ToolStripMenuItem("Copy SID", null, delegate { CopySelectedSid(); });
            openRegistryMenuItem = new ToolStripMenuItem("Open to Registry", null, delegate { OpenSelectedRegistryKey(); });
            contextMenu.Items.AddRange(new ToolStripItem[]
            {
                rebuildMenuItem,
                removeRegistryMenuItem,
                new ToolStripSeparator(),
                openProfilePathMenuItem,
                copySidMenuItem,
                openRegistryMenuItem,
                new ToolStripSeparator(),
                new ToolStripMenuItem("refresh", null, delegate { RefreshProfiles(); })
            });
            contextMenu.Opening += delegate { UpdateActionState(); };
            grid.ContextMenuStrip = contextMenu;

            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Bottom;
            statusPanel.Height = 34;
            statusPanel.Padding = new Padding(10, 8, 10, 6);
            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Text = "Ready.";
            Label creditLabel = new Label();
            creditLabel.Dock = DockStyle.Right;
            creditLabel.Width = 180;
            creditLabel.TextAlign = ContentAlignment.MiddleRight;
            creditLabel.ForeColor = Color.FromArgb(120, 120, 120);
            creditLabel.Font = new Font("Segoe UI", 8, FontStyle.Regular);
            creditLabel.Text = "Created by Flex3Designs";
            statusPanel.Controls.Add(statusLabel);
            statusPanel.Controls.Add(creditLabel);

            Controls.Add(grid);
            Controls.Add(topPanel);
            Controls.Add(statusPanel);
            Controls.Add(menuStrip);
            Shown += delegate { RefreshProfiles(); };
            FormClosed += delegate
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
                if (appIcon != null)
                {
                    appIcon.Dispose();
                }
                if (headerImage != null)
                {
                    headerImage.Dispose();
                }
            };
        }

        private NotifyIcon CreateTrayIcon()
        {
            try
            {
                NotifyIcon notifyIcon = new NotifyIcon();
                notifyIcon.Text = "Temp Profile Fixer";
                notifyIcon.Icon = appIcon ?? SystemIcons.Application;
                notifyIcon.Visible = true;
                notifyIcon.DoubleClick += delegate
                {
                    Show();
                    WindowState = FormWindowState.Normal;
                    Activate();
                };
                ContextMenuStrip trayMenu = new ContextMenuStrip();
                trayMenu.Items.Add("Show", null, delegate
                {
                    Show();
                    WindowState = FormWindowState.Normal;
                    Activate();
                });
                trayMenu.Items.Add("Refresh", null, delegate { RefreshProfiles(); });
                trayMenu.Items.Add("Exit", null, delegate { Close(); });
                notifyIcon.ContextMenuStrip = trayMenu;
                return notifyIcon;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException("Tray icon initialization failed", ex);
                return null;
            }
        }

        private void ShowHelpDialog()
        {
            Form dialog = new Form();
            dialog.Text = "Temp Profile Fixer Help / FAQ";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(900, 620);
            dialog.MinimumSize = new Size(760, 520);
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = true;
            if (appIcon != null)
            {
                dialog.Icon = appIcon;
            }

            Panel headerPanel = new Panel();
            headerPanel.Dock = DockStyle.Top;
            headerPanel.Height = 70;
            headerPanel.Padding = new Padding(18, 12, 18, 8);
            headerPanel.BackColor = Color.FromArgb(245, 248, 250);

            Label title = new Label();
            title.Text = "Temp Profile Fixer Help / FAQ";
            title.Font = new Font("Segoe UI", 15, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(18, 10);

            Label summary = new Label();
            summary.Text = "Quick reference for profile rebuilds, registry cleanup, command-line runs, and blocked states.";
            summary.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            summary.ForeColor = Color.FromArgb(82, 88, 96);
            summary.AutoSize = true;
            summary.Location = new Point(20, 40);

            headerPanel.Controls.Add(title);
            headerPanel.Controls.Add(summary);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Segoe UI", 9);
            AddOverviewHelpTab(tabs);
            AddButtonHelpTab(tabs);
            AddRightClickHelpTab(tabs);
            AddFaqHelpTab(tabs);
            AddCommandLineHelpTab(tabs);

            Button closeButton = new Button();
            closeButton.Text = "Close";
            closeButton.Width = 96;
            closeButton.Height = 30;
            closeButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            closeButton.Location = new Point(780, 10);
            closeButton.Click += delegate { dialog.Close(); };

            Panel buttonPanel = new Panel();
            buttonPanel.Dock = DockStyle.Bottom;
            buttonPanel.Height = 50;
            buttonPanel.Padding = new Padding(12, 10, 12, 10);
            buttonPanel.Controls.Add(closeButton);
            buttonPanel.Resize += delegate
            {
                closeButton.Left = buttonPanel.ClientSize.Width - closeButton.Width - 12;
            };

            dialog.Controls.Add(tabs);
            dialog.Controls.Add(buttonPanel);
            dialog.Controls.Add(headerPanel);
            dialog.ShowDialog(this);
        }

        private static void AddOverviewHelpTab(TabControl tabs)
        {
            RichTextBox box = AddHelpTab(tabs, "Overview");
            AppendHeading(box, "What this tool does");
            AppendParagraph(box, "Temp Profile Fixer inventories profile folders under C:\\Users, matches each folder to its base SID under HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList, and shows normal/.bak registry state.");
            AppendParagraph(box, "The rebuild workflow is designed for the common admin task of renaming a user profile to .old<date>, removing stale ProfileList state, and rebooting before the target user's next sign-in.");
            AppendHeading(box, "Safety model");
            AppendBullet(box, "Registry keys are exported before they are deleted.");
            AppendBullet(box, "Rebuild preserves the old folder as a timestamped .old rollback copy.");
            AppendBullet(box, "Loaded, current, special/system, missing, and ambiguous profiles are blocked.");
            AppendBullet(box, "The tool does not collect the target user's password or attempt a fake login.");
            AppendHeading(box, "Expected final step");
            AppendParagraph(box, "After rebuild and reboot, have the target user sign in normally so Windows creates a clean local profile.");
        }

        private static void AddButtonHelpTab(TabControl tabs)
        {
            RichTextBox box = AddHelpTab(tabs, "Buttons");
            AppendHeading(box, "Rebuild Profile");
            AppendParagraph(box, "Runs the full .old rebuild: export matching ProfileList keys, rename the selected C:\\Users folder to .old<date>, delete matching normal and .bak registry keys, then start the reboot flow.");
            AppendHeading(box, "Delete Profile");
            AppendParagraph(box, "Permanently deletes the selected profile folder and matching ProfileList registry keys after confirmation. This action does not create a .old folder copy.");
            AppendHeading(box, "Help / FAQ");
            AppendParagraph(box, "Opens this formatted help window.");
            AppendHeading(box, "Refresh");
            AppendParagraph(box, "Reloads the C:\\Users inventory, registry matches, loaded/special profile state, and blocked/warning status.");
        }

        private static void AddRightClickHelpTab(TabControl tabs)
        {
            RichTextBox box = AddHelpTab(tabs, "Right-click");
            AppendHeading(box, "Rebuild Profile");
            AppendParagraph(box, "Same action as the top Rebuild Profile button.");
            AppendHeading(box, "Remove Registry Entry");
            AppendParagraph(box, "Exports and deletes the selected profile's matching normal and .bak ProfileList keys only. It does not rename or delete the profile folder.");
            AppendHeading(box, "Open Profile Path");
            AppendParagraph(box, "Opens the selected profile folder in File Explorer.");
            AppendHeading(box, "Copy SID");
            AppendParagraph(box, "Copies the selected row's base SID to the clipboard.");
            AppendHeading(box, "Open to Registry");
            AppendParagraph(box, "Opens Registry Editor at the selected profile's normal ProfileList key when one is available.");
            AppendHeading(box, "Double-click a row");
            AppendParagraph(box, "Shows the non-destructive rebuild plan for the selected profile.");
        }

        private static void AddFaqHelpTab(TabControl tabs)
        {
            RichTextBox box = AddHelpTab(tabs, "FAQ");
            AppendHeading(box, "Why are Rebuild Profile and Delete Profile greyed out?");
            AppendParagraph(box, "The selected profile is blocked. Common reasons are that it is the current admin profile, loaded/locked, special/system, missing a matching SID, matched to multiple SIDs or normal keys, or Windows profile state could not be verified.");
            AppendHeading(box, "Does Rebuild Profile delete user files?");
            AppendParagraph(box, "No. Rebuild Profile renames the selected folder to a timestamped .old folder so it remains on disk as a rollback copy.");
            AppendHeading(box, "Does Delete Profile keep the old folder?");
            AppendParagraph(box, "No. Delete Profile permanently removes the selected profile folder after confirmation. Registry backups and logs are still written.");
            AppendHeading(box, "What happens to .bak keys?");
            AppendParagraph(box, "Rebuild Profile, Delete Profile, and Remove Registry Entry include the matching .bak key when one exists for the selected base SID.");
            AppendHeading(box, "Why reboot after a rebuild?");
            AppendParagraph(box, "The reboot clears the workflow before the target user's next real sign-in. After restart, Windows can create and load a clean profile using fresh ProfileList state.");
            AppendHeading(box, "Where are backups and logs?");
            AppendParagraph(box, "Backups and logs are written under backups\\ and logs\\ in the first writable data folder. The tool tries the EXE folder first, then ProgramData\\TempProfileFixer, then the user's Temp folder.");
            AppendHeading(box, "What should I run if the tool fails on a computer?");
            AppendParagraph(box, "Run TempProfileFixer.exe doctor on that computer. Elevated diagnostics are most complete, but doctor can also run unelevated so startup and prerequisite failures are visible. The diagnostics output checks elevation, users root access, ProfileList registry access, WMI profile state, helper tools, and writable log storage.");
            AppendHeading(box, "What if the EXE does not launch at all?");
            AppendParagraph(box, "Use the packaged Run-Diagnostics.cmd helper from the same folder. It checks launch prerequisites, runs doctor, and keeps the window open so the result is readable after a double-click launch.");
            AppendHeading(box, "What if Windows says the files are blocked?");
            AppendParagraph(box, "After confirming the ZIP came from the official GitHub release, run Unblock-Package.cmd from the extracted folder. It only removes the Windows download block from the tool files in that folder, using Unblock-File when available and direct Zone.Identifier stream clearing on older PowerShell versions.");
        }

        private static void AddCommandLineHelpTab(TabControl tabs)
        {
            RichTextBox box = AddHelpTab(tabs, "Command line");
            AppendHeading(box, "Local examples");
            AppendCode(box, "TempProfileFixer.exe list");
            AppendCode(box, "TempProfileFixer.exe doctor");
            AppendCode(box, "TempProfileFixer.exe dry-run --profile jsmith");
            AppendCode(box, "TempProfileFixer.exe rebuild --profile jsmith --yes --reboot");
            AppendHeading(box, "Remote examples");
            AppendParagraph(box, "Use --computer when the admin share and Remote Registry/WMI access are available for the target workstation.");
            AppendCode(box, "TempProfileFixer.exe doctor --computer PC-1234");
            AppendCode(box, "TempProfileFixer.exe list --computer PC-1234");
            AppendCode(box, "TempProfileFixer.exe dry-run --computer PC-1234 --profile jsmith");
            AppendCode(box, "TempProfileFixer.exe rebuild --computer PC-1234 --profile jsmith --yes --reboot");
            AppendCode(box, "TempProfileFixer.exe rebuild --computer PC-1234 --users-root \\\\PC-1234\\D$\\Users --profile jsmith --yes");
            AppendParagraph(box, "When --profile-root is omitted, the registry ProfileImagePath root is inferred from --users-root.");
            AppendHeading(box, "More commands");
            AppendParagraph(box, "The packaged README beside the EXE has the complete command reference with examples.");
        }

        private static RichTextBox AddHelpTab(TabControl tabs, string title)
        {
            TabPage page = new TabPage(title);
            RichTextBox box = new RichTextBox();
            box.Dock = DockStyle.Fill;
            box.ReadOnly = true;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.White;
            box.Font = new Font("Segoe UI", 10);
            box.Margin = new Padding(0);
            page.Padding = new Padding(18);
            page.Controls.Add(box);
            tabs.TabPages.Add(page);
            return box;
        }

        private static void AppendHeading(RichTextBox box, string text)
        {
            if (box.TextLength > 0)
            {
                box.AppendText(Environment.NewLine);
            }
            AppendStyled(box, text + Environment.NewLine, new Font("Segoe UI", 12, FontStyle.Bold), Color.FromArgb(32, 77, 120));
        }

        private static void AppendParagraph(RichTextBox box, string text)
        {
            AppendStyled(box, text + Environment.NewLine, new Font("Segoe UI", 10, FontStyle.Regular), Color.FromArgb(35, 35, 35));
        }

        private static void AppendBullet(RichTextBox box, string text)
        {
            AppendStyled(box, "  - " + text + Environment.NewLine, new Font("Segoe UI", 10, FontStyle.Regular), Color.FromArgb(35, 35, 35));
        }

        private static void AppendCode(RichTextBox box, string text)
        {
            AppendStyled(box, "  " + text + Environment.NewLine, new Font("Consolas", 10, FontStyle.Regular), Color.FromArgb(82, 48, 120));
        }

        private static void AppendStyled(RichTextBox box, string text, Font font, Color color)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionFont = font;
            box.SelectionColor = color;
            box.AppendText(text);
            box.SelectionColor = box.ForeColor;
            box.SelectionFont = box.Font;
        }

        private void AddColumn(string name, string header, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.Name = name;
            column.HeaderText = header;
            column.Width = width;
            column.ReadOnly = true;
            grid.Columns.Add(column);
        }

        private void RefreshProfiles()
        {
            try
            {
                statusLabel.Text = "Refreshing profiles...";
                Refresh();
                profiles = ProfileService.GetProfiles(usersRoot);
                grid.Rows.Clear();

                foreach (ProfileRecord profile in profiles)
                {
                    int rowIndex = grid.Rows.Add(
                        profile.FolderName,
                        profile.ProfilePath,
                        profile.BaseSid,
                        profile.NormalKeyPresent ? "Yes" : "No",
                        profile.BakKeyPresent ? "Yes" : "No",
                        profile.Loaded ? "Yes" : "No",
                        profile.Special ? "Yes" : "No",
                        profile.Status);
                    DataGridViewRow row = grid.Rows[rowIndex];
                    row.Tag = profile;
                    if (profile.IsBlocked)
                    {
                        row.DefaultCellStyle.BackColor = Color.FromArgb(255, 235, 238);
                    }
                    else if (profile.Warnings.Count > 0)
                    {
                        row.DefaultCellStyle.BackColor = Color.FromArgb(255, 249, 196);
                    }
                }

                statusLabel.Text = "Loaded " + profiles.Count + " profile folder(s) from " + usersRoot + ".";
                UpdateActionState();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Refresh failed.";
                MessageBox.Show(ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void GridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[Math.Max(e.ColumnIndex, 0)];
                UpdateActionState();
            }
        }

        private ProfileRecord GetSelectedProfile()
        {
            if (grid.SelectedRows.Count < 1)
            {
                return null;
            }

            return grid.SelectedRows[0].Tag as ProfileRecord;
        }

        private void UpdateActionState()
        {
            ProfileRecord selected = GetSelectedProfile();
            bool hasSelection = selected != null;
            bool canRebuild = hasSelection && !selected.IsBlocked;
            rebuildButton.Enabled = canRebuild;
            ApplyRebuildButtonStyle(canRebuild);
            deleteProfileButton.Enabled = canRebuild;
            ApplyDeleteProfileButtonStyle(canRebuild);
            rebuildMenuItem.Enabled = canRebuild;
            removeRegistryMenuItem.Enabled = hasSelection && selected.HasRegistryEntries && !selected.IsBlocked;
            copySidMenuItem.Enabled = hasSelection && !String.IsNullOrWhiteSpace(selected.BaseSid);
            openProfilePathMenuItem.Enabled = hasSelection && Directory.Exists(selected.ProfilePath);
            openRegistryMenuItem.Enabled = hasSelection && selected.HasRegistryEntries;
        }

        private void ApplyRebuildButtonStyle(bool canRebuild)
        {
            rebuildButton.BackColor = canRebuild ? Color.FromArgb(36, 115, 216) : Color.FromArgb(176, 180, 186);
            rebuildButton.ForeColor = canRebuild ? Color.White : Color.FromArgb(82, 88, 96);
            rebuildButton.Cursor = canRebuild ? Cursors.Hand : Cursors.Default;
        }

        private void ApplyDeleteProfileButtonStyle(bool canDelete)
        {
            deleteProfileButton.BackColor = canDelete ? Color.FromArgb(196, 48, 43) : Color.FromArgb(176, 180, 186);
            deleteProfileButton.ForeColor = canDelete ? Color.White : Color.FromArgb(82, 88, 96);
            deleteProfileButton.Cursor = canDelete ? Cursors.Hand : Cursors.Default;
        }

        private void ShowSelectedPlan()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected == null)
            {
                MessageBox.Show("Select a profile first.", "Dry Run", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RebuildPlan plan = ProfileService.CreateRebuildPlan(selected, DateTime.Now);
            ShowTextDialog("Dry Run / Rebuild Plan", plan.ToDisplayText());
        }

        private void RebuildSelectedProfileAndReboot()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected == null)
            {
                MessageBox.Show("Select a profile first.", "Rebuild Profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RebuildPlan plan = ProfileService.CreateRebuildPlan(selected, DateTime.Now);
            if (plan.IsBlocked)
            {
                MessageBox.Show(String.Join(Environment.NewLine, plan.BlockReasons.ToArray()), "Profile rebuild is blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirm = MessageBox.Show(
                plan.ToDisplayText() +
                Environment.NewLine +
                "After the rebuild succeeds, this tool will start a reboot so the target user can sign in cleanly after restart." +
                Environment.NewLine +
                Environment.NewLine +
                "Continue?",
                "Confirm profile rebuild",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                statusLabel.Text = "Rebuilding " + selected.FolderName + "...";
                Refresh();
                RebuildResult result = ProfileService.RebuildProfile(selected.ProfilePath, usersRoot);
                MessageBox.Show(
                    result.ToDisplayText() + Environment.NewLine + "Click OK to start the reboot now.",
                    "Rebuild completed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                statusLabel.Text = "Rebuild completed. Starting reboot...";
                ProfileService.RebootComputer();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Rebuild failed.";
                MessageBox.Show(ex.Message, "Rebuild failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteSelectedProfile()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected == null)
            {
                MessageBox.Show("Select a profile first.", "Delete Profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DeleteProfilePlan plan = ProfileService.CreateDeleteProfilePlan(selected);
            if (plan.IsBlocked)
            {
                MessageBox.Show(String.Join(Environment.NewLine, plan.BlockReasons.ToArray()), "Profile deletion is blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirm = MessageBox.Show(
                plan.ToDisplayText() +
                Environment.NewLine +
                "This permanently deletes the selected profile folder. It does not rename it to .old." +
                Environment.NewLine +
                Environment.NewLine +
                "Continue?",
                "Confirm profile deletion",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                statusLabel.Text = "Deleting " + selected.FolderName + "...";
                Refresh();
                DeleteProfileResult result = ProfileService.DeleteProfile(selected.ProfilePath, usersRoot);
                MessageBox.Show(result.ToDisplayText(), "Profile deleted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshProfiles();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Delete failed.";
                MessageBox.Show(ex.Message, "Delete failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RemoveSelectedRegistryEntries()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected == null || !selected.HasRegistryEntries)
            {
                MessageBox.Show("Select a profile that has a matching ProfileList registry entry.", "Remove Registry Entry", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RegistryRemovalPlan plan = ProfileService.CreateRegistryRemovalPlan(selected);
            if (plan.IsBlocked)
            {
                MessageBox.Show(String.Join(Environment.NewLine, plan.BlockReasons.ToArray()), "Registry removal is blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirm = MessageBox.Show(plan.ToDisplayText() + Environment.NewLine + "Continue?", "Confirm registry removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                RegistryRemovalResult result = ProfileService.RemoveRegistryEntries(selected.ProfilePath, usersRoot);
                MessageBox.Show(result.ToDisplayText(), "Registry removal completed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshProfiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Registry removal failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CopySelectedSid()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected != null && !String.IsNullOrWhiteSpace(selected.BaseSid))
            {
                Clipboard.SetText(selected.BaseSid);
            }
        }

        private void OpenSelectedProfilePath()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected == null)
            {
                MessageBox.Show("Select a profile first.", "Open Profile Path", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!Directory.Exists(selected.ProfilePath))
            {
                MessageBox.Show("The selected profile path does not exist: " + selected.ProfilePath, "Open Profile Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "explorer.exe";
            startInfo.Arguments = "\"" + selected.ProfilePath + "\"";
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        private void OpenSelectedRegistryKey()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected != null && selected.HasRegistryEntries)
            {
                ProfileService.OpenRegistryForProfile(selected);
            }
        }

        private void ShowTextDialog(string title, string text)
        {
            Form dialog = new Form();
            dialog.Text = title;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(820, 460);
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = true;

            TextBox textBox = new TextBox();
            textBox.Multiline = true;
            textBox.ReadOnly = true;
            textBox.ScrollBars = ScrollBars.Both;
            textBox.WordWrap = false;
            textBox.Dock = DockStyle.Fill;
            textBox.Font = new Font("Consolas", 10);
            textBox.Text = text;

            Button closeButton = new Button();
            closeButton.Text = "Close";
            closeButton.Width = 96;
            closeButton.Height = 28;
            closeButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            closeButton.Location = new Point(706, 8);
            closeButton.Click += delegate { dialog.Close(); };

            Panel buttonPanel = new Panel();
            buttonPanel.Dock = DockStyle.Bottom;
            buttonPanel.Height = 44;
            buttonPanel.Controls.Add(closeButton);

            dialog.Controls.Add(textBox);
            dialog.Controls.Add(buttonPanel);
            dialog.ShowDialog(this);
        }
    }

    internal static class ProfileService
    {
        public const string ProfileListRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        public const string ProfileListRegPath = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        private static readonly Regex OldProfileRegex = new Regex(@"\.old(\.?\d{8}-\d{6}(\.\d+)?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            List<UserProfileState> states = GetUserProfileStates(target.ComputerName, out stateError);
            string currentSid = target.IsRemote ? String.Empty : GetCurrentSid();
            return BuildInventory(folders, entries, states, currentSid, stateError, target.RegistryPath, registryError);
        }

        public static CompatibilityReport RunDiagnostics(ProfileTarget target)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            CompatibilityReport report = new CompatibilityReport { Target = target.DisplayName };
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

            CheckFileExists(report, "reg.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"));
            CheckFileExists(report, "shutdown.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"));

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
                List<UserProfileState> states = GetUserProfileStates(target.ComputerName, out stateError);
                if (String.IsNullOrWhiteSpace(stateError))
                {
                    report.AddOk("Win32_UserProfile", states.Count + " profile state record(s) found.");
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
            if (keys.Count == 0)
            {
                blockReasons.Add("No matching ProfileList registry key");
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
                Warnings = new List<string>(profile.Warnings ?? new List<string>())
            };
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

            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName);
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + ".log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting rebuild for " + profile.ProfilePath);
            WriteLog(logPath, "Base SID: " + profile.BaseSid);
            WriteLog(logPath, "Rename target: " + plan.RenameTo);

            try
            {
                foreach (string keyName in plan.RegistryKeyNames)
                {
                    string destination = Path.Combine(backupDirectory, keyName + ".reg");
                    WriteLog(logPath, "Exporting ProfileList key " + keyName + " to " + destination);
                    ExportRegistryKey(keyName, destination, target.ComputerName);
                }

                WriteLog(logPath, "Renaming " + plan.ProfilePath + " to " + plan.RenameTo);
                Directory.Move(plan.ProfilePath, plan.RenameTo);

                foreach (string keyName in plan.RegistryKeyNames)
                {
                    WriteLog(logPath, "Removing ProfileList key " + keyName);
                    RemoveRegistryKey(keyName, target.ComputerName);
                }

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
                throw;
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

            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName + "-registry");
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + "-registry.log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting registry entry removal for " + profile.ProfilePath);
            try
            {
                foreach (string keyName in plan.RegistryKeyNames)
                {
                    string destination = Path.Combine(backupDirectory, keyName + ".reg");
                    WriteLog(logPath, "Exporting ProfileList key " + keyName + " to " + destination);
                    ExportRegistryKey(keyName, destination, target.ComputerName);
                    WriteLog(logPath, "Removing ProfileList key " + keyName);
                    RemoveRegistryKey(keyName, target.ComputerName);
                }

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
                throw;
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

            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName + "-delete");
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + "-delete.log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting profile deletion for " + profile.ProfilePath);
            WriteLog(logPath, "Base SID: " + profile.BaseSid);

            try
            {
                foreach (string keyName in plan.RegistryKeyNames)
                {
                    string destination = Path.Combine(backupDirectory, keyName + ".reg");
                    WriteLog(logPath, "Exporting ProfileList key " + keyName + " to " + destination);
                    ExportRegistryKey(keyName, destination, target.ComputerName);
                }

                WriteLog(logPath, "Deleting profile folder " + plan.ProfilePath);
                DeleteDirectoryTree(plan.ProfilePath);

                foreach (string keyName in plan.RegistryKeyNames)
                {
                    WriteLog(logPath, "Removing ProfileList key " + keyName);
                    RemoveRegistryKey(keyName, target.ComputerName);
                }

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
                throw;
            }
        }

        public static BakRemovalResult RemoveBakKeys(string profilePath, string usersRoot)
        {
            return RemoveBakKeys(profilePath, ProfileTarget.Local(usersRoot));
        }

        public static BakRemovalResult RemoveBakKeys(string profilePath, ProfileTarget target)
        {
            ProfileRecord profile = FindProfileByPath(profilePath, target);
            if (!profile.BakKeyPresent)
            {
                throw new InvalidOperationException("No .bak ProfileList key was found for " + profile.ProfilePath + ".");
            }

            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string safeName = SafeFileName(profile.FolderName);
            string backupDirectory = Path.Combine(GetAppDirectory(), "backups", runId + "-" + safeName + "-bak");
            string logDirectory = Path.Combine(GetAppDirectory(), "logs");
            string logPath = Path.Combine(logDirectory, runId + "-" + safeName + "-bak.log");
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(logDirectory);

            WriteLog(logPath, "Starting .bak removal for " + profile.ProfilePath);
            try
            {
                foreach (string keyName in profile.BakKeyNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string destination = Path.Combine(backupDirectory, keyName + ".reg");
                    WriteLog(logPath, "Exporting ProfileList key " + keyName + " to " + destination);
                    ExportRegistryKey(keyName, destination, target.ComputerName);
                    WriteLog(logPath, "Removing ProfileList key " + keyName);
                    RemoveRegistryKey(keyName, target.ComputerName);
                }

                WriteLog(logPath, ".bak removal completed successfully.");

                return new BakRemovalResult
                {
                    ProfilePath = profile.ProfilePath,
                    RegistryRoot = profile.RegistryRoot,
                    RemovedKeys = new List<string>(profile.BakKeyNames),
                    BackupDirectory = backupDirectory,
                    LogPath = logPath,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "FAILED: " + ex.Message);
                throw;
            }
        }

        public static void RebootComputer()
        {
            RebootComputer(null);
        }

        public static void RebootComputer(string computerName)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            string target = IsRemoteComputer(computerName) ? " /m \\\\" + computerName.Trim().TrimStart('\\') : String.Empty;
            startInfo.Arguments = "/r" + target + " /t 0 /c \"Temp Profile Fixer requested reboot after profile rebuild.\"";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process.Start(startInfo);
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
            List<UserProfileState> states = new List<UserProfileState>();
            error = null;

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
                    foreach (ManagementObject profile in results)
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
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return states;
        }

        private static List<ProfileRecord> BuildInventory(
            List<FolderRecord> folders,
            List<ProfileListEntry> entries,
            List<UserProfileState> states,
            string currentSid,
            string stateError,
            string registryRoot,
            string registryError)
        {
            Dictionary<string, UserProfileState> statesBySid = states
                .Where(s => !String.IsNullOrWhiteSpace(s.Sid))
                .GroupBy(s => s.Sid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

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
                    state = statesBySid[baseSid];
                }
                else if (statesByPath.ContainsKey(folder.NormalizedPath) && statesByPath[folder.NormalizedPath].Count == 1)
                {
                    state = statesByPath[folder.NormalizedPath][0];
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

        private static void ExportRegistryKey(string keyName, string destinationPath, string computerName)
        {
            string regPath = GetProfileListRegPath(computerName) + "\\" + keyName;
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
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

        private static void RemoveRegistryKey(string keyName, string computerName)
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

        private static void DeleteDirectoryTree(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            DirectoryInfo root = new DirectoryInfo(directoryPath);
            foreach (FileInfo file in root.GetFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            foreach (DirectoryInfo directory in root.GetDirectories("*", SearchOption.AllDirectories))
            {
                directory.Attributes = FileAttributes.Normal;
            }

            root.Attributes = FileAttributes.Normal;
            root.Delete(true);
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

    internal sealed class FolderRecord
    {
        public string Name { get; set; }
        public string FullName { get; set; }
        public string NormalizedPath { get; set; }
        public string ActualNormalizedPath { get; set; }
    }

    internal sealed class ProfileListEntry
    {
        public string KeyName { get; set; }
        public string BaseSid { get; set; }
        public bool IsBak { get; set; }
        public string ProfileImagePath { get; set; }
        public string NormalizedProfilePath { get; set; }
        public object State { get; set; }
        public object RefCount { get; set; }
    }

    internal sealed class UserProfileState
    {
        public string Sid { get; set; }
        public string LocalPath { get; set; }
        public string NormalizedPath { get; set; }
        public bool Loaded { get; set; }
        public bool Special { get; set; }
    }

    internal sealed class CompatibilityReport
    {
        public string Target { get; set; }
        public List<DiagnosticCheck> Checks { get; private set; }

        public CompatibilityReport()
        {
            Checks = new List<DiagnosticCheck>();
        }

        public bool HasFailures
        {
            get { return Checks.Any(c => String.Equals(c.Status, "FAIL", StringComparison.OrdinalIgnoreCase)); }
        }

        public void AddOk(string name, string detail)
        {
            Checks.Add(new DiagnosticCheck { Status = "OK", Name = name, Detail = detail });
        }

        public void AddWarning(string name, string detail)
        {
            Checks.Add(new DiagnosticCheck { Status = "WARN", Name = name, Detail = detail });
        }

        public void AddFailure(string name, string detail)
        {
            Checks.Add(new DiagnosticCheck { Status = "FAIL", Name = name, Detail = detail });
        }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Temp Profile Fixer compatibility diagnostics");
            builder.AppendLine("Target: " + Target);
            builder.AppendLine();
            foreach (DiagnosticCheck check in Checks)
            {
                builder.AppendLine("[" + check.Status + "] " + check.Name + " - " + check.Detail);
            }
            builder.AppendLine();
            builder.AppendLine(HasFailures
                ? "One or more checks failed. Fix those before rebuilding a profile on this computer."
                : "All checks passed.");
            return builder.ToString();
        }
    }

    internal sealed class DiagnosticCheck
    {
        public string Status { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
    }

    internal sealed class ProfileRecord
    {
        public string FolderName { get; set; }
        public string ProfilePath { get; set; }
        public string NormalizedPath { get; set; }
        public string ActualNormalizedPath { get; set; }
        public string RegistryRoot { get; set; }
        public string BaseSid { get; set; }
        public int MatchingSidCount { get; set; }
        public List<string> NormalKeyNames { get; set; }
        public List<string> BakKeyNames { get; set; }
        public bool NormalKeyPresent { get; set; }
        public bool BakKeyPresent { get; set; }
        public bool Loaded { get; set; }
        public bool Special { get; set; }
        public bool IsBlocked { get; set; }
        public List<string> BlockReasons { get; set; }
        public List<string> Warnings { get; set; }
        public string Status { get; set; }
        public bool HasRegistryEntries
        {
            get
            {
                return (NormalKeyNames != null && NormalKeyNames.Count > 0) ||
                    (BakKeyNames != null && BakKeyNames.Count > 0);
            }
        }
    }

    internal sealed class RegistryRemovalPlan
    {
        public string FolderName { get; set; }
        public string ProfilePath { get; set; }
        public string BaseSid { get; set; }
        public string RegistryRoot { get; set; }
        public List<string> RegistryKeyNames { get; set; }
        public bool IsBlocked { get; set; }
        public List<string> BlockReasons { get; set; }
        public List<string> Warnings { get; set; }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Folder: " + FolderName);
            builder.AppendLine("Profile path: " + ProfilePath);
            builder.AppendLine("Base SID: " + BaseSid);
            builder.AppendLine("Blocked: " + IsBlocked);
            if (BlockReasons != null && BlockReasons.Count > 0)
            {
                builder.AppendLine("Block reasons: " + String.Join("; ", BlockReasons.ToArray()));
            }
            if (Warnings != null && Warnings.Count > 0)
            {
                builder.AppendLine("Warnings: " + String.Join("; ", Warnings.ToArray()));
            }
            builder.AppendLine("Registry keys to export and delete:");
            foreach (string keyName in RegistryKeyNames ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
            }
            builder.AppendLine();
            if (IsBlocked)
            {
                builder.AppendLine("Blocked: " + String.Join("; ", (BlockReasons ?? new List<string>()).ToArray()));
            }
            else
            {
                builder.AppendLine("This will remove the matching ProfileList registry key(s) without renaming the profile folder.");
            }
            return builder.ToString();
        }
    }

    internal sealed class DeleteProfilePlan
    {
        public string FolderName { get; set; }
        public string ProfilePath { get; set; }
        public string BaseSid { get; set; }
        public string RegistryRoot { get; set; }
        public List<string> RegistryKeyNames { get; set; }
        public bool IsBlocked { get; set; }
        public List<string> BlockReasons { get; set; }
        public List<string> Warnings { get; set; }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Folder: " + FolderName);
            builder.AppendLine("Profile path to delete: " + ProfilePath);
            builder.AppendLine("Base SID: " + BaseSid);
            builder.AppendLine("Blocked: " + IsBlocked);
            if (BlockReasons != null && BlockReasons.Count > 0)
            {
                builder.AppendLine("Block reasons: " + String.Join("; ", BlockReasons.ToArray()));
            }
            if (Warnings != null && Warnings.Count > 0)
            {
                builder.AppendLine("Warnings: " + String.Join("; ", Warnings.ToArray()));
            }
            builder.AppendLine("Registry keys to export and delete:");
            foreach (string keyName in RegistryKeyNames ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
            }
            builder.AppendLine();
            if (IsBlocked)
            {
                builder.AppendLine("Blocked: " + String.Join("; ", (BlockReasons ?? new List<string>()).ToArray()));
            }
            else
            {
                builder.AppendLine("This will permanently delete the profile folder and remove the matching ProfileList key(s).");
            }
            return builder.ToString();
        }
    }

    internal sealed class RebuildPlan
    {
        public string FolderName { get; set; }
        public string ProfilePath { get; set; }
        public string RenameTo { get; set; }
        public string BaseSid { get; set; }
        public string RegistryRoot { get; set; }
        public List<string> RegistryKeyNames { get; set; }
        public bool IsBlocked { get; set; }
        public List<string> BlockReasons { get; set; }
        public List<string> Warnings { get; set; }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Folder: " + FolderName);
            builder.AppendLine("Profile path: " + ProfilePath);
            builder.AppendLine("Rename to: " + RenameTo);
            builder.AppendLine("Base SID: " + BaseSid);
            builder.AppendLine("Blocked: " + IsBlocked);
            if (BlockReasons != null && BlockReasons.Count > 0)
            {
                builder.AppendLine("Block reasons: " + String.Join("; ", BlockReasons.ToArray()));
            }
            if (Warnings != null && Warnings.Count > 0)
            {
                builder.AppendLine("Warnings: " + String.Join("; ", Warnings.ToArray()));
            }
            builder.AppendLine("Registry keys to export and delete:");
            foreach (string keyName in RegistryKeyNames ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
            }
            builder.AppendLine();
            if (IsBlocked)
            {
                builder.AppendLine("Blocked: " + String.Join("; ", (BlockReasons ?? new List<string>()).ToArray()));
            }
            else
            {
                builder.AppendLine("This will rename the profile folder to .old<date> and remove the matching ProfileList key(s).");
            }
            return builder.ToString();
        }
    }

    internal sealed class RegistryRemovalResult
    {
        public string ProfilePath { get; set; }
        public string RegistryRoot { get; set; }
        public List<string> RemovedKeys { get; set; }
        public string BackupDirectory { get; set; }
        public string LogPath { get; set; }
        public bool Success { get; set; }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Registry removal completed.");
            builder.AppendLine();
            builder.AppendLine("Profile path: " + ProfilePath);
            builder.AppendLine("Removed keys:");
            foreach (string keyName in RemovedKeys ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
            }
            builder.AppendLine("Backup folder: " + BackupDirectory);
            builder.AppendLine("Log: " + LogPath);
            return builder.ToString();
        }
    }

    internal sealed class DeleteProfileResult
    {
        public string ProfilePath { get; set; }
        public string RegistryRoot { get; set; }
        public List<string> RemovedKeys { get; set; }
        public string BackupDirectory { get; set; }
        public string LogPath { get; set; }
        public bool Success { get; set; }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Profile deleted.");
            builder.AppendLine();
            builder.AppendLine("Deleted profile path: " + ProfilePath);
            builder.AppendLine("Removed keys:");
            foreach (string keyName in RemovedKeys ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
            }
            builder.AppendLine("Registry backup folder: " + BackupDirectory);
            builder.AppendLine("Log: " + LogPath);
            return builder.ToString();
        }
    }

    internal sealed class RebuildResult
    {
        public string ProfilePath { get; set; }
        public string RenamedTo { get; set; }
        public string RegistryRoot { get; set; }
        public List<string> RemovedKeys { get; set; }
        public string BackupDirectory { get; set; }
        public string LogPath { get; set; }
        public bool Success { get; set; }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Profile rebuild completed.");
            builder.AppendLine();
            builder.AppendLine("Original path: " + ProfilePath);
            builder.AppendLine("Renamed to: " + RenamedTo);
            builder.AppendLine("Removed keys:");
            foreach (string keyName in RemovedKeys ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
            }
            builder.AppendLine("Backup folder: " + BackupDirectory);
            builder.AppendLine("Log: " + LogPath);
            builder.AppendLine();
            builder.AppendLine("Reboot before the target user signs in again.");
            return builder.ToString();
        }
    }

    internal sealed class BakRemovalResult
    {
        public string ProfilePath { get; set; }
        public string RegistryRoot { get; set; }
        public List<string> RemovedKeys { get; set; }
        public string BackupDirectory { get; set; }
        public string LogPath { get; set; }
        public bool Success { get; set; }

        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(".bak removal completed.");
            builder.AppendLine();
            builder.AppendLine("Profile path: " + ProfilePath);
            builder.AppendLine("Removed .bak keys:");
            foreach (string keyName in RemovedKeys ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
            }
            builder.AppendLine("Backup folder: " + BackupDirectory);
            builder.AppendLine("Log: " + LogPath);
            return builder.ToString();
        }
    }
}
