using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using HS_Reconnector.Core;
using HS_Reconnector.LogHandler;
using HS_Reconnector.Native;

class TestHarness
{
    static int failures;

    sealed class FakeSink : ILogEndpointSink
    {
        public string Address;
        public ushort Port;
        public bool Unsupported;
        public int ClearedCount;
        public Action OnInitialEndpoint;

        public bool SetRemoteEndpoint(string address, ushort port, string source)
        {
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(address, out parsed)
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || port == 0)
            {
                Address = null;
                Port = 0;
                Unsupported = true;
                return false;
            }
            if (source == "initial log scan")
                OnInitialEndpoint?.Invoke();
            Address = parsed.ToString();
            Port = port;
            Unsupported = false;
            return true;
        }

        public void ClearRemoteEndpointForMatchEnd(string reason)
        {
            Address = null;
            Port = 0;
            Unsupported = false;
            ClearedCount++;
        }
    }

    static void Check(bool ok, string msg)
    {
        if (!ok) throw new Exception(msg);
    }

    static void Test(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message);
        }
    }

    [STAThread]
    static int Main()
    {
        AppLogger.DisableFileLogging = true;
        HdtBridge.BridgeLogger.DisableFileLogging = true;
        Test("parses real Hearthstone GotoGameServe line with leading space after =", () =>
        {
            var sink = new FakeSink();
            var handler = new ConnectionLogHandler();
            handler.Handle("I 23:10:14.3149554 [GameNetLogger] Network.GotoGameServe() - address= 198.51.100.42:1119, game=10001", sink);
            Check(sink.Address == "198.51.100.42" && sink.Port == 1119 && !sink.Unsupported, "endpoint not parsed");
        });

        Test("unbracketed IPv6 log entry invalidates cached IPv4", () =>
        {
            CheckUnsupportedEndpoint("2001:db8::1:1119");
        });

        Test("port 0 log entry invalidates cached IPv4", () =>
        {
            CheckUnsupportedEndpoint("203.0.113.20:0");
        });

        Test("bracketed IPv6 endpoint preserves port and invalidates IPv4", () =>
        {
            var handler = new ConnectionLogHandler();
            Check(handler.TryExtractEndpoint("Network.GotoGameServe() address=[2001:db8::1]:3724",
                out var host, out var port) && host == "2001:db8::1" && port == 3724, "bracketed IPv6 parsing failed");
            CheckUnsupportedEndpoint("[2001:db8::1]:3724");
        });

        Test("EndGameScreen clears active endpoint", () =>
        {
            var sink = new FakeSink();
            var handler = new ConnectionLogHandler();
            handler.Handle("Network.GotoGameServe() address=203.0.113.10:1119", sink);
            handler.Handle("I 23:38:28.7577792 [GameNetLogger] Network.DisconnectFromGameServer() - Reason: EndGameScreen ", sink);
            Check(sink.Address == null && sink.ClearedCount == 1, "match end did not clear endpoint");
        });

        Test("initial scan ignores EndGameScreen-ended match", () =>
        {
            var text = "Network.GotoGameServe() address=203.0.113.10:1119\nNetwork.DisconnectFromGameServer() - Reason: EndGameScreen\n";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                var found = LogWatcher.TryFindLatestEndpointWithEvent(stream, stream.Length, new ConnectionLogHandler(),
                    out _, out _, out var ended, out _, out _);
                Check(!found && ended, "ended match endpoint should not be selected");
            }
        });

        Test("initial scan ignores unterminated line and stops at unbracketed IPv6", () =>
        {
            var text = "Network.GotoGameServe() address=203.0.113.10:1119\nNetwork.GotoGameServe() address=2001:db8::1:1119\nNetwork.GotoGameServe() address=203.0.113.20:1";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                var found = LogWatcher.TryFindLatestEndpointWithEvent(stream, stream.Length, new ConnectionLogHandler(),
                    out var host, out var port, out _, out _, out _);
                Check(found && host == "2001:db8::1" && port == 1119, "scan fell back past IPv6");
            }
        });

        Test("stream reader completes partial line and recovers from truncation", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.20:1", new UTF8Encoding(false));
                using (var reader = new LogReader(path))
                {
                    string lastLine = null;
                    reader.OnNewLine += x => lastLine = x;
                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null);
                    Check(lastLine == null, "partial line emitted prematurely");
                    File.AppendAllText(path, "119 padding-before-truncation\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(lastLine != null && lastLine.Contains(":1119"), "completed line missed");

                    // Truncate file to shorter length and write a fresh line
                    File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.30:1119\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(lastLine != null && lastLine.Contains("203.0.113.30:1119"), "truncated file not re-read");
                }
            }
            finally { File.Delete(path); }
        });

        Test("log overwrite to >= old length resets endpoint and reads from start", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.10:1119\n", new UTF8Encoding(false));
                var sink = new FakeSink();
                var handler = new ConnectionLogHandler();
                using (var reader = new LogReader(path))
                {
                    var resetFired = false;
                    reader.OnReset += () =>
                    {
                        resetFired = true;
                        sink.ClearRemoteEndpointForMatchEnd("log reset");
                    };
                    reader.OnNewLine += line => handler.Handle(line, sink);

                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null);
                    Check(sink.Address == "203.0.113.10" && sink.Port == 1119, "initial endpoint not read");

                    // Overwrite with a LARGER file whose new endpoint is near byte 0 (below old offset)
                    var padding = new string('x', 200);
                    File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.99:3724\n" + padding + "\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(resetFired, "OnReset not fired on >= length overwrite");
                    Check(sink.Address == "203.0.113.99" && sink.Port == 3724, "new endpoint at start of overwritten file missed");

                    // Overwrite with a file containing NO match endpoint: old endpoint must remain cleared
                    File.WriteAllText(path, "I 00:00:01.0000000 Fresh session without match\n" + padding + "\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(sink.Address == null && sink.Port == 0, "stale endpoint survived log overwrite without match");
                }
            }
            finally { File.Delete(path); }
        });

        Test("timestamp-only change does not replay a large log", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                const long size = 17 * 1024 * 1024;
                using (var stream = File.OpenWrite(path))
                    stream.SetLength(size);
                using (var reader = new LogReader(path, size))
                {
                    typeof(LogReader).GetField("offset", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(reader, size);
                    var resets = 0;
                    reader.OnReset += () => resets++;
                    var read = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    read.Invoke(reader, null);
                    File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
                    read.Invoke(reader, null);
                    Check(resets == 0, "timestamp change reset the reader");
                }
            }
            finally { File.Delete(path); }
        });

        Test("same-length middle overwrite and file replacement reset and re-read endpoint", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                var head = new string('H', 600) + "\n";
                var tail = new string('T', 600) + "\n";
                var lineA = "Network.GotoGameServe() address=203.0.113.10:1119\n";
                var lineB = "Network.GotoGameServe() address=203.0.113.20:3724\n";
                var lineBlank = new string('X', lineA.Length - 1) + "\n";
                Check(lineA.Length == lineB.Length && lineA.Length == lineBlank.Length, "test lines must match length");

                File.WriteAllText(path, head + lineA + tail, new UTF8Encoding(false));
                var sink = new FakeSink();
                var handler = new ConnectionLogHandler();
                using (var reader = new LogReader(path))
                {
                    var resetCount = 0;
                    reader.OnReset += () =>
                    {
                        resetCount++;
                        sink.ClearRemoteEndpointForMatchEnd("log reset");
                    };
                    reader.OnNewLine += line => handler.Handle(line, sink);

                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null);
                    Check(sink.Address == "203.0.113.10" && sink.Port == 1119, "initial endpoint not read");

                    // 1. Same-length overwrite changing only the middle line (even with preserved head/tail 600B)
                    Thread.Sleep(25);
                    File.WriteAllText(path, head + lineB + tail, new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(resetCount == 1, "same-length middle overwrite did not reset");
                    Check(sink.Address == "203.0.113.20" && sink.Port == 3724, "new middle endpoint not picked up");

                    // 2. Overwrite in place WITH growth while head/tail around old offset look valid, modifying matched line
                    File.WriteAllText(path, head + lineBlank + tail + "extra_growth_bytes\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(resetCount == 2, "in-place middle overwrite with growth did not reset");
                    Check(sink.Address == null && sink.Port == 0, "cleared middle endpoint survived");

                    // 3. Atomic file replacement at same path with identical content & timestamp
                    File.WriteAllText(path, head + lineA + tail, new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    var countBeforeReplace = resetCount;
                    var replacement = path + ".new";
                    File.WriteAllText(replacement, head + lineA + tail, new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(path));
                    File.Replace(replacement, path, null);
                    readMethod.Invoke(reader, null);
                    Check(resetCount == countBeforeReplace + 1, "file replacement with identical bytes did not reset");
                    Check(sink.Address == "203.0.113.10" && sink.Port == 1119, "re-read after file replacement failed");
                }
            }
            finally { File.Delete(path); }
        });

        Test("watcher re-reads a log replaced between initial scan and live reader", () =>
        {
            var path = Path.GetTempFileName();
            var replacement = path + ".new";
            try
            {
                var endpoint = "Network.GotoGameServe() address=203.0.113.10:1119\n";
                var matchEnd = "Network.DisconnectFromGameServer() Reason: EndGameScreen\n";
                var previousLog = endpoint + new string('x', matchEnd.Length - 1) + "\n";
                File.WriteAllText(path, previousLog, new UTF8Encoding(false));
                File.WriteAllText(replacement, endpoint + matchEnd, new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(path));

                var sink = new FakeSink
                {
                    OnInitialEndpoint = () => File.Replace(replacement, path, null)
                };
                using (var watcher = new LogWatcher(sink, path))
                {
                    Check(watcher.StartAsync().GetAwaiter().GetResult(), "watcher failed to start");
                    Check(SpinWait.SpinUntil(() => Volatile.Read(ref sink.ClearedCount) == 2, 3000),
                        "replacement was not reset and read through match end");
                    Check(sink.Address == null, "finished match left a stale endpoint");
                }
            }
            finally
            {
                File.Delete(replacement);
                File.Delete(path);
            }
        });

        Test("watcher rejects missing log file", () =>
        {
            var sink = new FakeSink();
            using (var watcher = new LogWatcher(sink, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log")))
            {
                Check(!watcher.StartAsync().GetAwaiter().GetResult(), "missing file should fail startup");
            }
        });

        Test("settings normalize clamps bounds and rejects F12", () =>
        {
            var s = new AppSettings
            {
                ReconnectCooldownSeconds = 99,
                DisconnectTimeoutSeconds = 1,
                OverlayOpacityPercent = 1,
                OverlayBackgroundOpacityPercent = -5,
                OverlayScalePercent = 999,
                Hotkey = Key.F12,
                HotkeyModifiers = HotkeyModifiers.Control
            };
            s.Normalize();
            Check(s.ReconnectCooldownSeconds == 10 && s.DisconnectTimeoutSeconds == 5, "timers not clamped");
            Check(s.OverlayOpacityPercent == 15 && s.OverlayBackgroundOpacityPercent == 0 && s.OverlayScalePercent == 150, "overlay bounds not clamped");
            Check(s.Hotkey == Key.F10 && s.HotkeyModifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt), "F12 not reset");

            var low = new AppSettings { OverlayScalePercent = 10, NetworkOverlay = new NetworkOverlaySettings { Scale = 10 } };
            low.Normalize();
            Check(low.OverlayScalePercent == 30 && low.NetworkOverlay.Scale == 30, "minimum overlay scale should clamp to 30");

            var midLow = new AppSettings { OverlayScalePercent = 45, NetworkOverlay = new NetworkOverlaySettings { Scale = 45 } };
            midLow.Normalize();
            Check(midLow.OverlayScalePercent == 45 && midLow.NetworkOverlay.Scale == 45, "overlay scale 45% should be preserved");
        });

        Test("SnapOverlays defaults on, detaches network overlay when disabled, and persists across save/load", () =>
        {
            var path = Path.GetTempFileName();
            var originalSnap = AppSettings.Instance.SnapOverlays;
            var originalAttached = AppSettings.Instance.NetworkOverlay.AttachedToReconnect;
            var originalLeft = AppSettings.Instance.NetworkOverlay.Left;
            var originalTop = AppSettings.Instance.NetworkOverlay.Top;
            try
            {
                var defaults = new AppSettings();
                Check(defaults.SnapOverlays, "SnapOverlays should default to true");

                defaults.SnapOverlays = false;
                defaults.NetworkOverlay.AttachedToReconnect = true;
                defaults.NetworkOverlay.Left = double.NaN;
                defaults.NetworkOverlay.Top = double.NaN;
                defaults.Normalize();
                Check(!defaults.NetworkOverlay.AttachedToReconnect, "Normalize must clear AttachedToReconnect when SnapOverlays is false");

                File.WriteAllText(path, "<AppSettings><OverlayScalePercent>110</OverlayScalePercent></AppSettings>");
                AppSettings.Load(path);
                Check(AppSettings.Instance.SnapOverlays, "legacy settings without SnapOverlays should default to true");

                AppSettings.Instance.SnapOverlays = false;
                AppSettings.Instance.NetworkOverlay.Left = 320;
                AppSettings.Instance.NetworkOverlay.Top = 240;
                AppSettings.Instance.NetworkOverlay.AttachedToReconnect = true;
                AppSettings.Save(path);

                AppSettings.Instance.SnapOverlays = true;
                AppSettings.Load(path);
                Check(!AppSettings.Instance.SnapOverlays, "SnapOverlays=false did not persist");
                Check(!AppSettings.Instance.NetworkOverlay.AttachedToReconnect, "AttachedToReconnect stayed true after loading SnapOverlays=false");
                Check(AppSettings.Instance.NetworkOverlay.Left == 320 && AppSettings.Instance.NetworkOverlay.Top == 240,
                    "detached network overlay coordinates were lost");
            }
            finally
            {
                AppSettings.Instance.SnapOverlays = originalSnap;
                AppSettings.Instance.NetworkOverlay.AttachedToReconnect = originalAttached;
                AppSettings.Instance.NetworkOverlay.Left = originalLeft;
                AppSettings.Instance.NetworkOverlay.Top = originalTop;
                File.Delete(path);
            }
        });

        Test("HearthstoneLocator finds newest session log", () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "hs-loc-" + Guid.NewGuid().ToString("N"));
            try
            {
                var d1 = Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_1"));
                File.WriteAllText(Path.Combine(d1.FullName, "Hearthstone.log"), "old");
                Thread.Sleep(30);
                var d2 = Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_2"));
                var expected = Path.Combine(d2.FullName, "Hearthstone.log");
                File.WriteAllText(expected, "new");

                var found = HearthstoneLocator.FindLatestLogFile(tempRoot);
                Check(string.Equals(found, expected, StringComparison.OrdinalIgnoreCase), "did not pick newest log");

                Thread.Sleep(30);
                Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_3_empty"));
                var foundAfterEmpty = HearthstoneLocator.FindLatestLogFile(tempRoot);
                Check(string.Equals(foundAfterEmpty, expected, StringComparison.OrdinalIgnoreCase), "empty newer dir broke log lookup");
                var gameStarted = File.GetLastWriteTimeUtc(expected).AddSeconds(1);
                Check(HearthstoneLocator.FindLatestLogFile(tempRoot, gameStarted) == null, "old session log was accepted");
                File.SetLastWriteTimeUtc(expected, gameStarted);
                Check(HearthstoneLocator.FindLatestLogFile(tempRoot, gameStarted) == expected, "current session log was rejected");

                var unreadable = Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_4_unreadable"));
                var deniedAcl = unreadable.GetAccessControl();
                FileSystemAccessRule denyList;
                using (var identity = WindowsIdentity.GetCurrent())
                    denyList = new FileSystemAccessRule(identity.User, FileSystemRights.ListDirectory, AccessControlType.Deny);
                deniedAcl.AddAccessRule(denyList);
                unreadable.SetAccessControl(deniedAcl);
                try
                {
                    var accessDenied = false;
                    try { unreadable.GetFiles("Hearthstone.log"); }
                    catch (UnauthorizedAccessException) { accessDenied = true; }
                    Check(accessDenied, "test directory not actually blocked");
                    Check(HearthstoneLocator.FindLatestLogFile(tempRoot) == expected, "unreadable session masked valid log");
                }
                finally
                {
                    deniedAcl.RemoveAccessRuleSpecific(denyList);
                    unreadable.SetAccessControl(deniedAcl);
                }
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
        });

        Test("settings round trip preserves malformed XML before replacing it", () =>
        {
            var path = Path.GetTempFileName();
            var originalMini = AppSettings.Instance.UseMiniOverlay;
            const string invalid = "<AppSettings><UseMiniOverlay>";
            try
            {
                File.WriteAllText(path, invalid);
                AppSettings.Load(path);
                AppSettings.Instance.UseMiniOverlay = true;
                AppSettings.Save(path);
                var backups = Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.corrupt");
                Check(backups.Length == 1 && File.ReadAllText(backups[0]) == invalid, "invalid XML was not preserved");
                AppSettings.Instance.UseMiniOverlay = false;
                AppSettings.Load(path);
                Check(AppSettings.Instance.UseMiniOverlay, "saved overlay setting was not restored");
            }
            finally
            {
                AppSettings.Instance.UseMiniOverlay = originalMini;
                foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.corrupt"))
                    File.Delete(backup);
                File.Delete(path);
            }
        });

        Test("settings preserve the original after a temporary sharing violation", () =>
        {
            var path = Path.GetTempFileName();
            var originalScale = AppSettings.Instance.OverlayScalePercent;
            const string original = "<AppSettings><OverlayScalePercent>125</OverlayScalePercent></AppSettings>";
            try
            {
                File.WriteAllText(path, original);
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    AppSettings.Load(path);
                AppSettings.Save(path);
                var backup = Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.unreadable");
                Check(backup.Length == 1 && File.ReadAllText(backup[0]) == original, "unreadable settings were lost");
            }
            finally
            {
                AppSettings.Instance.OverlayScalePercent = originalScale;
                foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.unreadable"))
                    File.Delete(backup);
                File.Delete(path);
            }
        });

        Test("network overlay defaults off and detects the latest known region from the log", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                Check(!new AppSettings().NetworkOverlay.Enabled, "network overlay enabled by default");
                File.WriteAllText(path, "Connecting to server. address=us.actual.battle.net port=1119\n"
                    + "Connecting to server. address=kr.actual.battle.net port=1119\n"
                    + "Connecting to server. address=eu.actual.battle.net.example.org port=1119\n");
                Check(NetworkProbe.ReadRegion(path) == "ASIA", "incorrect region or address suffix not rejected");
                foreach (var sample in new[] { ("EU", "EU"), ("US", "US"), ("KR", "ASIA"),
                    ("TW", "ASIA"), ("ASIA", "ASIA"), ("CN", "CHINA"), ("CHINA", "CHINA") })
                {
                    File.WriteAllText(path, "Region: " + sample.Item1 + "\n");
                    Check(NetworkProbe.ReadRegion(path) == sample.Item2, "unexpected region for " + sample.Item1);
                }
                File.WriteAllText(path, "Network.GotoGameServe() address=192.0.2.1:1119\n");
                Check(NetworkProbe.ReadRegion(path) == null, "guessed a region from an IP");
            }
            finally { File.Delete(path); }
        });

        Test("TCP probe measures a successful loopback connection", () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                Check(NetworkProbe.MeasureAsync(IPAddress.Loopback, port).GetAwaiter().GetResult().HasValue,
                    "loopback connection was not measured");
            }
            finally { listener.Stop(); }
        });

        Test("reconnect selects only an established matching TCP row", () =>
        {
            var address = IPAddress.Parse("192.0.2.1");
            const ushort port = 3724;
            using (var process = Process.GetCurrentProcess())
            {
                var row = new Iphlpapi.MIB_TCPROW_OWNER_PID
                {
                    owningPid = (uint)process.Id,
                    state = (uint)Iphlpapi.MIB_TCP_STATE.MIB_TCP_STATE_ESTAB,
                    remoteAddr = BitConverter.ToUInt32(address.GetAddressBytes(), 0),
                    remotePort = new byte[] { 0x0e, 0x8c, 0, 0 }
                };
                var otherPid = row;
                otherPid.owningPid = 0;
                var otherPort = row;
                otherPort.remotePort = new byte[] { 0x8c, 0x0e, 0, 0 };
                var otherState = row;
                otherState.state = (uint)Iphlpapi.MIB_TCP_STATE.MIB_TCP_STATE_LISTEN;
                var otherAddress = row;
                otherAddress.remoteAddr = BitConverter.ToUInt32(IPAddress.Parse("192.0.2.2").GetAddressBytes(), 0);
                var rows = new List<Iphlpapi.MIB_TCPROW_OWNER_PID> { otherPid, otherPort, otherState, otherAddress, row };
                var select = typeof(Reconnector).GetMethod("FindMatchingTcpConnections", BindingFlags.NonPublic | BindingFlags.Static);
                var args = new object[] { rows, new[] { process }, address, port, null };
                Check((int)select.Invoke(null, args) == 1 && ((Iphlpapi.MIB_TCPROW_OWNER_PID)args[4]).RemotePort == port,
                    "incorrect TCP row selection or remote port byte order");
            }
        });

        Test("reconnect timeout and match end restore the controller state", () =>
        {
            const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var dispatcher = Dispatcher.CurrentDispatcher;
            using (var controller = new ReconnectController(dispatcher))
            {
                var type = typeof(ReconnectController);
                var reconnector = (Reconnector)type.GetField("reconnector", PrivateInstance).GetValue(controller);
                var pending = type.GetField("cooldownPendingAfterReconnect", PrivateInstance);
                reconnector.Status = ConnectionStatus.Disconnected;
                pending.SetValue(controller, true);
                Check(controller.GetSnapshot().VisualState == ReconnectVisualState.Disconnected, "missing disconnected state");
                type.GetMethod("DisconnectTimer_Tick", PrivateInstance).Invoke(controller, new object[] { null, EventArgs.Empty });
                Check(controller.GetSnapshot().VisualState == ReconnectVisualState.Cooldown, "timeout did not enter cooldown");

                reconnector.Status = ConnectionStatus.Disconnected;
                pending.SetValue(controller, true);
                controller.ClearRemoteEndpointForMatchEnd("synthetic match end");
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Check(reconnector.Status == ConnectionStatus.Connected
                    && controller.GetSnapshot().VisualState == ReconnectVisualState.Waiting, "match end did not clear cooldown");
            }
        });

        Test("controller resets cooldown on stop and isolates stale log watcher startup", () =>
        {
            const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var dispatcher = Dispatcher.CurrentDispatcher;
            using (var controller = new ReconnectController(dispatcher))
            {
                var type = typeof(ReconnectController);
                var reconnector = (Reconnector)type.GetField("reconnector", PrivateInstance).GetValue(controller);
                var pending = type.GetField("cooldownPendingAfterReconnect", PrivateInstance);
                var attemptField = type.GetField("reconnectAttemptId", PrivateInstance);
                var remoteAddrField = type.GetField("reconnectRemoteAddr", PrivateInstance);
                var watcherField = type.GetField("logWatcher", PrivateInstance);
                var startingField = type.GetField("logWatcherStartInProgress", PrivateInstance);

                reconnector.Status = ConnectionStatus.Disconnected;
                pending.SetValue(controller, true);
                remoteAddrField.SetValue(controller, "203.0.113.10");
                type.GetMethod("DisconnectTimer_Tick", PrivateInstance).Invoke(controller, new object[] { null, EventArgs.Empty });
                Check(controller.GetSnapshot().VisualState == ReconnectVisualState.Cooldown, "expected cooldown before stop reset");

                var attemptBefore = (long)attemptField.GetValue(controller);
                type.GetMethod("ResetDisconnectAndCooldownState", PrivateInstance).Invoke(controller, null);
                Check(controller.GetSnapshot().VisualState == ReconnectVisualState.Waiting, "cooldown was not cleared on game stop");
                Check((long)attemptField.GetValue(controller) > attemptBefore, "reconnectAttemptId was not incremented");
                Check(remoteAddrField.GetValue(controller) == null, "reconnectRemoteAddr was not cleared");

                var activeWatcher = new LogWatcher(controller, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));
                var staleWatcher = new LogWatcher(controller, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));
                try
                {
                    watcherField.SetValue(controller, activeWatcher);
                    startingField.SetValue(controller, true);
                    var startMethod = type.GetMethod("StartLogWatcherAsync", PrivateInstance);
                    var staleTask = (System.Threading.Tasks.Task)startMethod.Invoke(controller, new object[] { staleWatcher });
                    staleTask.GetAwaiter().GetResult();
                    Check((bool)startingField.GetValue(controller), "stale watcher cleared logWatcherStartInProgress for newer watcher");
                    Check(ReferenceEquals(watcherField.GetValue(controller), activeWatcher), "stale watcher replaced active watcher");
                }
                finally
                {
                    watcherField.SetValue(controller, null);
                    activeWatcher.Dispose();
                    staleWatcher.Dispose();
                }
            }
        });

        Test("NetworkProbe ignores invalid paths and HearthstoneLocator accepts freshly created logs", () =>
        {
            Check(NetworkProbe.ReadRegion("bad|path<>Hearthstone.log") == null, "invalid path should return null");

            var tempRoot = Path.Combine(Path.GetTempPath(), "hs-loc-ctime-" + Guid.NewGuid().ToString("N"));
            try
            {
                var sessionDir = Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_fresh"));
                var logPath = Path.Combine(sessionDir.FullName, "Hearthstone.log");
                File.WriteAllText(logPath, "fresh");
                var now = DateTime.UtcNow;
                File.SetCreationTimeUtc(logPath, now.AddSeconds(2));
                File.SetLastWriteTimeUtc(logPath, now.AddSeconds(-2));
                Check(string.Equals(HearthstoneLocator.FindLatestLogFile(tempRoot, now), logPath, StringComparison.OrdinalIgnoreCase),
                    "freshly created log with lagged LastWriteTimeUtc was rejected");
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
        });

        Test("watcher finds active match endpoint more than 16 MiB before EOF in background", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                var endpointLine = Encoding.UTF8.GetBytes("Network.GotoGameServe() address=203.0.113.55:1119\n");
                var fillerLine = Encoding.UTF8.GetBytes(new string('A', 1023) + "\n");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(endpointLine, 0, endpointLine.Length);
                    for (var i = 0; i < 17 * 1024; i++) // 17 MiB > MaxInitialEndpointScanBytes (16 MiB)
                        fs.Write(fillerLine, 0, fillerLine.Length);
                }

                var sink = new FakeSink();
                using (var watcher = new LogWatcher(sink, path))
                {
                    Check(watcher.StartAsync().GetAwaiter().GetResult(), "watcher failed to start on >16 MiB log");
                    Check(SpinWait.SpinUntil(() => sink.Address == "203.0.113.55" && sink.Port == 1119, 5000),
                        "background historical scan did not recover endpoint beyond 16 MiB");
                }

                // Also verify a line straddling the exact 16 MiB scan boundary is not missed
                var boundarySize = 16 * 1024 * 1024;
                var straddlePrefix = Encoding.UTF8.GetBytes("I 00:00:01.0000000 start\n");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(straddlePrefix, 0, straddlePrefix.Length);
                    fs.Write(endpointLine, 0, endpointLine.Length);
                    // Make total length = straddlePrefix + endpointLine/2 + 16 MiB so lowerBound lands inside endpointLine
                    var targetTail = boundarySize - (endpointLine.Length / 2);
                    var written = 0;
                    while (written < targetTail)
                    {
                        var chunk = Math.Min(fillerLine.Length, targetTail - written);
                        if (chunk < fillerLine.Length)
                        {
                            var lastLine = Encoding.UTF8.GetBytes(new string('B', Math.Max(0, chunk - 1)) + "\n");
                            fs.Write(lastLine, 0, lastLine.Length);
                            written += lastLine.Length;
                        }
                        else
                        {
                            fs.Write(fillerLine, 0, fillerLine.Length);
                            written += fillerLine.Length;
                        }
                    }
                }

                var boundarySink = new FakeSink();
                using (var watcher = new LogWatcher(boundarySink, path))
                {
                    Check(watcher.StartAsync().GetAwaiter().GetResult(), "watcher failed on boundary log");
                    Check(SpinWait.SpinUntil(() => boundarySink.Address == "203.0.113.55" && boundarySink.Port == 1119, 5000),
                        "endpoint straddling the 16 MiB chunk boundary was missed");
                }
            }
            finally { File.Delete(path); }
        });

        Test("non-EndGameScreen disconnect does not evict active endpoint fingerprint on growing overwrite", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                var head = new string('H', 600) + "\n";
                var tail = new string('T', 600) + "\n";
                var lineA = "Network.GotoGameServe() address=203.0.113.10:1119\n";
                var lineB = "Network.GotoGameServe() address=203.0.113.20:3724\n";
                var reconnectDisconnect = "Network.DisconnectFromGameServer() - Reason: Reconnect\n";
                Check(lineA.Length == lineB.Length, "test endpoint lines must have equal length");

                File.WriteAllText(path, head + lineA + reconnectDisconnect + tail, new UTF8Encoding(false));
                var sink = new FakeSink();
                var handler = new ConnectionLogHandler();
                using (var reader = new LogReader(path))
                {
                    var resetCount = 0;
                    reader.OnReset += () =>
                    {
                        resetCount++;
                        sink.ClearRemoteEndpointForMatchEnd("log reset");
                    };
                    reader.LineHandler = line => handler.Handle(line, sink);

                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null);
                    Check(sink.Address == "203.0.113.10" && sink.Port == 1119, "initial endpoint A not read");

                    // Overwrite address A with address B in-place while preserving head, reconnectDisconnect, tail, and growing the file
                    File.WriteAllText(path, head + lineB + reconnectDisconnect + tail + "normal_trailing_line\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(resetCount == 1, "growing overwrite of active endpoint after non-EndGameScreen disconnect did not trigger reset");
                    Check(sink.Address == "203.0.113.20" && sink.Port == 3724, "new endpoint B was not picked up after reset");
                }
            }
            finally { File.Delete(path); }
        });

        Test("NetworkProbe detects region after 256 KiB and scanner continues incrementally across split lines and truncation", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                var filler = new string('x', 1023) + "\n";
                var sb = new StringBuilder();
                for (var i = 0; i < 300; i++) // 300 KiB > 256 KiB
                    sb.Append(filler);
                File.WriteAllText(path, sb.ToString() + "Region: EU\n", new UTF8Encoding(false));
                Check(NetworkProbe.ReadRegion(path) == "EU", "ReadRegion missed Region: EU after 256 KiB");

                // Incremental RegionLogScanner across split line and truncation
                var scanner = new NetworkProbe.RegionLogScanner();
                File.WriteAllText(path, sb.ToString() + "Regi", new UTF8Encoding(false));
                Check(scanner.Scan(path) == null, "incomplete region line matched prematurely");
                File.AppendAllText(path, "on: US\n", new UTF8Encoding(false));
                Check(scanner.Scan(path) == "US", "scanner did not complete split region line");

                // Truncate file with a different region -> scanner must reset and read from start
                File.WriteAllText(path, "Region: ASIA\n", new UTF8Encoding(false));
                Check(scanner.Scan(path) == "ASIA", "scanner did not reset after truncation");
            }
            finally { File.Delete(path); }
        });

        Test("HdtBridgeClient parses CHECK responses for ready, partial, and error states", () =>
        {
            var detected = new HdtDetectionInfo
            {
                IsRunning = true,
                ProcessId = 4242,
                Is64Bit = true,
                RunningExePath = "HearthstoneDeckTracker.exe",
                RunningVersion = "1.60.8"
            };
            var ready = HdtBridgeClient.ParseCheckResponse(
                detected,
                "OK|bridge=" + HdtBridgeClient.ExpectedBridgeVersion + "|hdtVersion=1.60.8|ready=7|total=7|missing=|inMatch=1|isBg=1|pending=0|last=ready");
            Check(ready.State == HdtConnectionState.Ready && !ready.BridgeOutdated, "expected up-to-date Ready state");
            Check(ready.BridgeAttached && ready.ReadyHooks == 7 && ready.TotalHooks == 7, "expected 7/7 hooks");
            Check(ready.IsBattlegrounds && ready.InMatch, "expected BG match flags");

            var partial = HdtBridgeClient.ParseCheckResponse(
                detected,
                "OK|bridge=0.8|hdtVersion=1.60.9|ready=5|total=7|missing=bobsBuddy,leaderboard|inMatch=0|isBg=0|pending=0|last=ready");
            Check(partial.State == HdtConnectionState.Partial && partial.BridgeOutdated, "expected Partial outdated state");
            Check(partial.MissingHooks == "bobsBuddy,leaderboard", "expected missing hook list");

            var err = HdtBridgeClient.ParseCheckResponse(detected, "ERR|reason=boom");
            Check(err.State == HdtConnectionState.Error && err.ErrorDetail == "boom", "expected Error state");
        });

        Test("HdtStatePreserver probes 7/7 hooks and preserves all Battlegrounds state including leaderboard tier/tribe", () =>
        {
            var game = new Hearthstone_Deck_Tracker.Hearthstone.GameV2();
            var overlay = new Hearthstone_Deck_Tracker.Windows.OverlayWindow();
            Hearthstone_Deck_Tracker.Core.Game = game;
            Hearthstone_Deck_Tracker.Core.Overlay = overlay;
            Hearthstone_Deck_Tracker.OpponentDeadForTracker.Reset();
            Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker.ClearAll();

            using (var preserver = new HdtBridge.HdtStatePreserver())
            {
            var report = preserver.ProbeCapabilities();
            Check(report.ReadyCount == 7 && report.TotalCount == 7 && report.MissingItems.Count == 0,
                "expected 7/7 capabilities, missing: " + string.Join(",", report.MissingItems));

            var gameId1 = Guid.NewGuid();
            var start1 = DateTime.UtcNow.AddMinutes(-5);
            game.IsInMenu = false;
            game.IsBattlegroundsMatch = true;
            game.IsBattlegroundsCombatPhase = true;
            game.CurrentMode = "GAMEPLAY";
            game.GameEntity = new object();
            game.TurnNumber = 7;
            game.CurrentGameStats = new Hearthstone_Deck_Tracker.Hearthstone.FakeGameStats { GameId = gameId1, StartTime = start1 };
            game.MetaData.ServerInfo.GameHandle = 987654u;

            var heroBefore = new Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity(101) { CardId = "TB_BaconShop_HERO_01", IsHero = true };
            heroBefore.SetTag(HearthDb.Enums.GameTag.PLAYER_ID, 2);
            heroBefore.SetTag(HearthDb.Enums.GameTag.PLAYER_TECH_LEVEL, 4);
            game.Entities[101] = heroBefore;

            game.SetBoardSnapshot(2, "board-snapshot-p2");
            game.SetDeitySnapshot(2, "deity-snapshot-p2");
            game.SetTavernUp(101, 4, 6);
            game.SetTriples(101, 4, 2);
            Hearthstone_Deck_Tracker.OpponentDeadForTracker.Seed(new[] { 5 }, new[] { 2 });

            var inv1 = Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker.GetInstance(gameId1, 7, true);
            inv1.SetInput("saved-bobs-input");

            var tileBefore = new HearthMirror.Objects.BattlegroundsLeaderboardTile
            {
                HeroCardId = "TB_BaconShop_HERO_01",
                DominantRace = 20,
                DominantRaceCount = 5
            };
            overlay.BattlegroundsLeaderboardTilesVM.SetTeams(new[]
            {
                new HearthMirror.Objects.BattlegroundsLeaderboardTeam { Tiles = new[] { tileBefore } }
            });

            var save = preserver.SaveBeforeReconnect();
            Check(save.Saved, "SaveBeforeReconnect failed: " + save.Reason);
            Check(save.Boards == 1 && save.Deities == 1 && save.TavernEntries == 1 && save.TripleEntries == 1
                && save.DeadForCount == 1 && save.BobsBuddy && save.TechLevels == 1 && save.Races == 1,
                "unexpected saved counts");

            // Simulate HDT GameV2.Reset() + reconnect with a DIFFERENT hero entity id (201) and cleared tier/race/watchers
            game.ResetAll();
            Hearthstone_Deck_Tracker.OpponentDeadForTracker.Reset();
            Hearthstone_Deck_Tracker.Hearthstone.Watchers.BattlegroundsLeaderboardWatcher.Running = false;
            Hearthstone_Deck_Tracker.Hearthstone.Watchers.BattlegroundsLeaderboardTeamsWatcher.Running = false;

            var gameId2 = Guid.NewGuid();
            game.IsInMenu = false;
            game.IsBattlegroundsMatch = true;
            game.CurrentMode = "GAMEPLAY";
            game.GameEntity = new object();
            game.TurnNumber = 7;
            game.CurrentGameStats = new Hearthstone_Deck_Tracker.Hearthstone.FakeGameStats { GameId = gameId2, StartTime = start1.AddMinutes(1) };
            game.MetaData.ServerInfo.GameHandle = 987654u;

            var heroAfter = new Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity(201) { CardId = "TB_BaconShop_HERO_01", IsHero = true };
            heroAfter.SetTag(HearthDb.Enums.GameTag.PLAYER_ID, 2);
            game.Entities[201] = heroAfter;

            var tileAfter = new HearthMirror.Objects.BattlegroundsLeaderboardTile
            {
                HeroCardId = "TB_BaconShop_HERO_01",
                DominantRace = null,
                DominantRaceCount = 0
            };
            overlay.BattlegroundsLeaderboardTilesVM.SetTeams(new[]
            {
                new HearthMirror.Objects.BattlegroundsLeaderboardTeam { Tiles = new[] { tileAfter } }
            });

            preserver.NotifyReconnected();

            Check(Equals(game.GetBoardSnapshot(2), "board-snapshot-p2"), "board snapshot not restored");
            Check(Equals(game.GetDeitySnapshot(2), "deity-snapshot-p2"), "deity snapshot not restored");
            Check(game.GetTavernUp(201, 4) == 6, "tavern-up turn not remapped to new hero entity id 201");
            Check(game.GetTriples(201, 4) == 2, "triples by tier not remapped to new hero entity id 201");
            Check(Hearthstone_Deck_Tracker.OpponentDeadForTracker.DeadCount == 1 && overlay.DeadForUpdates > 0, "OpponentDeadForTracker not restored");

            var inv2 = Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker.GetInstance(gameId2, 7, false);
            Check(inv2 != null && Equals(inv2.GetInput(), "saved-bobs-input"), "BobsBuddy _input not restored");

            Check(heroAfter.GetTag(HearthDb.Enums.GameTag.PLAYER_TECH_LEVEL) == 4, "hero PLAYER_TECH_LEVEL not backfilled");
            Check(tileAfter.DominantRace == 20 && tileAfter.DominantRaceCount == 5, "leaderboard DominantRace not backfilled");
            Check(Hearthstone_Deck_Tracker.Hearthstone.Watchers.BattlegroundsLeaderboardWatcher.Running
                && Hearthstone_Deck_Tracker.Hearthstone.Watchers.BattlegroundsLeaderboardTeamsWatcher.Running,
                "leaderboard watchers were not restarted");
            Check(overlay.BattlegroundsLeaderboardTilesVM.UpdateCount > 0 && overlay.LeaderboardTileInfoUpdates > 0,
                "leaderboard tiles UI was not refreshed");

            var tileEvent = new HearthMirror.Objects.BattlegroundsLeaderboardTile
            {
                HeroCardId = "TB_BaconShop_HERO_01",
                DominantRace = null,
                DominantRaceCount = 0
            };
            Hearthstone_Deck_Tracker.Hearthstone.Watchers.BattlegroundsLeaderboardTeamsWatcher.RaiseChange(
                new[] { new HearthMirror.Objects.BattlegroundsLeaderboardTeam { Tiles = new[] { tileEvent } } });
            Check(tileEvent.DominantRace == 20, "TeamsWatcher Change guard did not backfill null DominantRace");
            }
        });

        Test("AppSettings defaults HideHdtButtonHighlight and OpponentMemory settings, normalizes, and persists", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "hdt_reconnector_opts_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var defaults = new AppSettings();
                Check(!defaults.HideHdtButtonHighlight, "HideHdtButtonHighlight must default to false");
                Check(defaults.OpponentMemory.Enabled
                    && defaults.OpponentMemory.DisplayMode == "Damage"
                    && defaults.OpponentMemory.VisualStyle == "Badge"
                    && defaults.OpponentMemory.ColorByResult
                    && defaults.OpponentMemory.WinColor == "#4CE68A"
                    && defaults.OpponentMemory.Scale == 100
                    && defaults.OpponentMemory.Opacity == 100,
                    "unexpected OpponentMemory defaults");

                AppSettings.Instance.HideHdtButtonHighlight = true;
                AppSettings.Instance.OpponentMemory = new OpponentMemorySettings
                {
                    Enabled = false,
                    DisplayMode = "dot",
                    VisualStyle = "outlined",
                    ColorByResult = false,
                    WinColor = "0f0",
                    LossColor = "not-a-color",
                    DrawColor = "#FFEEAA",
                    NeutralColor = "#112233",
                    Scale = 500,
                    Opacity = 5,
                    OffsetX = 99,
                    OffsetY = -99
                };
                AppSettings.Save(path);

                AppSettings.Instance.HideHdtButtonHighlight = false;
                AppSettings.Instance.OpponentMemory = new OpponentMemorySettings();
                AppSettings.Load(path);

                var om = AppSettings.Instance.OpponentMemory;
                Check(AppSettings.Instance.HideHdtButtonHighlight, "HideHdtButtonHighlight did not round-trip");
                Check(!om.Enabled && om.DisplayMode == "Dot" && om.VisualStyle == "Outlined" && !om.ColorByResult,
                    "OpponentMemory mode/style flags did not normalize and round-trip");
                Check(om.WinColor == "#00FF00"
                    && om.LossColor == OpponentMemorySettings.DefaultLossColor
                    && om.DrawColor == "#FFEEAA"
                    && om.NeutralColor == "#112233",
                    "OpponentMemory hex colors did not normalize properly");
                Check(om.Scale == 180 && om.Opacity == 20 && om.OffsetX == 40 && om.OffsetY == -40,
                    "OpponentMemory numeric bounds were not clamped");

                var cmd = HdtBridgeClient.BuildConfigCommand(om, verboseLogging: true);
                Check(cmd == "CONFIG|enabled=0|mode=Dot|style=Outlined|colorByResult=0|win=#00FF00|loss=#FF646B|draw=#FFEEAA|neutral=#112233|scale=180|opacity=20|offsetX=40|offsetY=-40|verbose=1",
                    "unexpected CONFIG payload: " + cmd);

                var lowScale = new OpponentMemorySettings { Scale = 10 };
                lowScale.Normalize();
                Check(lowScale.Scale == 30, "scale minimum should clamp to 30");
                var midLowScale = new OpponentMemorySettings { Scale = 45 };
                midLowScale.Normalize();
                Check(midLowScale.Scale == 45, "scale below 60 (e.g. 45) should be preserved");
            }
            finally
            {
                AppSettings.Instance.HideHdtButtonHighlight = false;
                AppSettings.Instance.OpponentMemory = new OpponentMemorySettings();
                if (File.Exists(path)) File.Delete(path);
            }
        });

        Test("LastOpponentTracker computes Win/Loss/Draw damage including reconnect, offsets, and CONFIG", () =>
        {
            // 1. Outcome & damage calculations
            var win = HdtBridge.LastOpponentTracker.ComputeOutcomeAndDamage(1, 2, 30, 30, 28, 16, false, null, null, false, true);
            Check(win.Outcome == HdtBridge.CombatOutcome.Win && win.ExactDamage == 12, "expected Win for 12 damage");

            var loss = HdtBridge.LastOpponentTracker.ComputeOutcomeAndDamage(1, 2, 30, 21, 28, 28, false, null, null, false, true);
            Check(loss.Outcome == HdtBridge.CombatOutcome.Loss && loss.ExactDamage == 9, "expected Loss for 9 damage");

            var draw = HdtBridge.LastOpponentTracker.ComputeOutcomeAndDamage(1, 2, 30, 30, 28, 28, false, null, null, false, true);
            Check(draw.Outcome == HdtBridge.CombatOutcome.Draw && draw.ExactDamage == 0, "expected Draw for 0 damage");

            var ghostWin = HdtBridge.LastOpponentTracker.ComputeOutcomeAndDamage(1, 2, 30, 30, 0, 0, true, null, null, true, true);
            Check(ghostWin.Outcome == HdtBridge.CombatOutcome.Win && !ghostWin.ExactDamage.HasValue, "expected ghost Win via BACON_WON_LAST_COMBAT");

            // 2. Opposite-side positioning & offsets relative to HDT tier+tribe icons on 1920x1080
            var screenRatio = (4.0 / 3.0) / (1920.0 / 1080.0);
            var tileH = 1080.0 * 0.69 / 8.0;
            var normalPos = HdtBridge.LastOpponentOverlay.ComputeBadgePosition(1920.0, screenRatio, 1080.0 * 0.15, tileH, tileH, 1, false, 34.0, 28.0);
            var normalTileX = 1920.0 * screenRatio * ((4.0 - 1.0) * 0.0017) + 1920.0 * (1.0 - screenRatio) / 2.0;
            Check(!normalPos.OnRightSide && normalPos.Left + 34.0 < normalTileX, "normal tile badge must sit on LEFT opposite HDT icons");

            var normalOffset = HdtBridge.LastOpponentOverlay.ComputeBadgePosition(1920.0, screenRatio, 1080.0 * 0.15, tileH, tileH, 1, false, 34.0, 28.0, 10, -5);
            Check(Math.Abs((normalPos.Left - normalOffset.Left) - 10.0) < 0.01 && Math.Abs((normalPos.Top - normalOffset.Top) - 5.0) < 0.01,
                "normal tile offsetX/offsetY not applied outward/vertically");

            var poppedPos = HdtBridge.LastOpponentOverlay.ComputeBadgePosition(1920.0, screenRatio, 1080.0 * 0.15, tileH, tileH, 3, true, 34.0, 28.0);
            var poppedTileX = 1920.0 * screenRatio * ((4.0 - 3.0) * 0.0017 + 0.023) + 1920.0 * (1.0 - screenRatio) / 2.0;
            Check(poppedPos.OnRightSide && poppedPos.Left > poppedTileX + tileH, "popped-out tile badge must sit on RIGHT opposite HDT icons");

            var poppedOffset = HdtBridge.LastOpponentOverlay.ComputeBadgePosition(1920.0, screenRatio, 1080.0 * 0.15, tileH, tileH, 3, true, 34.0, 28.0, 10, 6);
            Check(Math.Abs((poppedOffset.Left - poppedPos.Left) - 10.0) < 0.01 && Math.Abs((poppedOffset.Top - poppedPos.Top) - 6.0) < 0.01,
                "popped-out tile offsetX/offsetY not applied outward/vertically");

            // 3. Bridge CONFIG command parses all Opponent Memory + verbose options
            var cfgResp = HdtBridge.BridgeEntry.DispatchCommand(
                "CONFIG|enabled=1|mode=Dot|style=Outlined|colorByResult=0|win=#11AA22|loss=#CC2233|draw=#EECC00|neutral=#AABBCC|scale=130|opacity=80|offsetX=10|offsetY=-5|verbose=1");
            Check(cfgResp == "OK|enabled=1|mode=Dot|style=Outlined|colorByResult=0|verbose=1", "unexpected CONFIG response: " + cfgResp);
            Check(HdtBridge.BridgeLogger.VerboseEnabled, "BridgeLogger.VerboseEnabled was not toggled on");
            HdtBridge.BridgeEntry.DispatchCommand("CONFIG|verbose=0");
            Check(!HdtBridge.BridgeLogger.VerboseEnabled, "BridgeLogger.VerboseEnabled was not toggled off");
        });

        Test("VerboseLogging defaults off, persists, gates Debug logs, and crash report writes full context", () =>
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), "hdt_reconnector_verb_" + Guid.NewGuid().ToString("N") + ".xml");
            var crashDir = Path.Combine(Path.GetTempPath(), "hdt_reconnector_crash_" + Guid.NewGuid().ToString("N"));
            var origVerbose = AppLogger.VerboseEnabled;
            try
            {
                var defaults = new AppSettings();
                Check(!defaults.VerboseLogging, "VerboseLogging must default to false");

                AppSettings.Instance.VerboseLogging = true;
                AppSettings.Save(settingsPath);
                AppSettings.Instance.VerboseLogging = false;
                AppSettings.Load(settingsPath);
                Check(AppSettings.Instance.VerboseLogging, "VerboseLogging=true did not persist");

                AppLogger.Clear();
                AppLogger.VerboseEnabled = false;
                AppLogger.Debug("hidden-debug-line");
                Check(AppLogger.GetSnapshot().Count == 0, "Debug message should be suppressed when VerboseEnabled=false");

                AppLogger.VerboseEnabled = true;
                AppLogger.Debug("visible-debug-line");
                var snap = AppLogger.GetSnapshot();
                Check(snap.Count == 1 && snap[0].Contains("[DEBUG]") && snap[0].Contains("visible-debug-line"),
                    "Debug message was not logged when VerboseEnabled=true");

                var crashPath = AppLogger.WriteCrashReport(
                    "TestCrashSource",
                    new InvalidOperationException("synthetic crash payload"),
                    isTerminating: true,
                    directoryOverride: crashDir);
                Check(!string.IsNullOrEmpty(crashPath) && File.Exists(crashPath), "crash report file was not created");
                var content = File.ReadAllText(crashPath);
                Check(content.Contains("TestCrashSource")
                    && content.Contains("synthetic crash payload")
                    && content.Contains("visible-debug-line"),
                    "crash report missing expected details or recent log context");
            }
            finally
            {
                AppSettings.Instance.VerboseLogging = false;
                AppLogger.VerboseEnabled = origVerbose;
                if (File.Exists(settingsPath)) File.Delete(settingsPath);
                if (Directory.Exists(crashDir)) Directory.Delete(crashDir, true);
            }
        });

        Test("BridgeEntry.BridgeVersion matches HdtBridgeClient.ExpectedBridgeVersion", () =>
        {
            Check(HdtBridge.BridgeEntry.BridgeVersion == HdtBridgeClient.ExpectedBridgeVersion,
                "BridgeVersion (" + HdtBridge.BridgeEntry.BridgeVersion + ") != ExpectedBridgeVersion (" + HdtBridgeClient.ExpectedBridgeVersion + ")");
        });

        Test("Localization EN and RU dictionaries have exact key and placeholder parity", () =>
        {
            var locType = typeof(Localization);
            var en = (Dictionary<string, string>)locType.GetField("En", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var ru = (Dictionary<string, string>)locType.GetField("Ru", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Check(en.Count > 0 && en.Count == ru.Count, "EN/RU key count mismatch: " + en.Count + " vs " + ru.Count);

            var placeholderRx = new System.Text.RegularExpressions.Regex(@"\{\d+\}");
            foreach (var kv in en)
            {
                Check(ru.TryGetValue(kv.Key, out var ruVal), "missing RU localization key: " + kv.Key);
                var enPlaceholders = new HashSet<string>();
                foreach (System.Text.RegularExpressions.Match m in placeholderRx.Matches(kv.Value))
                    enPlaceholders.Add(m.Value);
                var ruPlaceholders = new HashSet<string>();
                foreach (System.Text.RegularExpressions.Match m in placeholderRx.Matches(ruVal))
                    ruPlaceholders.Add(m.Value);
                Check(enPlaceholders.SetEquals(ruPlaceholders), "format placeholder mismatch for key: " + kv.Key);
            }
        });

        Test("HdtReflection resolves overloads by argument type and accesses inherited fields", () =>
        {
            var target = new OverloadProbeDerived();
            var byEnum = HdtBridge.HdtReflection.InvokeMethod(target, "ReadTag", HearthDb.Enums.GameTag.PLAYER_TECH_LEVEL);
            var byInt = HdtBridge.HdtReflection.InvokeMethod(target, "ReadTag", 42);
            Check(Equals(byEnum, "enum:1377"), "expected GameTag overload, got: " + byEnum);
            Check(Equals(byInt, "int:42"), "expected int overload, got: " + byInt);
            Check(HdtBridge.HdtReflection.HasMember(typeof(OverloadProbeDerived), "_baseSecret", false), "base private field not detected by HasMember");
            Check(Equals(HdtBridge.HdtReflection.GetMemberValue(target, "_baseSecret"), 777), "base private field value mismatch");
        });

        Test("Named Pipe exchange works end-to-end and times out cleanly when server stalls", () =>
        {
            var pipeName = "HDT-HS-Reconnector-Test-" + Guid.NewGuid().ToString("N");
            using (var cts = new CancellationTokenSource())
            {
                var serverThread = new Thread(() => HdtBridge.BridgeEntry.RunPipeLoop(pipeName, cts.Token)) { IsBackground = true };
                serverThread.Start();
                Thread.Sleep(80);
                try
                {
                    Check(HdtBridgeClient.TrySendCommandToPipe(pipeName, "PING", 1500, out var pingResp)
                        && pingResp.StartsWith("OK|bridge=" + HdtBridge.BridgeEntry.BridgeVersion),
                        "PING over named pipe failed: " + pingResp);
                    Check(HdtBridgeClient.TrySendCommandToPipe(pipeName, "CONFIG|enabled=1|mode=Damage|style=Badge|verbose=0", 1500, out var cfgResp)
                        && cfgResp.StartsWith("OK|enabled=1|mode=Damage|style=Badge"),
                        "CONFIG over named pipe failed: " + cfgResp);
                }
                finally
                {
                    cts.Cancel();
                    serverThread.Join(1000);
                }
            }

            // Stalled pipe server must NOT hang client indefinitely
            var stalledPipe = "HDT-HS-Reconnector-Stall-" + Guid.NewGuid().ToString("N");
            var acceptedEvent = new ManualResetEventSlim(false);
            var releaseServer = new ManualResetEventSlim(false);
            var stallThread = new Thread(() =>
            {
                try
                {
                    using (var srv = new System.IO.Pipes.NamedPipeServerStream(stalledPipe, System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
                    {
                        srv.WaitForConnection();
                        acceptedEvent.Set();
                        releaseServer.Wait(3000);
                    }
                }
                catch { }
            }) { IsBackground = true };
            stallThread.Start();
            Thread.Sleep(50);
            try
            {
                var sw = Stopwatch.StartNew();
                var ok = HdtBridgeClient.TrySendCommandToPipe(stalledPipe, "PING", 250, out _);
                sw.Stop();
                Check(!ok && sw.ElapsedMilliseconds < 2000, "stalled pipe did not time out promptly (" + sw.ElapsedMilliseconds + "ms)");
            }
            finally
            {
                releaseServer.Set();
                stallThread.Join(1000);
                acceptedEvent.Dispose();
                releaseServer.Dispose();
            }
        });

        Test("LastOpponentTracker state machine handles normal win, ghost win/draw, armor gain, reconnect, and match end", () =>
        {
            var game = new Hearthstone_Deck_Tracker.Hearthstone.GameV2();
            var overlay = new Hearthstone_Deck_Tracker.Windows.OverlayWindow();
            game.IsInMenu = false;
            game.IsBattlegroundsMatch = true;
            game.IsBattlegroundsSoloMatch = true;
            game.CurrentGameType = "GT_BATTLEGROUNDS";
            game.MetaData.ServerInfo.GameHandle = 555123u;
            game.CurrentGameStats = new Hearthstone_Deck_Tracker.Hearthstone.FakeGameStats
            {
                GameId = Guid.NewGuid(),
                StartTime = DateTime.UtcNow.AddMinutes(-2),
                Result = "None"
            };

            var playerEntity = new Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity(1);
            playerEntity.SetTag(HearthDb.Enums.GameTag.PLAYER_ID, 1);
            game.PlayerEntity = playerEntity;

            // Populate 8 solo heroes
            for (var p = 1; p <= 8; p++)
            {
                var h = new Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity(100 + p)
                {
                    CardId = "HERO_" + p,
                    IsHero = true,
                    Health = p == 3 ? 0 : 30 // player 3 is Ghost
                };
                h.SetTag(HearthDb.Enums.GameTag.PLAYER_ID, p);
                h.SetTag(HearthDb.Enums.GameTag.PLAYER_LEADERBOARD_PLACE, p);
                h.SetTag(HearthDb.Enums.GameTag.ARMOR, p == 3 ? 0 : 5);
                game.Entities[100 + p] = h;
            }

            using (var tracker = new HdtBridge.LastOpponentTracker())
            {
                var t = DateTime.UtcNow;

                // Round 1 recruit -> schedule opp 2 -> combat -> opp 2 loses 12 durability -> round 2 recruit
                game.TurnNumber = 1;
                game.IsBattlegroundsCombatPhase = false;
                playerEntity.SetTag(HearthDb.Enums.GameTag.NEXT_OPPONENT_PLAYER_ID, 2);
                tracker.ProcessTick(game, overlay, t);

                game.IsBattlegroundsCombatPhase = true;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));

                game.Entities[102].Health = 23; // 35 -> 23 = 12 damage (armor still 5 -> 28, wait: set armor 0 so 35->23)
                game.Entities[102].SetTag(HearthDb.Enums.GameTag.ARMOR, 0);
                game.TurnNumber = 2;
                game.IsBattlegroundsCombatPhase = false;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                Check(tracker.LastOpponentPlayerId == 2 && tracker.LastOutcome == HdtBridge.CombatOutcome.Win && tracker.LastDamage == 12,
                    "round 1 expected Win 12 vs opp 2, got opp=" + tracker.LastOpponentPlayerId + " out=" + tracker.LastOutcome + " dmg=" + tracker.LastDamage);

                // Round 2 vs Ghost (player 3, Health=0) WITHOUT reconnect, local gains armor (35 -> 40), BACON_WON_LAST_COMBAT=1 -> Win
                playerEntity.SetTag(HearthDb.Enums.GameTag.NEXT_OPPONENT_PLAYER_ID, 3);
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                game.IsBattlegroundsCombatPhase = true;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));

                game.Entities[101].SetTag(HearthDb.Enums.GameTag.ARMOR, 10); // armor grew 5 -> 10
                playerEntity.SetTag(HearthDb.Enums.GameTag.BACON_WON_LAST_COMBAT, 1);
                game.TurnNumber = 3;
                game.IsBattlegroundsCombatPhase = false;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                Check(tracker.LastOpponentPlayerId == 3 && tracker.LastOutcome == HdtBridge.CombatOutcome.Win,
                    "round 2 ghost win with armor gain failed: out=" + tracker.LastOutcome);

                // Round 3 vs Ghost (player 3) Draw (local loss=0, BACON_WON_LAST_COMBAT=0) -> Draw 0
                playerEntity.SetTag(HearthDb.Enums.GameTag.BACON_WON_LAST_COMBAT, 0);
                playerEntity.SetTag(HearthDb.Enums.GameTag.NEXT_OPPONENT_PLAYER_ID, 3);
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                game.IsBattlegroundsCombatPhase = true;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));

                game.TurnNumber = 4;
                game.IsBattlegroundsCombatPhase = false;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                Check(tracker.LastOpponentPlayerId == 3 && tracker.LastOutcome == HdtBridge.CombatOutcome.Draw && tracker.LastDamage == 0,
                    "round 3 ghost draw failed: out=" + tracker.LastOutcome + " dmg=" + tracker.LastDamage);

                // Round 4 vs player 4 with reconnect during combat -> local takes 9 damage -> Loss 9
                playerEntity.SetTag(HearthDb.Enums.GameTag.NEXT_OPPONENT_PLAYER_ID, 4);
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                game.IsBattlegroundsCombatPhase = true;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                tracker.OnReconnectTriggered();

                game.Entities[101].SetTag(HearthDb.Enums.GameTag.ARMOR, 1); // 40 -> 31 = 9 loss
                game.TurnNumber = 5;
                game.IsBattlegroundsCombatPhase = false;
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                Check(tracker.LastOpponentPlayerId == 4 && tracker.LastOutcome == HdtBridge.CombatOutcome.Loss && tracker.LastDamage == 9,
                    "round 4 reconnect loss failed: opp=" + tracker.LastOpponentPlayerId + " out=" + tracker.LastOutcome + " dmg=" + tracker.LastDamage);

                // Match end in menu resets state
                game.IsInMenu = true;
                game.CurrentGameStats.Result = "Win";
                tracker.ProcessTick(game, overlay, t = t.AddMilliseconds(250));
                Check(!tracker.LastOpponentPlayerId.HasValue, "match end did not reset LastOpponentPlayerId");
            }
        });

        Test("Crash report throttles identical UI errors, prunes to 15 files, and AppSettings handles relative paths", () =>
        {
            var crashDir = Path.Combine(Path.GetTempPath(), "hdt_crash_prune_" + Guid.NewGuid().ToString("N"));
            var origDir = Environment.CurrentDirectory;
            try
            {
                var first = AppLogger.WriteCrashReport("ThrottleTest", new InvalidOperationException("dup-err-1"), isTerminating: false, directoryOverride: crashDir);
                var second = AppLogger.WriteCrashReport("ThrottleTest", new InvalidOperationException("dup-err-2-dynamic"), isTerminating: false, directoryOverride: crashDir);
                var alt = AppLogger.WriteCrashReport("ThrottleTest", new ArgumentException("alt-err-1"), isTerminating: false, directoryOverride: crashDir);
                var firstAgain = AppLogger.WriteCrashReport("ThrottleTest", new InvalidOperationException("dup-err-3"), isTerminating: false, directoryOverride: crashDir);
                Check(!string.IsNullOrEmpty(first) && second == null && !string.IsNullOrEmpty(alt) && firstAgain == null,
                    "repeated/alternating non-terminating crash reports were not throttled by exception type");

                for (var i = 0; i < 18; i++)
                {
                    Thread.Sleep(5);
                    var p = AppLogger.WriteCrashReport("PruneTest", new Exception("err-" + i), isTerminating: true, directoryOverride: crashDir);
                    Check(!string.IsNullOrEmpty(p), "terminating crash report should not be throttled");
                }
                var remaining = Directory.GetFiles(crashDir, "crash-*.log");
                Check(remaining.Length <= 15, "crash report pruning kept too many files: " + remaining.Length);

                // Relative settings path without directory
                Environment.CurrentDirectory = crashDir;
                AppSettings.Save("relative_settings.xml");
                Check(File.Exists(Path.Combine(crashDir, "relative_settings.xml")), "relative settings path failed to save");
            }
            finally
            {
                Environment.CurrentDirectory = origDir;
                if (Directory.Exists(crashDir)) Directory.Delete(crashDir, true);
            }
        });

        Test("ReadLineBounded rejects truncated messages without newline on server and client", () =>
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("CONFIG|enabled=0")))
            {
                var threw = false;
                try { HdtBridge.BridgeEntry.ReadLineBounded(ms, 8192, 1000); }
                catch (EndOfStreamException) { threw = true; }
                Check(threw, "BridgeEntry.ReadLineBounded should throw EndOfStreamException on truncated message without newline");
            }

            var truncPipe = "HDT-HS-Reconnector-Trunc-" + Guid.NewGuid().ToString("N");
            var srvThread = new Thread(() =>
            {
                try
                {
                    using (var srv = new System.IO.Pipes.NamedPipeServerStream(truncPipe, System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
                    {
                        srv.WaitForConnection();
                        HdtBridge.BridgeEntry.ReadLineBounded(srv, 8192, 1500);
                        var partialBytes = Encoding.UTF8.GetBytes("OK|bridge=0.7"); // intentionally missing '\n'
                        srv.Write(partialBytes, 0, partialBytes.Length);
                        srv.Flush();
                    }
                }
                catch { }
            }) { IsBackground = true };
            srvThread.Start();
            Thread.Sleep(50);
            try
            {
                var ok = HdtBridgeClient.TrySendCommandToPipe(truncPipe, "PING", 1500, out var resp);
                Check(!ok && resp == null, "client must reject truncated pipe response without newline");
            }
            finally
            {
                srvThread.Join(1000);
            }
        });

        Test("BridgeEntry.DispatchCommand handles uninitialized preserver cleanly for SAVE/RECONNECTED/CANCEL", () =>
        {
            Check(HdtBridge.BridgeEntry.DispatchCommand("SAVE") == "ERR|reason=not_initialized", "SAVE should return not_initialized");
            Check(HdtBridge.BridgeEntry.DispatchCommand("RECONNECTED") == "ERR|reason=not_initialized", "RECONNECTED should return not_initialized");
            Check(HdtBridge.BridgeEntry.DispatchCommand("CANCEL") == "ERR|reason=not_initialized", "CANCEL should return not_initialized");
        });

        Test("ReconnectController.ExecuteReconnect prevents double trigger and aborts timers on stale endpoint", () =>
        {
            const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var dispatcher = Dispatcher.CurrentDispatcher;
            using (var controller = new ReconnectController(dispatcher))
            {
                var type = typeof(ReconnectController);
                type.GetField("isElevated", PrivateInstance).SetValue(controller, true);
                var reconnector = (Reconnector)type.GetField("reconnector", PrivateInstance).GetValue(controller);
                var disconnectTimer = (DispatcherTimer)type.GetField("disconnectTimer", PrivateInstance).GetValue(controller);
                var restoreTimer = (DispatcherTimer)type.GetField("connectionRestoreTimer", PrivateInstance).GetValue(controller);

                controller.SetRemoteEndpoint("203.0.113.10", 1119, "test");
                var entered = new ManualResetEventSlim(false);
                var release = new ManualResetEventSlim(false);
                var callCount = 0;
                controller.ReconnectExecutorForTest = (addr, port) =>
                {
                    Interlocked.Increment(ref callCount);
                    entered.Set();
                    release.Wait(3000);
                    reconnector.Status = ConnectionStatus.Disconnected;
                    return Tuple.Create(new HdtSaveSummary { Saved = false }, true);
                };

                try
                {
                    controller.ExecuteReconnect();
                    Check(entered.Wait(2000), "first ExecuteReconnect did not start");
                    // Second concurrent trigger must be ignored
                    controller.ExecuteReconnect();
                    Check(Volatile.Read(ref callCount) == 1, "concurrent ExecuteReconnect was not blocked");

                    // Match ends while SAVE/Disconnect is still running -> endpointVersion increments
                    controller.ClearRemoteEndpointForMatchEnd("match ended mid-reconnect");
                    release.Set();

                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline
                        && (int)type.GetField("reconnectInProgress", PrivateInstance).GetValue(controller) != 0)
                    {
                        var frame = new DispatcherFrame();
                        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                        Thread.Sleep(10);
                    }

                    Check(reconnector.Status == ConnectionStatus.Connected, "stale reconnect left status Disconnected");
                    Check(!disconnectTimer.IsEnabled && !restoreTimer.IsEnabled, "stale reconnect started timers");
                    Check(controller.GetSnapshot().VisualState == ReconnectVisualState.Waiting, "stale reconnect did not settle to Waiting");
                }
                finally
                {
                    release.Set();
                    entered.Dispose();
                    release.Dispose();
                }
            }
        });

        Test("LogReader rolls back pendingLineBytes if LineHandler throws and recovers on retry", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.77:1119\n", new UTF8Encoding(false));
                using (var reader = new LogReader(path))
                {
                    var attempts = 0;
                    string received = null;
                    reader.LineHandler = line =>
                    {
                        attempts++;
                        if (attempts == 1)
                            throw new InvalidOperationException("transient handler error");
                        received = line;
                        return true;
                    };
                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    var threw = false;
                    try { readMethod.Invoke(reader, null); }
                    catch (TargetInvocationException) { threw = true; }
                    Check(threw, "first read should have thrown");

                    readMethod.Invoke(reader, null);
                    Check(received == "Network.GotoGameServe() address=203.0.113.77:1119",
                        "pendingLineBytes corrupted after handler exception: " + (received ?? "<null>"));
                }
            }
            finally { File.Delete(path); }
        });

        Test("LogReader restores carried partial prefix across chunks when second line throws", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                // Chunk 1 leaves a partial prefix in pendingLineBytes
                File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.", new UTF8Encoding(false));
                using (var reader = new LogReader(path))
                {
                    var lines = new List<string>();
                    var failSecond = true;
                    reader.LineHandler = line =>
                    {
                        if (failSecond && line.Contains("203.0.113.88"))
                        {
                            failSecond = false;
                            throw new InvalidOperationException("fail on second line");
                        }
                        lines.Add(line);
                        return true;
                    };
                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null); // buffers partial prefix

                    // Chunk 2 completes line 1 AND appends line 2 (which throws on first pass)
                    File.AppendAllText(path, "11:1119\nNetwork.GotoGameServe() address=203.0.113.88:3724\n", new UTF8Encoding(false));
                    var threw = false;
                    try { readMethod.Invoke(reader, null); }
                    catch (TargetInvocationException) { threw = true; }
                    Check(threw, "expected exception on line 2");

                    lines.Clear();
                    readMethod.Invoke(reader, null);
                    Check(lines.Count == 2
                        && lines[0] == "Network.GotoGameServe() address=203.0.113.11:1119"
                        && lines[1] == "Network.GotoGameServe() address=203.0.113.88:3724",
                        "partial prefix was corrupted after rollback: " + string.Join(" | ", lines));
                }
            }
            finally { File.Delete(path); }
        });

        Test("LastOpponentTracker serializes concurrent TrySubscribeGameEvents without duplicate subscriptions", () =>
        {
            Hearthstone_Deck_Tracker.API.GameEvents.Reset();
            using (var tracker = new HdtBridge.LastOpponentTracker())
            {
                var startGate = new ManualResetEventSlim(false);
                var threads = new Thread[8];
                for (var i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        startGate.Wait(2000);
                        tracker.TrySubscribeGameEvents();
                    }) { IsBackground = true };
                    threads[i].Start();
                }
                startGate.Set();
                foreach (var t in threads)
                    t.Join(2000);
                startGate.Dispose();

                Check(Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Count == 1
                    && Hearthstone_Deck_Tracker.API.GameEvents.OnTurnStart.Count == 1
                    && Hearthstone_Deck_Tracker.API.GameEvents.OnEntityWillTakeDamage.Count == 1,
                    string.Format("duplicate GameEvents subscriptions detected: {0}/{1}/{2}",
                        Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Count,
                        Hearthstone_Deck_Tracker.API.GameEvents.OnTurnStart.Count,
                        Hearthstone_Deck_Tracker.API.GameEvents.OnEntityWillTakeDamage.Count));
            }
        });

        Test("VersionChecker validates repositories, normalizes tags, and compares semantic versions", () =>
        {
            Check(VersionChecker.ValidateRepository("  testd1111/HDT-HS-Reconnector ") == "testd1111/HDT-HS-Reconnector",
                "valid repository was rejected");
            Check(VersionChecker.BuildReleasePageUrl(VersionChecker.DefaultRepository) == "https://github.com/testd1111/HDT-HS-Reconnector/releases/latest",
                "unexpected release URL");

            foreach (var badRepo in new[] { "", "owner", "owner/repo/extra", "owner/..repo", "https://github.com/a/b", "-owner/repo", "owner/repo?x=1" })
            {
                var threw = false;
                try { VersionChecker.ValidateRepository(badRepo); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "invalid repository should be rejected: " + badRepo);
            }

            Check(VersionChecker.NormalizeTag("v0.9.0") == "0.9.0", "leading v was not stripped");
            Check(VersionChecker.NormalizeTag("V1.2.3-beta.1") == "1.2.3-beta.1", "prerelease tag was not normalized");

            var installed = new Version(0, 9, 0, 0);
            Check(VersionChecker.CompareTagToInstalledVersion("v0.9.1", installed) > 0, "0.9.1 should be newer than 0.9.0");
            Check(VersionChecker.CompareTagToInstalledVersion("0.9.0", installed) == 0, "0.9.0 should equal 0.9.0");
            Check(VersionChecker.CompareTagToInstalledVersion("v0.9.0.0", installed) == 0, "0.9.0.0 should equal 0.9.0");
            Check(VersionChecker.CompareTagToInstalledVersion("v0.9.0-rc.1", installed) < 0, "prerelease should be older than stable");
            Check(VersionChecker.CompareTagToInstalledVersion("v0.8.9", installed) < 0, "0.8.9 should be older than 0.9.0");
            Check(VersionChecker.CompareTags("1.0.0-alpha.2", "1.0.0-alpha.10") < 0, "numeric prerelease identifiers should compare numerically");
            Check(VersionChecker.CompareTags("1.0.0-alpha.10", "1.0.0") < 0, "prerelease should be older than release");

            var invalidTagThrew = false;
            try { VersionChecker.NormalizeTag("release-latest"); }
            catch (FormatException) { invalidTagThrew = true; }
            Check(invalidTagThrew, "invalid tag should throw FormatException");
        });

        Test("TryGetHdtCoreObjects avoids lazy Core.Game/Core.Overlay getters while HDT initializes", () =>
        {
            LazyCoreProbe.Reset();
            Check(!HdtBridge.HdtReflection.TryGetHdtCoreObjectsFromType(typeof(LazyCoreProbe), out var g, out var o, out var init)
                && g == null && o == null && init
                && LazyCoreProbe.GameGetterCalls == 0 && LazyCoreProbe.OverlayGetterCalls == 0,
                "uninitialized Core should return initializing without invoking lazy getters");

            LazyCoreProbe.SetInitialized(true);
            Check(!HdtBridge.HdtReflection.TryGetHdtCoreObjectsFromType(typeof(LazyCoreProbe), out g, out o, out init)
                && g == null && o == null && init
                && LazyCoreProbe.GameGetterCalls == 0 && LazyCoreProbe.OverlayGetterCalls == 0,
                "null backing fields should return initializing without invoking lazy getters");

            var fakeGame = new object();
            var fakeOverlay = new object();
            LazyCoreProbe.SetBackingFields(fakeGame, fakeOverlay);
            Check(HdtBridge.HdtReflection.TryGetHdtCoreObjectsFromType(typeof(LazyCoreProbe), out g, out o, out init)
                && ReferenceEquals(g, fakeGame) && ReferenceEquals(o, fakeOverlay) && !init
                && LazyCoreProbe.GameGetterCalls == 0 && LazyCoreProbe.OverlayGetterCalls == 0,
                "populated backing fields should be read directly without invoking lazy getters");

            var parsedInit = HdtBridgeClient.ParseCheckResponse(
                new HdtDetectionInfo { IsRunning = true, ProcessId = 42, InstalledVersion = "1.60.8" },
                "OK|bridge=" + HdtBridgeClient.ExpectedBridgeVersion + "|hdtVersion=1.60.8|ready=0|total=7|missing=initializing|inMatch=0|isBg=0|pending=0|last=hdt_initializing");
            Check(parsedInit.State == HdtConnectionState.Checking && parsedInit.BridgeAttached && !parsedInit.BridgeOutdated,
                "initializing CHECK response should map to Checking state");
        });

        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }

    static void CheckUnsupportedEndpoint(string endpoint)
    {
        using (var controller = new ReconnectController(Dispatcher.CurrentDispatcher))
        {
            var handler = new ConnectionLogHandler();
            handler.Handle("Network.GotoGameServe() address=203.0.113.10:1119", controller);
            Check(controller.GetSnapshot().RemoteAddress == "203.0.113.10", "initial IPv4 not set");
            handler.Handle("Network.GotoGameServe() address=" + endpoint, controller);
            var snapshot = controller.GetSnapshot();
            Check(snapshot.RemoteAddress == null && snapshot.RemotePort == 0 && snapshot.EndpointUnsupported,
                "invalid endpoint did not clear the cached IPv4");
        }
    }
}

