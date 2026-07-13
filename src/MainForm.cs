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
    internal sealed class MainForm : Form
    {
        private readonly string usersRoot;
        private readonly DataGridView grid;
        private readonly Label statusLabel;
        private readonly Button rebuildButton;
        private readonly Button deleteProfileButton;
        private readonly Button helpButton;
        private readonly Button diagnosticsButton;
        private readonly Button refreshButton;
        private readonly RichTextBox detailsBox;
        private readonly ProgressBar progressBar;
        private readonly ToolStripMenuItem rebuildMenuItem;
        private readonly ToolStripMenuItem removeRegistryMenuItem;
        private readonly ToolStripMenuItem copySidMenuItem;
        private readonly ToolStripMenuItem openProfilePathMenuItem;
        private readonly ToolStripMenuItem openRegistryMenuItem;
        private readonly NotifyIcon trayIcon;
        private readonly Icon appIcon;
        private readonly Image headerImage;
        private List<ProfileRecord> profiles = new List<ProfileRecord>();
        private bool isBusy;

        public MainForm()
        {
            usersRoot = ProfileTarget.Local(null).UsersRoot;

            Text = "Temp Profile Fixer " + AppVersion.Current;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1220, 700);
            MinimumSize = new Size(1040, 560);
            AutoScaleMode = AutoScaleMode.Dpi;
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
            topPanel.Height = 132;
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

            refreshButton = new Button();
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

            diagnosticsButton = new Button();
            diagnosticsButton.Text = "Diagnostics";
            diagnosticsButton.Width = 132;
            diagnosticsButton.Height = 38;
            diagnosticsButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            diagnosticsButton.BackColor = Color.FromArgb(86, 91, 102);
            diagnosticsButton.ForeColor = Color.White;
            diagnosticsButton.FlatStyle = FlatStyle.Flat;
            diagnosticsButton.FlatAppearance.BorderSize = 0;
            diagnosticsButton.UseVisualStyleBackColor = false;
            diagnosticsButton.Cursor = Cursors.Hand;
            diagnosticsButton.Click += delegate { RunDiagnostics(); };

            titleLabel.Location = new Point(0, 4);
            subtitleLabel.Location = new Point(2, 35);
            Panel titlePanel = new Panel();
            titlePanel.Dock = DockStyle.Fill;
            titlePanel.Controls.Add(titleLabel);
            titlePanel.Controls.Add(subtitleLabel);

            FlowLayoutPanel actionPanel = new FlowLayoutPanel();
            actionPanel.Dock = DockStyle.Fill;
            actionPanel.FlowDirection = FlowDirection.LeftToRight;
            actionPanel.WrapContents = false;
            actionPanel.Padding = new Padding(0, 2, 0, 0);
            actionPanel.Controls.Add(rebuildButton);
            actionPanel.Controls.Add(deleteProfileButton);
            actionPanel.Controls.Add(helpButton);
            actionPanel.Controls.Add(diagnosticsButton);
            actionPanel.Controls.Add(refreshButton);

            TableLayoutPanel headerLayout = new TableLayoutPanel();
            headerLayout.Dock = DockStyle.Fill;
            headerLayout.ColumnCount = 2;
            headerLayout.RowCount = 2;
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            headerIcon.Dock = DockStyle.Fill;
            headerIcon.Location = Point.Empty;
            headerLayout.Controls.Add(headerIcon, 0, 0);
            headerLayout.SetRowSpan(headerIcon, 2);
            headerLayout.Controls.Add(titlePanel, 1, 0);
            headerLayout.Controls.Add(actionPanel, 1, 1);
            topPanel.Controls.Add(headerLayout);

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
            grid.SelectionChanged += delegate
            {
                UpdateActionState();
                UpdateSelectedDetails();
            };
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

            detailsBox = new RichTextBox();
            detailsBox.Dock = DockStyle.Fill;
            detailsBox.ReadOnly = true;
            detailsBox.BorderStyle = BorderStyle.FixedSingle;
            detailsBox.BackColor = Color.White;
            detailsBox.Font = new Font("Segoe UI", 9);
            detailsBox.Text = "Select a profile to view its safety details.";

            SplitContainer contentSplit = new SplitContainer();
            contentSplit.Dock = DockStyle.Fill;
            contentSplit.Orientation = Orientation.Horizontal;
            contentSplit.SplitterDistance = 390;
            contentSplit.Panel1.Controls.Add(grid);
            contentSplit.Panel2.Controls.Add(detailsBox);

            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Bottom;
            statusPanel.Height = 34;
            statusPanel.Padding = new Padding(10, 8, 10, 6);
            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Text = "Ready.";
            progressBar = new ProgressBar();
            progressBar.Dock = DockStyle.Right;
            progressBar.Width = 140;
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.MarqueeAnimationSpeed = 25;
            progressBar.Visible = false;
            Label creditLabel = new Label();
            creditLabel.Dock = DockStyle.Right;
            creditLabel.Width = 180;
            creditLabel.TextAlign = ContentAlignment.MiddleRight;
            creditLabel.ForeColor = Color.FromArgb(120, 120, 120);
            creditLabel.Font = new Font("Segoe UI", 8, FontStyle.Regular);
            creditLabel.Text = "Created by Flex3Designs";
            statusPanel.Controls.Add(statusLabel);
            statusPanel.Controls.Add(progressBar);
            statusPanel.Controls.Add(creditLabel);

            Controls.Add(contentSplit);
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
            dialog.Text = "Temp Profile Fixer " + AppVersion.Current + " Help / FAQ";
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
            title.Text = "Temp Profile Fixer " + AppVersion.Current + " Help / FAQ";
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
            AppendBullet(box, "Delete Profile can remove a folder-only temp profile with no matching SID when it is otherwise safe.");
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
            AppendParagraph(box, "Permanently deletes the selected profile folder and matching ProfileList registry keys after confirmation. If no matching SID or registry key exists, it can delete just the folder when the profile is otherwise safe. This action does not create a .old folder copy.");
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
            AppendParagraph(box, "The selected profile is blocked. Common reasons are that it is the current admin profile, loaded/locked, special/system, matched to multiple SIDs or normal keys, or Windows profile state could not be verified. Rebuild Profile also requires a matching SID; Delete Profile can still remove a folder-only temp profile when that missing SID is the only issue.");
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
            ProfileRecord selected = GetSelectedProfile();
            string selectedPath = selected == null ? null : selected.ProfilePath;
            RunBackground(
                "Refreshing profiles...",
                delegate { return ProfileService.GetProfiles(usersRoot); },
                delegate(object result)
                {
                    profiles = (List<ProfileRecord>)result;
                    RenderProfiles(selectedPath);
                    statusLabel.Text = "Loaded " + profiles.Count + " profile folder(s) from " + usersRoot + ".";
                });
        }

        private void RenderProfiles(string selectedPath)
        {
            grid.Rows.Clear();
            DataGridViewRow rowToSelect = null;
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
                if (!String.IsNullOrWhiteSpace(selectedPath) && String.Equals(profile.ProfilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    rowToSelect = row;
                }
            }

            grid.ClearSelection();
            if (rowToSelect == null && grid.Rows.Count > 0)
            {
                rowToSelect = grid.Rows[0];
            }
            if (rowToSelect != null)
            {
                rowToSelect.Selected = true;
                grid.CurrentCell = rowToSelect.Cells[0];
            }
            UpdateActionState();
            UpdateSelectedDetails();
        }

        private void RunDiagnostics()
        {
            RunBackground(
                "Running compatibility diagnostics...",
                delegate { return ProfileService.RunDiagnostics(ProfileTarget.Local(usersRoot)); },
                delegate(object result)
                {
                    CompatibilityReport report = (CompatibilityReport)result;
                    statusLabel.Text = report.HasIssues ? "Diagnostics completed with issues." : "Diagnostics passed.";
                    ShowTextDialog("Compatibility Diagnostics", report.ToDisplayText());
                });
        }

        private void RunBackground(string statusText, Func<object> work, Action<object> completed)
        {
            if (isBusy)
            {
                return;
            }

            SetBusy(true, statusText);
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = work(); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                SetBusy(false, null);
                worker.Dispose();
                if (e.Error != null)
                {
                    statusLabel.Text = "Operation failed.";
                    MessageBox.Show(e.Error.Message, "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                completed(e.Result);
            };
            worker.RunWorkerAsync();
        }

        private void SetBusy(bool busy, string statusText)
        {
            isBusy = busy;
            grid.Enabled = !busy;
            diagnosticsButton.Enabled = !busy;
            refreshButton.Enabled = !busy;
            progressBar.Visible = busy;
            if (!String.IsNullOrWhiteSpace(statusText))
            {
                statusLabel.Text = statusText;
            }
            UpdateActionState();
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
            bool canRebuild = !isBusy && hasSelection && !selected.IsBlocked;
            bool canDelete = !isBusy && hasSelection && !ProfileService.CreateDeleteProfilePlan(selected).IsBlocked;
            rebuildButton.Enabled = canRebuild;
            ApplyRebuildButtonStyle(canRebuild);
            deleteProfileButton.Enabled = canDelete;
            ApplyDeleteProfileButtonStyle(canDelete);
            rebuildMenuItem.Enabled = canRebuild;
            removeRegistryMenuItem.Enabled = !isBusy && hasSelection && selected.HasRegistryEntries && !selected.IsBlocked;
            copySidMenuItem.Enabled = !isBusy && hasSelection && !String.IsNullOrWhiteSpace(selected.BaseSid);
            openProfilePathMenuItem.Enabled = !isBusy && hasSelection && Directory.Exists(selected.ProfilePath);
            openRegistryMenuItem.Enabled = !isBusy && hasSelection && selected.HasRegistryEntries;
        }

        private void UpdateSelectedDetails()
        {
            ProfileRecord selected = GetSelectedProfile();
            if (selected == null)
            {
                detailsBox.Text = "Select a profile to view its safety details.";
                return;
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine(selected.FolderName + "  -  " + selected.ProfilePath);
            builder.AppendLine("SID: " + (String.IsNullOrWhiteSpace(selected.BaseSid) ? "(none matched)" : selected.BaseSid));
            builder.AppendLine("Loaded: " + (selected.Loaded ? "Yes" : "No") + "    Special: " + (selected.Special ? "Yes" : "No"));
            builder.AppendLine("Normal keys: " + FormatDetailList(selected.NormalKeyNames));
            builder.AppendLine(".bak keys: " + FormatDetailList(selected.BakKeyNames));
            builder.AppendLine("Blockers: " + FormatDetailList(selected.BlockReasons));
            builder.AppendLine("Warnings: " + FormatDetailList(selected.Warnings));
            detailsBox.Text = builder.ToString();
        }

        private static string FormatDetailList(IEnumerable<string> values)
        {
            List<string> list = new List<string>(values ?? Enumerable.Empty<string>());
            return list.Count == 0 ? "None" : String.Join("; ", list.ToArray());
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
                "After the rebuild succeeds, you can schedule a cancelable 60-second reboot or restart later." +
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

            string profilePath = selected.ProfilePath;
            RunBackground(
                "Rebuilding " + selected.FolderName + "... Do not close the application.",
                delegate { return ProfileService.RebuildProfile(profilePath, usersRoot); },
                delegate(object result)
                {
                    statusLabel.Text = "Rebuild completed.";
                    PromptForScheduledReboot((RebuildResult)result);
                    RefreshProfiles();
                });
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

            string profilePath = selected.ProfilePath;
            RunBackground(
                "Deleting " + selected.FolderName + "... Do not close the application.",
                delegate { return ProfileService.DeleteProfile(profilePath, usersRoot); },
                delegate(object result)
                {
                    MessageBox.Show(((DeleteProfileResult)result).ToDisplayText(), "Profile deleted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RefreshProfiles();
                });
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

            string profilePath = selected.ProfilePath;
            RunBackground(
                "Removing registry entries... Do not close the application.",
                delegate { return ProfileService.RemoveRegistryEntries(profilePath, usersRoot); },
                delegate(object result)
                {
                    MessageBox.Show(((RegistryRemovalResult)result).ToDisplayText(), "Registry removal completed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RefreshProfiles();
                });
        }

        private void PromptForScheduledReboot(RebuildResult result)
        {
            DialogResult restart = MessageBox.Show(
                result.ToDisplayText() + Environment.NewLine + Environment.NewLine +
                "Schedule a restart in 60 seconds? You will be able to cancel the countdown.",
                "Rebuild completed",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button2);
            if (restart != DialogResult.Yes)
            {
                statusLabel.Text = "Rebuild completed. Restart the computer before the target user signs in.";
                return;
            }

            try
            {
                ProfileService.ScheduleReboot(null, 60);
                using (RebootCountdownDialog dialog = new RebootCountdownDialog(60))
                {
                    dialog.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Rebuild completed, but reboot scheduling failed.";
                MessageBox.Show(ex.Message, "Reboot scheduling failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    internal sealed class RebootCountdownDialog : Form
    {
        private readonly Label countdownLabel;
        private readonly System.Windows.Forms.Timer timer;
        private int secondsRemaining;
        private bool resolved;

        public RebootCountdownDialog(int seconds)
        {
            secondsRemaining = seconds;
            Text = "Restart Scheduled";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 165);
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;

            Label explanation = new Label();
            explanation.Dock = DockStyle.Top;
            explanation.Height = 58;
            explanation.Padding = new Padding(18, 16, 18, 4);
            explanation.TextAlign = ContentAlignment.MiddleCenter;
            explanation.Text = "Windows will restart after the countdown unless you cancel it.";

            countdownLabel = new Label();
            countdownLabel.Dock = DockStyle.Top;
            countdownLabel.Height = 42;
            countdownLabel.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            countdownLabel.TextAlign = ContentAlignment.MiddleCenter;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 54;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Padding = new Padding(8);

            Button cancelButton = new Button();
            cancelButton.Text = "Cancel Restart";
            cancelButton.Width = 132;
            cancelButton.Height = 32;
            cancelButton.Click += delegate { CancelRestart(); };

            Button restartNowButton = new Button();
            restartNowButton.Text = "Restart Now";
            restartNowButton.Width = 120;
            restartNowButton.Height = 32;
            restartNowButton.Click += delegate { RestartNow(); };

            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(restartNowButton);
            Controls.Add(buttons);
            Controls.Add(countdownLabel);
            Controls.Add(explanation);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                secondsRemaining--;
                UpdateCountdown();
                if (secondsRemaining <= 0)
                {
                    resolved = true;
                    timer.Stop();
                    Close();
                }
            };
            Shown += delegate
            {
                UpdateCountdown();
                timer.Start();
            };
            FormClosing += delegate
            {
                if (!resolved)
                {
                    CancelRestart();
                }
            };
        }

        private void UpdateCountdown()
        {
            countdownLabel.Text = "Restarting in " + Math.Max(0, secondsRemaining) + " seconds";
        }

        private void CancelRestart()
        {
            if (resolved)
            {
                return;
            }
            ProfileService.AbortReboot(null);
            resolved = true;
            timer.Stop();
            Close();
        }

        private void RestartNow()
        {
            if (resolved)
            {
                return;
            }
            ProfileService.AbortReboot(null);
            ProfileService.ScheduleReboot(null, 0);
            resolved = true;
            timer.Stop();
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null)
            {
                timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
