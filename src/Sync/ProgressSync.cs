using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// Full progress transfer, what players did by sending each other save files. The game's save slot is
    /// <c>JsonUtility.ToJson(PersistentDataStore.GetPopulatedPersistentData())</c> (SaveSlotSegment.Serialise), and
    /// loading one is <c>PopulateFromLoadedPersistentData</c> + <c>SaveDataManager.ReloadState</c>
    /// (SaveDataManager.OnLoadGameCallback). The sender serializes its live progress the same way; the receiver merges in
    /// what stays per player - its own level and position, inventories, map notes, status effects, stats, difficulty,
    /// its own pickups (items, loot, money, lore: <see cref="WorldSync.IsPerPlayer"/>) and its player character's
    /// entries - and reloads its current level through the game's own load path. Nothing is written to disk.
    /// </summary>
    internal sealed class ProgressSync
    {
        private const float ApplyTimeout = 120f;
        private const int ChunksPerFrame = 4;

        private readonly SteamNetManager _net;
        private readonly WorldSync _world;
        private readonly Queue<(ulong Peer, ProgressTransfer.Outgoing Data, int Next)> _sending = new();
        private ProgressTransfer.Incoming _incoming;
        private string _pendingJson;
        private ulong _pendingFrom;
        private float _pendingSince;
        private uint _nextId = (uint)Environment.TickCount;

        public ProgressSync(SteamNetManager net, WorldSync world)
        {
            _net = net;
            _world = world;
        }

        public string Notice { get; private set; }

        /// <summary>Set while a received progress is being loaded: world sync stays quiet until the reload is done.</summary>
        public bool Applying { get; private set; }

        /// <summary>True for one Update after a received progress was loaded (read once).</summary>
        public bool Loaded
        {
            get
            {
                var loaded = _loaded;
                _loaded = false;
                return loaded;
            }
        }

        private bool _loaded;

        /// <summary>Why progress cannot be captured or loaded right now, or null.</summary>
        public string BlockedReason()
        {
            if (!GlobalReferences.Loaded || GameAccess.DataStore?.Data == null)
                return "还没进入存档";
            if (LocalPlayer.Find() == null)
                return "没有可控制的角色";
            if (FollowSync.TransitionActive())
                return "正在切换场景";
            if (GameAccess.InCutscene())
                return "正在看过场";
            if ((GameAccess.MenuFlags() & (GameAccess.MenuSystem | GameAccess.MenuOptions)) != 0)
                return "菜单打开着";
            if (_net.SaveBusy)
                return "正在共同存读档";
            var manager = SaveDataManager.Instance;
            if (manager != null && manager.IsBusy)
                return "游戏正在读写存档";
            return null;
        }

        /// <summary>Send our whole progress to <paramref name="peer"/>; false (with the reason shown) when not possible now.</summary>
        public bool Send(ulong peer, bool quiet = false)
        {
            if (peer == 0)
                return false;
            var blocked = BlockedReason();
            if (blocked != null)
            {
                if (!quiet)
                    _net.ShowNotice("现在不能发送进度：" + blocked);
                return false;
            }
            try
            {
                var data = GameAccess.DataStore.GetPopulatedPersistentData();
                var json = JsonUtility.ToJson(data);
                if (string.IsNullOrEmpty(json))
                    throw new InvalidDataException("存档数据为空");
                // first line: pickup objects this side has seen, so the receiver keeps its own state for them too
                var packed = ProgressTransfer.Pack(_nextId++, string.Join(",", _world.PickupGuids) + "\n" + json);
                _net.SendReliable(peer, PacketType.ProgressBegin, packed.WriteBegin);
                _sending.Enqueue((peer, packed, 0));
                Plugin.L.LogInfo($"Progress: sending {json.Length} chars ({packed.Compressed.Length} bytes, {packed.Chunks} chunks) to {_net.PeerName(peer)}");
                _net.ShowNotice($"正在把进度发给 {_net.PeerName(peer)}…");
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Progress: capture failed: {e}");
                _net.ShowNotice("打包进度失败（详见 BepInEx 日志）");
                return false;
            }
        }

        public void Update(float now)
        {
            SendChunks();
            if (_pendingJson == null)
            {
                Notice = null;
                return;
            }
            if (!_net.Connected || now - _pendingSince > ApplyTimeout)
            {
                Plugin.L.LogWarning("Progress: the received progress was not loaded in time and is dropped");
                _net.ShowNotice("收到的进度没能载入（等待超时或已断开），请让队友再按一次");
                _pendingJson = null;
                Notice = null;
                return;
            }
            var blocked = BlockedReason();
            if (blocked != null)
            {
                Notice = $"收到 {_net.PeerName(_pendingFrom)} 的进度，等你可以行动时载入（{blocked}）";
                return;
            }
            var json = _pendingJson;
            _pendingJson = null;
            Notice = null;
            Apply(json);
        }

        private void SendChunks()
        {
            var budget = ChunksPerFrame;
            while (budget > 0 && _sending.Count > 0)
            {
                var (peer, data, next) = _sending.Dequeue();
                while (budget > 0 && next < data.Chunks)
                {
                    var index = next;
                    if (!_net.SendReliable(peer, PacketType.ProgressChunk, w => data.WriteChunk(w, index)))
                    {
                        Plugin.L.LogWarning($"Progress: chunk {index}/{data.Chunks} to {_net.PeerName(peer)} could not be sent");
                        _net.ShowNotice("发送进度失败（网络），请稍后再试");
                        next = data.Chunks + 1;
                        break;
                    }
                    next++;
                    budget--;
                }
                if (next < data.Chunks)
                {
                    _sending.Enqueue((peer, data, next));
                    return;
                }
                if (next == data.Chunks)
                {
                    Plugin.L.LogInfo($"Progress: sent to {_net.PeerName(peer)}");
                    _net.ShowNotice($"进度已发给 {_net.PeerName(peer)}，对方可以行动时会自动载入");
                }
            }
        }

        public void OnBegin(ulong sender, BinaryReader r)
        {
            try
            {
                _incoming = ProgressTransfer.Incoming.Begin(sender, r);
                Notice = $"正在接收 {_net.PeerName(sender)} 的进度…";
            }
            catch (Exception e)
            {
                _incoming = null;
                Plugin.L.LogWarning($"Progress: bad transfer from {_net.PeerName(sender)}: {e.Message}");
            }
        }

        public void OnChunk(ulong sender, BinaryReader r)
        {
            if (_incoming == null)
                return;
            try
            {
                if (!_incoming.Add(sender, r))
                    return;
                var json = _incoming.Finish();
                _incoming = null;
                _pendingJson = json;
                _pendingFrom = sender;
                _pendingSince = Time.realtimeSinceStartup;
                Plugin.L.LogInfo($"Progress: received {json.Length} chars from {_net.PeerName(sender)}");
            }
            catch (Exception e)
            {
                _incoming = null;
                Plugin.L.LogWarning($"Progress: transfer from {_net.PeerName(sender)} dropped: {e.Message}");
                _net.ShowNotice("接收进度失败，请让队友再按一次");
            }
        }

        private void Apply(string json)
        {
            var store = GameAccess.DataStore;
            var manager = SaveDataManager.Instance;
            if (store == null || manager == null)
            {
                _net.ShowNotice("载入进度失败：游戏的存档系统还没准备好");
                return;
            }
            PersistentData incoming;
            try
            {
                var split = json.IndexOf('\n');
                if (split < 0)
                    throw new InvalidDataException("进度数据格式不对");
                foreach (var guid in json[..split].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    _world.MarkPickup(guid);
                json = json[(split + 1)..];
                incoming = new PersistentData();
                JsonUtility.FromJsonOverwrite(json, incoming);
                var own = store.GetPopulatedPersistentData();
                if (own == null)
                    throw new InvalidOperationException("当前进度为空");
                KeepPersonal(own, incoming);
            }
            catch (Exception e)
            {
                // nothing has been changed yet
                Plugin.L.LogError($"Progress: merge failed, nothing changed: {e}");
                _net.ShowNotice("载入进度失败，没有改动当前进度（详见 BepInEx 日志）");
                return;
            }
            try
            {
                Applying = true;
                store.PopulateFromLoadedPersistentData(incoming);
                _loaded = true;
                manager.ReloadState(false);
                Plugin.L.LogInfo($"Progress from {_net.PeerName(_pendingFrom)} loaded; reloading the current level");
                _net.ShowNotice($"已载入 {_net.PeerName(_pendingFrom)} 的进度（背包、位置、自己捡的东西保持不变）", 6f);
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Progress: load failed: {e}");
                _net.ShowNotice("载入进度时出错（详见 BepInEx 日志）；可以从自己的存档重新读取", 8f);
            }
            finally
            {
                Applying = false;
            }
        }

        /// <summary>Copies everything that stays per player from <paramref name="own"/> into <paramref name="incoming"/>.</summary>
        private void KeepPersonal(PersistentData own, PersistentData incoming)
        {
            incoming.m_playerPosition = own.m_playerPosition;
            incoming.m_activeLevelMetadataAssetReference = own.m_activeLevelMetadataAssetReference;
            incoming.m_activeLevelMetadata = own.m_activeLevelMetadata;
            incoming.m_activeScene = own.m_activeScene;
            incoming.m_characterInventory = own.m_characterInventory;
            incoming.m_stashInventory = own.m_stashInventory;
            incoming.m_loreInventory = own.m_loreInventory;
            incoming.m_collectableInventory = own.m_collectableInventory;
            incoming.m_userMapData = own.m_userMapData;
            incoming.m_statusEffects = own.m_statusEffects;
            incoming.m_stats = own.m_stats;
            incoming.m_playtime = own.m_playtime;
            incoming.m_saveCount = own.m_saveCount;
            incoming.m_timeStamp = own.m_timeStamp;
            incoming.m_currentDifficulty = own.m_currentDifficulty;
            incoming.m_lowestDifficulty = own.m_lowestDifficulty;
            incoming.m_tempLowestDifficulty = own.m_tempLowestDifficulty;
            incoming.m_difficultyDirty = own.m_difficultyDirty;
            incoming.m_gameUnlocksEnabled = own.m_gameUnlocksEnabled;

            var playerGuid = PlayerGuid();
            incoming.m_dataEntries = MergeEntries(own.m_dataEntries, incoming.m_dataEntries, playerGuid);

            var bools = new Dictionary<string, PersistentDataBool>();
            if (own.m_boolVariables != null)
                foreach (var variable in own.m_boolVariables)
                    if (variable?.GUID != null)
                        bools[variable.GUID] = variable;
            if (incoming.m_boolVariables != null)
                foreach (var variable in incoming.m_boolVariables)
                    if (variable?.GUID != null && _world.PerPlayerFlag(variable.GUID))
                        variable.Data = bools.TryGetValue(variable.GUID, out var mine) && mine.Data;
            var ints = new Dictionary<string, PersistentDataInt>();
            if (own.m_intVariables != null)
                foreach (var variable in own.m_intVariables)
                    if (variable?.GUID != null)
                        ints[variable.GUID] = variable;
            if (incoming.m_intVariables != null)
                foreach (var variable in incoming.m_intVariables)
                    if (variable?.GUID != null && _world.PerPlayerFlag(variable.GUID))
                        variable.Data = ints.TryGetValue(variable.GUID, out var mine) ? mine.Data : 0;
        }

        private Il2CppSystem.Collections.Generic.List<PersistentDataObject> MergeEntries(
            Il2CppSystem.Collections.Generic.List<PersistentDataObject> own,
            Il2CppSystem.Collections.Generic.List<PersistentDataObject> incoming, string playerGuid)
        {
            var mine = new Dictionary<string, PersistentDataObject>();
            if (own != null)
                foreach (var entry in own)
                    if (entry?.GUID != null)
                        mine[entry.GUID] = entry;
            // learn pickup GUIDs from both sides' keys before deciding entry by entry
            foreach (var list in new[] { own, incoming })
                if (list != null)
                    foreach (var entry in list)
                        if (entry?.GUID != null && ProgressTransfer.SplitKey(entry.GUID, out _, out var type) &&
                            ProgressTransfer.PickupTypes.Contains(type))
                            _world.IsPerPlayer(entry.GUID);

            bool Personal(string key) =>
                (!string.IsNullOrEmpty(playerGuid) && key.Contains(playerGuid, StringComparison.Ordinal)) || _world.IsPerPlayer(key);

            var merged = new Il2CppSystem.Collections.Generic.List<PersistentDataObject>();
            var taken = new HashSet<string>();
            var kept = 0;
            if (incoming != null)
                foreach (var entry in incoming)
                {
                    var key = entry?.GUID;
                    if (key == null || !taken.Add(key))
                        continue;
                    if (Personal(key))
                    {
                        if (mine.TryGetValue(key, out var ownEntry))
                        {
                            merged.Add(ownEntry);
                            kept++;
                        }
                        continue;   // the other player's pickup that this one never touched: left as never seen
                    }
                    merged.Add(entry);
                }
            foreach (var pair in mine)
                if (!taken.Contains(pair.Key) && Personal(pair.Key))
                {
                    merged.Add(pair.Value);
                    kept++;
                }
            Plugin.L.LogInfo($"Progress: {merged.Count} save entries, {kept} kept from this player (pickups, own character)");
            return merged;
        }

        private static string PlayerGuid()
        {
            var player = LocalPlayer.Find();
            if (player == null)
                return null;
            var identifier = player.GetComponentInChildren(Il2CppType.Of<PersistentDataIdentifier>(), true)
                ?.TryCast<PersistentDataIdentifier>();
            var guid = identifier?.GUID;
            return string.IsNullOrEmpty(guid) ? null : guid;
        }
    }
}