namespace HearthDb.Enums
{
    public enum GameTag
    {
        PLAYER_ID = 30,
        CARDTYPE = 202,
        ARMOR = 292,
        PLAYER_LEADERBOARD_PLACE = 1373,
        PLAYER_TECH_LEVEL = 1377,
        NEXT_OPPONENT_PLAYER_ID = 1988,
        BACON_WON_LAST_COMBAT = 2594
    }
}

class OverloadProbeBase
{
    private int _baseSecret = 777;
    public int TouchBase() => _baseSecret;
}

class OverloadProbeDerived : OverloadProbeBase
{
    public string ReadTag(int x) => "int:" + x;
    public string ReadTag(HearthDb.Enums.GameTag tag) => "enum:" + (int)tag;
}

class LazyCoreProbe
{
    private static object _game;
    private static object _overlay;
    public static bool Initialized { get; private set; }
    public static int GameGetterCalls;
    public static int OverlayGetterCalls;

    public static object Game
    {
        get
        {
            GameGetterCalls++;
            throw new InvalidOperationException("Lazy Game getter must not be invoked from background thread");
        }
    }

    public static object Overlay
    {
        get
        {
            OverlayGetterCalls++;
            throw new InvalidOperationException("Lazy Overlay getter must not be invoked from background thread");
        }
    }

