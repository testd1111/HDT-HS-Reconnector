using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HS_Reconnector.LogHandler;
using HS_Reconnector.Native;

namespace HS_Reconnector.Core
{
    internal enum ReconnectVisualState
    {
        Waiting,
        Ready,
        Disconnected,
        Cooldown,
        Unsupported
    }

    internal sealed class ControllerSnapshot
    {
        public bool IsElevated { get; set; }
        public bool IsHearthstoneRunning { get; set; }
        public string HearthstoneDirectory { get; set; }
        public string ActiveLogPath { get; set; }
        public string RemoteAddress { get; set; }
        public ushort RemotePort { get; set; }
        public bool EndpointUnsupported { get; set; }
        public int EstablishedTcpCount { get; set; }
        public ConnectionStatus ConnectionStatus { get; set; }
        public ReconnectVisualState VisualState { get; set; }
        public int CooldownRemainingSeconds { get; set; }
        public bool CanReconnect { get; set; }
        public HdtStatusSnapshot HdtStatus { get; set; }
    }

    internal sealed class ReconnectController : ILogEndpointSink, IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly Reconnector reconnector = new Reconnector();
        private readonly HotkeyManager hotkeyManager = new HotkeyManager();
        private readonly HdtBridgeClient hdtBridge = new HdtBridgeClient();
        private readonly DispatcherTimer monitorTimer;
        private readonly DispatcherTimer disconnectTimer;
        private readonly DispatcherTimer connectionRestoreTimer;
        private readonly DispatcherTimer cooldownTimer;
        private readonly object endpointLock = new object();
        private readonly bool isElevated = Win32.IsElevated();

        private LogWatcher logWatcher;
        private bool hasRunningWatcher;
        private bool logWatcherStartInProgress;
        private bool gameRunObserved;
        private DateTime gameStartedAtUtc;
        private string detectedHsDirectory;
        private string lastCustomHsDirectory;
        private string activeLogPath;
        private DateTime nextLogSearchUtc = DateTime.MinValue;
        private int logSearchRetryDelaySeconds = 2;
        private bool logSearchBackoffReported;

        private string remoteAddr;
        private ushort remotePort;
        private bool endpointUnsupported;
        private int establishedTcpCount;
        private long endpointVersion;

        private string reconnectRemoteAddr;
        private ushort reconnectRemotePort;
        private int connectionCheckInProgress;
        private int statusTcpCheckInProgress;
        private int tcpRecheckRequested;
        private int hdtRefreshInProgress;
        private int reconnectInProgress;
        private int monitorTickInProgress;
        private long logDiscoveryGeneration;
        private DateTime nextHdtRefreshUtc = DateTime.MinValue;
        internal Func<string, ushort, Tuple<HdtSaveSummary, bool>> ReconnectExecutorForTest { get; set; }
        private long reconnectAttemptId;
        private bool cooldownPendingAfterReconnect;
        private DateTime cooldownEndsAtUtc = DateTime.MinValue;
        private bool hotkeySuspended;
        private bool disposed;

        public event Action<ControllerSnapshot> StateChanged;

        public ReconnectController(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            hotkeyManager.Pressed += OnHotkeyPressed;

            monitorTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            monitorTimer.Tick += MonitorTimer_Tick;

            disconnectTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(AppSettings.Instance.DisconnectTimeoutSeconds)
            };
            disconnectTimer.Tick += DisconnectTimer_Tick;

            connectionRestoreTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            connectionRestoreTimer.Tick += ConnectionRestoreTimer_Tick;

            cooldownTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            cooldownTimer.Tick += CooldownTimer_Tick;
        }

        public bool Start()
        {
            var hotkeyApplied = ApplySettings();
            monitorTimer.Start();
            TickMonitor();
            RefreshHdtStatusAsync(forceAttach: true);
            return hotkeyApplied;
        }

        public void RefreshHdtStatusNow()
        {
            RefreshHdtStatusAsync(forceAttach: true);
        }

        public bool ApplySettings()
        {
            var settings = AppSettings.Instance;
            settings.Normalize();
            AppLogger.VerboseEnabled = settings.VerboseLogging;

            disconnectTimer.Interval = TimeSpan.FromSeconds(settings.DisconnectTimeoutSeconds);

            if (!string.Equals(lastCustomHsDirectory, settings.CustomHearthstoneDirectory, StringComparison.OrdinalIgnoreCase))
            {
                lastCustomHsDirectory = settings.CustomHearthstoneDirectory;
                RestartLogDiscovery("Hearthstone folder setting changed");
            }

            var hotkeyApplied = true;
            if (hotkeySuspended)
            {
                hotkeyManager.Unregister();
            }
            else if (!hotkeyManager.Register(settings.Hotkey, settings.HotkeyModifiers))
            {
                hotkeyApplied = false;
                AppLogger.Warn("Configured global hotkey could not be registered.");
            }

            var opponentMemory = settings.OpponentMemory.Clone();
            var verboseLogging = settings.VerboseLogging;
            AppLogger.Debug(string.Format(
                "ApplySettings: hotkey={0}+{1} (ok={2}), cooldown={3}s, timeout={4}s, verbose={5}, omEnabled={6}, omMode={7}, omStyle={8}, omScale={9}%",
                settings.HotkeyModifiers,
                settings.Hotkey,
                hotkeyApplied,
                settings.ReconnectCooldownSeconds,
                settings.DisconnectTimeoutSeconds,
                verboseLogging,
                opponentMemory.Enabled,
                opponentMemory.DisplayMode,
                opponentMemory.VisualStyle,
                opponentMemory.Scale));
            _ = Task.Run(() => hdtBridge.ApplyConfig(opponentMemory, verboseLogging));

            NotifyStateChanged();
            return hotkeyApplied;
        }

        public bool SuspendHotkey(bool suspend)
        {
            if (hotkeySuspended == suspend)
                return true;

            hotkeySuspended = suspend;
            if (!suspend)
                return ApplySettings();

            hotkeyManager.Unregister();
            return true;
        }

        public async void ExecuteReconnect()
        {
            if (disposed)
                return;

            if (!isElevated)
            {
                AppLogger.Error("Reconnect cancelled: Administrator privileges are required.");
                return;
            }

            if (IsOnCooldown())
            {
                AppLogger.Info("Reconnect ignored because cooldown is active.");
                return;
            }

            if (reconnector.Status != ConnectionStatus.Connected)
            {
                AppLogger.Info("Reconnect ignored while previous disconnect is still completing.");
                return;
            }

            GetEndpointState(out var addr, out var port, out var unsupported, out _, out var capturedEndpointVersion);
            if (unsupported || string.IsNullOrWhiteSpace(addr) || port == 0)
            {
                AppLogger.Info("Reconnect ignored: no active IPv4 game server endpoint.");
                return;
            }

            if (Interlocked.CompareExchange(ref reconnectInProgress, 1, 0) != 0)
            {
                AppLogger.Debug("Reconnect ignored: another reconnect attempt is already in progress.");
                return;
            }

            var capturedAttemptId = Interlocked.Read(ref reconnectAttemptId);
            AppLogger.Info(string.Format("Reconnect requested for {0}:{1}", addr, port));
            cooldownPendingAfterReconnect = false;
            NotifyStateChanged();

            try
            {
                var executor = ReconnectExecutorForTest;
                var outcome = await Task.Run(() =>
                {
                    if (executor != null)
                        return executor(addr, port);

                    var save = hdtBridge.SaveBeforeReconnect();
                    GetEndpointState(out _, out _, out _, out _, out var midEndpointVersion);
                    if (disposed
                        || midEndpointVersion != capturedEndpointVersion
                        || Interlocked.Read(ref reconnectAttemptId) != capturedAttemptId)
                    {
                        if (save.Saved)
                            hdtBridge.CancelPendingRestore();
                        return Tuple.Create(save, false);
                    }
                    var disconnected = reconnector.Disconnect(addr, port);
                    if (!disconnected && save.Saved)
                        hdtBridge.CancelPendingRestore();
                    return Tuple.Create(save, disconnected);
                });

                if (disposed)
                {
                    if (outcome.Item2 && reconnector.Status == ConnectionStatus.Disconnected)
                        reconnector.ResumeConnect();
                    return;
                }

                if (!outcome.Item2)
                {
                    RefreshTcpCountAsync();
                    return;
                }

                GetEndpointState(out _, out _, out _, out _, out var currentEndpointVersion);
                var stateStale = currentEndpointVersion != capturedEndpointVersion
                    || Interlocked.Read(ref reconnectAttemptId) != capturedAttemptId
                    || (executor == null && !gameRunObserved);
                if (stateStale)
                {
                    AppLogger.Debug("Reconnect completed after match/game state changed; resetting status without starting timers.");
                    if (reconnector.Status == ConnectionStatus.Disconnected)
                        reconnector.ResumeConnect();
                    if (outcome.Item1 != null && outcome.Item1.Saved)
                        _ = Task.Run(() => hdtBridge.CancelPendingRestore());
                    return;
                }

                cooldownPendingAfterReconnect = true;
                reconnectRemoteAddr = addr;
                reconnectRemotePort = port;
                Interlocked.Increment(ref reconnectAttemptId);
                lock (endpointLock)
                {
                    establishedTcpCount = 0;
                }

                disconnectTimer.Stop();
                disconnectTimer.Start();
                connectionRestoreTimer.Stop();
                connectionRestoreTimer.Start();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Reconnect execution failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref reconnectInProgress, 0);
                if (!disposed)
                    NotifyStateChanged();
            }
        }

        public ControllerSnapshot GetSnapshot()
        {
            GetEndpointState(out var addr, out var port, out var unsupported, out var tcpCount, out _);
            var onCooldown = IsOnCooldown();
            var cooldownRemaining = onCooldown
                ? Math.Max(1, (int)Math.Ceiling((cooldownEndsAtUtc - DateTime.UtcNow).TotalSeconds))
                : 0;

            var hasEndpoint = !string.IsNullOrWhiteSpace(addr) && port != 0;
            var hasExactSocket = tcpCount == 1;

            ReconnectVisualState visualState;
            if (reconnector.Status == ConnectionStatus.Disconnected)
                visualState = ReconnectVisualState.Disconnected;
            else if (onCooldown)
                visualState = ReconnectVisualState.Cooldown;
            else if (unsupported)
                visualState = ReconnectVisualState.Unsupported;
            else if (hasEndpoint && hasExactSocket)
                visualState = ReconnectVisualState.Ready;
            else
                visualState = ReconnectVisualState.Waiting;

            var canReconnect = isElevated
                && Volatile.Read(ref reconnectInProgress) == 0
                && reconnector.Status == ConnectionStatus.Connected
                && !onCooldown
                && !unsupported
                && hasEndpoint
                && hasExactSocket;

            return new ControllerSnapshot
            {
                IsElevated = isElevated,
                IsHearthstoneRunning = gameRunObserved,
                HearthstoneDirectory = detectedHsDirectory,
                ActiveLogPath = activeLogPath,
                RemoteAddress = addr,
                RemotePort = port,
                EndpointUnsupported = unsupported,
                EstablishedTcpCount = tcpCount,
                ConnectionStatus = reconnector.Status,
                VisualState = visualState,
                CooldownRemainingSeconds = cooldownRemaining,
                CanReconnect = canReconnect,
                HdtStatus = hdtBridge.GetCurrentSnapshot()
            };
        }

        public bool SetRemoteEndpoint(string address, ushort port, string source)
        {
            if (!IPAddress.TryParse(address, out var parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork
                || port == 0)
            {
                lock (endpointLock)
                {
                    remoteAddr = null;
                    remotePort = 0;
                    endpointUnsupported = true;
                    establishedTcpCount = 0;
                    endpointVersion++;
                }
                AppLogger.Warn(string.Format(
                    "Unsupported non-IPv4 or invalid endpoint from {0}: {1}:{2}",
                    source,
                    address ?? "<null>",
                    port));
                NotifyStateChangedAsync();
                return false;
            }

            var normalized = parsed.ToString();
            bool changed;
            lock (endpointLock)
            {
                changed = !string.Equals(remoteAddr, normalized, StringComparison.Ordinal)
                    || remotePort != port
                    || endpointUnsupported;
                if (changed)
                {
                    remoteAddr = normalized;
                    remotePort = port;
                    endpointUnsupported = false;
                    establishedTcpCount = 0;
                    endpointVersion++;
                }
            }

            if (changed)
            {
                AppLogger.Info(string.Format("Game server endpoint ({0}): {1}:{2}", source, normalized, port));
                NotifyEndpointDetectedAsync();
            }

            return true;
        }

        public void ClearRemoteEndpointForMatchEnd(string reason)
        {
            ClearEndpoint(reason);
            if (!disposed)
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (disposed)
                        return;
                    if (reconnector.Status == ConnectionStatus.Disconnected)
                    {
                        cooldownPendingAfterReconnect = false;
                        ResumeConnectionState();
                    }
                    else
                    {
                        NotifyStateChanged();
                    }
                }));
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Interlocked.Increment(ref reconnectAttemptId);
            monitorTimer.Stop();
            disconnectTimer.Stop();
            connectionRestoreTimer.Stop();
            cooldownTimer.Stop();
            hotkeyManager.Pressed -= OnHotkeyPressed;
            hotkeyManager.Dispose();

            logWatcher?.Stop();
            logWatcher = null;
            hdtBridge.Dispose();
        }

        private void OnHotkeyPressed()
        {
            if (disposed)
                return;

            dispatcher.BeginInvoke(new Action(ExecuteReconnect));
        }

        private void MonitorTimer_Tick(object sender, EventArgs e)
        {
            TickMonitor();
        }

        private async void TickMonitor()
        {
            if (disposed)
                return;

            if (Interlocked.CompareExchange(ref monitorTickInProgress, 1, 0) != 0)
                return;

            try
            {
                var customHsDir = AppSettings.Instance.CustomHearthstoneDirectory;
                var prevDetectedDir = detectedHsDirectory;
                var prevObserved = gameRunObserved;
                var prevStartedAt = gameStartedAtUtc;
                var canCheckLogs = !logWatcherStartInProgress;
                var hadWatcher = hasRunningWatcher;
                var prevLogPath = activeLogPath;
                var logSearchDue = DateTime.UtcNow >= nextLogSearchUtc;
                var capturedLogGen = Interlocked.Read(ref logDiscoveryGeneration);

                var probe = await Task.Run(() =>
                {
                    var running = HearthstoneLocator.IsHearthstoneRunning(out var startedUtc);
                    string discoveredDir = prevDetectedDir;
                    string candidateLog = null;
                    var candidateIsNewer = false;

                    if (running && canCheckLogs)
                    {
                        var restarted = prevObserved
                            && startedUtc != DateTime.MinValue
                            && prevStartedAt != DateTime.MinValue
                            && startedUtc != prevStartedAt;
                        var needStartWatcher = !prevObserved || restarted || (!hadWatcher && logSearchDue);
                        var effectiveStart = restarted || !prevObserved ? startedUtc : prevStartedAt;

                        if (needStartWatcher)
                        {
                            discoveredDir = HearthstoneLocator.FindHearthstoneDirectory(customHsDir);
                            if (!string.IsNullOrEmpty(discoveredDir))
                                candidateLog = HearthstoneLocator.FindLatestLogFile(discoveredDir, effectiveStart);
                        }
                        else if (hadWatcher)
                        {
                            var searchDir = !string.IsNullOrEmpty(prevDetectedDir)
                                ? prevDetectedDir
                                : HearthstoneLocator.FindHearthstoneDirectory(customHsDir);
                            discoveredDir = searchDir;
                            var latest = HearthstoneLocator.FindLatestLogFile(searchDir, effectiveStart);
                            if (!string.IsNullOrEmpty(latest)
                                && !string.Equals(latest, prevLogPath, StringComparison.OrdinalIgnoreCase)
                                && IsNewerLogFile(latest, prevLogPath))
                            {
                                candidateLog = latest;
                                candidateIsNewer = true;
                            }
                        }
                    }

                    return Tuple.Create(running, startedUtc, discoveredDir, candidateLog, candidateIsNewer);
                });

                if (disposed)
                    return;

                var isRunning = probe.Item1;
                var startedAtUtc = probe.Item2;
                var discoveredHsDir = probe.Item3;
                var candidateLogPath = probe.Item4;
                var isNewerCandidate = probe.Item5;

                if (isRunning && !gameRunObserved)
                {
                    gameRunObserved = true;
                    gameStartedAtUtc = startedAtUtc;
                    AppLogger.Info("Hearthstone process detected.");
                    if (reconnector.Status == ConnectionStatus.Disconnected)
                    {
                        cooldownPendingAfterReconnect = false;
                        ResumeConnectionState();
                    }
                    ClearEndpoint("Hearthstone started");
                    ResetLogSearchBackoff();
                    nextLogSearchUtc = DateTime.UtcNow;
                    NotifyStateChanged();
                }
                else if (isRunning && gameRunObserved
                    && startedAtUtc != DateTime.MinValue
                    && gameStartedAtUtc != DateTime.MinValue
                    && startedAtUtc != gameStartedAtUtc)
                {
                    gameStartedAtUtc = startedAtUtc;
                    AppLogger.Info("Hearthstone process restarted.");
                    var wasSameGenBeforeStop = Interlocked.Read(ref logDiscoveryGeneration) == capturedLogGen;
                    StopWatcher();
                    if (wasSameGenBeforeStop)
                        capturedLogGen = Interlocked.Read(ref logDiscoveryGeneration);
                    ResetDisconnectAndCooldownState();
                    ClearEndpoint("Hearthstone restarted");
                    ResetLogSearchBackoff();
                    nextLogSearchUtc = DateTime.UtcNow;
                    NotifyStateChanged();
                }
                else if (!isRunning && gameRunObserved)
                {
                    gameRunObserved = false;
                    gameStartedAtUtc = DateTime.MinValue;
                    AppLogger.Info("Hearthstone process closed.");
                    StopWatcher();
                    ResetDisconnectAndCooldownState();
                    ClearEndpoint("Hearthstone stopped");
                    ResetLogSearchBackoff();
                    nextLogSearchUtc = DateTime.MinValue;
                    NotifyStateChanged();
                }

                if (hasRunningWatcher && logWatcher != null && !logWatcher.IsHealthy)
                {
                    AppLogger.Warn("Hearthstone.log became unavailable; restarting log discovery.");
                    StopWatcher();
                    ClearEndpoint("log watcher unavailable");
                    ScheduleNextLogSearch();
                    NotifyStateChanged();
                }

                if (reconnector.Status == ConnectionStatus.Connected)
                    RefreshTcpCountAsync();

                if (isRunning && !logWatcherStartInProgress && Interlocked.Read(ref logDiscoveryGeneration) == capturedLogGen)
                {
                    if (!string.Equals(detectedHsDirectory, discoveredHsDir, StringComparison.OrdinalIgnoreCase))
                    {
                        detectedHsDirectory = discoveredHsDir;
                        NotifyStateChanged();
                    }

                    if (!hasRunningWatcher && DateTime.UtcNow >= nextLogSearchUtc)
                    {
                        if (!string.IsNullOrEmpty(candidateLogPath))
                            StartLogWatcherForPath(candidateLogPath);
                        else
                            ScheduleNextLogSearch();
                    }
                    else if (hasRunningWatcher && isNewerCandidate && !string.IsNullOrEmpty(candidateLogPath))
                    {
                        AppLogger.Info("Switching to newer Hearthstone log: " + candidateLogPath);
                        StopWatcher();
                        ClearEndpoint("newer Hearthstone log detected");
                        ResetLogSearchBackoff();
                        StartLogWatcherForPath(candidateLogPath);
                    }
                }

                if (DateTime.UtcNow >= nextHdtRefreshUtc)
                    RefreshHdtStatusAsync(forceAttach: false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Monitor tick failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref monitorTickInProgress, 0);
            }
        }

        private async void RefreshHdtStatusAsync(bool forceAttach)
        {
            if (disposed)
                return;

            if (Interlocked.CompareExchange(ref hdtRefreshInProgress, 1, 0) != 0)
                return;

            try
            {
                var opponentMemory = AppSettings.Instance.OpponentMemory.Clone();
                var verboseLogging = AppSettings.Instance.VerboseLogging;
                var snap = await Task.Run(() => hdtBridge.Refresh(forceAttach, opponentMemory, verboseLogging));
                if (disposed)
                    return;

                var delaySeconds = snap.State == HdtConnectionState.Ready && !snap.PendingRestore
                    ? 4
                    : snap.State == HdtConnectionState.Checking ? 1 : 2;
                nextHdtRefreshUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                AppLogger.Debug(string.Format(
                    "HDT status snapshot: state={0}, bridge={1} (outdated={2}), hooks={3}/{4}, inMatch={5}, bg={6}, pending={7}, nextCheck={8}s",
                    snap.State,
                    snap.BridgeVersion,
                    snap.BridgeOutdated,
                    snap.ReadyHooks,
                    snap.TotalHooks,
                    snap.InMatch,
                    snap.IsBattlegrounds,
                    snap.PendingRestore,
                    delaySeconds));
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                AppLogger.Error("HDT bridge check failed: " + ex.Message);
                nextHdtRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            }
            finally
            {
                Interlocked.Exchange(ref hdtRefreshInProgress, 0);
            }
        }

        private void StartLogWatcherForPath(string logPath)
        {
            var watcher = new LogWatcher(this, logPath);
            logWatcher = watcher;
            activeLogPath = logPath;
            logWatcherStartInProgress = true;
            _ = StartLogWatcherAsync(watcher);
        }

        private static bool IsNewerLogFile(string candidatePath, string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
                return true;

            return GetLogTimestampUtc(candidatePath) > GetLogTimestampUtc(currentPath);
        }

        private static DateTime GetLogTimestampUtc(string logPath)
        {
            var dir = Path.GetDirectoryName(logPath);
            var fileTime = File.GetCreationTimeUtc(logPath);
            var dirTime = !string.IsNullOrEmpty(dir) ? Directory.GetCreationTimeUtc(dir) : DateTime.MinValue;
            return fileTime >= dirTime ? fileTime : dirTime;
        }

        private async Task StartLogWatcherAsync(LogWatcher watcher)
        {
            var started = false;
            try
            {
                started = await watcher.StartAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unexpected log watcher startup error: " + ex.Message);
                watcher.Stop();
            }
            finally
            {
                if (!disposed && ReferenceEquals(logWatcher, watcher))
                {
                    logWatcherStartInProgress = false;
                    if (started)
                    {
                        hasRunningWatcher = true;
                        ResetLogSearchBackoff();
                    }
                    else
                    {
                        logWatcher = null;
                        activeLogPath = null;
                        hasRunningWatcher = false;
                        ScheduleNextLogSearch();
                    }
                }
                else
                {
                    if (logWatcher == null)
                        logWatcherStartInProgress = false;
                    watcher.Stop();
                }

                NotifyStateChanged();
            }
        }

        private void RestartLogDiscovery(string reason)
        {
            Interlocked.Increment(ref logDiscoveryGeneration);
            StopWatcher();
            ClearEndpoint(reason);
            detectedHsDirectory = null;
            ResetLogSearchBackoff();
            nextLogSearchUtc = DateTime.UtcNow;
        }

        private void StopWatcher()
        {
            Interlocked.Increment(ref logDiscoveryGeneration);
            logWatcher?.Stop();
            logWatcher = null;
            hasRunningWatcher = false;
            logWatcherStartInProgress = false;
            activeLogPath = null;
        }

        private void ResetDisconnectAndCooldownState()
        {
            disconnectTimer.Stop();
            connectionRestoreTimer.Stop();
            cooldownTimer.Stop();
            cooldownPendingAfterReconnect = false;
            cooldownEndsAtUtc = DateTime.MinValue;
            Interlocked.Increment(ref reconnectAttemptId);
            reconnectRemoteAddr = null;
            reconnectRemotePort = 0;
            if (reconnector.Status == ConnectionStatus.Disconnected)
                reconnector.ResumeConnect();
        }

        private async void ConnectionRestoreTimer_Tick(object sender, EventArgs e)
        {
            if (disposed || reconnector.Status != ConnectionStatus.Disconnected)
            {
                connectionRestoreTimer.Stop();
                return;
            }

            if (Interlocked.CompareExchange(ref connectionCheckInProgress, 1, 0) != 0)
                return;

            var attemptId = Interlocked.Read(ref reconnectAttemptId);
            var previousAddr = reconnectRemoteAddr;
            var previousPort = reconnectRemotePort;
            GetEndpointState(out var currentAddr, out var currentPort, out var currentUnsupported, out _, out var capturedVersion);

            var hasCurrent = !currentUnsupported && !string.IsNullOrWhiteSpace(currentAddr) && currentPort != 0;
            var hasDifferentPrevious = !string.IsNullOrWhiteSpace(previousAddr)
                && previousPort != 0
                && (!hasCurrent || !string.Equals(previousAddr, currentAddr, StringComparison.Ordinal) || previousPort != currentPort);

            try
            {
                var pollResult = await Task.Run(() =>
                {
                    var currentCount = hasCurrent ? reconnector.CountEstablishedConnections(currentAddr, currentPort) : 0;
                    var restored = currentCount > 0
                        || (hasDifferentPrevious && reconnector.HasEstablishedConnection(previousAddr, previousPort));
                    return Tuple.Create(restored, currentCount);
                });

                if (disposed
                    || attemptId != Interlocked.Read(ref reconnectAttemptId)
                    || reconnector.Status != ConnectionStatus.Disconnected
                    || !pollResult.Item1)
                    return;

                AppLogger.Info("Hearthstone TCP connection restored.");
                _ = Task.Run(() =>
                {
                    hdtBridge.NotifyReconnected();
                    if (!disposed)
                        dispatcher.BeginInvoke(new Action(() => RefreshHdtStatusAsync(forceAttach: false)));
                });
                bool needsRecheck;
                lock (endpointLock)
                {
                    if (capturedVersion == endpointVersion)
                    {
                        establishedTcpCount = pollResult.Item2;
                        needsRecheck = false;
                    }
                    else
                    {
                        establishedTcpCount = 0;
                        needsRecheck = true;
                    }
                }
                ResumeConnectionState();
                if (needsRecheck)
                    RefreshTcpCountAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("TCP connection poll failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref connectionCheckInProgress, 0);
            }
        }

        private void DisconnectTimer_Tick(object sender, EventArgs e)
        {
            disconnectTimer.Stop();
            connectionRestoreTimer.Stop();
            if (disposed)
                return;

            AppLogger.Warn("Disconnect timeout elapsed; restoring Ready state.");
            ResumeConnectionState();
            RefreshTcpCountAsync();
        }

        private void CooldownTimer_Tick(object sender, EventArgs e)
        {
            if (!IsOnCooldown())
                cooldownTimer.Stop();

            NotifyStateChanged();
        }

        private void ResumeConnectionState()
        {
            var startCooldown = cooldownPendingAfterReconnect;
            cooldownPendingAfterReconnect = false;
            disconnectTimer.Stop();
            connectionRestoreTimer.Stop();
            Interlocked.Increment(ref reconnectAttemptId);
            reconnectRemoteAddr = null;
            reconnectRemotePort = 0;
            reconnector.ResumeConnect();

            if (startCooldown && AppSettings.Instance.ReconnectCooldownSeconds > 0)
            {
                cooldownEndsAtUtc = DateTime.UtcNow.AddSeconds(AppSettings.Instance.ReconnectCooldownSeconds);
                cooldownTimer.Start();
            }
            else
            {
                cooldownEndsAtUtc = DateTime.MinValue;
                cooldownTimer.Stop();
            }

            NotifyStateChanged();
        }

        private async void RefreshTcpCountAsync()
        {
            GetEndpointState(out var addr, out var port, out var unsupported, out _, out var targetVersion);
            if (unsupported || string.IsNullOrWhiteSpace(addr) || port == 0)
                return;

            if (Interlocked.CompareExchange(ref statusTcpCheckInProgress, 1, 0) != 0)
            {
                Volatile.Write(ref tcpRecheckRequested, 1);
                return;
            }

            var attemptId = Interlocked.Read(ref reconnectAttemptId);
            try
            {
                var count = await Task.Run(() => reconnector.CountEstablishedConnections(addr, port));
                bool changed = false;
                lock (endpointLock)
                {
                    if (!disposed
                        && reconnector.Status == ConnectionStatus.Connected
                        && attemptId == Interlocked.Read(ref reconnectAttemptId)
                        && targetVersion == endpointVersion
                        && count != establishedTcpCount)
                    {
                        establishedTcpCount = count;
                        changed = true;
                    }
                }
                if (changed)
                {
                    AppLogger.Debug(string.Format("TCP established socket count changed for {0}:{1} -> {2}", addr, port, count));
                    NotifyStateChanged();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("TCP status check failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref statusTcpCheckInProgress, 0);
                if (!disposed
                    && Interlocked.Exchange(ref tcpRecheckRequested, 0) != 0
                    && reconnector.Status == ConnectionStatus.Connected)
                {
                    RefreshTcpCountAsync();
                }
            }
        }

        private void ClearEndpoint(string reason)
        {
            bool hadEndpoint;
            lock (endpointLock)
            {
                hadEndpoint = !string.IsNullOrEmpty(remoteAddr) || remotePort != 0 || endpointUnsupported;
                remoteAddr = null;
                remotePort = 0;
                endpointUnsupported = false;
                establishedTcpCount = 0;
                endpointVersion++;
            }

            if (hadEndpoint)
                AppLogger.Info("Cleared game server endpoint: " + reason);
        }

        private void GetEndpointState(out string addr, out ushort port, out bool unsupported)
        {
            lock (endpointLock)
            {
                addr = remoteAddr;
                port = remotePort;
                unsupported = endpointUnsupported;
            }
        }

        private void GetEndpointState(out string addr, out ushort port, out bool unsupported, out int tcpCount, out long version)
        {
            lock (endpointLock)
            {
                addr = remoteAddr;
                port = remotePort;
                unsupported = endpointUnsupported;
                tcpCount = establishedTcpCount;
                version = endpointVersion;
            }
        }

        private bool IsOnCooldown()
        {
            return AppSettings.Instance.ReconnectCooldownSeconds > 0
                && DateTime.UtcNow < cooldownEndsAtUtc;
        }

        private void ScheduleNextLogSearch()
        {
            nextLogSearchUtc = DateTime.UtcNow.AddSeconds(logSearchRetryDelaySeconds);
            if (logSearchRetryDelaySeconds >= 30)
            {
                if (!logSearchBackoffReported)
                {
                    AppLogger.Info("Hearthstone.log is still unavailable; checking every 30 seconds.");
                    logSearchBackoffReported = true;
                }
                return;
            }

            if (logSearchRetryDelaySeconds < 5)
                logSearchRetryDelaySeconds = 5;
            else if (logSearchRetryDelaySeconds < 10)
                logSearchRetryDelaySeconds = 10;
            else
                logSearchRetryDelaySeconds = 30;
        }

        private void ResetLogSearchBackoff()
        {
            logSearchRetryDelaySeconds = 2;
            logSearchBackoffReported = false;
        }

        private void NotifyStateChanged()
        {
            if (!disposed)
                StateChanged?.Invoke(GetSnapshot());
        }

        private void NotifyStateChangedAsync()
        {
            if (!disposed)
                dispatcher.BeginInvoke(new Action(NotifyStateChanged));
        }

        private void NotifyEndpointDetectedAsync()
        {
            if (disposed)
                return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed)
                    return;
                if (reconnector.Status == ConnectionStatus.Connected)
                    RefreshTcpCountAsync();
                NotifyStateChanged();
            }));
        }
    }
}
