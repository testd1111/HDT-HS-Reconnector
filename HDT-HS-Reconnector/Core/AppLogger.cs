using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace HS_Reconnector.Core
{
    internal static class AppLogger
    {
        private const int MaxEntries = 1000;
        private const long MaxFileSizeBytes = 2 * 1024 * 1024;
        private const int MaxCrashReports = 15;
        private const int CrashThrottleSeconds = 30;
        private const int MaxThrottleKeys = 64;
        private static readonly object Sync = new object();
        private static readonly List<string> Entries = new List<string>(MaxEntries);
        private static readonly Dictionary<string, DateTime> NonTerminatingCrashThrottle = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private static volatile bool disableFileLogging;
        private static volatile bool verboseEnabled;

        public static bool DisableFileLogging
        {
            get => disableFileLogging;
            set => disableFileLogging = value;
        }

        public static bool VerboseEnabled
        {
            get => verboseEnabled;
            set => verboseEnabled = value;
        }
        public static event Action EntryAdded;

        public static string LogFilePath => Path.Combine(AppSettings.DataDirectory, "hdt-hs-reconnector.log");
        public static string BridgeLogFilePath => Path.Combine(AppSettings.DataDirectory, "hdt-bridge.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(Exception ex) => Write("ERROR", ex?.ToString() ?? "<null>");

        public static void Error(string context, Exception ex)
        {
            Write("ERROR", string.Format("{0}: {1}", context ?? "Error", ex?.ToString() ?? "<null>"));
        }

        public static void Debug(string message)
        {
            if (!VerboseEnabled)
                return;
            Write("DEBUG", message);
        }

        public static string WriteCrashReport(string source, Exception ex, bool isTerminating, string directoryOverride = null)
        {
            var now = DateTime.Now;
            var nowUtc = DateTime.UtcNow;
            var key = string.Format("{0}|{1}", source ?? "Unknown", ex?.GetType().FullName ?? "null");

            if (!isTerminating)
            {
                lock (Sync)
                {
                    if (NonTerminatingCrashThrottle.TryGetValue(key, out var lastUtc)
                        && (nowUtc - lastUtc).TotalSeconds < CrashThrottleSeconds)
                    {
                        return null;
                    }
                    if (NonTerminatingCrashThrottle.Count >= MaxThrottleKeys)
                        NonTerminatingCrashThrottle.Clear();
                    NonTerminatingCrashThrottle[key] = nowUtc;
                }
            }

            var summary = string.Format(
                "Unhandled exception ({0}, terminating={1}): {2}",
                source ?? "Unknown",
                isTerminating,
                ex != null ? ex.ToString() : "<null>");
            Write("FATAL", summary);

            if (DisableFileLogging && string.IsNullOrEmpty(directoryOverride))
                return null;

            try
            {
                var dir = string.IsNullOrEmpty(directoryOverride) ? AppSettings.DataDirectory : directoryOverride;
                Directory.CreateDirectory(dir);
                var fileName = string.Format("crash-{0:yyyyMMdd-HHmmss-fff}-{1}.log", now, Process.GetCurrentProcess().Id);
                var crashPath = Path.Combine(dir, fileName);

                string[] recentLogs;
                lock (Sync)
                {
                    var count = Math.Min(Entries.Count, 80);
                    recentLogs = new string[count];
                    Entries.CopyTo(Entries.Count - count, recentLogs, 0, count);
                }

                var sb = new StringBuilder();
                sb.AppendLine("=== HDT-HS-Reconnector Crash Report ===");
                sb.AppendLine("LocalTime:     " + now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.AppendLine("UtcTime:       " + nowUtc.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.AppendLine("Source:        " + (source ?? "Unknown"));
                sb.AppendLine("IsTerminating: " + isTerminating);
                sb.AppendLine("AppVersion:    " + GetAppVersion());
                sb.AppendLine("OSVersion:     " + Environment.OSVersion);
                sb.AppendLine("CLRVersion:    " + Environment.Version);
                sb.AppendLine("Process64Bit:  " + Environment.Is64BitProcess + " (OS64Bit=" + Environment.Is64BitOperatingSystem + ")");
                sb.AppendLine("Thread:        id=" + Thread.CurrentThread.ManagedThreadId + " name=" + (Thread.CurrentThread.Name ?? "<unnamed>"));
                sb.AppendLine("VerboseMode:   " + VerboseEnabled);
                sb.AppendLine();
                sb.AppendLine("=== Exception ===");
                sb.AppendLine(ex != null ? ex.ToString() : "<null>");
                sb.AppendLine();
                sb.AppendLine("=== Recent Activity Log ===");
                foreach (var entry in recentLogs)
                    sb.AppendLine(entry);

                File.WriteAllText(crashPath, sb.ToString(), new UTF8Encoding(false));
                PruneOldCrashReports(dir);
                return crashPath;
            }
            catch
            {
                return null;
            }
        }

        private static void PruneOldCrashReports(string dir)
        {
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("crash-*.log");
                if (files.Length <= MaxCrashReports)
                    return;

                Array.Sort(files, (a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                for (var i = MaxCrashReports; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        public static IReadOnlyList<string> GetSnapshot()
        {
            lock (Sync)
                return Entries.ToArray();
        }

        public static void Clear()
        {
            lock (Sync)
                Entries.Clear();
        }

        private static void Write(string level, string message)
        {
            var now = DateTime.Now;
            var uiTime = string.Equals(level, "DEBUG", StringComparison.Ordinal)
                ? now.ToString("HH:mm:ss.fff")
                : now.ToString("HH:mm:ss");
            var uiLine = string.Format("[{0}] [{1}] {2}", uiTime, level, message ?? string.Empty);
            var fileLine = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}", now, level, message ?? string.Empty);

            lock (Sync)
            {
                if (Entries.Count >= MaxEntries)
                    Entries.RemoveAt(0);
                Entries.Add(uiLine);
                if (!DisableFileLogging)
                {
                    try
                    {
                        var logDir = AppSettings.DataDirectory;
                        Directory.CreateDirectory(logDir);
                        var logPath = Path.Combine(logDir, "hdt-hs-reconnector.log");
                        TrimIfOversized(logPath);
                        File.AppendAllText(logPath, fileLine + Environment.NewLine, new UTF8Encoding(false));
                    }
                    catch
                    {
                        // Logging must never crash the application.
                    }
                }
            }

            EntryAdded?.Invoke();
        }

        private static void TrimIfOversized(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxFileSizeBytes)
                return;

            var backup = path + ".old";
            if (File.Exists(backup))
                File.Delete(backup);
            File.Move(path, backup);
        }

        private static string GetAppVersion()
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? v.ToString() : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