    public static void Reset()
    {
        _game = null;
        _overlay = null;
        Initialized = false;
        GameGetterCalls = 0;
        OverlayGetterCalls = 0;
    }

    public static void SetInitialized(bool value) => Initialized = value;

    public static void SetBackingFields(object game, object overlay)
    {
        _game = game;
        _overlay = overlay;
    }
}

namespace HearthMirror.Objects
{
    public class BattlegroundsLeaderboardTile
    {
        public string HeroCardId { get; set; }
        public int? DominantRace { get; set; }
        public int DominantRaceCount { get; set; }
    }

    public class BattlegroundsLeaderboardTeam
    {
        public IReadOnlyList<BattlegroundsLeaderboardTile> Tiles { get; set; } = new List<BattlegroundsLeaderboardTile>();
    }
}

namespace Hearthstone_Deck_Tracker.API
{
    public class ActionList<T>
    {
        private readonly List<Action<T>> items = new List<Action<T>>();
        public int Count => items.Count;
        public void Add(Action<T> action) => items.Add(action);
        public void Clear() => items.Clear();
    }

    public class ActionList
    {
        private readonly List<Action> items = new List<Action>();
        public int Count => items.Count;
        public void Add(Action action) => items.Add(action);
        public void Clear() => items.Clear();
    }

