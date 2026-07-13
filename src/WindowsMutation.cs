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
    internal interface IProfileMutationAdapter
    {
        void ExportRegistryKey(string keyName, string destinationPath, string computerName);
        void RemoveRegistryKey(string keyName, string computerName);
        void MoveDirectory(string sourcePath, string destinationPath);
        void DeleteDirectoryTree(string directoryPath, string logPath);
    }

    internal sealed class WindowsProfileMutationAdapter : IProfileMutationAdapter
    {
        public void ExportRegistryKey(string keyName, string destinationPath, string computerName)
        {
            ProfileService.ExportRegistryKey(keyName, destinationPath, computerName);
        }

        public void RemoveRegistryKey(string keyName, string computerName)
        {
            ProfileService.RemoveRegistryKey(keyName, computerName);
        }

        public void MoveDirectory(string sourcePath, string destinationPath)
        {
            Directory.Move(sourcePath, destinationPath);
        }

        public void DeleteDirectoryTree(string directoryPath, string logPath)
        {
            ProfileService.DeleteDirectoryTree(directoryPath, logPath);
        }
    }

    internal sealed class ProfileOperationException : InvalidOperationException
    {
        public string Operation { get; private set; }
        public string Phase { get; private set; }
        public string BackupDirectory { get; private set; }
        public string LogPath { get; private set; }
        public string RenamedPath { get; private set; }
        public List<string> CompletedActions { get; private set; }

        public ProfileOperationException(
            string operation,
            string phase,
            string backupDirectory,
            string logPath,
            string renamedPath,
            IEnumerable<string> completedActions,
            Exception innerException)
            : base(BuildMessage(operation, phase, backupDirectory, logPath, renamedPath, completedActions, innerException), innerException)
        {
            Operation = operation;
            Phase = phase;
            BackupDirectory = backupDirectory;
            LogPath = logPath;
            RenamedPath = renamedPath;
            CompletedActions = new List<string>(completedActions ?? Enumerable.Empty<string>());
        }

        private static string BuildMessage(
            string operation,
            string phase,
            string backupDirectory,
            string logPath,
            string renamedPath,
            IEnumerable<string> completedActions,
            Exception innerException)
        {
            List<string> actions = new List<string>(completedActions ?? Enumerable.Empty<string>());
            StringBuilder builder = new StringBuilder();
            builder.Append(operation + " failed while " + phase + ": " + innerException.Message);
            builder.AppendLine();
            builder.AppendLine("Registry backups: " + backupDirectory);
            builder.AppendLine("Log: " + logPath);
            if (!String.IsNullOrWhiteSpace(renamedPath))
            {
                builder.AppendLine("Planned renamed profile path: " + renamedPath);
            }
            if (actions.Count > 0)
            {
                builder.AppendLine("Completed before failure: " + String.Join("; ", actions.ToArray()));
            }
            builder.Append("Review the log and validated .reg backups before attempting manual recovery. No automatic rollback was attempted.");
            return builder.ToString();
        }
    }

}
