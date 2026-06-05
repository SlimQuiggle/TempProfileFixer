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
            try
            {
                if (args.Length > 0)
                {
                    return CommandLine.Run(args);
                }

                NativeMethods.FreeConsole();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length > 0)
                {
                    Console.Error.WriteLine(ex.Message);
                }
                else
                {
                    MessageBox.Show(ex.Message, "Temp Profile Fixer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return 1;
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();
    }

    internal static class CommandLine
    {
        public static int Run(string[] args)
        {
            string command = args[0].Trim().ToLowerInvariant();
            ParsedArgs parsed = ParsedArgs.Parse(args.Skip(1).ToArray());
            string usersRoot = parsed.GetValue("users-root", Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "Users"));

            if (command == "help" || command == "--help" || command == "-h" || command == "/?")
            {
                PrintUsage();
                return 0;
            }

            if (command == "list")
            {
                ListProfiles(usersRoot);
                return 0;
            }

            if (command == "dry-run" || command == "plan")
            {
                string path = parsed.GetPath();
                if (String.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("dry-run requires --path C:\\Users\\<name>.");
                }

                ProfileRecord profile = ProfileService.FindProfileByPath(path, usersRoot);
                RebuildPlan plan = ProfileService.CreateRebuildPlan(profile, DateTime.Now);
                Console.WriteLine(plan.ToDisplayText());
                return 0;
            }

            if (command == "rebuild")
            {
                string path = parsed.GetPath();
                if (String.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("rebuild requires --path C:\\Users\\<name>.");
                }

                ProfileRecord profile = ProfileService.FindProfileByPath(path, usersRoot);
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

                RebuildResult result = ProfileService.RebuildProfile(path, usersRoot);
                Console.WriteLine(result.ToDisplayText());
                PromptForReboot(parsed);
                return 0;
            }

            if (command == "remove-registry" || command == "remove-reg" || command == "delete-registry")
            {
                string path = parsed.GetPath();
                if (String.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("remove-registry requires --path C:\\Users\\<name>.");
                }

                ProfileRecord profile = ProfileService.FindProfileByPath(path, usersRoot);
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

                RegistryRemovalResult result = ProfileService.RemoveRegistryEntries(path, usersRoot);
                Console.WriteLine(result.ToDisplayText());
                return 0;
            }

            if (command == "remove-bak" || command == "fix-bak")
            {
                string path = parsed.GetPath();
                if (String.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("remove-bak requires --path C:\\Users\\<name>.");
                }

                ProfileRecord profile = ProfileService.FindProfileByPath(path, usersRoot);
                if (!profile.BakKeyPresent)
                {
                    Console.WriteLine("No .bak ProfileList key was found for " + profile.ProfilePath + ".");
                    return 0;
                }

                Console.WriteLine("The following .bak ProfileList key(s) will be exported and deleted:");
                foreach (string keyName in profile.BakKeyNames)
                {
                    Console.WriteLine("  " + ProfileService.ProfileListRegPath + "\\" + keyName);
                }

                if (!parsed.HasFlag("yes") && !ConfirmTyped("Type REMOVEBAK to delete the listed .bak key(s): ", "REMOVEBAK"))
                {
                    Console.WriteLine("Cancelled.");
                    return 2;
                }

                BakRemovalResult result = ProfileService.RemoveBakKeys(path, usersRoot);
                Console.WriteLine(result.ToDisplayText());
                return 0;
            }

            PrintUsage();
            return 1;
        }

        private static void ListProfiles(string usersRoot)
        {
            List<ProfileRecord> profiles = ProfileService.GetProfiles(usersRoot);
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

        private static void PromptForReboot(ParsedArgs parsed)
        {
            if (parsed.HasFlag("no-reboot-prompt"))
            {
                return;
            }

            if (parsed.HasFlag("reboot"))
            {
                ProfileService.RebootComputer();
                return;
            }

            Console.Write("Rebuild complete. Reboot now? [y/N]: ");
            string answer = Console.ReadLine();
            if (String.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                ProfileService.RebootComputer();
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
            Console.WriteLine("  TempProfileFixer.exe list [--users-root C:\\Users]");
            Console.WriteLine("  TempProfileFixer.exe dry-run --path C:\\Users\\SomeUser");
            Console.WriteLine("  TempProfileFixer.exe rebuild --path C:\\Users\\SomeUser [--yes] [--reboot] [--no-reboot-prompt]");
            Console.WriteLine("  TempProfileFixer.exe remove-registry --path C:\\Users\\SomeUser [--yes]");
            Console.WriteLine("  TempProfileFixer.exe remove-bak --path C:\\Users\\SomeUser [--yes]");
            Console.WriteLine();
            Console.WriteLine("rebuild renames the profile folder to .old<date>, exports/deletes the matching normal SID key");
            Console.WriteLine("and matching .bak key, then prompts for reboot.");
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

        public string GetPath()
        {
            string value;
            if (values.TryGetValue("path", out value) || values.TryGetValue("p", out value))
            {
                return value;
            }

            return positional.Count > 0 ? positional[0] : null;
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

    internal sealed class MainForm : Form
    {
        private readonly string usersRoot;
        private readonly DataGridView grid;
        private readonly Label statusLabel;
        private readonly Button rebuildButton;
        private readonly Button dryRunButton;
        private readonly ToolStripMenuItem rebuildMenuItem;
        private readonly ToolStripMenuItem removeRegistryMenuItem;
        private readonly ToolStripMenuItem copySidMenuItem;
        private readonly ToolStripMenuItem copyPathMenuItem;
        private readonly ToolStripMenuItem openRegistryMenuItem;
        private readonly NotifyIcon trayIcon;
        private readonly Icon appIcon;
        private readonly Image headerImage;
        private List<ProfileRecord> profiles = new List<ProfileRecord>();

        public MainForm()
        {
            usersRoot = Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "Users");

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

            trayIcon = new NotifyIcon();
            trayIcon.Text = "Temp Profile Fixer";
            trayIcon.Icon = appIcon ?? SystemIcons.Application;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate
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
            trayIcon.ContextMenuStrip = trayMenu;

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
            refreshButton.Location = new Point(416, 78);
            refreshButton.Click += delegate { RefreshProfiles(); };

            dryRunButton = new Button();
            dryRunButton.Text = "Dry Run";
            dryRunButton.Width = 96;
            dryRunButton.Height = 30;
            dryRunButton.Location = new Point(304, 78);
            dryRunButton.Click += delegate { ShowSelectedPlan(); };

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

            topPanel.Controls.Add(headerIcon);
            topPanel.Controls.Add(titleLabel);
            topPanel.Controls.Add(subtitleLabel);
            topPanel.Controls.Add(refreshButton);
            topPanel.Controls.Add(dryRunButton);
            topPanel.Controls.Add(rebuildButton);

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
            copyPathMenuItem = new ToolStripMenuItem("Copy Profile Path", null, delegate { CopySelectedPath(); });
            copySidMenuItem = new ToolStripMenuItem("Copy SID", null, delegate { CopySelectedSid(); });
            openRegistryMenuItem = new ToolStripMenuItem("Open to Registry", null, delegate { OpenSelectedRegistryKey(); });
            contextMenu.Items.AddRange(new ToolStripItem[]
            {
                rebuildMenuItem,
                removeRegistryMenuItem,
                new ToolStripSeparator(),
                copyPathMenuItem,
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
            statusPanel.Controls.Add(statusLabel);

            Controls.Add(grid);
            Controls.Add(topPanel);
            Controls.Add(statusPanel);
            Shown += delegate { RefreshProfiles(); };
            FormClosed += delegate
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
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
            dryRunButton.Enabled = hasSelection;
            rebuildButton.Enabled = hasSelection && !selected.IsBlocked;
            rebuildMenuItem.Enabled = hasSelection && !selected.IsBlocked;
            removeRegistryMenuItem.Enabled = hasSelection && selected.HasRegistryEntries && !selected.IsBlocked;
            copySidMenuItem.Enabled = hasSelection && !String.IsNullOrWhiteSpace(selected.BaseSid);
            copyPathMenuItem.Enabled = hasSelection;
            openRegistryMenuItem.Enabled = hasSelection && selected.HasRegistryEntries;
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

        private void CopySelectedPath()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected != null)
            {
                Clipboard.SetText(selected.ProfilePath);
            }
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
            if (String.IsNullOrWhiteSpace(usersRoot))
            {
                usersRoot = Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "Users");
            }

            List<FolderRecord> folders = GetProfileFolders(usersRoot);
            List<ProfileListEntry> entries = GetProfileListEntries();
            string stateError;
            List<UserProfileState> states = GetUserProfileStates(out stateError);
            string currentSid = GetCurrentSid();
            return BuildInventory(folders, entries, states, currentSid, stateError);
        }

        public static ProfileRecord FindProfileByPath(string path, string usersRoot)
        {
            string normalized = NormalizePath(path);
            List<ProfileRecord> matches = GetProfiles(usersRoot).Where(p => p.NormalizedPath == normalized).ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException("Expected exactly one profile match for '" + path + "'; found " + matches.Count + ".");
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
                RegistryKeyNames = keys,
                IsBlocked = blockReasons.Count > 0,
                BlockReasons = blockReasons,
                Warnings = new List<string>(profile.Warnings ?? new List<string>())
            };
        }

        public static RebuildResult RebuildProfile(string profilePath, string usersRoot)
        {
            ProfileRecord profile = FindProfileByPath(profilePath, usersRoot);
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
                    ExportRegistryKey(keyName, destination);
                }

                WriteLog(logPath, "Renaming " + plan.ProfilePath + " to " + plan.RenameTo);
                Directory.Move(plan.ProfilePath, plan.RenameTo);

                foreach (string keyName in plan.RegistryKeyNames)
                {
                    WriteLog(logPath, "Removing ProfileList key " + keyName);
                    RemoveRegistryKey(keyName);
                }

                WriteLog(logPath, "Rebuild completed successfully.");

                return new RebuildResult
                {
                    ProfilePath = plan.ProfilePath,
                    RenamedTo = plan.RenameTo,
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
            ProfileRecord profile = FindProfileByPath(profilePath, usersRoot);
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
                    ExportRegistryKey(keyName, destination);
                    WriteLog(logPath, "Removing ProfileList key " + keyName);
                    RemoveRegistryKey(keyName);
                }

                WriteLog(logPath, "Registry entry removal completed successfully.");

                return new RegistryRemovalResult
                {
                    ProfilePath = profile.ProfilePath,
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
            ProfileRecord profile = FindProfileByPath(profilePath, usersRoot);
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
                    ExportRegistryKey(keyName, destination);
                    WriteLog(logPath, "Removing ProfileList key " + keyName);
                    RemoveRegistryKey(keyName);
                }

                WriteLog(logPath, ".bak removal completed successfully.");

                return new BakRemovalResult
                {
                    ProfilePath = profile.ProfilePath,
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
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            startInfo.Arguments = "/r /t 0 /c \"Temp Profile Fixer requested reboot after profile rebuild.\"";
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
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("TempProfileFixer.Assets.TempProfileFixer.png"))
            {
                if (stream != null)
                {
                    return Image.FromStream(stream);
                }
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

        public static string GetBaseSid(string sidOrKeyName)
        {
            if (sidOrKeyName != null && sidOrKeyName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                return sidOrKeyName.Substring(0, sidOrKeyName.Length - 4);
            }

            return sidOrKeyName ?? String.Empty;
        }

        private static List<FolderRecord> GetProfileFolders(string usersRoot)
        {
            if (!Directory.Exists(usersRoot))
            {
                throw new DirectoryNotFoundException("Users root '" + usersRoot + "' was not found.");
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

            return new DirectoryInfo(usersRoot)
                .GetDirectories()
                .Where(d => !excluded.Contains(d.Name) && !OldProfileRegex.IsMatch(d.Name))
                .OrderBy(d => d.Name)
                .Select(d => new FolderRecord
                {
                    Name = d.Name,
                    FullName = d.FullName,
                    NormalizedPath = NormalizePath(d.FullName)
                })
                .ToList();
        }

        private static List<ProfileListEntry> GetProfileListEntries()
        {
            List<ProfileListEntry> entries = new List<ProfileListEntry>();
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using (RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (RegistryKey profileList = localMachine.OpenSubKey(ProfileListRegistryPath, false))
            {
                if (profileList == null)
                {
                    throw new InvalidOperationException(ProfileListRegPath + " was not found.");
                }

                foreach (string keyName in profileList.GetSubKeyNames().OrderBy(n => n))
                {
                    using (RegistryKey key = profileList.OpenSubKey(keyName, false))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        string imagePath = Convert.ToString(key.GetValue("ProfileImagePath", String.Empty));
                        entries.Add(new ProfileListEntry
                        {
                            KeyName = keyName,
                            BaseSid = GetBaseSid(keyName),
                            IsBak = keyName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase),
                            ProfileImagePath = imagePath,
                            NormalizedProfilePath = NormalizePath(imagePath),
                            State = key.GetValue("State"),
                            RefCount = key.GetValue("RefCount")
                        });
                    }
                }
            }

            return entries;
        }

        private static List<UserProfileState> GetUserProfileStates(out string error)
        {
            List<UserProfileState> states = new List<UserProfileState>();
            error = null;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT SID, LocalPath, Loaded, Special FROM Win32_UserProfile"))
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
            string stateError)
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

        private static void ExportRegistryKey(string keyName, string destinationPath)
        {
            string regPath = ProfileListRegPath + "\\" + keyName;
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

        private static void RemoveRegistryKey(string keyName)
        {
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using (RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (RegistryKey profileList = localMachine.OpenSubKey(ProfileListRegistryPath, true))
            {
                if (profileList == null)
                {
                    throw new InvalidOperationException(ProfileListRegPath + " was not found.");
                }

                if (profileList.GetSubKeyNames().Any(k => String.Equals(k, keyName, StringComparison.OrdinalIgnoreCase)))
                {
                    profileList.DeleteSubKeyTree(keyName, false);
                }
            }
        }

        private static string GetAppDirectory()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return String.IsNullOrWhiteSpace(baseDirectory) ? Environment.CurrentDirectory : baseDirectory;
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

    internal sealed class ProfileRecord
    {
        public string FolderName { get; set; }
        public string ProfilePath { get; set; }
        public string NormalizedPath { get; set; }
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
                builder.AppendLine("  " + ProfileService.ProfileListRegPath + "\\" + keyName);
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

    internal sealed class RebuildPlan
    {
        public string FolderName { get; set; }
        public string ProfilePath { get; set; }
        public string RenameTo { get; set; }
        public string BaseSid { get; set; }
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
                builder.AppendLine("  " + ProfileService.ProfileListRegPath + "\\" + keyName);
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
                builder.AppendLine("  " + ProfileService.ProfileListRegPath + "\\" + keyName);
            }
            builder.AppendLine("Backup folder: " + BackupDirectory);
            builder.AppendLine("Log: " + LogPath);
            return builder.ToString();
        }
    }

    internal sealed class RebuildResult
    {
        public string ProfilePath { get; set; }
        public string RenamedTo { get; set; }
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
                builder.AppendLine("  " + ProfileService.ProfileListRegPath + "\\" + keyName);
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
                builder.AppendLine("  " + ProfileService.ProfileListRegPath + "\\" + keyName);
            }
            builder.AppendLine("Backup folder: " + BackupDirectory);
            builder.AppendLine("Log: " + LogPath);
            return builder.ToString();
        }
    }
}
