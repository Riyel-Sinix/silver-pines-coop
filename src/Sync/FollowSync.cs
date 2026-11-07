using System;
using System.Collections.Generic;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// Getting to the other player. Each side reports its level as the LevelMetadata asset GUID (LevelMetadata is an
    /// AddressableScriptableObject) and whether it got there through a story transition: a LevelTransitionEventData
    /// without a LevelTransition component (Actions.RaiseLevelTransitionEventChannel, raised by PlayMaker story scripts)
    /// instead of a door, walkable edge, ledge or manhole, which all pass their LevelTransition. Going there raises
    /// the same event the story action raises (type Transition, the level metadata, its scene name and scene asset
    /// reference), so LevelManager runs its normal transition and spawn; once the player is in the level it is moved
    /// to the other player's last reported position.
    /// </summary>
    internal sealed class FollowSync
    {
        private const float ArrivalTimeout = 45f;
        private const float SettleTime = 0.6f;
        private const float StoryWindow = 30f;

        private enum Phase { Idle, Loading, Transitioning, Settling }

        private readonly SteamNetManager _net;
        private readonly Dictionary<string, AsyncOperationHandle<LevelMetadata>> _levels = new();
        private Phase _phase;
        private ulong _peer;
        private string _levelGuid;
        private uint _targetRoom;
        private float _deadline;
        private float _settleAt;
        private float _autoAt = -1f;
        private ulong _autoPeer;
        private float _autoGiveUp;
        private IntPtr _seenTransition;
        private IntPtr _ownTransition;
        private bool _lastScripted;
        private float _lastTransitionAt = -100f;

        public string Notice { get; private set; }

        public FollowSync(SteamNetManager net)
        {
            _net = net;
        }

        public bool Busy => _phase != Phase.Idle;

        private static LevelManager s_levelManager;

        /// <summary>LevelManager.IsLevelTransitionActive reads a static, but the interop exposes it per instance.</summary>
        private static bool TransitionActive()
        {
            if (s_levelManager == null)
                s_levelManager = UnityEngine.Object.FindAnyObjectByType(Il2CppInterop.Runtime.Il2CppType.Of<LevelManager>())?.TryCast<LevelManager>();
            return s_levelManager != null && s_levelManager.IsLevelTransitionActive;
        }

        /// <summary>The local level's asset GUID ("" outside a level).</summary>
        public static string CurrentLevelGuid()
        {
            try
            {
                if (!GlobalReferences.Loaded)
                    return "";
                var level = GlobalReferences.Instance.Anchors?.Generic?.ActiveLevelMetadata?.Item;
                if (level == null)
                    return "";
                var guid = level.AssetGUID;
                return string.IsNullOrEmpty(guid) ? level.AssetReference?.AssetGUID ?? "" : guid;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>Watches level transitions as they start; true when the last one was raised by a story script.</summary>
        public void Watch(float now)
        {
            try
            {
                var transition = LevelManager.ActiveTransition;
                var ptr = transition?.Pointer ?? IntPtr.Zero;
                if (ptr == IntPtr.Zero || ptr == _seenTransition)
                    return;
                _seenTransition = ptr;
                _lastTransitionAt = now;
                _lastScripted = ptr != _ownTransition && transition.m_type == LevelTransitionEventType.Transition &&
                                transition.m_levelTransitionComponent == null && !transition.m_isFastTravelBoat;
            }
            catch (Exception)
            {
                // LevelManager not set up yet
            }
        }

        /// <summary>Consumed when the room changes: did a story transition bring us here?</summary>
        public bool TakeStoryArrival(float now)
        {
            var story = _lastScripted && now - _lastTransitionAt < StoryWindow;
            _lastScripted = false;
            return story;
        }

        /// <summary>The other player changed level. Follow automatically when a story transition took them away from
        /// the room we were both in.</summary>
        public void OnPeerMoved(ulong peer, uint previousRoom, uint localRoom, bool story, float now)
        {
            if (!story || !Plugin.Cfg.FollowStoryTransitions.Value || previousRoom != localRoom || localRoom == 0 || Busy)
                return;
            _autoPeer = peer;
            _autoAt = now + Math.Max(0f, Plugin.Cfg.FollowDelay.Value);
            _autoGiveUp = _autoAt + 90f;
            Plugin.L.LogInfo($"{_net.PeerName(peer)} left through a story transition: following in {Plugin.Cfg.FollowDelay.Value:0.#} s");
        }

        /// <summary>Teleport key: go to the other player now (also skips an automatic follow's countdown).</summary>
        public void Request(ulong peer, float now)
        {
            _autoAt = -1f;
            if (peer == 0)
            {
                _net.ShowNotice("没有可以传送过去的队友");
                return;
            }
            Begin(peer, now);
        }

        public void Cancel()
        {
            _autoAt = -1f;
            _phase = Phase.Idle;
            Notice = null;
        }

        public void Update(float now, uint localRoom)
        {
            if (_autoAt >= 0f)
            {
                if (now >= _autoAt)
                {
                    if (!_net.Connected || _net.PeerRoomHash(_autoPeer) == localRoom || now > _autoGiveUp)
                    {
                        _autoAt = -1f;   // left, came back, or never possible: nothing to follow
                        Notice = null;
                        return;
                    }
                    var blocked = BlockedReason();
                    if (blocked != null)
                    {
                        _autoAt = now + 1f;   // e.g. the same story cutscene is still playing here: go when it ends
                        Notice = $"{_net.PeerName(_autoPeer)} 被剧情带去了 {SteamNetManager.ShortRoom(_net.PeerRoomName(_autoPeer))}，" +
                                 "可以行动后自动跟过去";
                        return;
                    }
                    _autoAt = -1f;
                    Begin(_autoPeer, now);
                }
                else
                {
                    Notice = $"{_net.PeerName(_autoPeer)} 被剧情带去了 {SteamNetManager.ShortRoom(_net.PeerRoomName(_autoPeer))}，" +
                             $"{Math.Ceiling(_autoAt - now):0} 秒后跟过去（按 {Plugin.Cfg.TeleportKey.Value} 立即）";
                    return;
                }
            }
            switch (_phase)
            {
                case Phase.Idle:
                    return;
                case Phase.Loading:
                    UpdateLoading(now);
                    return;
                case Phase.Transitioning:
                    if (now > _deadline)
                    {
                        Fail("传送超时，没有进入队友所在的场景");
                        return;
                    }
                    if (localRoom != _targetRoom || TransitionActive())
                        return;   // (cutscene levels may have no player: MoveToPeer then just ends)
                    _phase = Phase.Settling;
                    _settleAt = now + SettleTime;
                    return;
                case Phase.Settling:
                    if (now < _settleAt)
                        return;
                    _phase = Phase.Idle;
                    MoveToPeer(localRoom);
                    return;
            }
        }

        private void Begin(ulong peer, float now)
        {
            if (!_net.Connected)
            {
                Notice = null;
                return;
            }
            var blocked = BlockedReason();
            if (blocked != null)
            {
                Fail(blocked);
                return;
            }
            _peer = peer;
            var peerRoom = _net.PeerRoomHash(peer);
            if (peerRoom != 0 && peerRoom == _net.RoomHash)
            {
                _phase = Phase.Idle;
                MoveToPeer(_net.RoomHash);
                return;
            }
            _levelGuid = _net.PeerLevelGuid(peer);
            if (string.IsNullOrEmpty(_levelGuid))
            {
                Fail($"不知道 {_net.PeerName(peer)} 所在的场景（对方可能在菜单或过场里）");
                return;
            }
            _targetRoom = peerRoom;
            _deadline = now + ArrivalTimeout;
            if (!_levels.ContainsKey(_levelGuid))
                _levels[_levelGuid] = new AssetReferenceT<LevelMetadata>(_levelGuid).LoadAssetAsync();
            _phase = Phase.Loading;
            Notice = $"正在前往 {_net.PeerName(peer)} 所在的 {SteamNetManager.ShortRoom(_net.PeerRoomName(peer))}…";
            UpdateLoading(now);
        }

        private void UpdateLoading(float now)
        {
            var handle = _levels[_levelGuid];
            if (!handle.IsDone)
            {
                if (now > _deadline)
                    Fail("读取队友所在的场景超时");
                return;
            }
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                _levels.Remove(_levelGuid);
                Fail("找不到队友所在的场景");
                return;
            }
            var blocked = BlockedReason();
            if (blocked != null)
            {
                Fail(blocked);
                return;
            }
            var level = handle.Result;
            try
            {
                var data = new LevelTransitionEventData
                {
                    m_type = LevelTransitionEventType.Transition,
                    m_levelMetadata = level,
                    m_targetSceneName = level.m_sceneName,
                    m_targetSceneAssetReference = level.m_targetSceneAssetReference,
                };
                _ownTransition = data.Pointer;
                GlobalReferences.Instance.EventChannels.LevelTransition.LevelTransition.Raise(data);
                _targetRoom = Packets.Hash(string.IsNullOrEmpty(level.SceneFileName) ? level.name : level.SceneFileName);
                _phase = Phase.Transitioning;
                Plugin.L.LogInfo($"Following {_net.PeerName(_peer)} to {level.SceneFileName}");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Level transition to {_levelGuid} failed: {e}");
                Fail("切换场景失败（详见 BepInEx 日志）");
            }
        }

        private void MoveToPeer(uint localRoom)
        {
            Notice = null;
            if (!_net.TryGetPeerPosition(_peer, out var position, out var room) || room != localRoom)
            {
                Plugin.L.LogInfo("Teleport: arrived, the other player's position is not known in this room");
                return;
            }
            var player = LocalPlayer.Find();
            if (player == null)
                return;
            player.transform.position = position;
            Plugin.L.LogInfo($"Teleport: moved to {_net.PeerName(_peer)} at {position}");
        }

        private void Fail(string message)
        {
            _phase = Phase.Idle;
            Notice = null;
            _net.ShowNotice(message);
            Plugin.L.LogWarning($"Teleport: {message}");
        }

        /// <summary>Why a transition must not start now, or null.</summary>
        private string BlockedReason()
        {
            if (LocalPlayer.Find() == null)
                return "现在不能传送：没有可控制的角色";
            if (TransitionActive())
                return "现在不能传送：正在切换场景";
            if (GameAccess.InCutscene())
                return "现在不能传送：正在看过场";
            if ((GameAccess.MenuFlags() & (GameAccess.MenuSystem | GameAccess.MenuOptions)) != 0)
                return "现在不能传送：菜单打开着";
            if (_net.SaveBusy)
                return "现在不能传送：正在共同存读档";
            return null;
        }
    }
}
