using System;
using System.Collections.Generic;
using System.Linq;
using BlockLimiter.Settings;
using ProtoBuf;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using static BlockLimiter.Network.NexusAPI;

namespace BlockLimiter.Network
{
    internal static class LimitsNexusSync
    {
        private const long ChannelId = 110150254010016L;
        private const int PeriodicSnapshotIntervalTicks = 2 * 60 * 60;

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, CountEntry> LocalCounts = new Dictionary<string, CountEntry>();
        private static readonly Dictionary<byte, Dictionary<string, CountEntry>> RemoteCounts =
            new Dictionary<byte, Dictionary<string, CountEntry>>();

        private static NexusAPI _nexus;
        private static bool _started;
        private static byte _thisServerId;
        private static int _tick;
        private static int _nextPeriodicSnapshotTick;

        internal static bool Ready => _started && _nexus != null && _nexus.Enabled;

        internal static void Start(NexusAPI nexus)
        {
            if (_started) return;
            _nexus = nexus;
            if (_nexus == null || !_nexus.Enabled) return;

            _thisServerId = _nexus.CurrentServerID;
            MyAPIGateway.Utilities.RegisterMessageHandler(ChannelId, OnMessage);

            _started = true;
            _tick = 0;
            _nextPeriodicSnapshotTick = PeriodicSnapshotIntervalTicks;
            BroadcastHello();
            BroadcastSnapshot();
        }

        internal static void Stop()
        {
            if (_started)
            {
                try { MyAPIGateway.Utilities.UnregisterMessageHandler(ChannelId, OnMessage); } catch { }
            }

            _started = false;
            _nexus = null;
            lock (SyncRoot)
            {
                var affected = new Dictionary<string, CountEntry>();
                foreach (var entry in LocalCounts.Values)
                    affected[MakeEntryKey(entry)] = entry;
                foreach (var serverCounts in RemoteCounts.Values)
                    foreach (var entry in serverCounts.Values)
                        affected[MakeEntryKey(entry)] = entry;

                RemoteCounts.Clear();
                foreach (var entry in affected.Values)
                    ApplyLocalOnlyNoLock(entry);

                LocalCounts.Clear();
            }
        }

        internal static void RunPeriodicSnapshotTick()
        {
            if (!Ready) return;
            _tick++;
            if (_tick < _nextPeriodicSnapshotTick) return;

            _nextPeriodicSnapshotTick = _tick + PeriodicSnapshotIntervalTicks;
            BroadcastSnapshot();
        }

        internal static bool TrySetLocalPlayerCount(LimitItem limit, long playerId, int count)
        {
            return TrySetLocalCount(limit, TargetKind.Player, playerId, count, true);
        }

        internal static bool TrySetLocalFactionCount(LimitItem limit, long factionId, int count)
        {
            return TrySetLocalCount(limit, TargetKind.Faction, factionId, count, true);
        }

        internal static bool TryAddLocalPlayerCount(LimitItem limit, long playerId, int amount)
        {
            return TryAddLocalCount(limit, TargetKind.Player, playerId, amount);
        }

        internal static bool TryAddLocalFactionCount(LimitItem limit, long factionId, int amount)
        {
            return TryAddLocalCount(limit, TargetKind.Faction, factionId, amount);
        }

        internal static void BroadcastSnapshot()
        {
            if (!Ready) return;
            var env = new Envelope { Kind = EnvelopeKind.Snapshot, Payload = Serialize(BuildSnapshot()) };
            _nexus.SendModMsgToAllServers(Serialize(env), ChannelId);
        }

        private static bool TryAddLocalCount(LimitItem limit, TargetKind kind, long targetId, int amount)
        {
            if (!Ready || limit == null || targetId == 0) return false;

            var limitKey = GetLimitKey(limit);
            var key = MakeEntryKey(limitKey, kind, targetId);
            int next;
            lock (SyncRoot)
            {
                next = GetLocalCountNoLock(key, limit, targetId) + amount;
            }

            return TrySetLocalCount(limit, kind, targetId, Math.Max(0, next), true);
        }

        private static bool TrySetLocalCount(LimitItem limit, TargetKind kind, long targetId, int count, bool broadcast)
        {
            if (!Ready || limit == null || targetId == 0) return false;
            if (kind == TargetKind.Player && !limit.LimitPlayers) return false;
            if (kind == TargetKind.Faction && !limit.LimitFaction) return false;

            var entry = new CountEntry
            {
                LimitKey = GetLimitKey(limit),
                Kind = kind,
                TargetId = targetId,
                Count = Math.Max(0, count)
            };

            var key = MakeEntryKey(entry.LimitKey, kind, targetId);
            lock (SyncRoot)
            {
                if (entry.Count <= 0) LocalCounts.Remove(key);
                else LocalCounts[key] = entry;
                ApplyAggregateNoLock(limit, entry);
            }

            if (broadcast)
                BroadcastCountUpdate(entry);

            return true;
        }

