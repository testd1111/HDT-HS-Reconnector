using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HdtBridge
{
    internal static class BridgeLogger
    {
        private const long MaxFileSizeBytes = 2 * 1024 * 1024;
        private static readonly object Sync = new object();
        private static readonly int CurrentPid = GetCurrentPid();
        private static readonly Dictionary<string, ThrottleState> ErrorThrottle = new Dictionary<string, ThrottleState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> LastDebugSignatures = new Dictionary<string, string>(StringComparer.Ordinal);

        private sealed class ThrottleState
        {
            public DateTime LastLoggedUtc;
            public int SuppressedCount;
        }

        private static volatile bool verboseEnabled;
        private static volatile bool disableFileLogging;

        public static bool VerboseEnabled
        {
            get => verboseEnabled;
            set => verboseEnabled = value;
        }

        public static bool DisableFileLogging
        {
            get => disableFileLogging;
            set => disableFileLogging = value;
        }

        public static string LogDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDT-HS-Reconnector");

        public static string LogFilePath => Path.Combine(LogDirectory, "hdt-bridge.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string context, Exception ex)
        {
            Write("ERROR", string.Format("{0}: {1}", context ?? "Bridge", ex != null ? ex.ToString() : "<null>"));
        }

        public static void ErrorThrottled(string key, string context, Exception ex, int minIntervalSeconds = 10)
        {
            var now = DateTime.UtcNow;
            var suppressed = 0;
            lock (Sync)
            {
                if (!ErrorThrottle.TryGetValue(key, out var state))
                {
                    state = new ThrottleState { LastLoggedUtc = now };
                    ErrorThrottle[key] = state;
                }
                else if ((now - state.LastLoggedUtc).TotalSeconds < minIntervalSeconds)
                {
                    state.SuppressedCount++;
                    return;
                }
                else
                {
                    suppressed = state.SuppressedCount;
                    state.SuppressedCount = 0;
                    state.LastLoggedUtc = now;
                }
            }

            var suffix = suppressed > 0 ? string.Format(" (+{0} similar errors suppressed)", suppressed) : string.Empty;
            Write("ERROR", string.Format("{0}{1}: {2}", context ?? key, suffix, ex != null ? ex.ToString() : "<null>"));
        }

        public static void Debug(string message)
        {
            if (!VerboseEnabled)
                return;
            Write("DEBUG", message);
        }

        public static void DebugOnce(string key, string signature, string message)
        {
            if (!VerboseEnabled)
                return;

            lock (Sync)
            {
                if (LastDebugSignatures.TryGetValue(key, out var prev) && string.Equals(prev, signature, StringComparison.Ordinal))
                    return;
                LastDebugSignatures[key] = signature;
            }

            Write("DEBUG", message);
        }

        public static void ResetDebugKeys()
        {
            lock (Sync)
                LastDebugSignatures.Clear();
        }

        private static void Write(string level, string message)
        {
            if (DisableFileLogging)
                return;

            var line = string.Format(
                "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] [PID:{2}] {3}",
                DateTime.Now,
                level,
                CurrentPid,
                message ?? string.Empty);

            lock (Sync)
            {
                try
                {
                    var dir = LogDirectory;
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "hdt-bridge.log");
                    TrimIfOversized(path);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // Logging inside HDT must never throw.
                }
            }
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

        private static int GetCurrentPid()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }
    }
}
