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

        public bool HasWarnings
        {
            get { return Checks.Any(c => String.Equals(c.Status, "WARN", StringComparison.OrdinalIgnoreCase)); }
        }

        public bool HasIssues
        {
            get { return HasFailures || HasWarnings; }
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
            if (HasFailures)
            {
                builder.AppendLine("One or more checks failed. Fix those before rebuilding a profile on this computer.");
            }
            else if (HasWarnings)
            {
                builder.AppendLine("One or more checks completed with warnings. Review those before rebuilding a profile on this computer.");
            }
            else
            {
                builder.AppendLine("All checks passed.");
            }
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
            builder.AppendLine("Base SID: " + (String.IsNullOrWhiteSpace(BaseSid) ? "(none matched)" : BaseSid));
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
            List<string> keys = RegistryKeyNames ?? new List<string>();
            if (keys.Count == 0)
            {
                builder.AppendLine("  (none found)");
            }
            foreach (string keyName in keys)
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
                if (keys.Count == 0)
                {
                    builder.AppendLine("This will permanently delete the profile folder. No matching ProfileList key was found.");
                }
                else
                {
                    builder.AppendLine("This will permanently delete the profile folder and remove the matching ProfileList key(s).");
                }
            }
            return builder.ToString();
        }
    }

    internal sealed class BakRemovalPlan
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
            builder.AppendLine(".bak registry keys to export and delete:");
            foreach (string keyName in RegistryKeyNames ?? new List<string>())
            {
                builder.AppendLine("  " + RegistryRoot + "\\" + keyName);
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
            List<string> keys = RemovedKeys ?? new List<string>();
            if (keys.Count == 0)
            {
                builder.AppendLine("  (none found)");
            }
            foreach (string keyName in keys)
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
