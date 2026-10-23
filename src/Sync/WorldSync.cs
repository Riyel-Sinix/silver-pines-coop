using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// World state through the game's own save layer. Every stateful object (door, pickup, interactable, trigger,
    /// puzzle, lift, ...) keeps its state in an entry of PersistentDataStore.Data.m_dataEntries: a PersistentDataObject
    /// whose key starts with the object's PersistentDataIdentifier GUID and whose Data is a small nested PersistentData
    /// class (NewSideDoor: door state / unlocked / destroyed, ItemPickup: items remaining, ...). The objects write that
    /// entry live, so polling the entries finds every change; a change is sent as raw value fields and written into the
    /// same entry on the other side, then the object re-reads it (PersistentDataIdentifier.UpdateFromDatastore) so a
    /// door opens and a picked-up item disappears there too. Entries of rooms the other player is not in are just
    /// stored and take effect when they get there. Character / AI state (health, movement, revival) is left alone:
    /// it is live per-world state, see <see cref="EnemySync"/>.
    /// Story flags (PersistentDataStore persistent bool variables) are synced the same way by persistent id.
    /// </summary>
    internal sealed class WorldSync
    {
        private const float PollInterval = 0.25f;
        private const float IdentifierRefreshInterval = 1f;

        private static readonly HashSet<string> CharacterState = new()
        {
            "CharacterHealth", "CharacterMovement", "LegacyCharacterMovement", "CharacterDirection", "CharacterNecromancy",
            "CharacterRandomizedScale", "MigratableEnemy", "AIWanderStrategy", "AIBirdFlockController",
        };

        private sealed class Entry
        {
            public IntPtr KeyPtr;             // the il2cpp string: a different one means a new entry at a reused address
            public string Key;
            public IntPtr Data;
            public Il2CppRaw.Layout Layout;   // null: not synced
            public ulong Hash;
        }

        private sealed class Variable
        {
            public string Id;
            public int Kind;                  // 0 bool, 1 int, 2 float
            public IntPtr Pointer;
            public int ValueOffset;
            public int Size;
            public ulong Hash;
        }

        private readonly SteamNetManager _net;
        private readonly Dictionary<IntPtr, Entry> _entries = new();
        private readonly Dictionary<string, IntPtr> _entryByKey = new();
        private readonly Dictionary<IntPtr, Variable> _variables = new();
        private readonly Dictionary<string, Variable> _variableById = new();
        private readonly List<(string Guid, PersistentDataIdentifier Identifier)> _identifiers = new();
        private readonly HashSet<string> _unknownClasses = new();
        private IntPtr _data;
        private int _dataEntriesOffset = -1, _entryKeyOffset = -1, _entryDataOffset = -1;
        private float _nextPoll;
        private float _nextIdentifierRefresh;
        private uint _identifierRoom;
        private bool _loggedInventory;

        public int Sent { get; private set; }
        public int Applied { get; private set; }

        public WorldSync(SteamNetManager net)
        {
            _net = net;
        }

        public void ResetBaseline() => _data = IntPtr.Zero;

        public void Poll(float now, bool connected)
        {
            if (now < _nextPoll)
                return;
            _nextPoll = now + PollInterval;
            var store = GameAccess.DataStore;
            if (store == null)
                return;
            var data = store.Data;
            if (data == null)
                return;
            if (data.Pointer != _data)
                Reset(data.Pointer);
            if (Plugin.Cfg.SyncWorld.Value)
                ScanEntries(connected);
            if (Plugin.Cfg.SyncStoryFlags.Value)
                ScanVariables(store, connected);
        }

        /// <summary>Another save was loaded (or a new game started): everything is baseline again.</summary>
        private void Reset(IntPtr data)
        {
            _data = data;
            _entries.Clear();
            _entryByKey.Clear();
            _variables.Clear();
            _variableById.Clear();
            _identifiers.Clear();
            _dataEntriesOffset = Il2CppRaw.FieldOffset(Il2CppRaw.ClassOf(data), "m_dataEntries");
        }

        private void ScanEntries(bool connected)
        {
            if (_dataEntriesOffset < 0 || !Il2CppRaw.ListItems(Il2CppRaw.ReadPointer(_data, _dataEntriesOffset), out var items, out var count))
                return;
            var synced = 0;
            for (var i = 0; i < count; i++)
            {
                var entryPtr = Il2CppRaw.ArrayElement(items, i);
                if (entryPtr == IntPtr.Zero)
                    continue;
                if (_entryKeyOffset < 0)
                {
                    var entryClass = Il2CppRaw.ClassOf(entryPtr);
                    _entryKeyOffset = Il2CppRaw.FieldOffset(entryClass, "m_guid");
                    _entryDataOffset = Il2CppRaw.FieldOffset(entryClass, "Data");
                    if (_entryKeyOffset < 0 || _entryDataOffset < 0)
                    {
                        Plugin.L.LogError("World sync: PersistentDataObject layout not found");
                        Plugin.Cfg.SyncWorld.Value = false;
                        return;
                    }
                }
                var dataObj = Il2CppRaw.ReadPointer(entryPtr, _entryDataOffset);
                var keyPtr = Il2CppRaw.ReadPointer(entryPtr, _entryKeyOffset);
                if (!_entries.TryGetValue(entryPtr, out var entry) || entry.KeyPtr != keyPtr)
                {
                    entry = new Entry { KeyPtr = keyPtr, Key = Il2CppRaw.ManagedString(keyPtr) };
                    _entries[entryPtr] = entry;
                }
                if (entry.Key != null)
                    _entryByKey[entry.Key] = entryPtr;   // the entry currently in the list wins
                if (entry.Data != dataObj)
                {
                    // first sight, or the owner created / replaced its data object: that is the baseline, not a change
                    entry.Data = dataObj;
                    entry.Layout = Syncable(dataObj);
                    entry.Hash = entry.Layout == null ? 0 : Il2CppRaw.Hash(dataObj, entry.Layout);
                    continue;
                }
                if (entry.Layout == null || entry.Key == null)
                    continue;
                synced++;
                var hash = Il2CppRaw.Hash(dataObj, entry.Layout);
                if (hash == entry.Hash)
                    continue;
                entry.Hash = hash;
                if (connected)
                    SendEntry(entry);
            }
            if (!_loggedInventory && count > 0)
            {
                _loggedInventory = true;
                Plugin.L.LogInfo($"World sync: {count} save entries, {synced} with syncable state");
            }
        }

        private static Il2CppRaw.Layout Syncable(IntPtr dataObj)
        {
            var layout = Il2CppRaw.LayoutOf(Il2CppRaw.ClassOf(dataObj));
            if (layout == null || layout.Fields.Length == 0 || CharacterState.Contains(layout.OuterName))
                return null;
            return layout;
        }

        private void SendEntry(Entry entry)
        {
            var layout = entry.Layout;
            var bytes = Il2CppRaw.Read(entry.Data, layout);
            _net.BroadcastReliable(PacketType.WorldEntry, w =>
            {
                w.Write(entry.Key);
                w.Write(layout.Image ?? "");
                w.Write(layout.Namespace ?? "");
                w.Write(layout.Name);
                w.Write((ushort)bytes.Length);
                w.Write(bytes);
            });
            Sent++;
            Plugin.L.LogDebug($"World sync: sent {layout.Name} {entry.Key}");
        }

        public void OnEntry(ulong sender, BinaryReader r, uint localRoom)
        {
            var key = r.ReadString();
            var image = r.ReadString();
            var ns = r.ReadString();
            var name = r.ReadString();
            var bytes = r.ReadBytes(r.ReadUInt16());
            if (!Plugin.Cfg.SyncWorld.Value || _data == IntPtr.Zero || _entryDataOffset < 0)
                return;
            var klass = Il2CppRaw.FindClass(image, ns, name);
            var layout = klass == IntPtr.Zero ? null : Il2CppRaw.LayoutOf(klass);
            if (layout == null)
            {
                if (_unknownClasses.Add(name))
                    Plugin.L.LogWarning($"World sync: class {name} ({image}) not found here");
                return;
            }
            if (!_entryByKey.TryGetValue(key, out var entryPtr))
            {
                // the other player has been somewhere this save has not: create the entry for when we get there
                var created = GameAccess.DataStore?.Data?.GetDataEntry(key, true);
                if (created == null)
                    return;
                entryPtr = created.Pointer;
                _entryByKey[key] = entryPtr;
                _entries[entryPtr] = new Entry { KeyPtr = Il2CppRaw.ReadPointer(entryPtr, _entryKeyOffset), Key = key };
            }
            var dataObj = Il2CppRaw.ReadPointer(entryPtr, _entryDataOffset);
            if (dataObj == IntPtr.Zero)
            {
                dataObj = Il2CppRaw.New(klass);
                if (dataObj == IntPtr.Zero)
                    return;
                new PersistentDataObject(entryPtr).Data = new Il2CppSystem.Object(dataObj);
            }
            else if (Il2CppRaw.ClassOf(dataObj) != klass)
            {
                Plugin.L.LogWarning($"World sync: {key} holds a different class here, ignored");
                return;
            }
            if (!Il2CppRaw.Write(dataObj, layout, bytes))
            {
                Plugin.L.LogWarning($"World sync: {name} has a different layout here, ignored");
                return;
            }
            var entry = _entries.TryGetValue(entryPtr, out var known)
                ? known
                : _entries[entryPtr] = new Entry { KeyPtr = Il2CppRaw.ReadPointer(entryPtr, _entryKeyOffset), Key = key };
            entry.Data = dataObj;
            entry.Layout = layout;
            entry.Hash = Il2CppRaw.Hash(dataObj, layout);   // so the poll does not send it back
            Applied++;
            Reapply(key, localRoom);
        }

        /// <summary>Has the loaded object owning <paramref name="key"/> read its entry again.</summary>
        private void Reapply(string key, uint room)
        {
            var now = Time.realtimeSinceStartup;
            if (!ReapplyLoaded(key) && (room != _identifierRoom || now >= _nextIdentifierRefresh))
            {
                RefreshIdentifiers(room, now);
                ReapplyLoaded(key);
            }
        }

        private bool ReapplyLoaded(string key)
        {
            var found = false;
            foreach (var (guid, identifier) in _identifiers)
            {
                if (!key.Contains(guid, StringComparison.Ordinal))
                    continue;
                if (identifier == null)
                    continue;
                found = true;
                try
                {
                    if (identifier.GetComponent(Il2CppType.Of<CharacterHealth>()) != null)
                        continue;   // never re-run a character's load path (it resets health)
                    identifier.m_receivedDataFromDatastore = false;   // UpdateFromDatastore runs once per object otherwise
                    identifier.UpdateFromDatastore();
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"World sync: re-applying {key} failed: {e.Message}");
                }
                Rebaseline(guid);
            }
            return found;
        }

        /// <summary>Re-reading its entries can make an object rewrite them (its own or its other components'):
        /// that is the applied state, not a local change to send back.</summary>
        private void Rebaseline(string guid)
        {
            foreach (var entry in _entries.Values)
                if (entry.Layout != null && entry.Data != IntPtr.Zero && entry.Key != null &&
                    entry.Key.Contains(guid, StringComparison.Ordinal))
                    entry.Hash = Il2CppRaw.Hash(entry.Data, entry.Layout);
        }

        private void RefreshIdentifiers(uint room, float now)
        {
            _identifierRoom = room;
            _nextIdentifierRefresh = now + IdentifierRefreshInterval;
            _identifiers.Clear();
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<PersistentDataIdentifier>(), FindObjectsSortMode.None))
            {
                var identifier = obj?.TryCast<PersistentDataIdentifier>();
                var guid = identifier?.GUID;
                if (!string.IsNullOrEmpty(guid))
                    _identifiers.Add((guid, identifier));
            }
        }

        // ---------------------------------------------------------------- story flags

        private void ScanVariables(PersistentDataStore store, bool connected)
        {
            ScanVariableList(store.m_persistentBoolVariables?.Pointer ?? IntPtr.Zero, 0, connected);
            if (Plugin.Cfg.SyncStoryNumbers.Value)
            {
                ScanVariableList(store.m_persistentIntVariables?.Pointer ?? IntPtr.Zero, 1, connected);
                ScanVariableList(store.m_persistentFloatVariables?.Pointer ?? IntPtr.Zero, 2, connected);
            }
        }

        private void ScanVariableList(IntPtr list, int kind, bool connected)
        {
            if (!Il2CppRaw.ListItems(list, out var items, out var count))
                return;
            for (var i = 0; i < count; i++)
            {
                var ptr = Il2CppRaw.ArrayElement(items, i);
                if (ptr == IntPtr.Zero)
                    continue;
                if (!_variables.TryGetValue(ptr, out var variable))
                {
                    variable = Describe(ptr, kind);
                    _variables[ptr] = variable;
                    if (variable != null)
                    {
                        _variableById[variable.Id] = variable;
                        Plugin.L.LogDebug($"Story variable [{"bool int float".Split(' ')[kind]}] {variable.Id}");
                    }
                    continue;
                }
                if (variable == null)
                    continue;
                var hash = HashValue(variable);
                if (hash == variable.Hash)
                    continue;
                variable.Hash = hash;
                if (connected)
                    SendVariable(variable);
            }
        }

        private static Variable Describe(IntPtr ptr, int kind)
        {
            string id;
            try
            {
                id = kind switch   // BaseVariable<T>.GetPersistentID of the matching instantiation
                {
                    0 => new BoolVariable(ptr).GetPersistentID(),
                    1 => new IntVariable(ptr).GetPersistentID(),
                    _ => new FloatVariable(ptr).GetPersistentID(),
                };
            }
            catch (Exception)
            {
                return null;
            }
            var offset = Il2CppRaw.FieldOffset(Il2CppRaw.ClassOf(ptr), "m_value");
            if (string.IsNullOrEmpty(id) || offset < 0)
                return null;
            var variable = new Variable { Id = id, Kind = kind, Pointer = ptr, ValueOffset = offset, Size = kind == 0 ? 1 : 4 };
            variable.Hash = HashValue(variable);
            return variable;
        }

        private static unsafe ulong HashValue(Variable v)
        {
            var p = (byte*)v.Pointer + v.ValueOffset;
            ulong h = 0;
            for (var i = 0; i < v.Size; i++)
                h = (h << 8) | p[i];
            return h;
        }

        private void SendVariable(Variable variable)
        {
            var value = HashValue(variable);
            _net.BroadcastReliable(PacketType.StoryFlag, w =>
            {
                w.Write((byte)variable.Kind);
                w.Write(variable.Id);
                w.Write(value);
            });
            Sent++;
            Plugin.L.LogInfo($"Story flag sent: {variable.Id} = {DescribeValue(variable.Kind, value)}");
        }

        public void OnVariable(ulong sender, BinaryReader r)
        {
            var kind = r.ReadByte();
            var id = r.ReadString();
            var raw = r.ReadUInt64();
            if (!Plugin.Cfg.SyncStoryFlags.Value || (kind != 0 && !Plugin.Cfg.SyncStoryNumbers.Value))
                return;
            if (!_variableById.TryGetValue(id, out var variable) || variable.Kind != kind)
            {
                Plugin.L.LogDebug($"Story flag {id} not known here");
                return;
            }
            variable.Hash = raw;   // set first: the setter's listeners must not make the poll echo it
            try
            {
                switch (kind)
                {
                    case 0:
                        new BoolVariable(variable.Pointer).Value = raw != 0;
                        break;
                    case 1:
                        new IntVariable(variable.Pointer).Value = BitConverter.ToInt32(Bytes(raw, 4), 0);
                        break;
                    default:
                        new FloatVariable(variable.Pointer).Value = BitConverter.ToSingle(Bytes(raw, 4), 0);
                        break;
                }
                variable.Hash = HashValue(variable);
                Applied++;
                Plugin.L.LogInfo($"Story flag received: {id} = {DescribeValue(kind, raw)}");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Story flag {id}: {e.Message}");
            }
        }

        /// <summary>The value bytes in memory order, as packed by <see cref="HashValue"/> (big end first).</summary>
        private static byte[] Bytes(ulong packed, int size)
        {
            var bytes = new byte[size];
            for (var i = size - 1; i >= 0; i--)
            {
                bytes[i] = (byte)packed;
                packed >>= 8;
            }
            return bytes;
        }

        private static string DescribeValue(int kind, ulong raw) => kind switch
        {
            0 => (raw != 0).ToString(),
            1 => BitConverter.ToInt32(Bytes(raw, 4), 0).ToString(),
            _ => BitConverter.ToSingle(Bytes(raw, 4), 0).ToString("0.###"),
        };
    }
}