    public static class GameEvents
    {
        public static ActionList OnGameStart = new ActionList();
        public static ActionList<string> OnTurnStart = new ActionList<string>();
        public static ActionList<object> OnEntityWillTakeDamage = new ActionList<object>();

        public static void Reset()
        {
            OnGameStart.Clear();
            OnTurnStart.Clear();
            OnEntityWillTakeDamage.Clear();
        }
    }
}

namespace Hearthstone_Deck_Tracker
{
    public static class Core
    {
        public static Hearthstone.GameV2 Game { get; set; }
        public static Windows.OverlayWindow Overlay { get; set; }
    }

    public static class OpponentDeadForTracker
    {
        private static List<int> _uniqueDeadPlayers = new List<int>();
        private static List<int> _deadTracker = new List<int>();
        public static int DeadCount => _uniqueDeadPlayers.Count;
        public static void Reset() { _uniqueDeadPlayers.Clear(); _deadTracker.Clear(); }
        public static void Seed(IEnumerable<int> players, IEnumerable<int> turns)
        {
            _uniqueDeadPlayers = new List<int>(players);
            _deadTracker = new List<int>(turns);
        }
    }
}

namespace Hearthstone_Deck_Tracker.Hearthstone.Entities
{
    public class Entity
    {
        public int Id { get; set; }
        public string CardId { get; set; }
        public bool IsHero { get; set; }
        public int Health { get; set; }
        public Dictionary<HearthDb.Enums.GameTag, int> Tags { get; set; } = new Dictionary<HearthDb.Enums.GameTag, int>();
        public Entity(int id) { Id = id; }
        public int GetTag(HearthDb.Enums.GameTag tag) => Tags.TryGetValue(tag, out var v) ? v : 0;
        public void SetTag(HearthDb.Enums.GameTag tag, int value) => Tags[tag] = value;
    }
}

