using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace HdtBridge
{
    internal sealed class CombatSnapshotResult
    {
        public CombatOutcome Outcome { get; set; }
        public int? ExactDamage { get; set; }
    }

    internal sealed class LastOpponentTracker : IDisposable
    {
        private sealed class LeaderboardEntry
        {
            public int PlayerId;
            public int Place;
            public bool IsLocal;
            public int Health;
            public int Armor;
            public bool WonLastCombat;
            public int Durability => Health + Armor;
        }

        private struct RenderDecision
        {
            public bool ShouldShow;
            public LastOpponentConfig Config;
            public int Place;
            public bool FallbackScheduled;
            public CombatOutcome Outcome;
            public int? Damage;
        }

        private static readonly TimeSpan ResultDelay = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan MissingDataTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan CompletionStateTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ReconnectIdentityTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan PartialLobbyStabilityDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan SubscribeRetryInterval = TimeSpan.FromSeconds(15);
        private const int MaxSubscribeAttempts = 10;

        private readonly object sync = new object();
        private readonly object subscribeLock = new object();
        private readonly LastOpponentOverlay overlayRenderer = new LastOpponentOverlay();
        private readonly Timer pollTimer;
        private int pollInProgress;
        private int disposed;
        private long renderGeneration;

        private LastOpponentConfig config = new LastOpponentConfig();
        private volatile bool subscribedGameStart;
        private volatile bool subscribedTurnStart;
        private volatile bool subscribedPredamage;
        private int subscribeAttempts;
        private DateTime nextSubscribeRetryUtc = DateTime.MinValue;

        // Match identity
        private bool hasMatchState;
        private uint? currentGameHandle;
        private bool sawMenu;
        private DateTime? identityWaitingSinceUtc;
        private bool standardLobbyInitialized;
        private string partialLobbySig;
        private DateTime partialLobbySinceUtc;

        // Encounter & combat state
        private bool? wasCombat;
        private int? scheduledOpponentId;
        private int? scheduledRound;
        private bool scheduledIsGhost;

        private int? activeCombatOpponentId;
        private int? activeCombatRound;
        private bool activeCombatWasGhost;
        private int localPlayerIdAtCombatStart;
        private int? localDurabilityAtStart;
        private int? opponentDurabilityAtStart;
        private int? predamagePlayerId;
        private int? predamageAmount;
        private bool restoredAtCombatStart;
        private long? clientHandleAtCombatStart;
        private int gameStartGenAtCombatStart;
        private int playerTurnStartGenAtCombatStart;
        private int gameStartGenAtMatchIdentity;

        // Completion gate
        private bool completionPending;
        private bool completionInterrupted;
        private DateTime? completionSupportedSinceUtc;
        private DateTime? completionStableSinceUtc;

        // Completed last opponent result
        private int? lastCountedRound;
        private int? lastCompletedOpponentId;
        private CombatOutcome lastCombatOutcome;
        private int? lastCombatDamage;

        // Event counters (updated by optional GameEvents hooks)
        private int gameStartGeneration;
        private int playerTurnStartGeneration;
        private int turnStartCompletionRequested;

        public LastOpponentTracker()
        {
            pollTimer = new Timer(_ => PollTick(), null, 250, 250);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try
            {
                pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
                pollTimer.Dispose();
            }
            catch
            {
                // Ignore timer disposal race.
            }
        }

        public bool Enabled { get { lock (sync) return config.Enabled; } }
        public int? LastOpponentPlayerId { get { lock (sync) return lastCompletedOpponentId; } }
        public CombatOutcome LastOutcome { get { lock (sync) return lastCombatOutcome; } }
        public int? LastDamage { get { lock (sync) return lastCombatDamage; } }

        public void SetEnabled(bool isEnabled)
        {
            LastOpponentConfig next;
            lock (sync)
            {
                next = config.Clone();
                next.Enabled = isEnabled;
            }
            ApplyConfig(next);
        }

        public void ApplyConfig(LastOpponentConfig newConfig)
        {
            if (newConfig == null)
                return;

            var normalized = newConfig.Clone();
            normalized.Normalize();

            lock (sync)
                config = normalized;

            Interlocked.Increment(ref renderGeneration);

            if (!normalized.Enabled)
            {
                var coreType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Core");
                var overlay = HdtReflection.GetStaticMemberValue(coreType, "Overlay");
                overlayRenderer.Hide(overlay, force: true);
            }
            else
            {
                PollTick();
            }
        }

        public LastOpponentConfig GetConfigSnapshot()
        {
            lock (sync)
                return config.Clone();
        }

        public void OnReconnectTriggered()
        {
            lock (sync)
            {
                if (activeCombatOpponentId.HasValue && activeCombatOpponentId.Value > 0)
                {
                    completionInterrupted = true;
                    BridgeLogger.Debug("LastOpponentTracker: reconnect during active combat with opponentId=" + activeCombatOpponentId.Value);
                }
            }
        }

        public void PollTick()
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            if (Interlocked.CompareExchange(ref pollInProgress, 1, 0) != 0)
                return;

            try
            {
                if ((!subscribedGameStart || !subscribedTurnStart || !subscribedPredamage)
                    && Volatile.Read(ref subscribeAttempts) < MaxSubscribeAttempts
                    && DateTime.UtcNow >= nextSubscribeRetryUtc)
                {
                    TrySubscribeGameEvents();
                }

                var coreType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Core");
                var game = HdtReflection.GetStaticMemberValue(coreType, "Game");
                var overlay = HdtReflection.GetStaticMemberValue(coreType, "Overlay");
                if (game == null || overlay == null)
                    return;

                ProcessTick(game, overlay, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                BridgeLogger.ErrorThrottled("lot_poll", "LastOpponentTracker.PollTick", ex, 10);
            }
            finally
            {
                Interlocked.Exchange(ref pollInProgress, 0);
            }
        }

        internal void ProcessTick(object game, object overlay, DateTime nowUtc)
        {
            RenderDecision decision = default;
            long capturedRenderGen;
            lock (sync)
            {
                capturedRenderGen = Interlocked.Read(ref renderGeneration);
                var isInMenu = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsInMenu") ?? true);
                var isSoloBg = IsSupportedSoloMatch(game, isInMenu);

                if (!isSoloBg)
                {
                    if (completionPending)
                    {
                        completionSupportedSinceUtc = null;
                        completionStableSinceUtc = null;
                    }
                    if (isInMenu)
                    {
                        if (activeCombatOpponentId.HasValue && activeCombatOpponentId.Value > 0)
                            completionInterrupted = true;
                        if (HasDefinitiveMatchResult(game))
                            ResetMatchStateLocked();
                        else
                            sawMenu = true;
                    }
                }
                else if (PrepareMatchStateLocked(game, nowUtc))
                {
                    var round = GetTurnNumber(game);
                    var combat = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsCombatPhase") ?? false);
                    var players = ReadLeaderboardPlayers(game);
                    var localPlayerId = GetLocalPlayerId(game);
                    var scheduled = GetScheduledOpponentPlayerId(game);

                    if (!wasCombat.HasValue)
                        wasCombat = combat;

                    if (Interlocked.Exchange(ref turnStartCompletionRequested, 0) != 0 && activeCombatOpponentId.HasValue)
                        BeginCombatCompletionLocked(interrupted: false);
                    if (wasCombat == true && !combat)
                        BeginCombatCompletionLocked(interrupted: false);
                    if (activeCombatOpponentId.HasValue && activeCombatRound.HasValue && round > activeCombatRound.Value)
                        BeginCombatCompletionLocked(interrupted: true);

                    if (completionPending)
                        TryFinalizeActiveCombatLocked(game, players, round, nowUtc);

                    if (!completionPending && combat)
                    {
                        if (!activeCombatOpponentId.HasValue)
                        {
                            if (scheduled.HasValue && scheduled.Value > 0)
                            {
                                scheduledOpponentId = scheduled.Value;
                                scheduledRound = round;
                                scheduledIsGhost = IsGhost(players, scheduled.Value);
                            }

                            if (round > 0 && scheduledOpponentId.HasValue && scheduledRound == round)
                            {
                                var oppId = scheduledOpponentId.Value;
                                activeCombatOpponentId = oppId;
                                activeCombatRound = round;
                                activeCombatWasGhost = scheduledIsGhost;
                                scheduledOpponentId = null;
                                scheduledRound = null;
                                scheduledIsGhost = false;

                                localPlayerIdAtCombatStart = localPlayerId;
                                localDurabilityAtStart = GetDurability(players, localPlayerId);
                                opponentDurabilityAtStart = GetDurability(players, oppId);
                                predamagePlayerId = null;
                                predamageAmount = null;
                                completionPending = false;
                                completionInterrupted = false;
                                completionSupportedSinceUtc = null;
                                completionStableSinceUtc = null;
                                restoredAtCombatStart = IsReconnectedStats(game);
                                clientHandleAtCombatStart = GetClientHandle(game);
                                gameStartGenAtCombatStart = Volatile.Read(ref gameStartGeneration);
                                playerTurnStartGenAtCombatStart = Volatile.Read(ref playerTurnStartGeneration);
                                BridgeLogger.Debug(string.Format(
                                    "LastOpponentTracker: combat start round={0} opp={1} ghost={2} localDur={3} oppDur={4}",
                                    round, oppId, activeCombatWasGhost, localDurabilityAtStart, opponentDurabilityAtStart));
                            }
                        }
                    }
                    else if (!completionPending && round > 0)
                    {
                        if (scheduled.HasValue && scheduled.Value > 0)
                        {
                            scheduledOpponentId = scheduled.Value;
                            scheduledRound = round;
                            scheduledIsGhost = IsGhost(players, scheduled.Value);
                        }
                        else if (scheduledRound != round)
                        {
                            scheduledOpponentId = null;
                            scheduledRound = null;
                            scheduledIsGhost = false;
                        }
                    }

                    wasCombat = combat;

                    var activeConfig = config.Clone();
                    if (activeConfig.Enabled && lastCompletedOpponentId.HasValue)
                    {
                        var requireEight = IsStandardBattlegrounds(game);
                        if (IsLeaderboardReadyLocked(players, requireEight, nowUtc))
                        {
                            var targetPlayer = players.FirstOrDefault(p => p.PlayerId == lastCompletedOpponentId.Value && !p.IsLocal);
                            if (targetPlayer != null)
                            {
                                decision = new RenderDecision
                                {
                                    ShouldShow = true,
                                    Config = activeConfig,
                                    Place = targetPlayer.Place,
                                    FallbackScheduled = scheduled.HasValue && scheduled.Value == targetPlayer.PlayerId,
                                    Outcome = lastCombatOutcome,
                                    Damage = lastCombatDamage
                                };
                            }
                        }
                    }
                }
            }

            // Never invoke WPF Dispatcher while holding lock(sync) to prevent AB-BA deadlocks.
            if (decision.ShouldShow)
            {
                var gen = capturedRenderGen;
                overlayRenderer.Update(
                    overlay,
                    decision.Config,
                    decision.Place,
                    decision.FallbackScheduled,
                    decision.Outcome,
                    decision.Damage,
                    () => Interlocked.Read(ref renderGeneration) == gen && Enabled);
            }
            else
            {
                overlayRenderer.Hide(overlay);
            }
        }

        private void BeginCombatCompletionLocked(bool interrupted)
        {
            if (!activeCombatOpponentId.HasValue)
                return;

            if (!completionPending)
            {
                completionPending = true;
                completionSupportedSinceUtc = null;
                completionStableSinceUtc = null;
            }
            completionInterrupted |= interrupted;
        }

        private void TryFinalizeActiveCombatLocked(object game, List<LeaderboardEntry> players, int round, DateTime nowUtc)
        {
            if (!activeCombatOpponentId.HasValue)
            {
                ResetCompletionGateLocked();
                return;
            }

            var opponentId = activeCombatOpponentId.Value;
            var currentClientHandle = GetClientHandle(game);
            var currentGameStartGen = Volatile.Read(ref gameStartGeneration);
            var stateRestoredDuringCombat = gameStartGenAtCombatStart != currentGameStartGen
                || (!restoredAtCombatStart && IsReconnectedStats(game))
                || (clientHandleAtCombatStart.HasValue && currentClientHandle.HasValue && clientHandleAtCombatStart.Value != currentClientHandle.Value);

            if (stateRestoredDuringCombat)
                completionInterrupted = true;

            var localDurability = GetDurability(players, localPlayerIdAtCombatStart);
            var opponentDurability = GetDurability(players, opponentId);
            var resultStateReady = localDurability.HasValue && (activeCombatWasGhost || opponentDurability.HasValue);
            var roundAdvanced = activeCombatRound.HasValue && round > activeCombatRound.Value;
            var completionStateReady = stateRestoredDuringCombat
                || roundAdvanced
                || playerTurnStartGenAtCombatStart != Volatile.Read(ref playerTurnStartGeneration);

            if (!completionSupportedSinceUtc.HasValue)
                completionSupportedSinceUtc = nowUtc;

            if (!completionStateReady)
            {
                completionStableSinceUtc = null;
                if (nowUtc - completionSupportedSinceUtc.Value >= CompletionStateTimeout)
                {
                    ClearActiveCombatLocked();
                }
                return;
            }

            if (!completionStableSinceUtc.HasValue)
            {
                completionStableSinceUtc = nowUtc;
                return;
            }

            var elapsed = nowUtc - completionStableSinceUtc.Value;
            var ready = resultStateReady ? elapsed >= ResultDelay : elapsed >= MissingDataTimeout;
            if (!ready)
                return;

            if (completionInterrupted)
            {
                predamagePlayerId = null;
                predamageAmount = null;
            }

            var wonLastCombat = (completionInterrupted || activeCombatWasGhost)
                && DidLocalPlayerWinLastCombat(game, players, localPlayerIdAtCombatStart);
            var result = ComputeOutcomeAndDamage(
                localPlayerIdAtCombatStart,
                opponentId,
                localDurabilityAtStart,
                localDurability,
                opponentDurabilityAtStart,
                opponentDurability,
                activeCombatWasGhost,
                predamagePlayerId,
                predamageAmount,
                wonLastCombat,
                completionStateReady && resultStateReady);

            var combatRound = activeCombatRound ?? 0;
            if (opponentId > 0 && combatRound > 0 && lastCountedRound != combatRound)
            {
                lastCountedRound = combatRound;
                lastCompletedOpponentId = opponentId;
                lastCombatOutcome = result.Outcome;
                lastCombatDamage = result.ExactDamage;
                BridgeLogger.Debug(string.Format(
                    "LastOpponentTracker: combat finalized round={0} opp={1} outcome={2} dmg={3} interrupted={4} localDur={5}->{6} oppDur={7}->{8} predmg={9}:{10}",
                    combatRound,
                    opponentId,
                    result.Outcome,
                    result.ExactDamage?.ToString() ?? "null",
                    completionInterrupted,
                    localDurabilityAtStart?.ToString() ?? "null",
                    localDurability?.ToString() ?? "null",
                    opponentDurabilityAtStart?.ToString() ?? "null",
                    opponentDurability?.ToString() ?? "null",
                    predamagePlayerId?.ToString() ?? "null",
                    predamageAmount?.ToString() ?? "null"));
            }

            ClearActiveCombatLocked();
        }

        internal static CombatSnapshotResult ComputeOutcomeAndDamage(
            int localPlayerId,
            int opponentPlayerId,
            int? localStartDurability,
            int? localEndDurability,
            int? opponentStartDurability,
            int? opponentEndDurability,
            bool opponentWasGhost,
            int? recordedDamagedPlayerId,
            int? recordedExactDamage,
            bool localWonLastCombatFlag,
            bool assumeDrawIfNoDamage)
        {
            if (localPlayerId <= 0 || opponentPlayerId <= 0)
                return new CombatSnapshotResult { Outcome = CombatOutcome.Unknown, ExactDamage = null };

            var localLoss = GetDurabilityLoss(localStartDurability, localEndDurability);
            var opponentLoss = opponentWasGhost ? (int?)null : GetDurabilityLoss(opponentStartDurability, opponentEndDurability);

            CombatOutcome outcome;
            if (localLoss > 0 && !(opponentLoss > 0))
                outcome = CombatOutcome.Loss;
            else if (opponentLoss > 0 && !(localLoss > 0))
                outcome = CombatOutcome.Win;
            else if (recordedDamagedPlayerId == localPlayerId)
                outcome = CombatOutcome.Loss;
            else if (recordedDamagedPlayerId == opponentPlayerId)
                outcome = CombatOutcome.Win;
            else if (localWonLastCombatFlag)
                outcome = CombatOutcome.Win;
            else
                outcome = assumeDrawIfNoDamage
                    && localLoss == 0
                    && (opponentLoss == 0 || (opponentWasGhost && !localWonLastCombatFlag))
                    ? CombatOutcome.Draw
                    : CombatOutcome.Unknown;

            int? damage = null;
            if (outcome == CombatOutcome.Draw)
                damage = 0;
            else if (outcome == CombatOutcome.Win && recordedDamagedPlayerId == opponentPlayerId)
                damage = Positive(recordedExactDamage) ?? Positive(opponentLoss);
            else if (outcome == CombatOutcome.Win)
                damage = Positive(opponentLoss);
            else if (outcome == CombatOutcome.Loss && recordedDamagedPlayerId == localPlayerId)
                damage = Positive(recordedExactDamage) ?? Positive(localLoss);
            else if (outcome == CombatOutcome.Loss)
                damage = Positive(localLoss);

            return new CombatSnapshotResult { Outcome = outcome, ExactDamage = damage };
        }

        private bool PrepareMatchStateLocked(object game, DateTime nowUtc)
        {
            GetGameHandles(game, out var metaHandle, out var statsHandle);
            var hasConflict = metaHandle.HasValue && statsHandle.HasValue && metaHandle.Value != statsHandle.Value;
            var available = metaHandle ?? statsHandle;

            if (!hasMatchState)
            {
                if (hasConflict)
                    return false;
                InitializeNewMatchLocked(available);
                return true;
            }

            if (hasConflict)
            {
                if (sawMenu && currentGameHandle.HasValue && metaHandle == currentGameHandle && statsHandle.HasValue && statsHandle != currentGameHandle)
                {
                    InitializeNewMatchLocked(statsHandle);
                    return true;
                }
                if (!sawMenu && currentGameHandle.HasValue && (metaHandle == currentGameHandle || statsHandle == currentGameHandle))
                {
                    identityWaitingSinceUtc = null;
                    return true;
                }
                return false;
            }

            var currentGen = Volatile.Read(ref gameStartGeneration);
            var genChanged = currentGen != gameStartGenAtMatchIdentity;
            if ((!currentGameHandle.HasValue || !available.HasValue) && (sawMenu || genChanged))
            {
                if (!identityWaitingSinceUtc.HasValue)
                {
                    identityWaitingSinceUtc = nowUtc;
                    return false;
                }
                if (IsReconnectedStats(game))
                {
                    sawMenu = false;
                    identityWaitingSinceUtc = null;
                    gameStartGenAtMatchIdentity = currentGen;
                    if (available.HasValue)
                        currentGameHandle = available;
                    return true;
                }
                var waitElapsed = nowUtc - identityWaitingSinceUtc.Value;
                if (!genChanged && waitElapsed >= TimeSpan.FromSeconds(1))
                {
                    sawMenu = false;
                    identityWaitingSinceUtc = null;
                    if (available.HasValue)
                        currentGameHandle = available;
                    return true;
                }
                if (genChanged && waitElapsed >= ReconnectIdentityTimeout)
                {
                    InitializeNewMatchLocked(available);
                    return true;
                }
                return false;
            }

            identityWaitingSinceUtc = null;
            if (sawMenu)
            {
                if (currentGameHandle.HasValue && available.Value == currentGameHandle.Value)
                {
                    sawMenu = false;
                    gameStartGenAtMatchIdentity = currentGen;
                    return true;
                }
                InitializeNewMatchLocked(available);
                return true;
            }

            if (available.HasValue && currentGameHandle.HasValue && available.Value != currentGameHandle.Value)
            {
                InitializeNewMatchLocked(available);
                return true;
            }

            if (available.HasValue)
                currentGameHandle = available;
            gameStartGenAtMatchIdentity = currentGen;
            return true;
        }

        private void InitializeNewMatchLocked(uint? handle)
        {
            ResetMatchStateLocked();
            hasMatchState = true;
            currentGameHandle = handle;
            gameStartGenAtMatchIdentity = Volatile.Read(ref gameStartGeneration);
            BridgeLogger.Debug("LastOpponentTracker: initialized solo BG match handle=" + (handle?.ToString() ?? "null"));
        }

        private void ResetMatchStateLocked()
        {
            hasMatchState = false;
            currentGameHandle = null;
            sawMenu = false;
            identityWaitingSinceUtc = null;
            standardLobbyInitialized = false;
            partialLobbySig = null;
            partialLobbySinceUtc = DateTime.MinValue;
            wasCombat = null;
            scheduledOpponentId = null;
            scheduledRound = null;
            scheduledIsGhost = false;
            lastCountedRound = null;
            lastCompletedOpponentId = null;
            lastCombatOutcome = CombatOutcome.Unknown;
            lastCombatDamage = null;
            ClearActiveCombatLocked();
        }

        private void ClearActiveCombatLocked()
        {
            activeCombatOpponentId = null;
            activeCombatRound = null;
            activeCombatWasGhost = false;
            localPlayerIdAtCombatStart = 0;
            localDurabilityAtStart = null;
            opponentDurabilityAtStart = null;
            predamagePlayerId = null;
            predamageAmount = null;
            restoredAtCombatStart = false;
            clientHandleAtCombatStart = null;
            gameStartGenAtCombatStart = Volatile.Read(ref gameStartGeneration);
            playerTurnStartGenAtCombatStart = Volatile.Read(ref playerTurnStartGeneration);
            ResetCompletionGateLocked();
        }

        private void ResetCompletionGateLocked()
        {
            completionPending = false;
            completionInterrupted = false;
            completionSupportedSinceUtc = null;
            completionStableSinceUtc = null;
        }

        private bool IsLeaderboardReadyLocked(List<LeaderboardEntry> players, bool requireEight, DateTime nowUtc)
        {
            if (players.Count == 0
                || !players.Any(p => p.IsLocal)
                || players.Any(p => p.Place < 1 || p.Place > 8)
                || players.Select(p => p.Place).Distinct().Count() != players.Count)
            {
                partialLobbySig = null;
                return false;
            }

            if (requireEight)
            {
                if (!standardLobbyInitialized)
                    standardLobbyInitialized = players.Count == 8;
                return standardLobbyInitialized;
            }

            var sig = string.Join(";", players.OrderBy(p => p.Place).Select(p => p.PlayerId + ":" + p.Place));
            if (!string.Equals(sig, partialLobbySig, StringComparison.Ordinal))
            {
                partialLobbySig = sig;
                partialLobbySinceUtc = nowUtc;
                return false;
            }

            return nowUtc - partialLobbySinceUtc >= PartialLobbyStabilityDelay;
        }

        // Optional GameEvents hooks (works identically even if GameEvents is removed in future HDT)
        internal void TrySubscribeGameEvents()
        {
            lock (subscribeLock)
            {
                var needGameStart = !subscribedGameStart;
                var needTurnStart = !subscribedTurnStart;
                var needPredamage = !subscribedPredamage;
                if (!needGameStart && !needTurnStart && !needPredamage)
                    return;

                if (subscribeAttempts >= MaxSubscribeAttempts || DateTime.UtcNow < nextSubscribeRetryUtc)
                    return;

                subscribeAttempts++;
                nextSubscribeRetryUtc = DateTime.UtcNow + SubscribeRetryInterval;

                try
                {
                    var gameEventsType = HdtReflection.FindType("Hearthstone_Deck_Tracker.API.GameEvents");
                    if (gameEventsType == null)
                        return;

                    if (needGameStart && SubscribeActionListNoArg(gameEventsType, "OnGameStart", nameof(OnHdtGameStart)))
                        subscribedGameStart = true;
                    if (needTurnStart && SubscribeActionListOneArg(gameEventsType, "OnTurnStart", nameof(OnHdtTurnStart)))
                        subscribedTurnStart = true;
                    if (needPredamage && SubscribeActionListOneArg(gameEventsType, "OnEntityWillTakeDamage", nameof(OnHdtPredamage)))
                        subscribedPredamage = true;

                    if (subscribedGameStart && subscribedTurnStart && subscribedPredamage)
                        BridgeLogger.Debug("LastOpponentTracker: subscribed to HDT GameEvents.");
                }
                catch (Exception ex)
                {
                    BridgeLogger.ErrorThrottled("lot_subscribe", "LastOpponentTracker.TrySubscribeGameEvents", ex, 30);
                }
            }
        }

        private bool SubscribeActionListNoArg(Type gameEventsType, string fieldName, string handlerMethodName)
        {
            var actionList = HdtReflection.GetStaticMemberValue(gameEventsType, fieldName);
            if (actionList == null)
                return false;

            var addMethod = actionList.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
            if (addMethod == null)
                return false;

            var method = typeof(LastOpponentTracker).GetMethod(handlerMethodName, BindingFlags.NonPublic | BindingFlags.Instance);
            var del = Delegate.CreateDelegate(typeof(Action), this, method);
            addMethod.Invoke(actionList, new object[] { del });
            return true;
        }

        private bool SubscribeActionListOneArg(Type gameEventsType, string fieldName, string handlerMethodName)
        {
            var actionList = HdtReflection.GetStaticMemberValue(gameEventsType, fieldName);
            if (actionList == null)
                return false;

            var addMethod = actionList.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
            if (addMethod == null)
                return false;

            var parameters = addMethod.GetParameters();
            if (parameters.Length != 1)
                return false;

            var actionType = parameters[0].ParameterType;
            var genericArgs = actionType.GetGenericArguments();
            if (genericArgs.Length != 1)
                return false;

            var param = Expression.Parameter(genericArgs[0], "arg");
            var handler = typeof(LastOpponentTracker).GetMethod(handlerMethodName, BindingFlags.NonPublic | BindingFlags.Instance);
            var call = Expression.Call(Expression.Constant(this), handler, Expression.Convert(param, typeof(object)));
            var compiled = Expression.Lambda(actionType, call, param).Compile();
            addMethod.Invoke(actionList, new object[] { compiled });
            return true;
        }

        private void OnHdtGameStart()
        {
            Interlocked.Increment(ref gameStartGeneration);
        }

        private void OnHdtTurnStart(object activePlayerObj)
        {
            if (string.Equals(Convert.ToString(activePlayerObj), "Player", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref playerTurnStartGeneration);
                Interlocked.Exchange(ref turnStartCompletionRequested, 1);
            }
        }

        private void OnHdtPredamage(object infoObj)
        {
            if (infoObj == null)
                return;

            try
            {
                var entity = HdtReflection.GetMemberValue(infoObj, "Entity");
                var valueObj = HdtReflection.GetMemberValue(infoObj, "Value");
                if (entity == null || !(valueObj is int damageValue) || damageValue <= 0)
                    return;

                var isHero = Convert.ToBoolean(HdtReflection.GetMemberValue(entity, "IsHero") ?? false);
                if (!isHero)
                    return;

                int localPid;
                int oppPid;
                int? combatRound;
                lock (sync)
                {
                    if (!activeCombatOpponentId.HasValue || activeCombatOpponentId.Value <= 0)
                        return;
                    localPid = localPlayerIdAtCombatStart;
                    oppPid = activeCombatOpponentId.Value;
                    combatRound = activeCombatRound;
                }

                var coreType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Core");
                var game = HdtReflection.GetStaticMemberValue(coreType, "Game");
                if (game == null)
                    return;

                var inCombat = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsCombatPhase") ?? false);
                if (!inCombat)
                    return;

                var playerIdTag = HdtReflection.ResolveGameTag("PLAYER_ID");
                var entityPid = playerIdTag != null ? GetTag(entity, playerIdTag) : 0;
                var playerObj = HdtReflection.GetMemberValue(game, "Player");
                var opponentObj = HdtReflection.GetMemberValue(game, "Opponent");
                var playerCtrlId = playerObj != null ? Convert.ToInt32(HdtReflection.GetMemberValue(playerObj, "Id") ?? 0) : 0;
                var oppCtrlId = opponentObj != null ? Convert.ToInt32(HdtReflection.GetMemberValue(opponentObj, "Id") ?? 0) : 0;

                var isLocal = entityPid == localPid
                    || (playerCtrlId > 0 && Convert.ToBoolean(HdtReflection.InvokeMethod(entity, "IsControlledBy", playerCtrlId) ?? false));
                var isOpp = !isLocal && (entityPid == oppPid
                    || (oppCtrlId > 0 && (entityPid == oppCtrlId || Convert.ToBoolean(HdtReflection.InvokeMethod(entity, "IsControlledBy", oppCtrlId) ?? false))));

                if (isLocal || isOpp)
                {
                    lock (sync)
                    {
                        if (activeCombatOpponentId == oppPid && activeCombatRound == combatRound)
                        {
                            predamagePlayerId = isLocal ? localPid : oppPid;
                            predamageAmount = damageValue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeLogger.ErrorThrottled("lot_predamage", "LastOpponentTracker.OnHdtPredamage", ex, 15);
            }
        }

        private static bool IsSupportedSoloMatch(object game, bool isInMenu)
        {
            if (isInMenu)
                return false;

            var isDuos = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsDuosMatch") ?? false);
            if (isDuos)
                return false;

            var soloVal = HdtReflection.GetMemberValue(game, "IsBattlegroundsSoloMatch");
            if (soloVal is bool isSolo)
                return isSolo;

            return Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsMatch") ?? false);
        }

        private static bool IsStandardBattlegrounds(object game)
        {
            var gameType = Convert.ToString(HdtReflection.GetMemberValue(game, "CurrentGameType"));
            return string.Equals(gameType, "GT_BATTLEGROUNDS", StringComparison.Ordinal);
        }

        private static bool IsReconnectedStats(object game)
        {
            var stats = HdtReflection.GetMemberValue(game, "CurrentGameStats");
            return stats != null && Convert.ToBoolean(HdtReflection.GetMemberValue(stats, "IsReconnect") ?? false);
        }

        private static bool HasDefinitiveMatchResult(object game)
        {
            var stats = HdtReflection.GetMemberValue(game, "CurrentGameStats");
            if (stats == null)
                return false;
            var result = Convert.ToString(HdtReflection.GetMemberValue(stats, "Result"));
            return !string.IsNullOrEmpty(result) && !string.Equals(result, "None", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetTurnNumber(object game)
        {
            var val = HdtReflection.InvokeMethod(game, "GetTurnNumber");
            return val is int i ? i : 0;
        }

        private static int GetLocalPlayerId(object game)
        {
            var playerIdTag = HdtReflection.ResolveGameTag("PLAYER_ID");
            var playerEntity = HdtReflection.GetMemberValue(game, "PlayerEntity");
            if (playerEntity != null && playerIdTag != null)
            {
                var pid = GetTag(playerEntity, playerIdTag);
                if (pid > 0)
                    return pid;
            }
            var player = HdtReflection.GetMemberValue(game, "Player");
            return player != null ? Convert.ToInt32(HdtReflection.GetMemberValue(player, "Id") ?? 0) : 0;
        }

        private static int? GetScheduledOpponentPlayerId(object game)
        {
            var nextOppTag = HdtReflection.ResolveGameTag("NEXT_OPPONENT_PLAYER_ID");
            var playerEntity = HdtReflection.GetMemberValue(game, "PlayerEntity");
            if (playerEntity == null || nextOppTag == null)
                return null;
            var id = GetTag(playerEntity, nextOppTag);
            return id > 0 ? (int?)id : null;
        }

        private static void GetGameHandles(object game, out uint? metaHandle, out uint? statsHandle)
        {
            metaHandle = ReadPositiveUInt(HdtReflection.GetMemberValue(HdtReflection.GetMemberValue(game, "MetaData"), "ServerInfo"), "GameHandle");
            statsHandle = ReadPositiveUInt(HdtReflection.GetMemberValue(HdtReflection.GetMemberValue(game, "CurrentGameStats"), "ServerInfo"), "GameHandle");
        }

        private static long? GetClientHandle(object game)
        {
            var meta = ReadPositiveLong(HdtReflection.GetMemberValue(HdtReflection.GetMemberValue(game, "MetaData"), "ServerInfo"), "ClientHandle");
            if (meta.HasValue)
                return meta;
            return ReadPositiveLong(HdtReflection.GetMemberValue(HdtReflection.GetMemberValue(game, "CurrentGameStats"), "ServerInfo"), "ClientHandle");
        }

        private static bool DidLocalPlayerWinLastCombat(object game, List<LeaderboardEntry> players, int localPlayerId)
        {
            var wonTag = HdtReflection.ResolveGameTag("BACON_WON_LAST_COMBAT");
            if (wonTag != null)
            {
                var playerEntity = HdtReflection.GetMemberValue(game, "PlayerEntity");
                if (playerEntity != null && GetTag(playerEntity, wonTag) > 0)
                    return true;
            }

            var localEntry = players.FirstOrDefault(p => p.PlayerId == localPlayerId);
            return localEntry != null && localEntry.WonLastCombat;
        }

        private static List<LeaderboardEntry> ReadLeaderboardPlayers(object game)
        {
            var result = new List<LeaderboardEntry>();
            var entities = HdtReflection.GetMemberValue(game, "Entities") as IDictionary;
            if (entities == null)
                return result;

            var playerIdTag = HdtReflection.ResolveGameTag("PLAYER_ID");
            var placeTag = HdtReflection.ResolveGameTag("PLAYER_LEADERBOARD_PLACE");
            var armorTag = HdtReflection.ResolveGameTag("ARMOR");
            var wonTag = HdtReflection.ResolveGameTag("BACON_WON_LAST_COMBAT");
            if (playerIdTag == null || placeTag == null)
                return result;

            var localPid = GetLocalPlayerId(game);
            var seen = new HashSet<int>();

            var values = SnapshotValues(entities);
            foreach (var entity in values)
            {
                if (entity == null)
                    continue;
                if (!Convert.ToBoolean(HdtReflection.GetMemberValue(entity, "IsHero") ?? false))
                    continue;

                var pid = GetTag(entity, playerIdTag);
                var place = GetTag(entity, placeTag);
                if (pid <= 0 || place < 1 || place > 8 || !seen.Add(pid))
                    continue;

                var health = Convert.ToInt32(HdtReflection.GetMemberValue(entity, "Health") ?? 0);
                var armor = armorTag != null ? GetTag(entity, armorTag) : 0;
                var won = wonTag != null && GetTag(entity, wonTag) > 0;

                result.Add(new LeaderboardEntry
                {
                    PlayerId = pid,
                    Place = place,
                    IsLocal = pid == localPid,
                    Health = health,
                    Armor = armor,
                    WonLastCombat = won
                });
            }

            result.Sort((a, b) => a.Place.CompareTo(b.Place));
            return result;
        }

        private static List<object> SnapshotValues(IDictionary dict)
        {
            var list = new List<object>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                list.Clear();
                try
                {
                    foreach (DictionaryEntry entry in dict)
                        list.Add(entry.Value);
                    return list;
                }
                catch (InvalidOperationException)
                {
                    Thread.Sleep(5);
                }
            }
            return list;
        }

        private static int GetTag(object entity, object gameTagValue)
        {
            var val = HdtReflection.InvokeMethod(entity, "GetTag", gameTagValue);
            return val is int i ? i : 0;
        }

        private static int? GetDurability(List<LeaderboardEntry> players, int playerId)
        {
            var p = players.FirstOrDefault(x => x.PlayerId == playerId);
            return p == null ? (int?)null : p.Durability;
        }

        private static bool IsGhost(List<LeaderboardEntry> players, int playerId)
        {
            var p = players.FirstOrDefault(x => x.PlayerId == playerId);
            return p != null && p.Health <= 0;
        }

        private static int? GetDurabilityLoss(int? before, int? after)
        {
            if (!before.HasValue || !after.HasValue)
                return null;
            return after.Value > before.Value ? 0 : before.Value - after.Value;
        }

        private static int? Positive(int? value) => value.HasValue && value.Value > 0 ? value : null;

        private static uint? ReadPositiveUInt(object target, string member)
        {
            if (target == null)
                return null;
            var val = HdtReflection.GetMemberValue(target, member);
            if (val == null)
                return null;
            try
            {
                var u = Convert.ToUInt32(val);
                return u > 0 ? (uint?)u : null;
            }
            catch
            {
                return null;
            }
        }

        private static long? ReadPositiveLong(object target, string member)
        {
            if (target == null)
                return null;
            var val = HdtReflection.GetMemberValue(target, member);
            if (val == null)
                return null;
            try
            {
                var l = Convert.ToInt64(val);
                return l > 0 ? (long?)l : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
