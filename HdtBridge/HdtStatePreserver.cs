using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace HdtBridge
{
    internal sealed class HdtCapabilityReport
    {
        public string HdtVersion { get; set; } = string.Empty;
        public int ReadyCount { get; set; }
        public int TotalCount { get; set; }
        public List<string> ReadyItems { get; } = new List<string>();
        public List<string> MissingItems { get; } = new List<string>();
        public bool InMatch { get; set; }
        public bool IsBattlegrounds { get; set; }
        public bool PendingRestore { get; set; }
        public string LastAction { get; set; } = string.Empty;
    }

    internal sealed class HdtSaveResult
    {
        public bool Saved { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string GameHandle { get; set; } = string.Empty;
        public int Boards { get; set; }
        public int Deities { get; set; }
        public int TavernEntries { get; set; }
        public int TripleEntries { get; set; }
        public int DeadForCount { get; set; }
        public bool BobsBuddy { get; set; }
        public int TechLevels { get; set; }
        public int Races { get; set; }
    }

    internal sealed class HdtStatePreserver : IDisposable
    {
        private sealed class HeroMapSnapshot
        {
            public int EntityId;
            public int PlayerId;
            public string CardId;
            public Dictionary<int, int> Values;

            public HeroMapSnapshot Clone()
            {
                return new HeroMapSnapshot
                {
                    EntityId = EntityId,
                    PlayerId = PlayerId,
                    CardId = CardId,
                    Values = Values != null ? new Dictionary<int, int>(Values) : new Dictionary<int, int>()
                };
            }
        }

        private sealed class CurrentEntityIdentity
        {
            public bool IsHero;
            public int PlayerId;
            public string CardId;
        }

        private sealed class LeaderboardRaceSnapshot
        {
            public int Race;
            public int Count;
        }

        private sealed class PendingSnapshot
        {
            public string GameHandle;
            public Guid SavedGameId;
            public DateTime StartTime;
            public DateTime SavedUtc;
            public DateTime DeadlineUtc;
            public DateTime? InMenuSinceUtc;
            public int SavedTurn;

            public Dictionary<object, object> Boards = new Dictionary<object, object>();
            public Dictionary<object, object> Deities = new Dictionary<object, object>();
            public List<HeroMapSnapshot> TavernUpTurns = new List<HeroMapSnapshot>();
            public List<HeroMapSnapshot> TriplesByTier = new List<HeroMapSnapshot>();
            public List<int> UniqueDeadPlayers = new List<int>();
            public List<int> DeadTracker = new List<int>();
            public object BobsBuddyInput;
            public Dictionary<string, int> TechLevelByHeroCardId = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<int, int> TechLevelByPlayerId = new Dictionary<int, int>();
            public Dictionary<string, LeaderboardRaceSnapshot> RaceByHeroCardId = new Dictionary<string, LeaderboardRaceSnapshot>(StringComparer.Ordinal);

            public bool CoreStateRestored;
            public bool WatchersRestarted;
            public bool BobsBuddyRestored;
            public int ReinforcementPassesRemaining;
            public DateTime NextReinforcementUtc;
        }

        private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan InMenuAbortDelay = TimeSpan.FromSeconds(15);

        private readonly object sync = new object();
        private readonly Timer monitorTimer;
        private int pollInProgress;
        private int disposed;
        private PendingSnapshot pending;
        private string activeRaceGuardGameHandle;
        private Dictionary<string, LeaderboardRaceSnapshot> activeRaceGuardMap;
        private Dictionary<string, int> activeTechGuardByCardId;
        private bool teamsWatcherHooked;
        private string lastAction = "ready";

        public HdtStatePreserver()
        {
            monitorTimer = new Timer(_ => PollTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try
            {
                monitorTimer.Change(Timeout.Infinite, Timeout.Infinite);
                monitorTimer.Dispose();
            }
            catch
            {
                // Ignore timer disposal race.
            }
        }

        public HdtCapabilityReport ProbeCapabilities()
        {
            var report = new HdtCapabilityReport
            {
                TotalCount = 7
            };

            lock (sync)
            {
                report.PendingRestore = pending != null;
                report.LastAction = lastAction;
            }

            var hdtAsm = HdtReflection.FindHdtAssembly();
            report.HdtVersion = GetHdtVersion(hdtAsm);

            if (!HdtReflection.TryGetHdtCoreObjects(out var game, out var overlay, out var initializing))
            {
                report.MissingItems.Add(initializing ? "initializing" : "core");
                report.LastAction = initializing ? "hdt_initializing" : "hdt_not_ready";
                return report;
            }

            var coreType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Core");
            var gameType = game?.GetType() ?? HdtReflection.FindType("Hearthstone_Deck_Tracker.Hearthstone.GameV2");
            var overlayType = overlay?.GetType() ?? HdtReflection.FindType("Hearthstone_Deck_Tracker.Windows.OverlayWindow");
            var entityType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity");

            // 1. Core / Game / Overlay
            var coreOk = coreType != null
                && game != null
                && overlay != null
                && HdtReflection.HasMember(gameType, "IsBattlegroundsMatch", false)
                && HdtReflection.HasMember(gameType, "Entities", false)
                && HdtReflection.HasMember(gameType, "MetaData", false)
                && HdtReflection.HasMember(gameType, "CurrentGameStats", false)
                && HdtReflection.HasMethod(gameType, "GetTurnNumber", false, 0);
            RecordCheck(report, "core", coreOk);

            // 2. BattlegroundsBoardState
            var boardStateType = HdtReflection.GetMemberType(gameType, "_battlegroundsBoardState", false)
                ?? HdtReflection.FindType("Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsBoardState");
            var boardOk = gameType != null
                && HdtReflection.HasMember(gameType, "_battlegroundsBoardState", false)
                && boardStateType != null
                && (HdtReflection.HasMember(boardStateType, "LastKnownBoardState", false)
                    || HdtReflection.HasMember(boardStateType, "<LastKnownBoardState>k__BackingField", false));
            RecordCheck(report, "boardState", boardOk);

            // 3. BattlegroundsDeityState
            var deityStateType = HdtReflection.GetMemberType(gameType, "_battlegroundsDeityState", false)
                ?? HdtReflection.FindType("Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsDeityState");
            var deityOk = gameType != null
                && HdtReflection.HasMember(gameType, "_battlegroundsDeityState", false)
                && deityStateType != null
                && (HdtReflection.HasMember(deityStateType, "LastKnownDeity", false)
                    || HdtReflection.HasMember(deityStateType, "<LastKnownDeity>k__BackingField", false));
            RecordCheck(report, "deityState", deityOk);

            // 4. Hero tavern-up turns & triples by tier
            var tavernTriplesOk = gameType != null
                && HdtReflection.HasMember(gameType, "_battlegroundsHeroLatestTavernUpTurn", false)
                && HdtReflection.HasMember(gameType, "_battlegroundsHeroTriplesByTier", false);
            RecordCheck(report, "tavernTriples", tavernTriplesOk);

            // 5. OpponentDeadForTracker
            var deadForType = HdtReflection.FindType("Hearthstone_Deck_Tracker.OpponentDeadForTracker");
            var deadForOk = deadForType != null
                && HdtReflection.HasMember(deadForType, "_uniqueDeadPlayers", true)
                && HdtReflection.HasMember(deadForType, "_deadTracker", true)
                && overlayType != null
                && HdtReflection.HasMethod(overlayType, "UpdateOpponentDeadForTurns", false, 1);
            RecordCheck(report, "deadFor", deadForOk);

            // 6. BobsBuddyInvoker
            var bobsType = HdtReflection.FindType("Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker");
            var bobsOk = bobsType != null
                && HdtReflection.FindMethod(bobsType, "GetInstance", true, typeof(Guid), typeof(int), typeof(bool)) != null
                && HdtReflection.HasMember(bobsType, "_input", false);
            RecordCheck(report, "bobsBuddy", bobsOk);

            // 7. Leaderboard tier & tribe icons + watchers
            var watchersType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Hearthstone.Watchers");
            var lbWatcher = HdtReflection.GetStaticMemberValue(watchersType, "BattlegroundsLeaderboardWatcher");
            var lbTeamsWatcher = HdtReflection.GetStaticMemberValue(watchersType, "BattlegroundsLeaderboardTeamsWatcher");
            var tilesVm = HdtReflection.GetMemberValue(overlay, "BattlegroundsLeaderboardTilesVM");
            var tilesVmType = tilesVm?.GetType()
                ?? HdtReflection.FindType("Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.BattlegroundsLeaderboardTilesViewModel");
            var leaderboardOk = watchersType != null
                && lbWatcher != null
                && lbTeamsWatcher != null
                && HdtReflection.HasMethod(lbWatcher.GetType(), "Run", false, 0)
                && HdtReflection.HasMethod(lbTeamsWatcher.GetType(), "Run", false, 0)
                && tilesVm != null
                && HdtReflection.HasMember(tilesVmType, "_teams", false)
                && HdtReflection.HasMethod(tilesVmType, "Update", false, 0)
                && overlayType != null
                && HdtReflection.HasMethod(overlayType, "UpdateLeaderboardTileInfos", false, 0)
                && entityType != null
                && HdtReflection.HasMethod(entityType, "SetTag", false, 2)
                && HdtReflection.ResolveGameTag("PLAYER_TECH_LEVEL") != null;
            RecordCheck(report, "leaderboard", leaderboardOk);

            if (game != null)
            {
                try
                {
                    var isInMenu = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsInMenu") ?? true);
                    var isBg = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsMatch") ?? false);
                    var stats = HdtReflection.GetMemberValue(game, "CurrentGameStats");
                    var ended = IsGameStatsEnded(stats);
                    report.InMatch = !isInMenu && stats != null && !ended;
                    report.IsBattlegrounds = isBg && report.InMatch;
                }
                catch
                {
                    // Non-fatal while HDT initializes.
                }
            }

            return report;
        }

        public HdtSaveResult SaveBeforeReconnect()
        {
            var result = new HdtSaveResult();
            try
            {
                if (!HdtReflection.TryGetHdtCoreObjects(out var game, out var overlay, out _))
                {
                    result.Reason = "hdt_not_ready";
                    return result;
                }

                var isBg = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsMatch") ?? false);
                if (!isBg)
                {
                    result.Reason = "not_battlegrounds";
                    return result;
                }

                var gameStats = HdtReflection.GetMemberValue(game, "CurrentGameStats");
                if (gameStats == null)
                {
                    result.Reason = "no_game_stats";
                    return result;
                }

                if (!TryGetGameHandle(game, out var gameHandle))
                {
                    result.Reason = "no_game_handle";
                    return result;
                }

                var startTimeObj = HdtReflection.GetMemberValue(gameStats, "StartTime");
                var startTime = startTimeObj is DateTime dt ? dt : DateTime.MinValue;
                var gameIdObj = HdtReflection.GetMemberValue(gameStats, "GameId");
                var gameId = gameIdObj is Guid guid ? guid : Guid.Empty;
                var turnObj = HdtReflection.InvokeMethod(game, "GetTurnNumber");
                var turn = turnObj is int t ? t : 0;

                var snap = new PendingSnapshot
                {
                    GameHandle = gameHandle,
                    SavedGameId = gameId,
                    StartTime = startTime,
                    SavedUtc = DateTime.UtcNow,
                    DeadlineUtc = DateTime.UtcNow + PendingTimeout,
                    SavedTurn = turn
                };

                // 1. Past opponent boards
                var boardState = HdtReflection.GetMemberValue(game, "_battlegroundsBoardState");
                var boardDict = GetDictionaryMember(boardState, "LastKnownBoardState", "<LastKnownBoardState>k__BackingField");
                foreach (var entry in SafeSnapshotDictionary(boardDict))
                    snap.Boards[entry.Key] = entry.Value;

                // 2. Deity state
                var deityState = HdtReflection.GetMemberValue(game, "_battlegroundsDeityState");
                var deityDict = GetDictionaryMember(deityState, "LastKnownDeity", "<LastKnownDeity>k__BackingField");
                foreach (var entry in SafeSnapshotDictionary(deityDict))
                    snap.Deities[entry.Key] = entry.Value;

                // Entities snapshot for mapping hero entity id <-> (PlayerId, CardId) and capturing PLAYER_TECH_LEVEL
                var entitiesDict = HdtReflection.GetMemberValue(game, "Entities") as IDictionary;
                var entityMetaById = new Dictionary<int, Tuple<int, string>>();
                var playerIdTag = HdtReflection.ResolveGameTag("PLAYER_ID");
                var techLevelTag = HdtReflection.ResolveGameTag("PLAYER_TECH_LEVEL");

                foreach (var entry in SafeSnapshotDictionary(entitiesDict))
                {
                    if (!(entry.Key is int entityId) || entry.Value == null)
                        continue;

                    var isHero = Convert.ToBoolean(HdtReflection.GetMemberValue(entry.Value, "IsHero") ?? false);
                    var cardId = HdtReflection.GetMemberValue(entry.Value, "CardId") as string;
                    var playerId = playerIdTag != null ? GetEntityTag(entry.Value, playerIdTag) : 0;

                    if (isHero || playerId > 0 || !string.IsNullOrEmpty(cardId))
                        entityMetaById[entityId] = Tuple.Create(playerId, cardId ?? string.Empty);

                    if (isHero && techLevelTag != null)
                    {
                        var tech = GetEntityTag(entry.Value, techLevelTag);
                        if (tech > 0)
                        {
                            if (!string.IsNullOrEmpty(cardId))
                            {
                                if (!snap.TechLevelByHeroCardId.TryGetValue(cardId, out var existingTech) || tech > existingTech)
                                    snap.TechLevelByHeroCardId[cardId] = tech;
                            }
                            if (playerId > 0)
                            {
                                if (!snap.TechLevelByPlayerId.TryGetValue(playerId, out var existingByPid) || tech > existingByPid)
                                    snap.TechLevelByPlayerId[playerId] = tech;
                            }
                        }
                    }
                }

                // 3. Tavern-up turns and triples by tier
                CaptureHeroIntMaps(
                    HdtReflection.GetMemberValue(game, "_battlegroundsHeroLatestTavernUpTurn") as IDictionary,
                    entityMetaById,
                    snap.TavernUpTurns);
                CaptureHeroIntMaps(
                    HdtReflection.GetMemberValue(game, "_battlegroundsHeroTriplesByTier") as IDictionary,
                    entityMetaById,
                    snap.TriplesByTier);

                // 4. OpponentDeadForTracker
                var deadForType = HdtReflection.FindType("Hearthstone_Deck_Tracker.OpponentDeadForTracker");
                if (deadForType != null)
                {
                    if (HdtReflection.GetStaticMemberValue(deadForType, "_uniqueDeadPlayers") is IEnumerable uniqueList)
                    {
                        foreach (var item in uniqueList)
                        {
                            if (item is int pid)
                                snap.UniqueDeadPlayers.Add(pid);
                        }
                    }
                    if (HdtReflection.GetStaticMemberValue(deadForType, "_deadTracker") is IEnumerable trackerList)
                    {
                        foreach (var item in trackerList)
                        {
                            if (item is int turnsDead)
                                snap.DeadTracker.Add(turnsDead);
                        }
                    }
                }

                // 5. Bob's Buddy combat input
                var isCombat = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsCombatPhase") ?? false);
                if (isCombat && gameId != Guid.Empty && turn >= 0)
                {
                    var bobsType = HdtReflection.FindType("Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker");
                    var getInstance = HdtReflection.FindMethod(bobsType, "GetInstance", true, typeof(Guid), typeof(int), typeof(bool));
                    if (getInstance != null)
                    {
                        var invoker = getInstance.Invoke(null, new object[] { gameId, turn, false });
                        if (invoker != null)
                            snap.BobsBuddyInput = HdtReflection.GetMemberValue(invoker, "_input");
                    }
                }

                // 6. Leaderboard tiles (DominantRace by HeroCardId + fallback tech levels from existing tiles)
                var tilesVm = HdtReflection.GetMemberValue(overlay, "BattlegroundsLeaderboardTilesVM");
                if (tilesVm != null)
                {
                    if (HdtReflection.GetMemberValue(tilesVm, "_teams") is IEnumerable teams)
                    {
                        foreach (var team in teams)
                        {
                            if (team == null)
                                continue;
                            if (!(HdtReflection.GetMemberValue(team, "Tiles") is IEnumerable tiles))
                                continue;
                            foreach (var tile in tiles)
                            {
                                if (tile == null)
                                    continue;
                                var heroCardId = HdtReflection.GetMemberValue(tile, "HeroCardId") as string;
                                if (string.IsNullOrEmpty(heroCardId))
                                    continue;
                                var domRaceObj = HdtReflection.GetMemberValue(tile, "DominantRace");
                                if (domRaceObj is int domRace)
                                {
                                    var domCountObj = HdtReflection.GetMemberValue(tile, "DominantRaceCount");
                                    var domCount = domCountObj is int dc ? dc : 0;
                                    snap.RaceByHeroCardId[heroCardId] = new LeaderboardRaceSnapshot
                                    {
                                        Race = domRace,
                                        Count = domCount
                                    };
                                }
                            }
                        }
                    }
                }

                // Ensure TeamsWatcher observer is registered once for DominantRace recovery
                EnsureLeaderboardTeamsWatcherHooked();

                // Merge with any existing pending snapshot and active guard maps for the SAME GameHandle
                // (e.g. if user reconnects again while HDT is still loading entities after an earlier reconnect).
                lock (sync)
                {
                    if (pending != null && string.Equals(pending.GameHandle, gameHandle, StringComparison.Ordinal))
                        MergePendingSnapshotInto(pending, snap);

                    if (string.Equals(activeRaceGuardGameHandle, gameHandle, StringComparison.Ordinal))
                    {
                        if (activeRaceGuardMap != null)
                        {
                            foreach (var kv in activeRaceGuardMap)
                            {
                                if (!snap.RaceByHeroCardId.ContainsKey(kv.Key))
                                    snap.RaceByHeroCardId[kv.Key] = kv.Value;
                            }
                        }
                        if (activeTechGuardByCardId != null)
                        {
                            foreach (var kv in activeTechGuardByCardId)
                            {
                                if (!snap.TechLevelByHeroCardId.TryGetValue(kv.Key, out var curTech) || kv.Value > curTech)
                                    snap.TechLevelByHeroCardId[kv.Key] = kv.Value;
                            }
                        }
                    }

                    pending = snap;
                    activeRaceGuardGameHandle = gameHandle;
                    activeRaceGuardMap = new Dictionary<string, LeaderboardRaceSnapshot>(snap.RaceByHeroCardId, StringComparer.Ordinal);
                    activeTechGuardByCardId = new Dictionary<string, int>(snap.TechLevelByHeroCardId, StringComparer.Ordinal);
                    lastAction = string.Format(
                        "saved handle={0} boards={1} tiers={2} races={3}",
                        gameHandle,
                        snap.Boards.Count,
                        snap.TechLevelByHeroCardId.Count,
                        snap.RaceByHeroCardId.Count);
                }

                TryChangeTimer(350, 350);

                result.Saved = true;
                result.Reason = "saved";
                result.GameHandle = gameHandle;
                result.Boards = snap.Boards.Count;
                result.Deities = snap.Deities.Count;
                result.TavernEntries = snap.TavernUpTurns.Count;
                result.TripleEntries = snap.TriplesByTier.Count;
                result.DeadForCount = snap.UniqueDeadPlayers.Count;
                result.BobsBuddy = snap.BobsBuddyInput != null;
                result.TechLevels = snap.TechLevelByHeroCardId.Count;
                result.Races = snap.RaceByHeroCardId.Count;
                return result;
            }
            catch (Exception ex)
            {
                BridgeLogger.Error("HdtStatePreserver.SaveBeforeReconnect", ex);
                result.Saved = false;
                result.Reason = "error:" + ex.GetType().Name;
                lock (sync)
                    lastAction = "save_error:" + ex.Message;
                return result;
            }
        }

        public void CancelPending()
        {
            lock (sync)
            {
                pending = null;
                lastAction = "cancelled";
            }
            TryChangeTimer(Timeout.Infinite, Timeout.Infinite);
        }

        public string NotifyReconnected()
        {
            PollTick();
            lock (sync)
                return lastAction;
        }

        private void PollTick()
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            if (Interlocked.CompareExchange(ref pollInProgress, 1, 0) != 0)
                return;

            try
            {
                PendingSnapshot snap;
                lock (sync)
                    snap = pending;

                if (snap == null)
                {
                    TryChangeTimer(Timeout.Infinite, Timeout.Infinite);
                    return;
                }

                var now = DateTime.UtcNow;
                if (now >= snap.DeadlineUtc)
                {
                    ClearPending("restore_timeout", clearGuard: false, expected: snap);
                    return;
                }

                if (!HdtReflection.TryGetHdtCoreObjects(out var game, out var overlay, out _))
                    return;

                var isInMenu = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsInMenu") ?? false);
                if (isInMenu)
                {
                    if (!snap.InMenuSinceUtc.HasValue)
                    {
                        snap.InMenuSinceUtc = now;
                        BridgeLogger.Debug("Restore waiting: HDT reports IsInMenu=true.");
                    }
                    else if (now - snap.InMenuSinceUtc.Value >= InMenuAbortDelay && !snap.CoreStateRestored)
                        ClearPending("returned_to_menu", clearGuard: true, expected: snap);
                    return;
                }
                snap.InMenuSinceUtc = null;

                var gameStats = HdtReflection.GetMemberValue(game, "CurrentGameStats");
                if (gameStats == null)
                {
                    BridgeLogger.DebugOnce("preserver_wait", "no_stats", "Restore waiting: CurrentGameStats is null.");
                    return;
                }

                var currentStartObj = HdtReflection.GetMemberValue(gameStats, "StartTime");
                var currentStart = currentStartObj is DateTime dt ? dt : DateTime.MinValue;
                var currentGameIdObj = HdtReflection.GetMemberValue(gameStats, "GameId");
                var currentGameId = currentGameIdObj is Guid gid ? gid : Guid.Empty;
                var isNewStatsSession = (currentGameId != Guid.Empty && snap.SavedGameId != Guid.Empty && currentGameId != snap.SavedGameId)
                    || currentStart > snap.StartTime;
                if (!isNewStatsSession)
                {
                    BridgeLogger.DebugOnce("preserver_wait", "same_start", "Restore waiting: waiting for new GameStats session after reconnect.");
                    return;
                }

                if (IsGameStatsEnded(gameStats))
                {
                    ClearPending("match_ended", clearGuard: true, expected: snap);
                    return;
                }

                if (!TryGetGameHandle(game, out var currentHandle))
                {
                    BridgeLogger.DebugOnce("preserver_wait", "no_handle", "Restore waiting: GameHandle not populated yet.");
                    return;
                }

                if (!string.Equals(currentHandle, snap.GameHandle, StringComparison.Ordinal))
                {
                    BridgeLogger.Info(string.Format("Restore aborted: GameHandle changed ({0} -> {1}).", snap.GameHandle, currentHandle));
                    ClearPending("different_match", clearGuard: true, expected: snap);
                    return;
                }

                var currentMode = Convert.ToString(HdtReflection.GetMemberValue(game, "CurrentMode"));
                if (!string.Equals(currentMode, "GAMEPLAY", StringComparison.OrdinalIgnoreCase))
                {
                    BridgeLogger.DebugOnce("preserver_wait", "mode_" + currentMode, "Restore waiting: CurrentMode=" + currentMode);
                    return;
                }

                var gameEntity = HdtReflection.GetMemberValue(game, "GameEntity");
                var turnObj = HdtReflection.InvokeMethod(game, "GetTurnNumber");
                var turn = turnObj is int t ? t : 0;
                if (gameEntity == null || turn <= 0)
                {
                    BridgeLogger.DebugOnce("preserver_wait", "entity_turn_" + turn, "Restore waiting: GameEntity or turn not ready (turn=" + turn + ").");
                    return;
                }

                var isBg = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsMatch") ?? false);
                if (!isBg)
                {
                    BridgeLogger.DebugOnce("preserver_wait", "not_bg", "Restore waiting: IsBattlegroundsMatch=false.");
                    return;
                }

                if (!snap.CoreStateRestored)
                {
                    ApplyRestore(game, overlay, gameStats, snap);
                    snap.CoreStateRestored = true;
                    snap.ReinforcementPassesRemaining = 3;
                    snap.NextReinforcementUtc = now.AddMilliseconds(1200);
                    string actionSnapshot;
                    lock (sync)
                        actionSnapshot = lastAction;
                    BridgeLogger.Info("Restore pass #1 completed: " + actionSnapshot);
                    return;
                }

                if (snap.ReinforcementPassesRemaining > 0 && now >= snap.NextReinforcementUtc)
                {
                    ApplyRestore(game, overlay, gameStats, snap);
                    snap.ReinforcementPassesRemaining--;
                    snap.NextReinforcementUtc = now.AddMilliseconds(1800);
                    string actionSnapshot;
                    lock (sync)
                        actionSnapshot = lastAction;
                    BridgeLogger.Debug("Restore reinforcement pass done (remaining=" + snap.ReinforcementPassesRemaining + "): " + actionSnapshot);
                    if (snap.ReinforcementPassesRemaining <= 0)
                        ClearPending(actionSnapshot, clearGuard: false, expected: snap);
                }
            }
            catch (Exception ex)
            {
                BridgeLogger.ErrorThrottled("preserver_poll", "HdtStatePreserver.PollTick", ex, 10);
                lock (sync)
                    lastAction = "restore_error:" + ex.GetType().Name + ":" + ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref pollInProgress, 0);
            }
        }

        private void TryChangeTimer(int dueTime, int period)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;
            try
            {
                monitorTimer.Change(dueTime, period);
            }
            catch (ObjectDisposedException)
            {
                // Ignore if disposed concurrently.
            }
        }

        private void ApplyRestore(object game, object overlay, object gameStats, PendingSnapshot snap)
        {
            var restoredBoards = 0;
            var restoredDeities = 0;
            var restoredTavern = 0;
            var restoredTriples = 0;
            var restoredTechTags = 0;

            // 1. Restore LastKnownBoardState
            var boardState = HdtReflection.GetMemberValue(game, "_battlegroundsBoardState");
            var boardDict = GetDictionaryMember(boardState, "LastKnownBoardState", "<LastKnownBoardState>k__BackingField");
            if (boardDict != null && snap.Boards.Count > 0)
            {
                foreach (var kv in snap.Boards)
                {
                    if (!boardDict.Contains(kv.Key))
                    {
                        boardDict[kv.Key] = kv.Value;
                        restoredBoards++;
                    }
                }
            }

            // 2. Restore LastKnownDeity
            var deityState = HdtReflection.GetMemberValue(game, "_battlegroundsDeityState");
            var deityDict = GetDictionaryMember(deityState, "LastKnownDeity", "<LastKnownDeity>k__BackingField");
            if (deityDict != null && snap.Deities.Count > 0)
            {
                foreach (var kv in snap.Deities)
                {
                    if (!deityDict.Contains(kv.Key))
                    {
                        deityDict[kv.Key] = kv.Value;
                        restoredDeities++;
                    }
                }
            }

            // Build lookup for reconnected entities and heroes by PLAYER_ID and CardId
            var entitiesDict = HdtReflection.GetMemberValue(game, "Entities") as IDictionary;
            var entityByPlayerId = new Dictionary<int, int>();
            var entityByCardId = new Dictionary<string, int>(StringComparer.Ordinal);
            var currentEntityMetaById = new Dictionary<int, CurrentEntityIdentity>();
            var playerIdTag = HdtReflection.ResolveGameTag("PLAYER_ID");
            var techLevelTag = HdtReflection.ResolveGameTag("PLAYER_TECH_LEVEL");

            foreach (var entry in SafeSnapshotDictionary(entitiesDict))
            {
                if (!(entry.Key is int entityId) || entry.Value == null)
                    continue;

                var isHero = Convert.ToBoolean(HdtReflection.GetMemberValue(entry.Value, "IsHero") ?? false);
                var cardId = HdtReflection.GetMemberValue(entry.Value, "CardId") as string;
                var playerId = playerIdTag != null ? GetEntityTag(entry.Value, playerIdTag) : 0;

                currentEntityMetaById[entityId] = new CurrentEntityIdentity
                {
                    IsHero = isHero,
                    PlayerId = playerId,
                    CardId = cardId ?? string.Empty
                };

                if (!isHero)
                    continue;

                if (playerId > 0)
                    entityByPlayerId[playerId] = entityId;
                if (!string.IsNullOrEmpty(cardId))
                    entityByCardId[cardId] = entityId;

                // 6a. Backfill missing PLAYER_TECH_LEVEL tag on hero entity so BattlegroundsLeaderboardTilesVM renders tier icon
                if (techLevelTag != null)
                {
                    var currentTech = GetEntityTag(entry.Value, techLevelTag);
                    if (currentTech <= 0)
                    {
                        var savedTech = 0;
                        if (!string.IsNullOrEmpty(cardId) && snap.TechLevelByHeroCardId.TryGetValue(cardId, out var byCard))
                            savedTech = byCard;
                        else if (playerId > 0 && snap.TechLevelByPlayerId.TryGetValue(playerId, out var byPid))
                            savedTech = byPid;

                        if (savedTech > 0)
                        {
                            SetEntityTag(entry.Value, techLevelTag, savedTech);
                            restoredTechTags++;
                        }
                    }
                    else if (!string.IsNullOrEmpty(cardId))
                    {
                        lock (sync)
                        {
                            if (activeTechGuardByCardId != null)
                                activeTechGuardByCardId[cardId] = currentTech;
                        }
                    }
                }
            }

            // 3. Restore Tavern-up turns & Triples by tier
            var tavernDict = HdtReflection.GetMemberValue(game, "_battlegroundsHeroLatestTavernUpTurn") as IDictionary;
            restoredTavern = RestoreHeroIntMaps(tavernDict, snap.TavernUpTurns, entityByPlayerId, entityByCardId, currentEntityMetaById, isTriples: false);

            var triplesDict = HdtReflection.GetMemberValue(game, "_battlegroundsHeroTriplesByTier") as IDictionary;
            restoredTriples = RestoreHeroIntMaps(triplesDict, snap.TriplesByTier, entityByPlayerId, entityByCardId, currentEntityMetaById, isTriples: true);

            // 4. Restore OpponentDeadForTracker (including merging when HDT partially populated dead players)
            if (snap.UniqueDeadPlayers.Count > 0)
            {
                var deadForType = HdtReflection.FindType("Hearthstone_Deck_Tracker.OpponentDeadForTracker");
                var uniqueList = HdtReflection.GetStaticMemberValue(deadForType, "_uniqueDeadPlayers") as IList;
                var trackerList = HdtReflection.GetStaticMemberValue(deadForType, "_deadTracker") as IList;
                if (uniqueList != null && trackerList != null
                    && MergeDeadForLists(uniqueList, trackerList, snap.UniqueDeadPlayers, snap.DeadTracker, out var updatedDeadTracker))
                {
                    HdtReflection.InvokeOnDispatcher(overlay, () =>
                    {
                        HdtReflection.InvokeMethod(overlay, "UpdateOpponentDeadForTurns", updatedDeadTracker);
                    });
                }
            }

            // 5. Restore Bob's Buddy combat input
            if (snap.BobsBuddyInput != null && !snap.BobsBuddyRestored)
            {
                var gameIdObj = HdtReflection.GetMemberValue(gameStats, "GameId");
                if (gameIdObj is Guid newGameId && newGameId != Guid.Empty)
                {
                    var bobsType = HdtReflection.FindType("Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker");
                    var getInstance = HdtReflection.FindMethod(bobsType, "GetInstance", true, typeof(Guid), typeof(int), typeof(bool));
                    var invoker = getInstance?.Invoke(null, new object[] { newGameId, snap.SavedTurn, true });
                    if (invoker != null)
                    {
                        var existingInput = HdtReflection.GetMemberValue(invoker, "_input");
                        if (existingInput == null)
                            HdtReflection.SetMemberValue(invoker, "_input", snap.BobsBuddyInput);
                        snap.BobsBuddyRestored = true;
                    }
                }
            }

            // 6b. Restart Battlegrounds leaderboard watchers once and refresh leaderboard tiles UI
            if (!snap.WatchersRestarted)
            {
                RestartBattlegroundsWatchers(game);
                snap.WatchersRestarted = true;
            }
            RefreshLeaderboardUi(overlay, new Dictionary<string, LeaderboardRaceSnapshot>(snap.RaceByHeroCardId, StringComparer.Ordinal));

            lock (sync)
            {
                lastAction = string.Format(
                    "restored boards={0} deities={1} tavern={2} triples={3} tech={4} bobs={5}",
                    restoredBoards,
                    restoredDeities,
                    restoredTavern,
                    restoredTriples,
                    restoredTechTags,
                    snap.BobsBuddyRestored ? 1 : 0);
            }
        }

        private void RestartBattlegroundsWatchers(object game)
        {
            var watchersType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Hearthstone.Watchers");
            if (watchersType == null)
                return;

            RunWatcher(watchersType, "BattlegroundsLeaderboardWatcher");
            RunWatcher(watchersType, "BattlegroundsLeaderboardTeamsWatcher");
            RunWatcher(watchersType, "BattlegroundsLobbyInfoWatcher");

            var isDuos = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsBattlegroundsDuosMatch") ?? false);
            if (isDuos)
                RunWatcher(watchersType, "BattlegroundsTeammateBoardStateWatcher");
        }

        private static void RunWatcher(Type watchersType, string propertyName)
        {
            try
            {
                var watcher = HdtReflection.GetStaticMemberValue(watchersType, propertyName);
                if (watcher != null)
                    HdtReflection.InvokeMethod(watcher, "Run");
            }
            catch (Exception ex)
            {
                BridgeLogger.ErrorThrottled("run_watcher_" + propertyName, "RunWatcher(" + propertyName + ")", ex, 30);
            }
        }

        private void RefreshLeaderboardUi(object overlay, Dictionary<string, LeaderboardRaceSnapshot> savedRaces)
        {
            if (overlay == null)
                return;

            HdtReflection.InvokeOnDispatcher(overlay, () =>
            {
                var tilesVm = HdtReflection.GetMemberValue(overlay, "BattlegroundsLeaderboardTilesVM");
                if (tilesVm != null)
                {
                    if (savedRaces != null && savedRaces.Count > 0)
                        BackfillTeamsDominantRace(HdtReflection.GetMemberValue(tilesVm, "_teams") as IEnumerable, savedRaces);
                    HdtReflection.InvokeMethod(tilesVm, "Update");
                }
                HdtReflection.InvokeMethod(overlay, "UpdateLeaderboardTileInfos");
            });
        }

        private void EnsureLeaderboardTeamsWatcherHooked()
        {
            lock (sync)
            {
                if (teamsWatcherHooked)
                    return;
            }

            try
            {
                var watchersType = HdtReflection.FindType("Hearthstone_Deck_Tracker.Hearthstone.Watchers");
                var teamsWatcher = HdtReflection.GetStaticMemberValue(watchersType, "BattlegroundsLeaderboardTeamsWatcher");
                if (teamsWatcher == null)
                    return;

                var changeEvent = teamsWatcher.GetType().GetEvent("Change", HdtReflection.AnyInstance);
                if (changeEvent == null)
                    return;

                var handlerMethod = typeof(HdtStatePreserver).GetMethod(
                    nameof(OnLeaderboardTeamsChanged),
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var del = Delegate.CreateDelegate(changeEvent.EventHandlerType, this, handlerMethod, false);
                if (del == null)
                    return;

                changeEvent.AddEventHandler(teamsWatcher, del);
                lock (sync)
                    teamsWatcherHooked = true;
                BridgeLogger.Debug("Hooked BattlegroundsLeaderboardTeamsWatcher.Change for DominantRace guard.");
            }
            catch (Exception ex)
            {
                BridgeLogger.ErrorThrottled("hook_teams_watcher", "EnsureLeaderboardTeamsWatcherHooked", ex, 30);
            }
        }

        private void OnLeaderboardTeamsChanged(object sender, object args)
        {
            string guardHandle;
            Dictionary<string, LeaderboardRaceSnapshot> guardRaces;
            lock (sync)
            {
                guardHandle = activeRaceGuardGameHandle;
                guardRaces = activeRaceGuardMap != null && activeRaceGuardMap.Count > 0
                    ? new Dictionary<string, LeaderboardRaceSnapshot>(activeRaceGuardMap, StringComparer.Ordinal)
                    : null;
            }

            if (string.IsNullOrEmpty(guardHandle) || guardRaces == null || guardRaces.Count == 0 || args == null)
                return;

            try
            {
                if (!HdtReflection.TryGetHdtCoreObjects(out var game, out var overlay, out _))
                    return;

                var isInMenu = Convert.ToBoolean(HdtReflection.GetMemberValue(game, "IsInMenu") ?? false);
                if (isInMenu || !TryGetGameHandle(game, out var currentHandle))
                    return;

                if (!string.Equals(currentHandle, guardHandle, StringComparison.Ordinal))
                {
                    lock (sync)
                    {
                        activeRaceGuardGameHandle = null;
                        activeRaceGuardMap = null;
                        activeTechGuardByCardId = null;
                    }
                    return;
                }

                var teams = HdtReflection.GetMemberValue(args, "Teams") as IEnumerable;
                if (BackfillTeamsDominantRace(teams, guardRaces))
                {
                    BridgeLogger.Debug("TeamsWatcher Change guard backfilled DominantRace on leaderboard tiles.");
                    HdtReflection.InvokeOnDispatcher(overlay, () =>
                    {
                        var tilesVm = HdtReflection.GetMemberValue(overlay, "BattlegroundsLeaderboardTilesVM");
                        if (tilesVm != null)
                            HdtReflection.InvokeMethod(tilesVm, "Update");
                        HdtReflection.InvokeMethod(overlay, "UpdateLeaderboardTileInfos");
                    });
                }
            }
            catch (Exception ex)
            {
                BridgeLogger.ErrorThrottled("teams_changed", "OnLeaderboardTeamsChanged", ex, 15);
            }
        }

        private bool BackfillTeamsDominantRace(IEnumerable teams, Dictionary<string, LeaderboardRaceSnapshot> savedRaces)
        {
            if (teams == null || savedRaces == null || savedRaces.Count == 0)
                return false;

            var modified = false;
            foreach (var team in teams)
            {
                if (team == null)
                    continue;
                if (!(HdtReflection.GetMemberValue(team, "Tiles") is IEnumerable tiles))
                    continue;
                foreach (var tile in tiles)
                {
                    if (tile == null)
                        continue;
                    var heroCardId = HdtReflection.GetMemberValue(tile, "HeroCardId") as string;
                    if (string.IsNullOrEmpty(heroCardId))
                        continue;

                    var currentRace = HdtReflection.GetMemberValue(tile, "DominantRace");
                    if (currentRace is int liveRace)
                    {
                        var liveCountObj = HdtReflection.GetMemberValue(tile, "DominantRaceCount");
                        var liveCount = liveCountObj is int lc ? lc : 0;
                        lock (sync)
                        {
                            if (activeRaceGuardMap != null)
                                activeRaceGuardMap[heroCardId] = new LeaderboardRaceSnapshot { Race = liveRace, Count = liveCount };
                        }
                    }
                    else if (savedRaces.TryGetValue(heroCardId, out var saved))
                    {
                        HdtReflection.SetMemberValue(tile, "DominantRace", (int?)saved.Race);
                        HdtReflection.SetMemberValue(tile, "DominantRaceCount", saved.Count);
                        modified = true;
                    }
                }
            }

            return modified;
        }

        private void ClearPending(string action, bool clearGuard, PendingSnapshot expected = null)
        {
            BridgeLogger.Info(string.Format("Restore finished/cleared: action={0}, clearGuard={1}", action, clearGuard));
            lock (sync)
            {
                if (expected != null && !ReferenceEquals(pending, expected))
                    return;
                pending = null;
                lastAction = action;
                if (clearGuard)
                {
                    activeRaceGuardGameHandle = null;
                    activeRaceGuardMap = null;
                    activeTechGuardByCardId = null;
                }
            }
            TryChangeTimer(Timeout.Infinite, Timeout.Infinite);
        }

        private static void RecordCheck(HdtCapabilityReport report, string key, bool ok)
        {
            if (ok)
            {
                report.ReadyCount++;
                report.ReadyItems.Add(key);
            }
            else
            {
                report.MissingItems.Add(key);
            }
        }

        private static string GetHdtVersion(Assembly hdtAsm)
        {
            if (hdtAsm == null)
                return string.Empty;

            try
            {
                var location = hdtAsm.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    var info = FileVersionInfo.GetVersionInfo(location);
                    if (!string.IsNullOrEmpty(info.ProductVersion))
                        return info.ProductVersion.Trim();
                    if (!string.IsNullOrEmpty(info.FileVersion))
                        return info.FileVersion.Trim();
                }
            }
            catch
            {
                // Fallback to AssemblyName.Version below.
            }

            var ver = hdtAsm.GetName().Version;
            return ver == null ? string.Empty : ver.ToString(3);
        }

        private static IDictionary GetDictionaryMember(object owner, string propName, string backingFieldName)
        {
            if (owner == null)
                return null;

            return HdtReflection.GetMemberValue(owner, propName) as IDictionary
                ?? HdtReflection.GetMemberValue(owner, backingFieldName) as IDictionary;
        }

        private static bool TryGetGameHandle(object game, out string gameHandle)
        {
            gameHandle = null;
            var metaData = HdtReflection.GetMemberValue(game, "MetaData");
            var serverInfo = HdtReflection.GetMemberValue(metaData, "ServerInfo");
            var value = HdtReflection.GetMemberValue(serverInfo, "GameHandle");
            var text = value == null ? null : Convert.ToString(value);
            if (string.IsNullOrWhiteSpace(text) || text == "0")
                return false;

            gameHandle = text;
            return true;
        }

        private static bool IsGameStatsEnded(object gameStats)
        {
            if (gameStats == null)
                return false;

            var startObj = HdtReflection.GetMemberValue(gameStats, "StartTime");
            var endObj = HdtReflection.GetMemberValue(gameStats, "EndTime");
            if (startObj is DateTime start && endObj is DateTime end)
                return end > start;

            return false;
        }

        private static int GetEntityTag(object entity, object gameTagValue)
        {
            var val = HdtReflection.InvokeMethod(entity, "GetTag", gameTagValue);
            return val is int i ? i : 0;
        }

        private static void SetEntityTag(object entity, object gameTagValue, int value)
        {
            HdtReflection.InvokeMethod(entity, "SetTag", gameTagValue, value);
        }

        private static List<KeyValuePair<object, object>> SafeSnapshotDictionary(IDictionary source)
        {
            var snapshot = new List<KeyValuePair<object, object>>();
            if (source == null)
                return snapshot;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                snapshot.Clear();
                try
                {
                    foreach (DictionaryEntry entry in source)
                        snapshot.Add(new KeyValuePair<object, object>(entry.Key, entry.Value));
                    return snapshot;
                }
                catch (InvalidOperationException)
                {
                    Thread.Sleep(5);
                }
            }

            return snapshot;
        }

        private static void CaptureHeroIntMaps(
            IDictionary source,
            Dictionary<int, Tuple<int, string>> entityMetaById,
            List<HeroMapSnapshot> target)
        {
            if (source == null)
                return;

            foreach (var outer in SafeSnapshotDictionary(source))
            {
                if (!(outer.Key is int entityId) || !(outer.Value is IDictionary inner) || inner.Count == 0)
                    continue;

                var cloned = new Dictionary<int, int>();
                foreach (var innerEntry in SafeSnapshotDictionary(inner))
                {
                    if (innerEntry.Key is int k && innerEntry.Value is int v)
                        cloned[k] = v;
                }

                if (cloned.Count == 0)
                    continue;

                var playerId = 0;
                var cardId = string.Empty;
                if (entityMetaById.TryGetValue(entityId, out var meta))
                {
                    playerId = meta.Item1;
                    cardId = meta.Item2;
                }

                target.Add(new HeroMapSnapshot
                {
                    EntityId = entityId,
                    PlayerId = playerId,
                    CardId = cardId,
                    Values = cloned
                });
            }
        }

        private static void MergePendingSnapshotInto(PendingSnapshot previous, PendingSnapshot current)
        {
            if (previous == null || current == null)
                return;

            foreach (var kv in previous.Boards)
            {
                if (!current.Boards.ContainsKey(kv.Key))
                    current.Boards[kv.Key] = kv.Value;
            }

            foreach (var kv in previous.Deities)
            {
                if (!current.Deities.ContainsKey(kv.Key))
                    current.Deities[kv.Key] = kv.Value;
            }

            MergeHeroMapSnapshotList(previous.TavernUpTurns, current.TavernUpTurns, isTriples: false);
            MergeHeroMapSnapshotList(previous.TriplesByTier, current.TriplesByTier, isTriples: true);

            if (previous.UniqueDeadPlayers.Count > 0)
            {
                MergeDeadForLists(
                    current.UniqueDeadPlayers,
                    current.DeadTracker,
                    previous.UniqueDeadPlayers,
                    previous.DeadTracker,
                    out _);
            }

            if (current.BobsBuddyInput == null
                && previous.BobsBuddyInput != null
                && current.SavedTurn == previous.SavedTurn)
            {
                current.BobsBuddyInput = previous.BobsBuddyInput;
            }

            foreach (var kv in previous.TechLevelByHeroCardId)
            {
                if (!current.TechLevelByHeroCardId.TryGetValue(kv.Key, out var curTech) || kv.Value > curTech)
                    current.TechLevelByHeroCardId[kv.Key] = kv.Value;
            }

            foreach (var kv in previous.TechLevelByPlayerId)
            {
                if (!current.TechLevelByPlayerId.TryGetValue(kv.Key, out var curTech) || kv.Value > curTech)
                    current.TechLevelByPlayerId[kv.Key] = kv.Value;
            }

            foreach (var kv in previous.RaceByHeroCardId)
            {
                if (!current.RaceByHeroCardId.ContainsKey(kv.Key))
                    current.RaceByHeroCardId[kv.Key] = kv.Value;
            }
        }

        private static void MergeHeroMapSnapshotList(List<HeroMapSnapshot> previous, List<HeroMapSnapshot> current, bool isTriples)
        {
            if (previous == null || previous.Count == 0 || current == null)
                return;

            foreach (var prevItem in previous)
            {
                if (prevItem == null || prevItem.Values == null || prevItem.Values.Count == 0)
                    continue;

                HeroMapSnapshot match = null;
                foreach (var curItem in current)
                {
                    if (SameHeroSnapshot(prevItem, curItem))
                    {
                        match = curItem;
                        break;
                    }
                }

                if (match == null)
                {
                    current.Add(prevItem.Clone());
                    continue;
                }

                if (match.PlayerId <= 0 && prevItem.PlayerId > 0)
                    match.PlayerId = prevItem.PlayerId;
                if (string.IsNullOrEmpty(match.CardId) && !string.IsNullOrEmpty(prevItem.CardId))
                    match.CardId = prevItem.CardId;

                if (isTriples)
                {
                    MergeTriplesDictionary(match.Values, prevItem.Values);
                }
                else
                {
                    foreach (var kv in prevItem.Values)
                    {
                        if (!match.Values.ContainsKey(kv.Key))
                            match.Values[kv.Key] = kv.Value;
                    }
                }
            }
        }

        private static bool SameHeroSnapshot(HeroMapSnapshot left, HeroMapSnapshot right)
        {
            if (left == null || right == null)
                return false;

            if (left.PlayerId > 0 && right.PlayerId > 0)
                return left.PlayerId == right.PlayerId;

            if (!string.IsNullOrEmpty(left.CardId) && !string.IsNullOrEmpty(right.CardId))
                return string.Equals(left.CardId, right.CardId, StringComparison.Ordinal);

            if (left.PlayerId > 0 || right.PlayerId > 0
                || !string.IsNullOrEmpty(left.CardId) || !string.IsNullOrEmpty(right.CardId))
            {
                return false;
            }

            return left.EntityId > 0 && left.EntityId == right.EntityId;
        }

        private static bool MergeDeadForLists(
            IList uniqueList,
            IList trackerList,
            List<int> savedUniquePlayers,
            List<int> savedDeadTracker,
            out List<int> updatedDeadTracker)
        {
            updatedDeadTracker = null;
            if (uniqueList == null || trackerList == null || savedUniquePlayers == null || savedDeadTracker == null)
                return false;

            var savedCount = Math.Min(savedUniquePlayers.Count, savedDeadTracker.Count);
            if (savedCount <= 0)
                return false;

            var existingCount = Math.Min(uniqueList.Count, trackerList.Count);
            var existingPids = new List<int>(existingCount);
            var existingTurns = new List<int>(existingCount);
            var existingTurnsByPid = new Dictionary<int, int>();

            for (var i = 0; i < existingCount; i++)
            {
                if (!(uniqueList[i] is int pid) || !(trackerList[i] is int turns))
                    continue;
                existingPids.Add(pid);
                existingTurns.Add(turns);
                if (!existingTurnsByPid.TryGetValue(pid, out var prev) || turns > prev)
                    existingTurnsByPid[pid] = turns;
            }

            var mergedPids = new List<int>(savedCount + existingPids.Count);
            var mergedTurns = new List<int>(savedCount + existingPids.Count);
            var included = new HashSet<int>();

            for (var i = 0; i < savedCount; i++)
            {
                var pid = savedUniquePlayers[i];
                if (!included.Add(pid))
                    continue;
                var savedVal = savedDeadTracker[i];
                var mergedVal = existingTurnsByPid.TryGetValue(pid, out var curVal)
                    ? Math.Max(curVal, savedVal)
                    : savedVal;
                mergedPids.Add(pid);
                mergedTurns.Add(mergedVal);
            }

            for (var i = 0; i < existingPids.Count; i++)
            {
                var pid = existingPids[i];
                if (!included.Add(pid))
                    continue;
                mergedPids.Add(pid);
                mergedTurns.Add(existingTurns[i]);
            }

            var changed = uniqueList.Count != mergedPids.Count || trackerList.Count != mergedTurns.Count;
            if (!changed)
            {
                for (var i = 0; i < mergedPids.Count; i++)
                {
                    if (!(uniqueList[i] is int u) || u != mergedPids[i]
                        || !(trackerList[i] is int t) || t != mergedTurns[i])
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed)
                return false;

            uniqueList.Clear();
            trackerList.Clear();
            for (var i = 0; i < mergedPids.Count; i++)
            {
                uniqueList.Add(mergedPids[i]);
                trackerList.Add(mergedTurns[i]);
            }

            updatedDeadTracker = new List<int>(mergedTurns);
            return true;
        }

        private static int ResolveTargetHeroEntityId(
            HeroMapSnapshot item,
            Dictionary<int, int> entityByPlayerId,
            Dictionary<string, int> entityByCardId,
            Dictionary<int, CurrentEntityIdentity> currentEntityMetaById)
        {
            if (item == null)
                return 0;

            if (item.PlayerId > 0 && entityByPlayerId.TryGetValue(item.PlayerId, out var byPid))
                return byPid;

            if (!string.IsNullOrEmpty(item.CardId) && entityByCardId.TryGetValue(item.CardId, out var byCard))
            {
                if (item.PlayerId <= 0
                    || !currentEntityMetaById.TryGetValue(byCard, out var cardMeta)
                    || cardMeta.PlayerId <= 0
                    || cardMeta.PlayerId == item.PlayerId)
                {
                    return byCard;
                }
            }

            // If we know the hero's PlayerId or CardId, defer instead of risking assigning history
            // to a reused EntityId that now belongs to (or will belong to) a different hero.
            if (item.PlayerId > 0 || !string.IsNullOrEmpty(item.CardId))
                return 0;

            if (item.EntityId <= 0)
                return 0;

            if (currentEntityMetaById.TryGetValue(item.EntityId, out var occupant) && !occupant.IsHero)
                return 0;

            return item.EntityId;
        }

        private static bool MergeTriplesDictionary(IDictionary destination, Dictionary<int, int> savedValues)
        {
            if (destination == null || savedValues == null || savedValues.Count == 0)
                return false;

            var savedTiers = new List<KeyValuePair<int, int>>(savedValues.Count);
            foreach (var kv in savedValues)
            {
                if (kv.Value > 0)
                    savedTiers.Add(kv);
            }
            if (savedTiers.Count == 0)
                return false;
            savedTiers.Sort((a, b) => a.Key.CompareTo(b.Key));

            var modified = false;
            foreach (var kv in savedTiers)
            {
                var tier = kv.Key;
                var savedCount = kv.Value;
                if (destination.Contains(tier))
                    continue;

                // HDT's UpdateBattlegroundsPlayerTriples records (totalTriples - sum(lower tiers))
                // into the hero's current tier after a reset. Deduct the restored lower-tier triples
                // from the lowest existing higher tier(s) that absorbed them so totals are not doubled.
                var remainingToDeduct = savedCount;
                var higherTiers = new List<int>();
                foreach (DictionaryEntry entry in destination)
                {
                    if (entry.Key is int existingTier && existingTier > tier && entry.Value is int existingVal && existingVal > 0)
                        higherTiers.Add(existingTier);
                }
                higherTiers.Sort();

                foreach (var absorberTier in higherTiers)
                {
                    if (remainingToDeduct <= 0)
                        break;

                    var currentVal = (int)destination[absorberTier];
                    var deduction = Math.Min(remainingToDeduct, currentVal);
                    var updatedVal = currentVal - deduction;
                    if (updatedVal > 0)
                        destination[absorberTier] = updatedVal;
                    else
                        destination.Remove(absorberTier);
                    remainingToDeduct -= deduction;
                }

                destination[tier] = savedCount;
                modified = true;
            }

            return modified;
        }

        private static int RestoreHeroIntMaps(
            IDictionary destination,
            List<HeroMapSnapshot> saved,
            Dictionary<int, int> entityByPlayerId,
            Dictionary<string, int> entityByCardId,
            Dictionary<int, CurrentEntityIdentity> currentEntityMetaById,
            bool isTriples)
        {
            if (destination == null || saved == null || saved.Count == 0)
                return 0;

            var restored = 0;
            foreach (var item in saved)
            {
                var targetId = ResolveTargetHeroEntityId(item, entityByPlayerId, entityByCardId, currentEntityMetaById);
                if (targetId <= 0)
                    continue;

                var inner = destination[targetId] as IDictionary;
                if (inner == null)
                {
                    inner = new Dictionary<int, int>();
                    destination[targetId] = inner;
                }

                bool anyAdded;
                if (isTriples)
                {
                    anyAdded = MergeTriplesDictionary(inner, item.Values);
                }
                else
                {
                    anyAdded = false;
                    foreach (var kv in item.Values)
                    {
                        if (!inner.Contains(kv.Key))
                        {
                            inner[kv.Key] = kv.Value;
                            anyAdded = true;
                        }
                    }
                }

                if (anyAdded)
                    restored++;
            }

            return restored;
        }
    }
}
