using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HS_Reconnector.Native;

namespace HS_Reconnector.Core
{
    internal enum HdtConnectionState
    {
        Checking,
        BridgeFilesMissing,
        NotFound,
        InstalledNotRunning,
        UnsupportedArch,
        NotElevated,
        Ready,
        Partial,
        Error
    }

    internal sealed class HdtStatusSnapshot
    {
        public HdtConnectionState State { get; set; } = HdtConnectionState.Checking;
        public string ExePath { get; set; } = string.Empty;
        public string HdtVersion { get; set; } = string.Empty;
        public bool IsRunning { get; set; }
        public int ProcessId { get; set; }
        public bool BridgeAttached { get; set; }
        public bool BridgeOutdated { get; set; }
        public string BridgeVersion { get; set; } = string.Empty;
        public int ReadyHooks { get; set; }
        public int TotalHooks { get; set; } = 7;
        public string MissingHooks { get; set; } = string.Empty;
        public bool InMatch { get; set; }
        public bool IsBattlegrounds { get; set; }
        public bool PendingRestore { get; set; }
        public string LastAction { get; set; } = string.Empty;
        public string ErrorDetail { get; set; } = string.Empty;
    }

    internal sealed class HdtSaveSummary
    {
        public bool Attempted { get; set; }
        public bool Saved { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    internal sealed class HdtBridgeClient : IDisposable
    {
        private const string PipePrefix = "HDT-HS-Reconnector-Bridge-";
        private const string BootstrapDllName = "HdtBootstrap.dll";
        private const string BridgeDllName = "HdtBridge.dll";
        private const int MaxPipeResponseBytes = 8192;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        internal const string ExpectedBridgeVersion = "0.8";

        private readonly object sync = new object();
        private readonly SemaphoreSlim ipcLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource disposeCts = new CancellationTokenSource();
        private int disposed;
        private readonly string baseDirectory;
        private HdtStatusSnapshot lastSnapshot = new HdtStatusSnapshot();
        private int lastInjectedPid;
        private int lastConfiguredPid;
        private string lastConfiguredConfigPayload;
        private OpponentMemorySettings latestDesiredOpponentMemory;
        private bool latestDesiredVerbose;
        private bool hasDesiredConfig;
        private DateTime nextAttachRetryUtc = DateTime.MinValue;
        private string lastLoggedSummary = string.Empty;

        public HdtBridgeClient(string baseDirectory = null)
        {
            this.baseDirectory = string.IsNullOrEmpty(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
        }

        public HdtStatusSnapshot GetCurrentSnapshot()
        {
            lock (sync)
                return lastSnapshot;
        }

        public HdtStatusSnapshot Refresh(bool forceAttach = false, OpponentMemorySettings opponentMemory = null, bool? verboseLogging = null)
        {
            if (Volatile.Read(ref disposed) != 0)
                return GetCurrentSnapshot();

            if (opponentMemory != null)
            {
                lock (sync)
                {
                    latestDesiredOpponentMemory = opponentMemory.Clone();
                    latestDesiredVerbose = verboseLogging ?? AppLogger.VerboseEnabled;
                    hasDesiredConfig = true;
                }
            }

            try
            {
                ipcLock.Wait(disposeCts.Token);
            }
            catch (OperationCanceledException)
            {
                return GetCurrentSnapshot();
            }
            catch (ObjectDisposedException)
            {
                return GetCurrentSnapshot();
            }

            try
            {
                if (Volatile.Read(ref disposed) != 0)
                    return GetCurrentSnapshot();
                return RefreshCore(forceAttach);
            }
            finally
            {
                try { ipcLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            try { disposeCts.Cancel(); } catch { }

            var acquired = false;
            try
            {
                acquired = ipcLock.Wait(250);
            }
            catch
            {
                // Ignore if already disposed or contended during shutdown.
            }

            if (acquired)
            {
                try { ipcLock.Dispose(); } catch { }
                try { disposeCts.Dispose(); } catch { }
            }
        }

        private HdtStatusSnapshot RefreshCore(bool forceAttach)
        {
            var bootstrapPath = Path.Combine(baseDirectory, BootstrapDllName);
            var bridgePath = Path.Combine(baseDirectory, BridgeDllName);

            var missingFiles = new List<string>();
            if (!File.Exists(bootstrapPath))
                missingFiles.Add(BootstrapDllName);
            if (!File.Exists(bridgePath))
                missingFiles.Add(BridgeDllName);

            var detected = HdtLocator.Inspect();
            AppLogger.Debug(string.Format(
                "HDT refresh: running={0}, pid={1}, x64={2}, ver={3}, forceAttach={4}",
                detected.IsRunning,
                detected.ProcessId,
                detected.Is64Bit,
                detected.EffectiveVersion,
                forceAttach));

            if (missingFiles.Count > 0)
            {
                var missingSnap = new HdtStatusSnapshot
                {
                    State = HdtConnectionState.BridgeFilesMissing,
                    ExePath = detected.EffectiveExePath ?? string.Empty,
                    HdtVersion = detected.EffectiveVersion ?? string.Empty,
                    IsRunning = detected.IsRunning,
                    ProcessId = detected.ProcessId,
                    ErrorDetail = "Missing: " + string.Join(", ", missingFiles)
                };
                StoreSnapshot(missingSnap);
                return missingSnap;
            }

            if (!detected.IsRunning)
            {
                lock (sync)
                {
                    lastInjectedPid = 0;
                    lastConfiguredPid = 0;
                    lastConfiguredConfigPayload = null;
                    nextAttachRetryUtc = DateTime.MinValue;
                }

                var offlineSnap = new HdtStatusSnapshot
                {
                    State = string.IsNullOrEmpty(detected.InstalledExePath)
                        ? HdtConnectionState.NotFound
                        : HdtConnectionState.InstalledNotRunning,
                    ExePath = detected.InstalledExePath ?? string.Empty,
                    HdtVersion = detected.InstalledVersion ?? string.Empty,
                    IsRunning = false
                };
                StoreSnapshot(offlineSnap);
                return offlineSnap;
            }

            if (!detected.Is64Bit)
            {
                var archSnap = new HdtStatusSnapshot
                {
                    State = HdtConnectionState.UnsupportedArch,
                    ExePath = detected.EffectiveExePath ?? string.Empty,
                    HdtVersion = detected.EffectiveVersion ?? string.Empty,
                    IsRunning = true,
                    ProcessId = detected.ProcessId,
                    ErrorDetail = "HearthstoneDeckTracker.exe is not 64-bit."
                };
                StoreSnapshot(archSnap);
                return archSnap;
            }

            // 1. Check if bridge is already alive inside this HDT process
            if (TrySendCommand(detected.ProcessId, "CHECK", 300, out var checkResponse))
            {
                var snap = ParseCheckResponse(detected, checkResponse);
                if (snap.BridgeAttached && !snap.BridgeOutdated)
                    SyncLatestConfigLocked(detected.ProcessId, forceSend: false);
                StoreSnapshot(snap);
                return snap;
            }

            // 2. Need to attach HdtBootstrap.dll -> HdtBridge.dll
            if (!Win32.IsElevated())
            {
                var noAdminSnap = new HdtStatusSnapshot
                {
                    State = HdtConnectionState.NotElevated,
                    ExePath = detected.EffectiveExePath ?? string.Empty,
                    HdtVersion = detected.EffectiveVersion ?? string.Empty,
                    IsRunning = true,
                    ProcessId = detected.ProcessId,
                    ErrorDetail = "Administrator privileges are required to connect the HDT bridge."
                };
                StoreSnapshot(noAdminSnap);
                return noAdminSnap;
            }

            lock (sync)
            {
                if (!forceAttach
                    && lastInjectedPid == detected.ProcessId
                    && DateTime.UtcNow < nextAttachRetryUtc)
                {
                    return lastSnapshot;
                }
            }

            ClearBootstrapErrorFile(detected.ProcessId);
            AppLogger.Debug("Injecting HdtBootstrap.dll into HDT PID " + detected.ProcessId + "...");

            if (!Win32.InjectDll(detected.ProcessId, bootstrapPath, out var injectError))
            {
                AppLogger.Warn("HdtBootstrap.dll injection failed for PID " + detected.ProcessId + ": " + injectError);
                lock (sync)
                {
                    lastInjectedPid = detected.ProcessId;
                    nextAttachRetryUtc = DateTime.UtcNow.AddSeconds(10);
                }

                var failSnap = new HdtStatusSnapshot
                {
                    State = HdtConnectionState.Error,
                    ExePath = detected.EffectiveExePath ?? string.Empty,
                    HdtVersion = detected.EffectiveVersion ?? string.Empty,
                    IsRunning = true,
                    ProcessId = detected.ProcessId,
                    ErrorDetail = injectError
                };
                StoreSnapshot(failSnap);
                return failSnap;
            }

            // 3. Wait for HdtBridge pipe server to start inside HDT
            var deadline = DateTime.UtcNow.AddMilliseconds(3500);
            while (DateTime.UtcNow < deadline)
            {
                if (TrySendCommand(detected.ProcessId, "CHECK", 250, out checkResponse))
                {
                    lock (sync)
                    {
                        lastInjectedPid = detected.ProcessId;
                        nextAttachRetryUtc = DateTime.MinValue;
                    }
                    var readySnap = ParseCheckResponse(detected, checkResponse);
                    if (readySnap.BridgeAttached && !readySnap.BridgeOutdated)
                        SyncLatestConfigLocked(detected.ProcessId, forceSend: true);
                    StoreSnapshot(readySnap);
                    return readySnap;
                }

                var bootstrapErr = ReadBootstrapErrorFile(detected.ProcessId);
                if (!string.IsNullOrEmpty(bootstrapErr))
                {
                    lock (sync)
                    {
                        lastInjectedPid = detected.ProcessId;
                        nextAttachRetryUtc = DateTime.UtcNow.AddSeconds(10);
                    }
                    var errSnap = new HdtStatusSnapshot
                    {
                        State = HdtConnectionState.Error,
                        ExePath = detected.EffectiveExePath ?? string.Empty,
                        HdtVersion = detected.EffectiveVersion ?? string.Empty,
                        IsRunning = true,
                        ProcessId = detected.ProcessId,
                        ErrorDetail = "Bootstrap failed (" + bootstrapErr + ")"
                    };
                    StoreSnapshot(errSnap);
                    return errSnap;
                }

                Thread.Sleep(100);
            }

            lock (sync)
            {
                lastInjectedPid = detected.ProcessId;
                nextAttachRetryUtc = DateTime.UtcNow.AddSeconds(10);
            }

            var timeoutSnap = new HdtStatusSnapshot
            {
                State = HdtConnectionState.Error,
                ExePath = detected.EffectiveExePath ?? string.Empty,
                HdtVersion = detected.EffectiveVersion ?? string.Empty,
                IsRunning = true,
                ProcessId = detected.ProcessId,
                ErrorDetail = "HDT bridge did not respond in time."
            };
            StoreSnapshot(timeoutSnap);
            return timeoutSnap;
        }

        public HdtSaveSummary SaveBeforeReconnect()
        {
            var summary = new HdtSaveSummary();
            var status = GetCurrentSnapshot();
            if (!status.BridgeAttached || status.ProcessId <= 0)
            {
                summary.Reason = "bridge_unavailable:" + status.State;
                AppLogger.Warn("HDT state save skipped: " + summary.Reason);
                return summary;
            }

            summary.Attempted = true;
            if (!TrySendCommand(status.ProcessId, "SAVE", 1200, out var response))
            {
                summary.Reason = "pipe_timeout";
                AppLogger.Warn("HDT bridge did not respond to SAVE before reconnect.");
                return summary;
            }

            var map = ParseKeyValues(response, out var ok);
            if (!ok)
            {
                summary.Reason = GetValue(map, "reason", "error");
                AppLogger.Warn("HDT state save returned error: " + summary.Reason);
                return summary;
            }

            summary.Saved = GetValue(map, "saved") == "1";
            summary.Reason = GetValue(map, "reason");
            summary.Details = string.Format(
                "handle={0}, boards={1}, deities={2}, tavern={3}, triples={4}, deadFor={5}, bobs={6}, tiers={7}, races={8}",
                GetValue(map, "handle"),
                GetValue(map, "boards", "0"),
                GetValue(map, "deities", "0"),
                GetValue(map, "tavern", "0"),
                GetValue(map, "triples", "0"),
                GetValue(map, "deadFor", "0"),
                GetValue(map, "bobs", "0"),
                GetValue(map, "tiers", "0"),
                GetValue(map, "races", "0"));

            if (summary.Saved)
                AppLogger.Info("Saved HDT Battlegrounds state (" + summary.Details + ").");
            else
                AppLogger.Info("HDT state save skipped (" + summary.Reason + ").");

            return summary;
        }

        public void ApplyConfig(OpponentMemorySettings opponentMemory, bool verboseLogging = false)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            lock (sync)
            {
                latestDesiredOpponentMemory = (opponentMemory ?? new OpponentMemorySettings()).Clone();
                latestDesiredVerbose = verboseLogging;
                hasDesiredConfig = true;
            }

            try
            {
                ipcLock.Wait(disposeCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref disposed) != 0)
                    return;

                var current = GetCurrentSnapshot();
                if (!current.BridgeAttached || current.ProcessId <= 0 || current.BridgeOutdated)
                {
                    AppLogger.Debug(string.Format(
                        "HDT ApplyConfig deferred (attached={0}, pid={1}, outdated={2}).",
                        current.BridgeAttached,
                        current.ProcessId,
                        current.BridgeOutdated));
                    return;
                }

                SyncLatestConfigLocked(current.ProcessId, forceSend: false);
            }
            finally
            {
                try { ipcLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        internal static string BuildConfigCommand(OpponentMemorySettings settings, bool verboseLogging = false)
        {
            var cfg = (settings ?? new OpponentMemorySettings()).Clone();
            cfg.Normalize();
            return string.Format(
                CultureInfo.InvariantCulture,
                "CONFIG|enabled={0}|mode={1}|style={2}|colorByResult={3}|win={4}|loss={5}|draw={6}|neutral={7}|scale={8}|opacity={9}|offsetX={10}|offsetY={11}|verbose={12}",
                cfg.Enabled ? "1" : "0",
                cfg.DisplayMode,
                cfg.VisualStyle,
                cfg.ColorByResult ? "1" : "0",
                cfg.WinColor,
                cfg.LossColor,
                cfg.DrawColor,
                cfg.NeutralColor,
                cfg.Scale,
                cfg.Opacity,
                cfg.OffsetX,
                cfg.OffsetY,
                verboseLogging ? "1" : "0");
        }

        private void SyncLatestConfigLocked(int pid, bool forceSend)
        {
            OpponentMemorySettings desired;
            bool desiredVerbose;
            lock (sync)
            {
                if (!hasDesiredConfig)
                    return;
                desired = latestDesiredOpponentMemory?.Clone();
                desiredVerbose = latestDesiredVerbose;
            }

            var cmd = BuildConfigCommand(desired, desiredVerbose);
            lock (sync)
            {
                if (!forceSend
                    && lastConfiguredPid == pid
                    && string.Equals(lastConfiguredConfigPayload, cmd, StringComparison.Ordinal))
                {
                    return;
                }
            }

            AppLogger.Debug("Sending HDT bridge config: " + cmd);
            if (TrySendCommand(pid, cmd, 500, out var response))
            {
                AppLogger.Debug("HDT bridge config response: " + response);
                ParseKeyValues(response, out var ok);
                if (ok)
                {
                    lock (sync)
                    {
                        lastConfiguredPid = pid;
                        lastConfiguredConfigPayload = cmd;
                    }
                }
            }
            else
            {
                AppLogger.Debug("HDT bridge config send timed out or failed for PID " + pid);
            }
        }

        public void CancelPendingRestore()
        {
            var snap = GetCurrentSnapshot();
            if (!snap.BridgeAttached || snap.ProcessId <= 0)
                return;

            TrySendCommand(snap.ProcessId, "CANCEL", 400, out _);
        }

        public void NotifyReconnected()
        {
            var snap = GetCurrentSnapshot();
            if (!snap.BridgeAttached || snap.ProcessId <= 0)
                return;

            if (TrySendCommand(snap.ProcessId, "RECONNECTED", 600, out var response))
            {
                var map = ParseKeyValues(response, out var ok);
                if (ok)
                {
                    var last = GetValue(map, "last");
                    if (!string.IsNullOrEmpty(last))
                        AppLogger.Info("HDT bridge post-reconnect status: " + last);
                }
            }
        }

        internal static HdtStatusSnapshot ParseCheckResponse(HdtDetectionInfo detected, string response)
        {
            var map = ParseKeyValues(response, out var ok);
            if (!ok)
            {
                return new HdtStatusSnapshot
                {
                    State = HdtConnectionState.Error,
                    ExePath = detected?.EffectiveExePath ?? string.Empty,
                    HdtVersion = detected?.EffectiveVersion ?? string.Empty,
                    IsRunning = detected?.IsRunning ?? false,
                    ProcessId = detected?.ProcessId ?? 0,
                    ErrorDetail = GetValue(map, "reason", "Invalid CHECK response")
                };
            }

            var ready = ParseInt(GetValue(map, "ready"));
            var total = Math.Max(1, ParseInt(GetValue(map, "total"), 7));
            var missing = GetValue(map, "missing");
            var reportedVersion = GetValue(map, "hdtVersion");
            var effectiveVersion = !string.IsNullOrEmpty(reportedVersion)
                ? reportedVersion
                : (detected?.EffectiveVersion ?? string.Empty);

            var bridgeVer = GetValue(map, "bridge");
            return new HdtStatusSnapshot
            {
                State = ready >= total ? HdtConnectionState.Ready : HdtConnectionState.Partial,
                ExePath = detected?.EffectiveExePath ?? string.Empty,
                HdtVersion = effectiveVersion,
                IsRunning = true,
                ProcessId = detected?.ProcessId ?? 0,
                BridgeAttached = true,
                BridgeOutdated = !string.IsNullOrEmpty(bridgeVer) && !string.Equals(bridgeVer, ExpectedBridgeVersion, StringComparison.Ordinal),
                BridgeVersion = bridgeVer,
                ReadyHooks = ready,
                TotalHooks = total,
                MissingHooks = missing,
                InMatch = GetValue(map, "inMatch") == "1",
                IsBattlegrounds = GetValue(map, "isBg") == "1",
                PendingRestore = GetValue(map, "pending") == "1",
                LastAction = GetValue(map, "last")
            };
        }

        internal static Dictionary<string, string> ParseKeyValues(string response, out bool ok)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ok = false;
            if (string.IsNullOrWhiteSpace(response))
                return result;

            var parts = response.Split('|');
            if (parts.Length == 0)
                return result;

            ok = string.Equals(parts[0], "OK", StringComparison.OrdinalIgnoreCase);
            for (var i = 1; i < parts.Length; i++)
            {
                var eq = parts[i].IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = parts[i].Substring(0, eq).Trim();
                var value = parts[i].Substring(eq + 1);
                result[key] = value;
            }

            return result;
        }

        private static bool TrySendCommand(int pid, string command, int timeoutMs, out string response)
        {
            return TrySendCommandToPipe(PipePrefix + pid, command, timeoutMs, out response);
        }

        internal static bool TrySendCommandToPipe(string pipeName, string command, int timeoutMs, out string response)
        {
            response = null;
            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    client.Connect(timeoutMs);
                    using (var cts = new CancellationTokenSource(timeoutMs))
                    using (cts.Token.Register(() => { try { client.Dispose(); } catch { } }))
                    {
                        var payload = Utf8NoBom.GetBytes((command ?? string.Empty) + "\n");
                        client.WriteAsync(payload, 0, payload.Length, cts.Token).GetAwaiter().GetResult();
                        client.FlushAsync(cts.Token).GetAwaiter().GetResult();
                        response = ReadLineBounded(client, MaxPipeResponseBytes, cts.Token);
                        return !string.IsNullOrEmpty(response);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!string.Equals(command, "CHECK", StringComparison.Ordinal))
                    AppLogger.Debug(string.Format("Pipe command '{0}' to '{1}' failed: {2}", command, pipeName, ex.Message));
                return false;
            }
        }

        private static string ReadLineBounded(Stream stream, int maxBytes, CancellationToken ct)
        {
            var buffer = new byte[512];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var read = stream.ReadAsync(buffer, 0, buffer.Length, ct).GetAwaiter().GetResult();
                    if (read <= 0)
                        break;

                    for (var i = 0; i < read; i++)
                    {
                        var b = buffer[i];
                        if (b == (byte)'\n')
                        {
                            var bytes = ms.ToArray();
                            var len = bytes.Length;
                            if (len > 0 && bytes[len - 1] == (byte)'\r')
                                len--;
                            return Utf8NoBom.GetString(bytes, 0, len);
                        }

                        if (ms.Length >= maxBytes)
                            throw new InvalidDataException("Pipe response exceeded " + maxBytes + " bytes.");
                        ms.WriteByte(b);
                    }
                }

                if (ms.Length == 0)
                    return null;

                throw new EndOfStreamException("Truncated pipe response without newline terminator.");
            }
        }

        private void StoreSnapshot(HdtStatusSnapshot snap)
        {
            string logLine = null;
            lock (sync)
            {
                lastSnapshot = snap;
                var summary = string.Format(
                    "{0}|pid={1}|ver={2}|hooks={3}/{4}|missing={5}|err={6}",
                    snap.State,
                    snap.ProcessId,
                    snap.HdtVersion,
                    snap.ReadyHooks,
                    snap.TotalHooks,
                    snap.MissingHooks,
                    snap.ErrorDetail);
                if (!string.Equals(summary, lastLoggedSummary, StringComparison.Ordinal))
                {
                    lastLoggedSummary = summary;
                    logLine = FormatLogSummary(snap);
                }
            }

            if (!string.IsNullOrEmpty(logLine))
                AppLogger.Info(logLine);
        }

        private static string FormatLogSummary(HdtStatusSnapshot snap)
        {
            switch (snap.State)
            {
                case HdtConnectionState.Ready:
                    return string.Format("HDT bridge connected (PID {0}, HDT {1}, {2}/{3} hooks ready).",
                        snap.ProcessId, snap.HdtVersion, snap.ReadyHooks, snap.TotalHooks);
                case HdtConnectionState.Partial:
                    return string.Format("HDT bridge partially ready (PID {0}, HDT {1}, {2}/{3}, missing: {4}).",
                        snap.ProcessId, snap.HdtVersion, snap.ReadyHooks, snap.TotalHooks, snap.MissingHooks);
                case HdtConnectionState.InstalledNotRunning:
                    return string.Format("HDT executable found ({0}), waiting for HearthstoneDeckTracker.exe to start.",
                        string.IsNullOrEmpty(snap.HdtVersion) ? snap.ExePath : "v" + snap.HdtVersion);
                case HdtConnectionState.NotFound:
                    return "HearthstoneDeckTracker.exe not found on disk and not running.";
                case HdtConnectionState.BridgeFilesMissing:
                    return "HDT bridge files missing: " + snap.ErrorDetail;
                case HdtConnectionState.Error:
                case HdtConnectionState.UnsupportedArch:
                case HdtConnectionState.NotElevated:
                    return "HDT bridge unavailable: " + snap.ErrorDetail;
                default:
                    return string.Empty;
            }
        }

        private static string GetBootstrapErrorPath(int pid)
        {
            return Path.Combine(Path.GetTempPath(), "hdt-hs-reconnector-bootstrap-" + pid + ".err");
        }

        private static void ClearBootstrapErrorFile(int pid)
        {
            try
            {
                var path = GetBootstrapErrorPath(pid);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Ignore cleanup failure.
            }
        }

        private static string ReadBootstrapErrorFile(int pid)
        {
            try
            {
                var path = GetBootstrapErrorPath(pid);
                if (File.Exists(path))
                    return File.ReadAllText(path).Trim();
            }
            catch
            {
                // Ignore read failure.
            }
            return null;
        }

        private static string GetValue(Dictionary<string, string> map, string key, string fallback = "")
        {
            return map != null && map.TryGetValue(key, out var val) ? val : fallback;
        }

        private static int ParseInt(string text, int fallback = 0)
        {
            return int.TryParse(text, out var value) ? value : fallback;
        }
    }
}
