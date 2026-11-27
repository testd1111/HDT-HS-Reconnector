using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HS_Reconnector.Native;
using Microsoft.Win32;

namespace HS_Reconnector.Core
{
    internal sealed class HdtDetectionInfo
    {
        public string InstalledExePath { get; set; }
        public string InstalledVersion { get; set; }
        public bool IsRunning { get; set; }
        public int ProcessId { get; set; }
        public bool Is64Bit { get; set; }
        public string RunningExePath { get; set; }
        public string RunningVersion { get; set; }
        public string EffectiveExePath => !string.IsNullOrEmpty(RunningExePath) ? RunningExePath : InstalledExePath;
        public string EffectiveVersion => !string.IsNullOrEmpty(RunningVersion) ? RunningVersion : InstalledVersion;
    }

    internal static class HdtLocator
    {
        private const string HdtProcessName = "HearthstoneDeckTracker";
        private const string HdtExeName = "HearthstoneDeckTracker.exe";

        public static HdtDetectionInfo Inspect()
        {
            var info = new HdtDetectionInfo();
            DetectRunningProcess(info);

            if (!string.IsNullOrEmpty(info.RunningExePath))
            {
                info.InstalledExePath = info.RunningExePath;
                info.InstalledVersion = info.RunningVersion;
                return info;
            }

            var installed = FindInstalledExecutable();
            if (!string.IsNullOrEmpty(installed))
            {
                info.InstalledExePath = installed;
                info.InstalledVersion = ReadFileVersion(installed);
            }

            return info;
        }

        private static void DetectRunningProcess(HdtDetectionInfo info)
        {
            var currentSessionId = Win32.CurrentSessionId;

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(HdtProcessName);
            }
            catch
            {
                return;
            }

            try
            {
                foreach (var process in processes.OrderBy(p => p.Id))
                {
                    try
                    {
                        if (process.HasExited || process.SessionId != currentSessionId)
                            continue;

                        info.IsRunning = true;
                        info.ProcessId = process.Id;
                        info.Is64Bit = Win32.IsProcess64Bit(process.Id);
                        info.RunningExePath = Win32.TryGetProcessPath(process.Id);
                        if (!string.IsNullOrEmpty(info.RunningExePath))
                            info.RunningVersion = ReadFileVersion(info.RunningExePath);
                        return;
                    }
                    catch
                    {
                        // Process exited or inaccessible while inspecting.
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        internal static string FindInstalledExecutable()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var candidates = new[]
                {
                    Path.Combine(localAppData, "HearthstoneDeckTracker", "current", HdtExeName),
                    Path.Combine(localAppData, "HearthstoneDeckTracker", HdtExeName)
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }

                // Legacy Squirrel app-x.y.z folders fallback
                var hdtRoot = Path.Combine(localAppData, "HearthstoneDeckTracker");
                if (Directory.Exists(hdtRoot))
                {
                    try
                    {
                        var appDirs = Directory.GetDirectories(hdtRoot, "app-*")
                            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d));
                        foreach (var dir in appDirs)
                        {
                            var exe = Path.Combine(dir, HdtExeName);
                            if (File.Exists(exe))
                                return exe;
                        }
                    }
                    catch
                    {
                        // Ignore directory enumeration errors.
                    }
                }
            }

            var fromRegistry = FindFromUninstallRegistry(RegistryHive.CurrentUser, RegistryView.Registry64)
                ?? FindFromUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry64)
                ?? FindFromUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry32);
            if (!string.IsNullOrEmpty(fromRegistry))
                return fromRegistry;

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                var pfExe = Path.Combine(programFiles, "Hearthstone Deck Tracker", HdtExeName);
                if (File.Exists(pfExe))
                    return pfExe;
            }

            return null;
        }

        private static string FindFromUninstallRegistry(RegistryHive hive, RegistryView view)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall == null)
                        return null;

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using (var subKey = uninstall.OpenSubKey(subKeyName))
                        {
                            var displayName = subKey?.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(displayName)
                                || displayName.IndexOf("Hearthstone Deck Tracker", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            var installLocation = subKey.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(installLocation))
                            {
                                var currentExe = Path.Combine(installLocation, "current", HdtExeName);
                                if (File.Exists(currentExe))
                                    return currentExe;

                                var directExe = Path.Combine(installLocation, HdtExeName);
                                if (File.Exists(directExe))
                                    return directExe;
                            }

                            var displayIcon = subKey.GetValue("DisplayIcon") as string;
                            if (!string.IsNullOrEmpty(displayIcon))
                            {
                                var cleaned = displayIcon.Trim().Trim('"');
                                var commaIndex = cleaned.IndexOf(',');
                                if (commaIndex > 0)
                                    cleaned = cleaned.Substring(0, commaIndex).Trim();
                                if (File.Exists(cleaned)
                                    && cleaned.EndsWith(HdtExeName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return cleaned;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore registry access failures.
            }

            return null;
        }

        internal static string ReadFileVersion(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return string.Empty;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                    return info.ProductVersion.Trim();
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    return info.FileVersion.Trim();
            }
            catch
            {
                // Ignore version inspection errors.
            }

            return string.Empty;
        }
    }
}
