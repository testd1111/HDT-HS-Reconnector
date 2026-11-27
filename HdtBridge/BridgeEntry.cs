using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HdtBridge
{
    public static class BridgeEntry
    {
        public const string BridgeVersion = "0.9";
        private const string PipePrefix = "HDT-HS-Reconnector-Bridge-";
        private const int MaxLineBytes = 8192;
        private const int ClientReadTimeoutMs = 3000;
        private const int ClientWriteTimeoutMs = 2000;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static int initialized;
        private static HdtStatePreserver preserver;
        private static LastOpponentTracker lastOpponentTracker;
        private static Thread serverThread;

        // Exact signature required by ICLRRuntimeHost::ExecuteInDefaultAppDomain
        public static int Initialize(string arg)
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
                return 0;

            try
            {
                preserver = new HdtStatePreserver();
                lastOpponentTracker = new LastOpponentTracker();
                var pipeName = PipePrefix + Process.GetCurrentProcess().Id;

                BridgeLogger.Info(string.Format(
                    "HdtBridge v{0} initialized in HDT process (pipe={1}).",
                    BridgeVersion,
                    pipeName));

                serverThread = new Thread(() => RunPipeLoop(pipeName))
                {
                    IsBackground = true,
                    Name = "HdtBridgePipeServer"
                };
                serverThread.Start();
                return 0;
            }
            catch (Exception ex)
            {
                BridgeLogger.Error("Initialize", ex);
                Interlocked.Exchange(ref initialized, 0);
                return 1;
            }
        }

        private static void RunPipeLoop(string pipeName)
        {
            RunPipeLoop(pipeName, CancellationToken.None);
        }

        internal static void RunPipeLoop(string pipeName, CancellationToken stopToken)
        {
            var security = CreatePipeSecurity();
            while (!stopToken.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        4096,
                        4096,
                        security);

                    using (stopToken.Register(() => { try { server.Dispose(); } catch { } }))
                    {
                        server.WaitForConnectionAsync(stopToken).GetAwaiter().GetResult();
                    }

                    var accepted = server;
                    server = null;
                    ThreadPool.QueueUserWorkItem(_ => HandleClientAndDispose(accepted));
                }
                catch (OperationCanceledException)
                {
                    try { server?.Dispose(); } catch { }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    if (stopToken.IsCancellationRequested)
                        break;
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    try { server?.Dispose(); } catch { }
                    if (stopToken.IsCancellationRequested)
                        break;
                    BridgeLogger.ErrorThrottled("pipe_loop", "RunPipeLoop", ex, 15);
                    Thread.Sleep(100);
                }
            }
        }

        private static void HandleClientAndDispose(NamedPipeServerStream server)
        {
            using (server)
            {
                try
                {
                    var line = ReadLineBounded(server, MaxLineBytes, ClientReadTimeoutMs);
                    if (string.IsNullOrWhiteSpace(line))
                        return;

                    var response = DispatchCommand(line.Trim());
                    WriteLineBounded(server, response, ClientWriteTimeoutMs);
                }
                catch (TimeoutException ex)
                {
                    BridgeLogger.Warn("Pipe client timed out: " + ex.Message);
                }
                catch (EndOfStreamException ex)
                {
                    BridgeLogger.Warn("Pipe client closed prematurely: " + ex.Message);
                }
                catch (InvalidDataException ex)
                {
                    BridgeLogger.Warn("Pipe client rejected: " + ex.Message);
                }
                catch (Exception ex)
                {
                    BridgeLogger.ErrorThrottled("pipe_client", "HandleClient", ex, 15);
                }
            }
        }

        internal static string ReadLineBounded(Stream stream, int maxBytes, int timeoutMs)
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            using (cts.Token.Register(() => { try { stream.Dispose(); } catch { } }))
            {
                var buffer = new byte[512];
                using (var ms = new MemoryStream())
                {
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            throw new TimeoutException("Read exceeded " + timeoutMs + "ms.");
                        }
                        catch (ObjectDisposedException)
                        {
                            if (cts.IsCancellationRequested)
                                throw new TimeoutException("Read exceeded " + timeoutMs + "ms.");
                            throw;
                        }

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
                                throw new InvalidDataException("Pipe message exceeded " + maxBytes + " bytes.");
                            ms.WriteByte(b);
                        }
                    }

                    if (ms.Length == 0)
                        return null;

                    throw new EndOfStreamException("Truncated pipe message without newline terminator.");
                }
            }
        }

        internal static void WriteLineBounded(Stream stream, string line, int timeoutMs)
        {
            var payload = Utf8NoBom.GetBytes((line ?? string.Empty) + "\n");
            using (var cts = new CancellationTokenSource(timeoutMs))
            using (cts.Token.Register(() => { try { stream.Dispose(); } catch { } }))
            {
                try
                {
                    stream.WriteAsync(payload, 0, payload.Length, cts.Token).GetAwaiter().GetResult();
                    stream.FlushAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Write exceeded " + timeoutMs + "ms.");
                }
                catch (ObjectDisposedException)
                {
                    if (cts.IsCancellationRequested)
                        throw new TimeoutException("Write exceeded " + timeoutMs + "ms.");
                    throw;
                }
            }
        }

        internal static string DispatchCommand(string command)
        {
            try
            {
                if (command.StartsWith("CONFIG|", StringComparison.OrdinalIgnoreCase))
                {
                    var cfg = lastOpponentTracker != null
                        ? lastOpponentTracker.GetConfigSnapshot()
                        : new LastOpponentConfig();
                    var parts = command.Split('|');
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var eq = parts[i].IndexOf('=');
                        if (eq <= 0)
                            continue;
                        var key = parts[i].Substring(0, eq).Trim();
                        var val = parts[i].Substring(eq + 1).Trim();
                        switch (key.ToLowerInvariant())
                        {
                            case "enabled":
                            case "lastopponentdamage":
                                cfg.Enabled = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                                break;
                            case "mode":
                                cfg.DisplayMode = string.Equals(val, "Dot", StringComparison.OrdinalIgnoreCase)
                                    ? OpponentDisplayMode.Dot
                                    : OpponentDisplayMode.Damage;
                                break;
                            case "style":
                                if (string.Equals(val, "Outlined", StringComparison.OrdinalIgnoreCase))
                                    cfg.VisualStyle = OpponentVisualStyle.Outlined;
                                else if (string.Equals(val, "Plain", StringComparison.OrdinalIgnoreCase))
                                    cfg.VisualStyle = OpponentVisualStyle.Plain;
                                else
                                    cfg.VisualStyle = OpponentVisualStyle.Badge;
                                break;
                            case "colorbyresult":
                                cfg.ColorByResult = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                                break;
                            case "win":
                                cfg.WinColor = val;
                                break;
                            case "loss":
                                cfg.LossColor = val;
                                break;
                            case "draw":
                                cfg.DrawColor = val;
                                break;
                            case "neutral":
                                cfg.NeutralColor = val;
                                break;
                            case "scale":
                                if (int.TryParse(val, out var sc)) cfg.Scale = sc;
                                break;
                            case "opacity":
                                if (int.TryParse(val, out var op)) cfg.Opacity = op;
                                break;
                            case "offsetx":
                                if (int.TryParse(val, out var ox)) cfg.OffsetX = ox;
                                break;
                            case "offsety":
                                if (int.TryParse(val, out var oy)) cfg.OffsetY = oy;
                                break;
                            case "verbose":
                                var nextVerbose = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                                if (BridgeLogger.VerboseEnabled != nextVerbose)
                                {
                                    BridgeLogger.VerboseEnabled = nextVerbose;
                                    BridgeLogger.ResetDebugKeys();
                                    BridgeLogger.Info("Bridge verbose logging " + (nextVerbose ? "enabled." : "disabled."));
                                }
                                break;
                        }
                    }
                    cfg.Normalize();
                    lastOpponentTracker?.ApplyConfig(cfg);
                    var cfgResponse = string.Format(
                        "OK|enabled={0}|mode={1}|style={2}|colorByResult={3}|verbose={4}",
                        cfg.Enabled ? "1" : "0",
                        cfg.DisplayMode,
                        cfg.VisualStyle,
                        cfg.ColorByResult ? "1" : "0",
                        BridgeLogger.VerboseEnabled ? "1" : "0");
                    BridgeLogger.Debug("Pipe CONFIG applied: " + command + " -> " + cfgResponse);
                    return cfgResponse;
                }

                switch (command.ToUpperInvariant())
                {
                    case "PING":
                        var pingResp = "OK|bridge=" + BridgeVersion + "|pid=" + Process.GetCurrentProcess().Id;
                        BridgeLogger.DebugOnce("pipe_ping", pingResp, "Pipe PING -> " + pingResp);
                        return pingResp;

                    case "CHECK":
                    {
                        var report = preserver != null ? preserver.ProbeCapabilities() : new HdtCapabilityReport { TotalCount = 7, LastAction = "not_initialized" };
                        var checkResp = string.Format(
                            "OK|bridge={0}|hdtVersion={1}|ready={2}|total={3}|missing={4}|inMatch={5}|isBg={6}|pending={7}|last={8}",
                            BridgeVersion,
                            Sanitize(report.HdtVersion),
                            report.ReadyCount,
                            report.TotalCount,
                            Sanitize(string.Join(",", report.MissingItems)),
                            report.InMatch ? "1" : "0",
                            report.IsBattlegrounds ? "1" : "0",
                            report.PendingRestore ? "1" : "0",
                            Sanitize(report.LastAction));
                        BridgeLogger.DebugOnce("pipe_check", checkResp, "Pipe CHECK -> " + checkResp);
                        return checkResp;
                    }

                    case "SAVE":
                    {
                        if (preserver == null)
                            return "ERR|reason=not_initialized";
                        lastOpponentTracker?.OnReconnectTriggered();
                        var save = preserver.SaveBeforeReconnect();
                        var saveResp = string.Format(
                            "OK|saved={0}|reason={1}|handle={2}|boards={3}|deities={4}|tavern={5}|triples={6}|deadFor={7}|bobs={8}|tiers={9}|races={10}",
                            save.Saved ? "1" : "0",
                            Sanitize(save.Reason),
                            Sanitize(save.GameHandle),
                            save.Boards,
                            save.Deities,
                            save.TavernEntries,
                            save.TripleEntries,
                            save.DeadForCount,
                            save.BobsBuddy ? "1" : "0",
                            save.TechLevels,
                            save.Races);
                        BridgeLogger.Info("Pipe SAVE -> " + saveResp);
                        return saveResp;
                    }

                    case "RECONNECTED":
                    {
                        if (preserver == null)
                            return "ERR|reason=not_initialized";
                        var status = preserver.NotifyReconnected();
                        var recResp = "OK|last=" + Sanitize(status);
                        BridgeLogger.Info("Pipe RECONNECTED -> " + recResp);
                        return recResp;
                    }

                    case "CANCEL":
                    {
                        if (preserver == null)
                            return "ERR|reason=not_initialized";
                        preserver.CancelPending();
                        BridgeLogger.Info("Pipe CANCEL -> OK|cancelled=1");
                        return "OK|cancelled=1";
                    }

                    default:
                        BridgeLogger.Warn("Unknown pipe command: " + Sanitize(command));
                        return "ERR|reason=unknown_command";
                }
            }
            catch (Exception ex)
            {
                BridgeLogger.Error("DispatchCommand(" + Sanitize(command) + ")", ex);
                return "ERR|reason=" + Sanitize(ex.GetType().Name + ":" + ex.Message);
            }
        }

        private static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                security.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            return security;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('|', '/');
        }
    }
}