namespace Hearthstone_Deck_Tracker.Hearthstone
{
    public class FakeServerInfo { public uint GameHandle { get; set; } }
    public class FakeMetaData { public FakeServerInfo ServerInfo { get; set; } = new FakeServerInfo(); }
    public class FakeGameStats
    {
        public Guid GameId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string Result { get; set; } = "None";
    }

    internal class BattlegroundsBoardState
    {
        private Dictionary<int, object> LastKnownBoardState { get; } = new Dictionary<int, object>();
        public void Set(int pid, object val) => LastKnownBoardState[pid] = val;
        public object Get(int pid) => LastKnownBoardState.TryGetValue(pid, out var v) ? v : null;
        public void Reset() => LastKnownBoardState.Clear();
    }

    internal class BattlegroundsDeityState
    {
        private Dictionary<int, object> LastKnownDeity { get; } = new Dictionary<int, object>();
        public void Set(int pid, object val) => LastKnownDeity[pid] = val;
        public object Get(int pid) => LastKnownDeity.TryGetValue(pid, out var v) ? v : null;
        public void Reset() => LastKnownDeity.Clear();
    }

    public class FakeWatcher
    {
        public bool Running;
        public void Run() { Running = true; }
    }

    public class FakeTeamsArgs : EventArgs
    {
        public IReadOnlyList<HearthMirror.Objects.BattlegroundsLeaderboardTeam> Teams { get; set; }
    }

