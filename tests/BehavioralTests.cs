using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TempProfileFixer.Tests
{
    internal static class BehavioralTests
    {
        private static int failures;

        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "TempProfileFixer-Behavioral-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TestPlansFailClosed();
                TestPartialWmiBlocksInventory(root);
                TestFolderOnlyDeletePolicy();
                TestRegistryBackupOrdering(root);
                TestBackupValidationPreventsRemoval(root);
                TestPartialFailureDetails(root);
                TestRunIdsAreUnique();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            if (failures > 0)
            {
                Console.Error.WriteLine(failures + " behavioral test(s) failed.");
                return 1;
            }

            Console.WriteLine("Behavioral tests passed.");
            return 0;
        }

        private static void TestPlansFailClosed()
        {
            string[] reasons =
            {
                "Current admin profile",
                "Profile is loaded",
                "Special/system profile",
                "Multiple SIDs match this folder",
                "Could not verify loaded profile state",
                "Could not fully verify loaded profile state"
            };

            foreach (string reason in reasons)
            {
                ProfileRecord profile = CreateProfile(reason);
                Assert(ProfileService.CreateRebuildPlan(profile, DateTime.Now).IsBlocked, "Rebuild must block: " + reason);
                Assert(ProfileService.CreateDeleteProfilePlan(profile).IsBlocked, "Delete must block: " + reason);
                Assert(ProfileService.CreateRegistryRemovalPlan(profile).IsBlocked, "Registry removal must block: " + reason);
                Assert(ProfileService.CreateBakRemovalPlan(profile).IsBlocked, ".bak removal must block: " + reason);
            }
        }

        private static void TestPartialWmiBlocksInventory(string root)
        {
            string profilePath = Path.Combine(root, "PartialWmiUser");
            Directory.CreateDirectory(profilePath);
            string normalized = ProfileService.NormalizePath(profilePath);
            List<FolderRecord> folders = new List<FolderRecord>
            {
                new FolderRecord { Name = "PartialWmiUser", FullName = profilePath, NormalizedPath = normalized, ActualNormalizedPath = normalized }
            };
            List<ProfileListEntry> entries = new List<ProfileListEntry>
            {
                new ProfileListEntry { KeyName = "S-1-test", BaseSid = "S-1-test", NormalizedProfilePath = normalized }
            };

            ProfileRecord record = ProfileService.BuildInventory(
                folders,
                entries,
                new List<UserProfileState>(),
                String.Empty,
                String.Empty,
                null,
                "Skipped unreadable Win32_UserProfile row(s): row 2",
                ProfileService.ProfileListRegPath,
                null).Single();

            Assert(record.IsBlocked, "Partial WMI inventory must block mutations.");
            Assert(record.BlockReasons.Contains("Could not fully verify loaded profile state"), "Partial WMI block reason must be explicit.");
        }

        private static void TestFolderOnlyDeletePolicy()
        {
            ProfileRecord profile = CreateProfile("No matching ProfileList SID");
            profile.BaseSid = String.Empty;
            profile.NormalKeyNames.Clear();
            profile.BakKeyNames.Clear();
            Assert(!ProfileService.CreateDeleteProfilePlan(profile).IsBlocked, "Folder-only deletion should remain allowed when missing SID is the only blocker.");

            profile.BlockReasons.Add("Could not fully verify loaded profile state");
            Assert(ProfileService.CreateDeleteProfilePlan(profile).IsBlocked, "Folder-only deletion must block when WMI verification is partial.");
        }

        private static void TestRegistryBackupOrdering(string root)
        {
            string backup = Path.Combine(root, "ordered-backups");
            Directory.CreateDirectory(backup);
            string log = Path.Combine(root, "ordered.log");
            FakeMutationAdapter adapter = new FakeMutationAdapter();
            List<string> keys = new List<string> { "S-1-first", "S-1-second.bak" };
            List<string> completed = new List<string>();

            ProfileService.ExportAndValidateRegistryKeys(keys, backup, null, log, adapter);
            ProfileService.RemoveRegistryKeys(keys, null, log, adapter, completed);

            Assert(String.Join(",", adapter.Events.ToArray()) == "export:S-1-first,export:S-1-second.bak,remove:S-1-first,remove:S-1-second.bak", "Every registry backup must complete before the first removal.");
            Assert(completed.Count == 2, "Completed registry removals must be tracked.");
        }

        private static void TestBackupValidationPreventsRemoval(string root)
        {
            string backup = Path.Combine(root, "invalid-backups");
            Directory.CreateDirectory(backup);
            string log = Path.Combine(root, "invalid.log");
            FakeMutationAdapter adapter = new FakeMutationAdapter { WriteInvalidBackup = true };
            bool failed = false;
            try
            {
                ProfileService.ExportAndValidateRegistryKeys(new[] { "S-1-invalid" }, backup, null, log, adapter);
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }

            Assert(failed, "Invalid registry backup must fail validation.");
            Assert(!adapter.Events.Any(e => e.StartsWith("remove:", StringComparison.Ordinal)), "Backup validation failure must happen before registry removal.");
        }

        private static void TestPartialFailureDetails(string root)
        {
            string log = Path.Combine(root, "partial.log");
            File.WriteAllText(log, String.Empty);
            FakeMutationAdapter adapter = new FakeMutationAdapter { FailRemoveKey = "S-1-second" };
            List<string> completed = new List<string>();
            Exception caught = null;
            try
            {
                ProfileService.RemoveRegistryKeys(new[] { "S-1-first", "S-1-second" }, null, log, adapter, completed);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            ProfileOperationException failure = ProfileService.CreateOperationFailure(
                "Registry removal",
                "removing ProfileList registry keys",
                root,
                log,
                null,
                completed,
                caught);
            Assert(completed.Count == 1, "Partial registry removal must track only completed keys.");
            Assert(failure.Message.Contains("Removed registry key S-1-first"), "Failure must identify completed mutations.");
            Assert(failure.Message.Contains(root) && failure.Message.Contains(log), "Failure must include recovery locations.");
        }

        private static void TestRunIdsAreUnique()
        {
            string first = ProfileService.CreateRunId();
            string second = ProfileService.CreateRunId();
            Assert(first != second, "Run identifiers must be collision resistant within one process.");
            Assert(first.Contains("-p"), "Run identifiers must include the process ID.");
        }

        private static ProfileRecord CreateProfile(string blockReason)
        {
            List<string> reasons = new List<string>();
            if (!String.IsNullOrWhiteSpace(blockReason))
            {
                reasons.Add(blockReason);
            }
            return new ProfileRecord
            {
                FolderName = "TestUser",
                ProfilePath = @"C:\Users\TestUser",
                RegistryRoot = ProfileService.ProfileListRegPath,
                BaseSid = "S-1-test",
                NormalKeyNames = new List<string> { "S-1-test" },
                BakKeyNames = new List<string> { "S-1-test.bak" },
                NormalKeyPresent = true,
                BakKeyPresent = true,
                IsBlocked = reasons.Count > 0,
                BlockReasons = reasons,
                Warnings = new List<string>()
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                failures++;
                Console.Error.WriteLine("FAIL: " + message);
            }
        }

        private sealed class FakeMutationAdapter : IProfileMutationAdapter
        {
            public readonly List<string> Events = new List<string>();
            public bool WriteInvalidBackup;
            public string FailRemoveKey;

            public void ExportRegistryKey(string keyName, string destinationPath, string computerName)
            {
                Events.Add("export:" + keyName);
                File.WriteAllText(destinationPath, WriteInvalidBackup ? "invalid" : "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\Test]");
            }

            public void RemoveRegistryKey(string keyName, string computerName)
            {
                Events.Add("remove:" + keyName);
                if (String.Equals(keyName, FailRemoveKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("simulated removal failure");
                }
            }

            public void MoveDirectory(string sourcePath, string destinationPath)
            {
                Events.Add("move");
            }

            public void DeleteDirectoryTree(string directoryPath, string logPath)
            {
                Events.Add("delete");
            }
        }
    }
}
