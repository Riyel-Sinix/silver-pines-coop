using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
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
            public bool Inventory;
        }

        private sealed class PendingInventory
        {
            public string Key;
            public uint Room;
            public bool Relay;
            public List<InventoryItemRecord> Items;
            public List<InventoryItemRecord> FlatItems;
            public List<AssetReferenceT<ItemDefinition>> References;
            public List<AsyncOperationHandle<ItemDefinition>> Handles;
            public List<bool> OwnsHandle;
            public List<AssetReferenceT<ItemDefinition>> AmmoReferences;
            public List<AsyncOperationHandle<ItemDefinition>> AmmoHandles;
            public List<bool> OwnsAmmoHandle;
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
        private readonly Dictionary<string, PendingInventory> _pendingInventories = new();
        private readonly Dictionary<string, (AssetReferenceT<ItemDefinition> Reference, AsyncOperationHandle<ItemDefinition> Handle)> _loadedDefinitions = new();
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
            PollPendingInventories();
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
            foreach (var pending in _pendingInventories.Values)
                ReleasePending(pending);
            _pendingInventories.Clear();
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
                    entry.Inventory = IsSharedInventory(dataObj);
                    entry.Layout = entry.Inventory ? null : Syncable(dataObj);
                    entry.Hash = entry.Inventory ? InventoryHash(dataObj) :
                        entry.Layout == null ? 0 : Il2CppRaw.Hash(dataObj, entry.Layout);
                    if (entry.Inventory && connected && _net.IsHost && entry.Key != null)
                        SendInventory(entry.Key, ReadInventory(dataObj));
                    continue;
                }
                if (entry.Inventory)
                {
                    if (entry.Key == null || _pendingInventories.ContainsKey(entry.Key))
                        continue;
                    synced++;
                    var inventoryHash = InventoryHash(dataObj);
                    if (inventoryHash == entry.Hash)
                        continue;
                    entry.Hash = inventoryHash;
                    if (connected)
                        SendInventory(entry.Key, ReadInventory(dataObj));
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

        private static bool IsSharedInventory(IntPtr dataObj)
        {
            if (dataObj == IntPtr.Zero)
                return false;
            return Il2CppRaw.Str(IL2CPP.il2cpp_class_get_name(Il2CppRaw.ClassOf(dataObj))) == "PersistentDataInventory";
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

        private List<InventoryItemRecord> ReadInventory(IntPtr dataObj)
        {
            try
            {
                var records = new List<InventoryItemRecord>();
                var inventory = new PersistentDataInventory(dataObj);
                var items = inventory.m_items;
                if (items == null || items.Count > InventorySnapshotCodec.MaxItems)
                    return null;
                var totalItems = 0;
                for (var i = 0; i < items.Count; i++)
                {
                    var record = ReadInventoryItem(items[i], 0, ref totalItems);
                    if (record == null)
                        return null;
                    records.Add(record);
                }
                return records;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Shared storage snapshot skipped: {e.Message}");
                return null;
            }
        }

        private InventoryItemRecord ReadInventoryItem(ItemInstance item, int depth, ref int totalItems)
        {
            if (depth > InventorySnapshotCodec.MaxDepth || ++totalItems > InventorySnapshotCodec.MaxItems || item == null)
                throw new InvalidDataException("Shared storage contains too many nested items");
            var reference = item.m_itemDefinitionAssetReference;
            var guid = reference?.AssetGUID;
            var layout = Il2CppRaw.LayoutOf(Il2CppRaw.ClassOf(item.Pointer));
            if (string.IsNullOrEmpty(guid) || layout == null || layout.Fields.Length == 0)
            {
                Plugin.L.LogWarning("Shared storage contains an item without a serializable definition; snapshot skipped");
                return null;
            }
            var fields = Il2CppRaw.Read(item.Pointer, layout);
            if (fields.Length > InventorySnapshotCodec.MaxFieldBytes)
                throw new InvalidDataException("Shared storage item state exceeds the snapshot limit");
            var record = new InventoryItemRecord
            {
                Image = layout.Image ?? "", Namespace = layout.Namespace ?? "", Type = layout.Name,
                Guid = guid, Fields = fields,
            };
            if (item.TryCast<ProjectileWeaponItemInstance>() is { } projectile)
                record.AmmoGuid = projectile.m_loadedAmmoTypeAssetGUID ?? "";

            var nestedList = item.TryCast<ProjectileWeaponItemInstance>() is { } weapon
                ? weapon.m_attachedUpgrades?.Pointer ?? IntPtr.Zero
                : item.TryCast<KeyRingItemInstance>() is { } keyRing
                    ? keyRing.m_containedItems?.Pointer ?? IntPtr.Zero
                    : IntPtr.Zero;
            if (nestedList != IntPtr.Zero)
            {
                if (!Il2CppRaw.ListItems(nestedList, out var nestedItems, out var nestedCount) ||
                    nestedCount > InventorySnapshotCodec.MaxItems)
                    throw new InvalidDataException("Shared storage contains an invalid nested item list");
                for (var i = 0; i < nestedCount; i++)
                {
                    var child = new ItemInstance(Il2CppRaw.ArrayElement(nestedItems, i));
                    var childRecord = ReadInventoryItem(child, depth + 1, ref totalItems);
                    if (childRecord == null)
                        return null;
                    record.Children.Add(childRecord);
                }
            }
            return record;
        }

        private ulong InventoryHash(IntPtr dataObj)
        {
            var records = ReadInventory(dataObj);
            if (records == null)
                return 0;
            byte[] payload;
            try { payload = InventorySnapshotCodec.Encode("", records); }
            catch (InvalidDataException e)
            {
                Plugin.L.LogWarning($"Shared storage snapshot skipped: {e.Message}");
                return 0;
            }
            var hash = 14695981039346656037ul;
            foreach (var value in payload)
            {
                hash ^= value;
                hash *= 1099511628211ul;
            }
            return hash;
        }

        private void SendInventory(string key, List<InventoryItemRecord> records)
        {
            if (records == null || _net.LocalTestMode || !_net.CheckpointPeerCompatible)
                return;
            byte[] payload;
            try { payload = InventorySnapshotCodec.Encode(key, records); }
            catch (InvalidDataException e)
            {
                Plugin.L.LogWarning($"Shared storage snapshot for {key}: {e.Message}");
                return;
            }
            if (payload.Length + 1 > Packets.MaxMessageSize)
            {
                Plugin.L.LogWarning($"Shared storage snapshot for {key} exceeds the network message limit");
                return;
            }
            if (_net.IsHost)
                _net.BroadcastReliable(PacketType.SharedInventory, w => w.Write(payload));
            else
                _net.SendReliable(_net.HostId, PacketType.SharedInventory, w => w.Write(payload));
            Sent++;
        }

        public void OnInventory(ulong sender, BinaryReader reader, uint localRoom)
        {
            if (_net.LocalTestMode || !_net.CheckpointPeerCompatible ||
                _data == IntPtr.Zero || _entryDataOffset < 0)
                return;
            if (_net.IsHost ? sender != _net.PartnerId : sender != _net.HostId)
                return;
            if (!InventorySnapshotCodec.TryDecode(reader, out var key, out var records))
                return;
            var flatItems = new List<InventoryItemRecord>();
            foreach (var record in records)
                FlattenItems(record, flatItems);
            if (!_entryByKey.TryGetValue(key, out var entryPtr))
            {
                var created = GameAccess.DataStore?.Data?.GetDataEntry(key, true);
                if (created == null)
                    return;
                entryPtr = created.Pointer;
                _entryByKey[key] = entryPtr;
            }
            var dataObj = Il2CppRaw.ReadPointer(entryPtr, _entryDataOffset);
            if (dataObj != IntPtr.Zero && !IsSharedInventory(dataObj))
                return;

            if (_pendingInventories.TryGetValue(key, out var previous))
                ReleasePending(previous);
            var pending = new PendingInventory
            {
                Key = key, Room = localRoom, Relay = _net.IsHost,
                Items = records,
                FlatItems = flatItems,
                References = new List<AssetReferenceT<ItemDefinition>>(flatItems.Count),
                Handles = new List<AsyncOperationHandle<ItemDefinition>>(flatItems.Count),
                OwnsHandle = new List<bool>(flatItems.Count),
                AmmoReferences = new List<AssetReferenceT<ItemDefinition>>(flatItems.Count),
                AmmoHandles = new List<AsyncOperationHandle<ItemDefinition>>(flatItems.Count),
                OwnsAmmoHandle = new List<bool>(flatItems.Count),
            };
            var loading = new Dictionary<string, (AssetReferenceT<ItemDefinition>, AsyncOperationHandle<ItemDefinition>)>();
            foreach (var item in flatItems)
            {
                if (_loadedDefinitions.TryGetValue(item.Guid, out var cached))
                {
                    pending.References.Add(cached.Reference);
                    pending.Handles.Add(cached.Handle);
                    pending.OwnsHandle.Add(false);
                }
                else if (loading.TryGetValue(item.Guid, out var existing))
                {
                    pending.References.Add(existing.Item1);
                    pending.Handles.Add(existing.Item2);
                    pending.OwnsHandle.Add(false);
                }
                else
                {
                    var reference = new AssetReferenceT<ItemDefinition>(item.Guid);
                    var handle = reference.LoadAssetAsync();
                    loading[item.Guid] = (reference, handle);
                    pending.References.Add(reference);
                    pending.Handles.Add(handle);
                    pending.OwnsHandle.Add(true);
                }

                if (string.IsNullOrEmpty(item.AmmoGuid))
                {
                    pending.AmmoReferences.Add(null);
                    pending.AmmoHandles.Add(default);
                    pending.OwnsAmmoHandle.Add(false);
                }
                else if (_loadedDefinitions.TryGetValue(item.AmmoGuid, out var cachedAmmo))
                {
                    pending.AmmoReferences.Add(cachedAmmo.Reference);
                    pending.AmmoHandles.Add(cachedAmmo.Handle);
                    pending.OwnsAmmoHandle.Add(false);
                }
                else if (loading.TryGetValue(item.AmmoGuid, out var existingAmmo))
                {
                    pending.AmmoReferences.Add(existingAmmo.Item1);
                    pending.AmmoHandles.Add(existingAmmo.Item2);
                    pending.OwnsAmmoHandle.Add(false);
                }
                else
                {
                    var ammoReference = new AssetReferenceT<ItemDefinition>(item.AmmoGuid);
                    var ammoHandle = ammoReference.LoadAssetAsync();
                    loading[item.AmmoGuid] = (ammoReference, ammoHandle);
                    pending.AmmoReferences.Add(ammoReference);
                    pending.AmmoHandles.Add(ammoHandle);
                    pending.OwnsAmmoHandle.Add(true);
                }
            }
            _pendingInventories[key] = pending;
            if (flatItems.Count == 0)
                ApplyInventory(pending);
        }

        private static void FlattenItems(InventoryItemRecord item, List<InventoryItemRecord> flattened)
        {
            flattened.Add(item);
            foreach (var child in item.Children)
                FlattenItems(child, flattened);
        }

        private void PollPendingInventories()
        {
            if (_pendingInventories.Count == 0)
                return;
            var keys = new List<string>(_pendingInventories.Keys);
            foreach (var key in keys)
            {
                if (!_pendingInventories.TryGetValue(key, out var pending))
                    continue;
                var ready = true;
                for (var i = 0; i < pending.FlatItems.Count; i++)
                {
                    var handle = pending.Handles[i];
                    if (!handle.IsDone)
                    {
                        ready = false;
                        break;
                    }
                    if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                    {
                        Plugin.L.LogWarning($"Shared storage item definition {pending.FlatItems[i].Guid} failed to load");
                        ready = false;
                        ReleasePending(pending);
                        _pendingInventories.Remove(key);
                        break;
                    }
                    if (!string.IsNullOrEmpty(pending.FlatItems[i].AmmoGuid))
                    {
                        var ammo = pending.AmmoHandles[i];
                        if (!ammo.IsDone)
                        {
                            ready = false;
                            break;
                        }
                        if (ammo.Status != AsyncOperationStatus.Succeeded || ammo.Result == null)
                        {
                            Plugin.L.LogWarning($"Shared storage ammunition {pending.FlatItems[i].AmmoGuid} failed to load");
                            ready = false;
                            ReleasePending(pending);
                            _pendingInventories.Remove(key);
                            break;
                        }
                    }
                }
                if (ready)
                    ApplyInventory(pending);
            }
        }

        private void ApplyInventory(PendingInventory pending)
        {
            try
            {
                var entryPtr = _entryByKey.TryGetValue(pending.Key, out var known) ? known : IntPtr.Zero;
                if (entryPtr == IntPtr.Zero)
                    return;
                var dataObj = Il2CppRaw.ReadPointer(entryPtr, _entryDataOffset);
                var inventoryClass = IL2CPP.GetIl2CppClass("CarpenterScripts.dll", "", "PersistentDataInventory");
                if (inventoryClass == IntPtr.Zero)
                    throw new InvalidOperationException("PersistentDataInventory type is unavailable");
                if (dataObj == IntPtr.Zero)
                {
                    dataObj = Il2CppRaw.New(inventoryClass);
                    if (dataObj == IntPtr.Zero)
                        throw new InvalidOperationException("Could not create shared inventory data");
                    new PersistentDataObject(entryPtr).Data = new Il2CppSystem.Object(dataObj);
                }

                var items = new Il2CppSystem.Collections.Generic.List<ItemInstance>();
                var itemIndex = 0;
                foreach (var record in pending.Items)
                    items.Add(CreateInventoryItem(record, pending, ref itemIndex));
                if (itemIndex != pending.FlatItems.Count)
                    throw new InvalidOperationException("Shared inventory item tree is inconsistent");

                var inventory = new PersistentDataInventory(dataObj) { m_items = items };
                var entry = new PersistentDataObject(entryPtr);
                entry.Data = new Il2CppSystem.Object(inventory.Pointer);
                for (var i = 0; i < pending.FlatItems.Count; i++)
                {
                    if (pending.OwnsHandle[i])
                    {
                        _loadedDefinitions[pending.FlatItems[i].Guid] = (pending.References[i], pending.Handles[i]);
                        pending.OwnsHandle[i] = false;
                    }
                    if (pending.OwnsAmmoHandle[i])
                    {
                        _loadedDefinitions[pending.FlatItems[i].AmmoGuid] = (pending.AmmoReferences[i], pending.AmmoHandles[i]);
                        pending.OwnsAmmoHandle[i] = false;
                    }
                }
                var state = _entries.TryGetValue(entryPtr, out var existing)
                    ? existing
                    : _entries[entryPtr] = new Entry { KeyPtr = Il2CppRaw.ReadPointer(entryPtr, _entryKeyOffset), Key = pending.Key };
                state.Data = inventory.Pointer;
                state.Inventory = true;
                state.Layout = null;
                state.Hash = InventoryHash(state.Data);
                Applied++;
                Reapply(pending.Key, pending.Room);
                if (pending.Relay)
                    _net.BroadcastReliable(PacketType.SharedInventory, w => w.Write(InventorySnapshotCodec.Encode(pending.Key, pending.Items)));
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Shared storage {pending.Key}: {e.Message}");
            }
            finally
            {
                ReleasePending(pending);
                _pendingInventories.Remove(pending.Key);
            }
        }

        private static ItemInstance CreateInventoryItem(InventoryItemRecord record, PendingInventory pending, ref int itemIndex)
        {
            var index = itemIndex++;
            if (index >= pending.FlatItems.Count)
                throw new InvalidOperationException("Shared inventory item tree is inconsistent");
            var klass = Il2CppRaw.FindClass(record.Image, record.Namespace, record.Type);
            if (!IsItemInstanceClass(klass))
                throw new InvalidOperationException($"Unsupported shared item type {record.Type}");
            var layout = Il2CppRaw.LayoutOf(klass);
            var pointer = layout == null ? IntPtr.Zero : Il2CppRaw.New(klass);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException($"Shared item layout mismatch: {record.Type}");
            var item = new ItemInstance(pointer)
            {
                m_itemDefinition = pending.Handles[index].Result,
                m_itemDefinitionAssetReference = pending.References[index],
            };
            if (!Il2CppRaw.Write(pointer, layout, record.Fields))
                throw new InvalidOperationException($"Shared item layout mismatch: {record.Type}");
            if (!string.IsNullOrEmpty(record.AmmoGuid) && record.Type == "ProjectileWeaponItemInstance")
            {
                var projectile = new ProjectileWeaponItemInstance(pointer)
                {
                    m_loadedAmmoTypeAssetGUID = record.AmmoGuid,
                    m_loadedAmmoType = pending.AmmoHandles[index].Result.TryCast<AmmunitionItemDefinition>(),
                };
            }
            if (record.Type == "ProjectileWeaponItemInstance")
            {
                var upgrades = new Il2CppSystem.Collections.Generic.List<ItemInstance>();
                foreach (var child in record.Children)
                    upgrades.Add(CreateInventoryItem(child, pending, ref itemIndex));
                new ProjectileWeaponItemInstance(pointer).m_attachedUpgrades = upgrades;
            }
            else if (record.Type == "KeyRingItemInstance")
            {
                var contained = new Il2CppSystem.Collections.Generic.List<ItemInstance>();
                foreach (var child in record.Children)
                    contained.Add(CreateInventoryItem(child, pending, ref itemIndex));
                new KeyRingItemInstance(pointer).m_containedItems = contained;
            }
            else if (record.Children.Count > 0)
            {
                throw new InvalidOperationException($"Unsupported nested shared item type {record.Type}");
            }
            return item;
        }

        private static bool IsItemInstanceClass(IntPtr klass)
        {
            var itemClass = IL2CPP.GetIl2CppClass("CarpenterScripts.dll", "", "ItemInstance");
            for (var current = klass; current != IntPtr.Zero; current = IL2CPP.il2cpp_class_get_parent(current))
                if (current == itemClass)
                    return true;
            return false;
        }

        private static void ReleasePending(PendingInventory pending)
        {
            for (var i = 0; i < pending.Handles.Count; i++)
                if (pending.OwnsHandle[i] && pending.Handles[i].IsValid())
                    pending.Handles[i].Release();
            for (var i = 0; i < pending.AmmoHandles.Count; i++)
                if (pending.OwnsAmmoHandle[i] && pending.AmmoHandles[i].IsValid())
                    pending.AmmoHandles[i].Release();
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