    public class FakeTeamsWatcher
    {
        public bool Running;
        public event EventHandler<FakeTeamsArgs> Change;
        public void Run() { Running = true; }
        public void RaiseChange(IReadOnlyList<HearthMirror.Objects.BattlegroundsLeaderboardTeam> teams)
        {
            Change?.Invoke(this, new FakeTeamsArgs { Teams = teams });
        }
    }

    public static class Watchers
    {
        public static FakeWatcher BattlegroundsLeaderboardWatcher { get; } = new FakeWatcher();
        public static FakeTeamsWatcher BattlegroundsLeaderboardTeamsWatcher { get; } = new FakeTeamsWatcher();
        public static FakeWatcher BattlegroundsLobbyInfoWatcher { get; } = new FakeWatcher();
        public static FakeWatcher BattlegroundsTeammateBoardStateWatcher { get; } = new FakeWatcher();
    }

    public class GameV2
    {
        private BattlegroundsBoardState _battlegroundsBoardState = new BattlegroundsBoardState();
        private BattlegroundsDeityState _battlegroundsDeityState = new BattlegroundsDeityState();
        private Dictionary<int, Dictionary<int, int>> _battlegroundsHeroLatestTavernUpTurn = new Dictionary<int, Dictionary<int, int>>();
        private Dictionary<int, Dictionary<int, int>> _battlegroundsHeroTriplesByTier = new Dictionary<int, Dictionary<int, int>>();