        private static void BroadcastCountUpdate(CountEntry entry)
        {
            if (!Ready) return;
            var env = new Envelope { Kind = EnvelopeKind.Diff, Payload = Serialize(entry) };
            _nexus.SendModMsgToAllServers(Serialize(env), ChannelId);
        }

        private static void BroadcastHello()
        {
            var hello = new Hello { ServerId = _thisServerId };
            var env = new Envelope { Kind = EnvelopeKind.Hello, Payload = Serialize(hello) };
            _nexus.SendModMsgToAllServers(Serialize(env), ChannelId);
        }

        private static void SendSnapshotTo(byte targetServer)
        {
            if (!Ready) return;
            var env = new Envelope { Kind = EnvelopeKind.Snapshot, Payload = Serialize(BuildSnapshot()) };
            _nexus.SendModMsgToServer(Serialize(env), ChannelId, targetServer);
        }

        private static LimitsState BuildSnapshot()
        {
            var state = new LimitsState();
            lock (SyncRoot)
            {
                foreach (var limit in BlockLimiterConfig.Instance.AllLimits)
                {
                    if (!limit.LimitPlayers && !limit.LimitFaction) continue;

                    var limitKey = GetLimitKey(limit);
                    foreach (var found in limit.FoundEntities)
                    {
                        TargetKind kind;
                        if (!TryResolveTargetKind(limit, found.Key, out kind)) continue;

                        var key = MakeEntryKey(limitKey, kind, found.Key);
                        var count = GetSnapshotLocalCountNoLock(key, limit, found.Key);
                        if (count <= 0) continue;

                        var entry = new CountEntry
                        {
                            LimitKey = limitKey,
                            Kind = kind,
                            TargetId = found.Key,
                            Count = count
                        };
                        LocalCounts[key] = entry;
                        state.Counts.Add(entry);
                    }
                }
            }

            return state;
        }

        private static void OnMessage(object obj)
        {
            try
            {
                var apiMsg = MyAPIGateway.Utilities.SerializeFromBinary<ModAPIMsg>((byte[])obj);
                if (apiMsg == null) return;
                if (apiMsg.targetModMessageID != ChannelId) return;
                if (apiMsg.fromServerID == _thisServerId) return;
                if (apiMsg.toServerID != 0 && apiMsg.toServerID != _thisServerId) return;

                var env = Deserialize<Envelope>(apiMsg.msgData);
                if (env == null) return;

                switch (env.Kind)
                {
                    case EnvelopeKind.Hello:
                        var hello = Deserialize<Hello>(env.Payload);
                        if (hello == null) return;
                        SendSnapshotTo(hello.ServerId);
                        break;

                    case EnvelopeKind.Snapshot:
                        var state = Deserialize<LimitsState>(env.Payload);
                        if (state == null) return;
                        ApplySnapshot(apiMsg.fromServerID, state);
                        break;

                    case EnvelopeKind.Diff:
                        var diff = Deserialize<CountEntry>(env.Payload);
                        if (diff == null) return;
                        ApplyDiff(apiMsg.fromServerID, diff);
                        break;
                }
            }
            catch (Exception ex)
            {
                BlockLimiter.Instance.Log.Warn(ex, "Failed to process Nexus limit sync message");
            }
        }

        private static void ApplySnapshot(byte serverId, LimitsState state)
        {
            lock (SyncRoot)
            {
                var affected = new Dictionary<string, CountEntry>();
                Dictionary<string, CountEntry> old;
                if (RemoteCounts.TryGetValue(serverId, out old))
                {
                    foreach (var entry in old.Values)
                        affected[MakeEntryKey(entry)] = entry;
                }

                var counts = new Dictionary<string, CountEntry>();
                foreach (var entry in state.Counts)
                {
                    if (!IsValid(entry)) continue;
                    var key = MakeEntryKey(entry);
                    counts[key] = entry;
                    affected[key] = entry;
                }

                RemoteCounts[serverId] = counts;
                foreach (var entry in affected.Values)
                    ApplyRemoteEntryNoLock(entry);
            }
        }

        private static void ApplyDiff(byte serverId, CountEntry entry)
        {
            if (!IsValid(entry)) return;

            lock (SyncRoot)
            {
                Dictionary<string, CountEntry> counts;
                if (!RemoteCounts.TryGetValue(serverId, out counts))
                {
                    counts = new Dictionary<string, CountEntry>();
                    RemoteCounts[serverId] = counts;
                }

                var key = MakeEntryKey(entry);
                if (entry.Count <= 0) counts.Remove(key);
                else counts[key] = entry;

                ApplyRemoteEntryNoLock(entry);
            }
        }

        private static void ApplyRemoteEntryNoLock(CountEntry entry)
        {
            var limit = FindLimit(entry.LimitKey);
            if (limit == null) return;
            ApplyAggregateNoLock(limit, entry);
        }

