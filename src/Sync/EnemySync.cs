using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// Enemy health in the current room. Every CharacterHealth except the local player's that sits under a
    /// PersistentDataIdentifier (level-placed enemies: same GUID on both clients) is watched; when its health drops
    /// here, the drop is sent as MonsterDamage, and the other side lowers the same enemy's Health by that much.
    /// CharacterHealth.Health's setter runs the game's own death path (OnDead, death event channel) at 0, so a kill on
    /// one side kills the enemy on the other. Deltas, not absolute values: hits landing on both sides at once add up.
    /// Enemies spawned at runtime get random GUIDs per client and are not matched.
    /// </summary>
    internal sealed class EnemySync
    {
        private const float RefreshInterval = 2f;

        private sealed class Enemy
        {
            public CharacterHealth Health;
            public IntPtr Pointer;
            public int Last;
            public string Name;
        }

        private readonly SteamNetManager _net;
        private readonly Dictionary<uint, Enemy> _enemies = new();
        private readonly List<uint> _stale = new();
        private int _healthOffset = -1;
        private uint _room;
        private float _nextRefresh;

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        public int Tracked => _enemies.Count;

        public EnemySync(SteamNetManager net)
        {
            _net = net;
        }

        public void ResetBaseline()
        {
            _enemies.Clear();
            _nextRefresh = 0f;
        }

        public void Poll(float now, uint room, bool connected)
        {
            if (room != _room || now >= _nextRefresh)
                Refresh(now, room);
            _stale.Clear();
            foreach (var pair in _enemies)
            {
                var enemy = pair.Value;
                if (enemy.Health == null)
                {
                    _stale.Add(pair.Key);
                    continue;
                }
                var health = Il2CppRaw.ReadInt(enemy.Pointer, _healthOffset);
                if (health < enemy.Last && connected)
                {
                    _net.SendMonsterDamage(new MonsterDamage(pair.Key, enemy.Last - health, 0, 0f, 0f));
                    Sent++;
                }
                enemy.Last = health;
            }
            foreach (var id in _stale)
                _enemies.Remove(id);
        }

        private void Refresh(float now, uint room)
        {
            _nextRefresh = now + RefreshInterval;
            if (room != _room)
            {
                _room = room;
                _enemies.Clear();
            }
            var player = LocalPlayer.Find();
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<CharacterHealth>(), FindObjectsSortMode.None))
            {
                var health = obj?.TryCast<CharacterHealth>();
                if (health == null)
                    continue;
                if (player != null && (health.gameObject == player || health.transform.IsChildOf(player.transform)))
                    continue;
                var identifier = health.GetComponentInParent(Il2CppType.Of<PersistentDataIdentifier>(), true)
                                       ?.TryCast<PersistentDataIdentifier>();
                var guid = identifier?.GUID;
                if (string.IsNullOrEmpty(guid))
                    continue;
                var id = Packets.Hash(guid);
                if (_enemies.ContainsKey(id))
                    continue;
                if (_healthOffset < 0)
                    _healthOffset = Il2CppRaw.FieldOffset(Il2CppRaw.ClassOf(health.Pointer), "m_currentHealth");
                if (_healthOffset < 0)
                    return;
                _enemies[id] = new Enemy
                {
                    Health = health,
                    Pointer = health.Pointer,
                    Last = Il2CppRaw.ReadInt(health.Pointer, _healthOffset),
                    Name = health.gameObject.name,
                };
            }
        }

        public void OnDamage(ulong sender, MonsterDamage damage)
        {
            if (!_enemies.TryGetValue(damage.MonsterId, out var enemy) || enemy.Health == null)
                return;   // not in our room (or a runtime-spawned enemy)
            var current = Il2CppRaw.ReadInt(enemy.Pointer, _healthOffset);
            var next = Math.Max(0, current - (int)Math.Round(damage.Amount));
            if (next == current)
                return;
            try
            {
                enemy.Health.Health = next;   // the setter clamps, and runs OnDead at 0
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Enemy sync: {enemy.Name}: {e.Message}");
                return;
            }
            enemy.Last = Il2CppRaw.ReadInt(enemy.Pointer, _healthOffset);   // not our own damage: do not send it back
            Applied++;
            Plugin.L.LogInfo($"Enemy {enemy.Name}: -{damage.Amount} from the other player -> {enemy.Last}");
        }
    }
}