        public bool IsInMenu { get; set; } = true;
        public bool IsBattlegroundsMatch { get; set; }
        public bool IsBattlegroundsSoloMatch { get; set; }
        public bool IsBattlegroundsDuosMatch { get; set; }
        public bool IsBattlegroundsCombatPhase { get; set; }
        public string CurrentGameType { get; set; } = "GT_BATTLEGROUNDS";
        public string CurrentMode { get; set; } = "HUB";
        public object GameEntity { get; set; }
        public Entities.Entity PlayerEntity { get; set; }
        public int TurnNumber { get; set; }
        public Dictionary<int, Entities.Entity> Entities { get; } = new Dictionary<int, Entities.Entity>();
        public FakeMetaData MetaData { get; } = new FakeMetaData();
        public FakeGameStats CurrentGameStats { get; set; }

        public int GetTurnNumber() => TurnNumber;
        public void SetBoardSnapshot(int pid, object val) => _battlegroundsBoardState.Set(pid, val);
        public object GetBoardSnapshot(int pid) => _battlegroundsBoardState.Get(pid);
        public void SetDeitySnapshot(int pid, object val) => _battlegroundsDeityState.Set(pid, val);
        public object GetDeitySnapshot(int pid) => _battlegroundsDeityState.Get(pid);
        public void SetTavernUp(int id, int tier, int turn)
        {
            if (!_battlegroundsHeroLatestTavernUpTurn.ContainsKey(id))
                _battlegroundsHeroLatestTavernUpTurn[id] = new Dictionary<int, int>();
            _battlegroundsHeroLatestTavernUpTurn[id][tier] = turn;
        }
        public int GetTavernUp(int id, int tier)
            => _battlegroundsHeroLatestTavernUpTurn.TryGetValue(id, out var d) && d.TryGetValue(tier, out var v) ? v : 0;
        public void SetTriples(int id, int tier, int count)
        {
            if (!_battlegroundsHeroTriplesByTier.ContainsKey(id))
                _battlegroundsHeroTriplesByTier[id] = new Dictionary<int, int>();
            _battlegroundsHeroTriplesByTier[id][tier] = count;
        }
        public int GetTriples(int id, int tier)
            => _battlegroundsHeroTriplesByTier.TryGetValue(id, out var d) && d.TryGetValue(tier, out var v) ? v : 0;