        private static void ApplyLocalOnlyNoLock(CountEntry entry)
        {
            var limit = FindLimit(entry.LimitKey);
            if (limit == null) return;

            CountEntry local;
            var key = MakeEntryKey(entry);
            var count = LocalCounts.TryGetValue(key, out local) ? Math.Max(0, local.Count) : 0;
            if (count <= 0) limit.FoundEntities.Remove(entry.TargetId);
            else limit.FoundEntities[entry.TargetId] = count;
        }

        private static void ApplyAggregateNoLock(LimitItem limit, CountEntry entry)
        {
            var key = MakeEntryKey(entry);
            var local = GetLocalCountNoLock(key, limit, entry.TargetId);
            var remote = RemoteCounts.Values.Sum(serverCounts =>
            {
                CountEntry remoteEntry;
                return serverCounts.TryGetValue(key, out remoteEntry) ? Math.Max(0, remoteEntry.Count) : 0;
            });

            var total = Math.Max(0, local + remote);
            if (total <= 0) limit.FoundEntities.Remove(entry.TargetId);
            else limit.FoundEntities[entry.TargetId] = total;
        }

        private static int GetLocalCountNoLock(string key, LimitItem limit, long targetId)
        {
            CountEntry local;
            if (LocalCounts.TryGetValue(key, out local))
                return Math.Max(0, local.Count);

            return 0;
        }

        private static int GetSnapshotLocalCountNoLock(string key, LimitItem limit, long targetId)
        {
            CountEntry local;
            if (LocalCounts.TryGetValue(key, out local))
                return Math.Max(0, local.Count);

            int found;
            if (!limit.FoundEntities.TryGetValue(targetId, out found))
                return 0;

            var remote = RemoteCounts.Values.Sum(serverCounts =>
            {
                CountEntry remoteEntry;
                return serverCounts.TryGetValue(key, out remoteEntry) ? Math.Max(0, remoteEntry.Count) : 0;
            });

            return Math.Max(0, found - remote);
        }

        private static bool TryResolveTargetKind(LimitItem limit, long targetId, out TargetKind kind)
        {
            if (limit.LimitPlayers && MySession.Static.Players.HasIdentity(targetId))
            {
                kind = TargetKind.Player;
                return true;
            }

            if (limit.LimitFaction && MySession.Static.Factions.TryGetFactionById(targetId) != null)
            {
                kind = TargetKind.Faction;
                return true;
            }

            kind = TargetKind.Player;
            return false;
        }

        private static LimitItem FindLimit(string limitKey)
        {
            return BlockLimiterConfig.Instance.AllLimits.FirstOrDefault(limit => GetLimitKey(limit) == limitKey);
        }

        private static string GetLimitKey(LimitItem limit)
        {
            var name = string.IsNullOrWhiteSpace(limit.Name) ? string.Empty : limit.Name.Trim();
            var blocks = limit.BlockList == null
                ? string.Empty
                : string.Join("\u001f", limit.BlockList
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            return string.Join("\u001e", new[]
            {
                name,
                blocks,
                limit.SearchType.ToString(),
                limit.GridTypeBlock.ToString(),
                limit.LimitFilterType.ToString(),
                limit.LimitFilterOperator.ToString(),
                limit.FilterValue ?? string.Empty
            });
        }

        private static string MakeEntryKey(CountEntry entry)
        {
            return MakeEntryKey(entry.LimitKey, entry.Kind, entry.TargetId);
        }

        private static string MakeEntryKey(string limitKey, TargetKind kind, long targetId)
        {
            return limitKey + "\u001d" + (byte)kind + "\u001d" + targetId;
        }

        private static bool IsValid(CountEntry entry)
        {
            return entry != null && !string.IsNullOrEmpty(entry.LimitKey) && entry.TargetId != 0;
        }

        private static byte[] Serialize<T>(T obj) => MyAPIGateway.Utilities.SerializeToBinary(obj);
        private static T Deserialize<T>(byte[] data) => MyAPIGateway.Utilities.SerializeFromBinary<T>(data);

        [ProtoContract]
        private class Envelope
        {
            [ProtoMember(1)] internal EnvelopeKind Kind { get; set; }
            [ProtoMember(2)] internal byte[] Payload { get; set; }
        }

        private enum EnvelopeKind : byte
        {
            Hello = 1,
            Snapshot = 2,
            Diff = 3
        }

        private enum TargetKind : byte
        {
            Player = 1,
            Faction = 2
        }

        [ProtoContract]
        private class Hello
        {
            [ProtoMember(1)] internal byte ServerId { get; set; }
        }

        [ProtoContract]
        private class LimitsState
        {
            [ProtoMember(1)] internal List<CountEntry> Counts { get; } = new List<CountEntry>();
        }

        [ProtoContract]
        private class CountEntry
        {
            [ProtoMember(1)] internal string LimitKey { get; set; }
            [ProtoMember(2)] internal TargetKind Kind { get; set; }
            [ProtoMember(3)] internal long TargetId { get; set; }
            [ProtoMember(4)] internal int Count { get; set; }
        }
    }
}