        public void ResetAll()
        {
            Entities.Clear();
            _battlegroundsBoardState.Reset();
            _battlegroundsDeityState.Reset();
            _battlegroundsHeroLatestTavernUpTurn = new Dictionary<int, Dictionary<int, int>>();
            _battlegroundsHeroTriplesByTier = new Dictionary<int, Dictionary<int, int>>();
        }
    }
}

namespace Hearthstone_Deck_Tracker.BobsBuddy
{
    public class BobsBuddyInvoker
    {
        private static readonly Dictionary<string, BobsBuddyInvoker> Instances = new Dictionary<string, BobsBuddyInvoker>();
        private object _input;

        public static void ClearAll() => Instances.Clear();
        public void SetInput(object value) => _input = value;
        public object GetInput() => _input;

        public static BobsBuddyInvoker GetInstance(Guid gameId, int turn, bool createInstanceIfNoneFound = true)
        {
            var key = gameId + "_" + turn;
            if (!Instances.TryGetValue(key, out var inst) && createInstanceIfNoneFound)
            {
                inst = new BobsBuddyInvoker();
                Instances[key] = inst;
            }
            return inst;
        }
    }
}

namespace Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds
{
    public class BattlegroundsLeaderboardTilesViewModel
    {
        private IReadOnlyList<HearthMirror.Objects.BattlegroundsLeaderboardTeam> _teams = new List<HearthMirror.Objects.BattlegroundsLeaderboardTeam>();
        public int UpdateCount { get; private set; }
        public void SetTeams(IReadOnlyList<HearthMirror.Objects.BattlegroundsLeaderboardTeam> teams) => _teams = teams;
        public void Update() => UpdateCount++;
    }
}

namespace Hearthstone_Deck_Tracker.Windows
{
    public class OverlayWindow
    {
        public Controls.Overlay.Battlegrounds.BattlegroundsLeaderboardTilesViewModel BattlegroundsLeaderboardTilesVM { get; }
            = new Controls.Overlay.Battlegrounds.BattlegroundsLeaderboardTilesViewModel();
        public int DeadForUpdates { get; private set; }
        public int LeaderboardTileInfoUpdates { get; private set; }
        public void UpdateOpponentDeadForTurns(List<int> deadFor) => DeadForUpdates++;
        public void UpdateLeaderboardTileInfos() => LeaderboardTileInfoUpdates++;
    }
}
