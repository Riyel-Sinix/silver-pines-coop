using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using Il2CppInterop.Runtime.Attributes;
using SilverPinesCoop.Game;
using SilverPinesCoop.LocalTest;
using SilverPinesCoop.Steam;
using SilverPinesCoop.Sync;
using UnityEngine;

namespace SilverPinesCoop.Net
{
    public delegate void PacketHandler(ulong sender, BinaryReader reader);

    /// <summary>
    /// Co-op over Steam, as a singleton MonoBehaviour on BepInEx's persistent manager object.
    /// <list type="bullet">
    /// <item>Lobby: CreateLobbyKey (F5) creates a friends-only SteamMatchmaking lobby and opens the invite overlay; an
    /// invite accepted in the Steam overlay (GameLobbyJoinRequested_t) or a "+connect_lobby id" launch joins one;
    /// LobbyEnter_t completes both.</item>
    /// <item>Transport: SteamNetworkingMessages (no SendP2PPacket). Session requests are accepted automatically, but
    /// only from members of the current lobby.</item>
    /// <item>Channel 0, unreliable, TickRate Hz: PlayerState (position, room, drawn sprites) drives one
    /// <see cref="Puppet"/> per remote player. Channel 1, reliable: typed packets dispatched to
    /// <see cref="RegisterHandler"/> handlers (Hello, ChangeRoom, MonsterDamage, ...).</item>
    /// </list>
    /// Steam itself belongs to the game (Steamworks.NET's SteamManager, app 2333000); see <see cref="SteamNative"/>.
    /// </summary>
    public sealed class SteamNetManager : MonoBehaviour
    {
        public static SteamNetManager Instance;

        /// <summary>A remote player changed room: (peer, room hash, room name).</summary>
        public Action<ulong, uint, string> PeerChangedRoom;

        /// <summary>A remote player reported damage to a monster (reserved: not applied to the game yet).</summary>
        public Action<ulong, MonsterDamage> MonsterDamaged;

        private enum LobbyState { Idle, Creating, Joining, InLobby }

        private const float MemberRefreshInterval = 1f;
        private const float SteamProbeInterval = 1f;
        private const ulong LoopbackPeer = 1;
        private const float LocalPeerTimeout = 5f;   // two-copy test: the other copy counts as gone after this silence

        private readonly Dictionary<PacketType, PacketHandler> _handlers = new();
        private readonly Dictionary<ulong, Puppet> _puppets = new();
        private readonly Dictionary<ulong, PeerInfo> _peers = new();
        private readonly HashSet<ulong> _members = new();
        private readonly List<ulong> _scratch = new();
        private readonly List<ulong> _gone = new();
        private readonly PacketWriter _tx = new();
        private readonly PlayerSnapshot _local = new();
        private readonly PlayerSnapshot _incoming = new();
        private readonly SpriteCatalog _sprites = new();
        private readonly MaterialCatalog _materials = new();
        private readonly byte[] _rx = new byte[Packets.MaxMessageSize];
        private LocalPlayer _player;
        private MemoryStream _rxStream;
        private BinaryReader _rxReader;

        private bool _steamReady;
        private float _nextSteamProbe;
        private IntPtr _user, _utils, _friends, _matchmaking, _messages;
        private ulong _self;
        private uint _appId;
        private string _selfName = "";

        private LobbyState _state = LobbyState.Idle;
        private ulong _lobby;
        private ulong _pendingLobbyCall;    // CreateLobby / JoinLobby call in flight
        private ulong _targetLobby;         // the lobby being joined (0 while creating: id not known yet)
        private bool _refreshedThisFrame;
        private float _nextMemberRefresh;
        private float _tickAccumulator;
        private uint _sequence;
        private string _roomName = "";
        private uint _roomHash;
        private bool _playerPresent;
        private string _status = "";
        private string _fatal;
        private WorldSync _world;
        private EnemySync _enemySync;
        private PauseSync _pause;
        private SaveSync _save;
        private bool _worldSuspended;
        private CutsceneSync _cutscenes;
        private GUIStyle _statusStyle;
        private GUIStyle _noticeStyle;
        private GUIStyle _activityStyle;
        private int _styleHeight;
        private bool _quitting;
        private LocalLink _link;            // two-copy test over 127.0.0.1 instead of a Steam lobby (--coop-local)
        private TestWindow _window;
        private float _peerLastHeard;
        private static string[] s_commandLine;

        public SteamNetManager(IntPtr ptr) : base(ptr) { }

        // ------------------------------------------------------------------ public API

        public bool InLobby
        {
            [HideFromIl2Cpp] get => _state == LobbyState.InLobby;
        }

        public ulong LobbyId
        {
            [HideFromIl2Cpp] get => _lobby;
        }

        public ulong LocalSteamId
        {
            [HideFromIl2Cpp] get => _self;
        }

        /// <summary>True while another player is connected (lobby with 2+ members, or the other two-copy window).</summary>
        public bool Connected
        {
            [HideFromIl2Cpp] get => _link != null ? _members.Contains(_link.PeerId) : _state == LobbyState.InLobby && _members.Count >= 2;
        }

        /// <summary>Keep simulating while the window is not focused: always in the two-copy test, else while connected.</summary>
        public bool KeepRunning
        {
            [HideFromIl2Cpp] get => _link != null || Connected;
        }

        [HideFromIl2Cpp]
        public ulong PartnerId
        {
            get
            {
                if (_members.Count != 2)
                    return 0;
                foreach (var member in _members)
                    if (member != _self)
                        return member;
                return 0;
            }
        }

        [HideFromIl2Cpp]
        public ulong HostId
        {
            get
            {
                if (_link != null)
                    return _link.Copy == 1 ? _self : _link.PeerId;
                if (!_steamReady || _state != LobbyState.InLobby || _lobby == 0)
                    return 0;
                return SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyOwner(_matchmaking, _lobby);
            }
        }

        [HideFromIl2Cpp]
        public bool IsHost => HostId == _self && _self != 0;

        [HideFromIl2Cpp]
        public bool LocalTestMode => _link != null;

        [HideFromIl2Cpp]
        public bool CheckpointPeerCompatible
        {
            get
            {
                var peer = PartnerId;
                return peer != 0 && _peers.TryGetValue(peer, out var info) &&
                       info.ProtocolVersion == Packets.ProtocolVersion && info.GameVersion == Application.version &&
                       info.ModVersion == Plugin.Version;
            }
        }

        [HideFromIl2Cpp]
        public string PeerName(ulong peer) => Name(peer);

        /// <summary>Handle a reliable packet type (channel 1). One handler per type; registering again replaces it.</summary>
        [HideFromIl2Cpp]
        public void RegisterHandler(PacketType type, PacketHandler handler) => _handlers[type] = handler;

        /// <summary>Send a reliable packet to one lobby member; <paramref name="body"/> writes the payload after the type byte.</summary>
        [HideFromIl2Cpp]
        public bool SendReliable(ulong peer, PacketType type, Action<BinaryWriter> body)
        {
            var w = _tx.Begin(type);
            body?.Invoke(w);
            w.Flush();
            return Send(peer, Packets.ChannelReliable, SteamNative.SendReliable | SteamNative.SendAutoRestartBrokenSession);
        }

        /// <summary>Send a reliable packet to every other lobby member.</summary>
        [HideFromIl2Cpp]
        public void BroadcastReliable(PacketType type, Action<BinaryWriter> body)
        {
            var w = _tx.Begin(type);
            body?.Invoke(w);
            w.Flush();
            if (_link != null)
            {
                Send(_link.PeerId, Packets.ChannelReliable, 0);
                return;
            }
            foreach (var peer in _members)
                if (peer != _self)
                    Send(peer, Packets.ChannelReliable, SteamNative.SendReliable | SteamNative.SendAutoRestartBrokenSession);
        }

        /// <summary>Tell the others which room (Unity scene) we are in. Sent automatically whenever the room changes.</summary>
        [HideFromIl2Cpp]
        public void SendChangeRoom() => BroadcastReliable(PacketType.ChangeRoom, WriteRoom);

        [HideFromIl2Cpp]
        private void WriteRoom(BinaryWriter w)
        {
            w.Write(_roomHash);
            w.Write(_roomName);
        }

        /// <summary>Reserved: report damage we dealt to a monster (monsters still need a shared network id).</summary>
        [HideFromIl2Cpp]
        public void SendMonsterDamage(MonsterDamage damage) => BroadcastReliable(PacketType.MonsterDamage, damage.Write);

        [HideFromIl2Cpp]
        public void CreateLobby()
        {
            if (_link != null || !_steamReady || _state != LobbyState.Idle)
                return;
            var call = SteamNative.SteamAPI_ISteamMatchmaking_CreateLobby(_matchmaking, SteamNative.LobbyTypeFriendsOnly,
                                                                         Math.Max(2, Plugin.Cfg.MaxPlayers.Value));
            _pendingLobbyCall = call;
            _targetLobby = 0;
            SteamCallbackPump.ExpectCallResult(call, SteamNative.CallbackLobbyCreated);
            _state = LobbyState.Creating;
            Plugin.L.LogInfo("Creating lobby...");
        }

        [HideFromIl2Cpp]
        public void JoinLobby(ulong lobby)
        {
            if (_link != null || !_steamReady || lobby == 0 || lobby == _lobby)
                return;
            if (_state != LobbyState.Idle)
                LeaveLobby();
            var call = SteamNative.SteamAPI_ISteamMatchmaking_JoinLobby(_matchmaking, lobby);
            _pendingLobbyCall = call;
            _targetLobby = lobby;
            SteamCallbackPump.ExpectCallResult(call, SteamNative.CallbackLobbyEnter);
            _state = LobbyState.Joining;
            Plugin.L.LogInfo($"Joining lobby {lobby}...");
        }

        [HideFromIl2Cpp]
        public void LeaveLobby()
        {
            _save?.Disconnect();
            if (_link != null)
                return;   // the two-copy test link is not a lobby
            if (_lobby != 0 && SteamAlive())
            {
                foreach (var peer in _members)
                    if (peer != _self)
                        CloseSession(peer);
                SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(_matchmaking, _lobby);
                Plugin.L.LogInfo($"Left lobby {_lobby}");
            }
            _lobby = 0;
            _pendingLobbyCall = 0;
            _targetLobby = 0;
            _state = LobbyState.Idle;
            _pause?.Release();
            _members.Clear();
            _peers.Clear();
            foreach (var pair in _puppets)
                if (pair.Key != LoopbackPeer)
                    pair.Value.Destroy();
            _scratch.Clear();
            foreach (var key in _puppets.Keys)
                if (key != LoopbackPeer)
                    _scratch.Add(key);
            foreach (var key in _scratch)
                _puppets.Remove(key);
        }

        [HideFromIl2Cpp]
        public void OpenInviteOverlay()
        {
            if (_steamReady && _lobby != 0)
                SteamNative.SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(_friends, _lobby);
        }

        // ------------------------------------------------------------------ Unity messages

        private void Awake()
        {
            Instance = this;
            _player = new LocalPlayer(_sprites, _materials);
            _rxStream = new MemoryStream(_rx, 0, _rx.Length, true, true);   // fixed capacity, never reallocates
            _rxReader = new BinaryReader(_rxStream);
            RegisterHandler(PacketType.Hello, OnHello);
            RegisterHandler(PacketType.ChangeRoom, OnChangeRoom);
            RegisterHandler(PacketType.MonsterDamage, OnMonsterDamage);
            _world = new WorldSync(this);
            _enemySync = new EnemySync(this);
            _pause = new PauseSync(this);
            _save = new SaveSync(this, _pause);
            _cutscenes = new CutsceneSync(this);
            RegisterHandler(PacketType.PauseState, _pause.OnState);
            RegisterHandler(PacketType.WorldEntry, OnWorldEntry);
            RegisterHandler(PacketType.SharedInventory, OnSharedInventory);
            RegisterHandler(PacketType.StoryFlag, OnStoryFlag);
            RegisterHandler(PacketType.CutsceneStart, OnCutsceneStart);
            RegisterHandler(PacketType.CheckpointSession, _save.OnSession);
            RegisterHandler(PacketType.CheckpointSaveRequest, _save.OnSaveRequest);
            RegisterHandler(PacketType.CheckpointSavePrepare, _save.OnSavePrepare);
            RegisterHandler(PacketType.CheckpointSaveAck, _save.OnSaveAck);
            RegisterHandler(PacketType.CheckpointSaveFinish, _save.OnSaveFinish);
            RegisterHandler(PacketType.CheckpointLoadRequest, _save.OnLoadRequest);
            RegisterHandler(PacketType.CheckpointLoadPrepare, _save.OnLoadPrepare);
            RegisterHandler(PacketType.CheckpointLoadAck, _save.OnLoadAck);
            RegisterHandler(PacketType.CheckpointLoadFinish, _save.OnLoadFinish);
            RegisterHandler(PacketType.CheckpointAbort, _save.OnAbort);
            RegisterHandler(PacketType.CheckpointDeath, _save.OnDeathRequest);
            RegisterHandler(PacketType.CheckpointSessionAck, _save.OnSessionAck);
            RegisterHandler(PacketType.CheckpointReady, _save.OnReady);
            RegisterHandler(PacketType.CheckpointExecute, _save.OnExecute);
            if (HasLaunchFlag(LocalLink.LaunchFlag))
                StartLocalTest();
        }

        private void Update()
        {
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                Plugin.L.LogError(e);
            }
        }

        private void OnGUI()
        {
            EnsureStyles();
            var notice = _pause?.Notice;
            if (!string.IsNullOrEmpty(notice))
            {
                var w = Screen.width * 0.6f;
                var h = _noticeStyle.fontSize * 3f;
                GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height * 0.4f, w, h), notice, _noticeStyle);
            }
            var camera = Camera.main;
            if (camera != null && _pause != null)
                foreach (var pair in _puppets)
                {
                    var activity = _pause.Activity(pair.Key);
                    if (string.IsNullOrEmpty(activity) || !pair.Value.TryGetWorldPosition(out var position))
                        continue;
                    var screen = camera.WorldToScreenPoint(position + Vector3.up * 1.35f);
                    if (screen.z <= 0f)
                        continue;
                    const float width = 220f;
                    var height = _activityStyle.fontSize * 1.6f;
                    var activityArea = new Rect(screen.x - width / 2f, Screen.height - screen.y - height / 2f, width, height);
                    var activityColor = GUI.color;
                    GUI.color = Color.black;
                    GUI.Label(new Rect(activityArea.x + 1f, activityArea.y + 1f, activityArea.width, activityArea.height), activity, _activityStyle);
                    GUI.color = activityColor;
                    GUI.Label(activityArea, activity, _activityStyle);
                }
            if (!Plugin.Cfg.ShowStatus.Value || string.IsNullOrEmpty(_status))
                return;
            var area = new Rect(12f, 8f, Screen.width * 0.5f, _statusStyle.fontSize * 10f);
            var color = GUI.color;
            GUI.color = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), _status, _statusStyle);
            GUI.color = color;
            GUI.Label(area, _status, _statusStyle);
        }

        /// <summary>Text sized for the window: the default IMGUI font is tiny at 1440p / 1600p.</summary>
        [HideFromIl2Cpp]
        private void EnsureStyles()
        {
            if (_statusStyle != null && _styleHeight == Screen.height)
                return;
            _styleHeight = Screen.height;
            _statusStyle = new GUIStyle(GUI.skin.label) { fontSize = Math.Max(14, Screen.height / 60), wordWrap = false };
            _noticeStyle = new GUIStyle(GUI.skin.box) { fontSize = Math.Max(18, Screen.height / 32), alignment = TextAnchor.MiddleCenter };
            _activityStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Math.Max(16, Screen.height / 48), alignment = TextAnchor.MiddleCenter, wordWrap = false,
                normal = { textColor = Color.white },
            };
        }

        private void OnApplicationQuit()
        {
            // still before SteamManager.OnDestroy shuts Steam down; afterwards no Steam call is safe any more
            LeaveLobby();
            _pause?.Release();
            _steamReady = false;
            _quitting = true;
            _link?.Dispose();
        }

        private void OnDestroy()
        {
            LeaveLobby();
            _save?.Dispose();
            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------------ frame

        [HideFromIl2Cpp]
        private void Frame()
        {
            if (_quitting)
                return;
            var now = Time.realtimeSinceStartup;
            _refreshedThisFrame = false;
            if (_fatal != null)
            {
                _status = "联机：" + _fatal;
                return;
            }
            if (_link != null)
            {
                _window?.Update(now);
                HandleKeys();
                PollLink(now);
            }
            else
            {
                if (!_steamReady)
                    ProbeSteam(now);
                HandleKeys();
                if (_steamReady)
                {
                    PumpCallbacks();
                    if (_state == LobbyState.InLobby && now >= _nextMemberRefresh)
                        RefreshMembers(now);
                    Receive(Packets.ChannelUnreliable);
                    Receive(Packets.ChannelReliable);
                }
            }

            _save.Update(now);
            if (_worldSuspended && !_save.SuppressWorldSync)
            {
                _world.ResetBaseline();
                _enemySync.ResetBaseline();
            }
            _worldSuspended = _save.SuppressWorldSync;
            _pause.Update(now, Connected);

            _tickAccumulator += Time.unscaledDeltaTime;
            var interval = 1f / Mathf.Clamp(Plugin.Cfg.TickRate.Value, 1, 60);
            if (_tickAccumulator >= interval)
            {
                _tickAccumulator = Math.Min(_tickAccumulator - interval, interval);
                Tick(now);
            }

            if (_puppets.Count == 0)
                return;
            var delay = Plugin.Cfg.InterpolationDelay.Value;
            var depth = PuppetDepth();
            foreach (var pair in _puppets)
            {
                var offset = pair.Key == LoopbackPeer ? new Vector3(Plugin.Cfg.LoopbackOffsetX.Value, 0f, 0f) : Vector3.zero;
                pair.Value.Render(now, delay, _roomHash, _playerPresent, offset + depth, _sprites, _materials);
            }
        }

        /// <summary>Puppets sit slightly behind the local player along the camera's view axis: at the same depth and
        /// sorting order the two players' sprites flicker in front of each other.</summary>
        [HideFromIl2Cpp]
        private Vector3 PuppetDepth()
        {
            var distance = Plugin.Cfg.PuppetDepthOffset.Value;
            if (distance == 0f)
                return Vector3.zero;
            var camera = Camera.main;
            var forward = camera != null ? camera.transform.forward : Vector3.forward;
            return forward * distance;
        }

        [HideFromIl2Cpp]
        private void Tick(float now)
        {
            UpdateRoom();
            UpdateStatus();
            var connected = Connected && !_save.SuppressWorldSync;
            _world.Poll(now, connected);
            if (Plugin.Cfg.SyncEnemies.Value && !_save.SuppressWorldSync)
                _enemySync.Poll(now, _roomHash, connected);
            if (!_save.SuppressWorldSync)
                _cutscenes.Update(now, _roomHash, connected);
            var loopback = Plugin.Cfg.LoopbackTest.Value;
            var sending = _state == LobbyState.InLobby && (_link != null || _members.Count >= 2);
            if (!loopback && !sending && _puppets.Count == 0)
            {
                _playerPresent = LocalPlayer.Find() != null;
                return;
            }
            _playerPresent = _player.Capture(_local, _roomHash);
            _local.Sequence = ++_sequence;

            if (loopback)
            {
                _local.ReceivedAt = now;
                Puppet(LoopbackPeer, "loopback").Push(_local);
            }
            else if (_puppets.TryGetValue(LoopbackPeer, out var loop))
            {
                loop.Destroy();
                _puppets.Remove(LoopbackPeer);
            }

            if (!sending)
                return;
            var w = _tx.Begin(PacketType.PlayerState);
            _local.Write(w);
            w.Flush();
            if (_link != null)
                Send(_link.PeerId, Packets.ChannelUnreliable, 0);   // also how the other copy finds out we are here
            else
                foreach (var peer in _members)
                    if (peer != _self)
                        Send(peer, Packets.ChannelUnreliable, SteamNative.SendUnreliableNoNagle | SteamNative.SendAutoRestartBrokenSession);
        }

        [HideFromIl2Cpp]
        private void UpdateRoom()
        {
            var name = LocalPlayer.CurrentRoom() ?? "";
            if (name == _roomName)
                return;
            _roomName = name;
            _roomHash = Packets.Hash(_roomName);
            _sprites.Invalidate();
            Plugin.L.LogInfo($"Room: {_roomName}");
            if (_state == LobbyState.InLobby)
                SendChangeRoom();
        }

        [HideFromIl2Cpp]
        private void HandleKeys()
        {
            var input = UnityInput.Current;
            if (_link == null)
            {
                if (input.GetKeyDown(Plugin.Cfg.CreateLobbyKey.Value))
                {
                    if (_state == LobbyState.Idle)
                        CreateLobby();
                    else
                        OpenInviteOverlay();
                }
                if (input.GetKeyDown(Plugin.Cfg.InviteKey.Value))
                    OpenInviteOverlay();
                if (input.GetKeyDown(Plugin.Cfg.LeaveKey.Value))
                    LeaveLobby();
            }
            if (input.GetKeyDown(Plugin.Cfg.LoopbackKey.Value))
            {
                var on = Plugin.Cfg.LoopbackTest.Value = !Plugin.Cfg.LoopbackTest.Value;
                Plugin.L.LogInfo($"Loopback test {(on ? "on" : "off")}");
            }
            if (input.GetKeyDown(Plugin.Cfg.SaveCheckpointKey.Value))
                _save?.RequestSave();
        }

        // ------------------------------------------------------------------ Steam

        [HideFromIl2Cpp]
        private void ProbeSteam(float now)
        {
            if (now < _nextSteamProbe)
                return;
            _nextSteamProbe = now + SteamProbeInterval;
            if (SteamNative.LoadedModule == IntPtr.Zero || SteamNative.SteamAPI_GetHSteamUser() == 0)
                return;   // the game's SteamManager has not initialized Steam yet
            _user = SteamNative.SteamAPI_SteamUser_v021();
            _utils = SteamNative.SteamAPI_SteamUtils_v010();
            _friends = SteamNative.SteamAPI_SteamFriends_v017();
            _matchmaking = SteamNative.SteamAPI_SteamMatchmaking_v009();
            _messages = SteamNative.SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
            if (_user == IntPtr.Zero || _utils == IntPtr.Zero || _friends == IntPtr.Zero || _matchmaking == IntPtr.Zero ||
                _messages == IntPtr.Zero)
                return;
            _self = SteamNative.SteamAPI_ISteamUser_GetSteamID(_user);
            if (_self == 0)
                return;
            try
            {
                SteamCallbackPump.TryInstall();
            }
            catch (Exception e)
            {
                _fatal = "Steam 回调挂钩失败，联机不可用（详见 BepInEx 日志）";
                Plugin.L.LogError($"Could not hook SteamAPI_ManualDispatch_GetNextCallback, co-op disabled: {e}");
                return;
            }
            var netUtils = SteamNative.SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
            if (netUtils != IntPtr.Zero)
                SteamNative.SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(netUtils);
            _appId = SteamNative.SteamAPI_ISteamUtils_GetAppID(_utils);
            _selfName = SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetPersonaName(_friends)) ?? "";
            _steamReady = true;
            Plugin.L.LogInfo($"Steam ready: app {_appId}, {_selfName} ({_self})");
            var lobby = CommandLineLobby();
            if (lobby != 0)
                JoinLobby(lobby);
        }

        [HideFromIl2Cpp]
        private void PumpCallbacks()
        {
            while (SteamCallbackPump.TryDequeue(out var cb))
            {
                switch (cb.Id)
                {
                    case SteamNative.CallbackLobbyCreated:
                        OnLobbyCreated(cb);
                        break;
                    case SteamNative.CallbackLobbyEnter:
                        OnLobbyEnter(cb);
                        break;
                    case SteamNative.CallbackGameLobbyJoinRequested:
                        // GameLobbyJoinRequested_t: CSteamID m_steamIDLobby, CSteamID m_steamIDFriend
                        var lobby = BitConverter.ToUInt64(cb.Data, 0);
                        var friend = BitConverter.ToUInt64(cb.Data, 8);
                        Plugin.L.LogInfo($"Join requested through Steam: lobby {lobby} (from {Name(friend)})");
                        JoinLobby(lobby);
                        break;
                    case SteamNative.CallbackLobbyChatUpdate:
                        // LobbyChatUpdate_t: lobby, user changed, user making change, state change flags
                        if (BitConverter.ToUInt64(cb.Data, 0) == _lobby)
                            _nextMemberRefresh = 0f;
                        break;
                    case SteamNative.CallbackSessionRequest:
                        OnSessionRequest(cb);
                        break;
                    case SteamNative.CallbackSessionFailed:
                        // SteamNetworkingMessagesSessionFailed_t: SteamNetConnectionInfo_t, m_identityRemote first
                        Plugin.L.LogWarning($"Session with {Name(BitConverter.ToUInt64(cb.Data, 8))} failed");
                        break;
                }
            }
        }

        [HideFromIl2Cpp]
        private void OnLobbyCreated(SteamCallback cb)
        {
            // LobbyCreated_t: EResult m_eResult, uint64 m_ulSteamIDLobby
            var result = BitConverter.ToInt32(cb.Data, 0);
            var lobby = BitConverter.ToUInt64(cb.Data, 8);
            if (cb.ApiCall != _pendingLobbyCall)
            {
                // a CreateLobby we gave up on (left / joined another lobby meanwhile)
                if (!cb.IoFailure && result == SteamNative.ResultOK && lobby != 0 && lobby != _lobby && SteamAlive())
                    SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(_matchmaking, lobby);
                return;
            }
            if (cb.IoFailure || result != SteamNative.ResultOK || lobby == 0)
            {
                Plugin.L.LogError($"CreateLobby failed (EResult {result}, io failure {cb.IoFailure})");
                _state = LobbyState.Idle;
                return;
            }
            Plugin.L.LogInfo($"Lobby {lobby} created");
            if (_state == LobbyState.Creating)
                _targetLobby = lobby;   // LobbyEnter_t (before or after this) finishes the setup
        }

        [HideFromIl2Cpp]
        private unsafe void OnLobbyEnter(SteamCallback cb)
        {
            // LobbyEnter_t (pack 8): uint64 lobby, uint32 chat permissions, bool locked, uint32 response @16
            var lobby = BitConverter.ToUInt64(cb.Data, 0);
            var response = BitConverter.ToUInt32(cb.Data, 16);
            if (_state == LobbyState.InLobby && lobby == _lobby)
                return;   // delivered twice: as the JoinLobby call result and as a broadcast
            if (_state != LobbyState.Creating && _state != LobbyState.Joining)
                return;
            if (cb.ApiCall != 0 && cb.ApiCall != _pendingLobbyCall)
                return;   // result of an older JoinLobby
            if (_targetLobby != 0 && lobby != _targetLobby)
                return;   // some other lobby (e.g. one we created and then abandoned)
            if (response != SteamNative.ChatRoomEnterSuccess)
            {
                Plugin.L.LogError($"Entering lobby {lobby} failed (EChatRoomEnterResponse {response})");
                _state = LobbyState.Idle;
                return;
            }
            var created = _state == LobbyState.Creating;
            _lobby = lobby;
            _state = LobbyState.InLobby;
            _pendingLobbyCall = 0;
            _targetLobby = 0;
            if (created)
            {
                SetLobbyData("sp_coop", Packets.ProtocolVersion.ToString());
                SetLobbyData("sp_coop_game", Application.version);
                OpenInviteOverlay();
            }
            else
            {
                var protocol = GetLobbyData("sp_coop");
                if (protocol != Packets.ProtocolVersion.ToString())
                    Plugin.L.LogWarning($"Lobby {lobby} has co-op protocol '{protocol}', this mod speaks {Packets.ProtocolVersion}");
            }
            Plugin.L.LogInfo($"In lobby {lobby} (owner {Name(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyOwner(_matchmaking, lobby))})");
            RefreshMembers(Time.realtimeSinceStartup);
            SendChangeRoom();
        }

        [HideFromIl2Cpp]
        private unsafe void OnSessionRequest(SteamCallback cb)
        {
            // SteamNetworkingMessagesSessionRequest_t: SteamNetworkingIdentity m_identityRemote
            var type = BitConverter.ToInt32(cb.Data, 0);
            var peer = BitConverter.ToUInt64(cb.Data, 8);
            if (type == SteamNative.IdentitySteamIdType && _state == LobbyState.InLobby && !_members.Contains(peer))
                RefreshMembers(Time.realtimeSinceStartup);   // the request can arrive before LobbyChatUpdate_t
            if (type != SteamNative.IdentitySteamIdType || !_members.Contains(peer))
            {
                Plugin.L.LogWarning($"Ignored a session request from {peer} (not in our lobby)");
                return;
            }
            var identity = SteamNative.Identity(peer);
            var ok = SteamNative.SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_messages, &identity) != 0;
            Plugin.L.LogInfo($"Session with {Name(peer)} {(ok ? "accepted" : "could not be accepted")}");
        }

        [HideFromIl2Cpp]
        private void RefreshMembers(float now)
        {
            _nextMemberRefresh = now + MemberRefreshInterval;
            var count = SteamNative.SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(_matchmaking, _lobby);
            _scratch.Clear();
            for (var i = 0; i < count; i++)
            {
                var member = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(_matchmaking, _lobby, i);
                if (member != 0)
                    _scratch.Add(member);
            }
            foreach (var member in _scratch)
            {
                if (member == _self)
                    _members.Add(member);
                else if (!_members.Contains(member))
                    AddPeer(member);
            }
            _gone.Clear();
            foreach (var member in _members)
                if (!_scratch.Contains(member))
                    _gone.Add(member);
            foreach (var member in _gone)
                RemovePeer(member);
        }

        [HideFromIl2Cpp]
        private void AddPeer(ulong peer)
        {
            _members.Add(peer);
            _peers[peer] = new PeerInfo { Name = Name(peer) };
            Plugin.L.LogInfo($"{_peers[peer].Name} joined");
            SendHello(peer);   // with Steam this also opens our side of the session
            _pause.SendTo(peer);
        }

        [HideFromIl2Cpp]
        private void RemovePeer(ulong peer)
        {
            Plugin.L.LogInfo($"{Name(peer)} left");
            _members.Remove(peer);
            _peers.Remove(peer);
            _pause.Forget(peer);
            if (_link == null)
                CloseSession(peer);
            if (_puppets.TryGetValue(peer, out var puppet))
            {
                puppet.Destroy();
                _puppets.Remove(peer);
            }
        }

        // ------------------------------------------------------------------ two-copy test

        [HideFromIl2Cpp]
        private void StartLocalTest()
        {
            var port = Plugin.Cfg.LocalTestPort.Value;
            _link = LocalLink.Open(port);
            if (_link == null)
            {
                _fatal = $"双开测试：本机端口 {port} 和 {port + 1} 都被占用";
                Plugin.L.LogError($"Two-copy test: UDP ports {port} and {port + 1} on 127.0.0.1 are both taken");
                return;
            }
            _self = _link.LocalId;
            _selfName = $"窗口 {_link.Copy}";
            _members.Add(_self);
            _state = LobbyState.InLobby;
            var width = Plugin.Cfg.LocalTestWindowWidth.Value;
            if (width >= 0)
                _window = new TestWindow(_link.Copy, width);
            Plugin.L.LogInfo($"Two-copy test: this is copy {_link.Copy}, 127.0.0.1:{_link.LocalId} <-> {_link.PeerId}");
        }

        [HideFromIl2Cpp]
        private void PollLink(float now)
        {
            var peer = _link.PeerId;
            for (var budget = 256; budget > 0; budget--)
            {
                if (!_link.TryReceive(out var channel, out var buffer, out var offset, out var length))
                    break;
                if (length <= 0 || length > _rx.Length)
                    continue;
                _peerLastHeard = now;
                if (!_members.Contains(peer))
                    AddPeer(peer);
                _rxStream.SetLength(length);   // first: growing the length zero-fills the new range
                Buffer.BlockCopy(buffer, offset, _rx, 0, length);
                _rxStream.Position = 0;
                try
                {
                    Dispatch(peer, channel);
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"Bad packet on channel {channel}: {e.Message}");
                }
            }
            if (_members.Contains(peer) && now - _peerLastHeard > LocalPeerTimeout)
                RemovePeer(peer);
        }

        // ------------------------------------------------------------------ messages

        [HideFromIl2Cpp]
        private unsafe bool Send(ulong peer, int channel, int flags)
        {
            if (_link != null)
                return _link.Send(channel, _tx.Buffer, _tx.Length);
            if (!_steamReady || peer == 0)
                return false;
            var identity = SteamNative.Identity(peer);
            int result;
            fixed (byte* data = _tx.Buffer)
                result = SteamNative.SteamAPI_ISteamNetworkingMessages_SendMessageToUser(_messages, &identity, data, (uint)_tx.Length, flags, channel);
            return result == SteamNative.ResultOK;
        }

        [HideFromIl2Cpp]
        private unsafe void Receive(int channel)
        {
            const int batch = 32;
            var messages = stackalloc IntPtr[batch];
            for (var round = 0; round < 16; round++)
            {
                var n = SteamNative.SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_messages, channel, messages, batch);
                if (n <= 0)
                    return;
                for (var i = 0; i < n; i++)
                {
                    var msg = (byte*)messages[i];
                    try
                    {
                        var peerType = *(int*)(msg + SteamNative.MsgPeerOffset);
                        var peer = *(ulong*)(msg + SteamNative.MsgPeerOffset + 8);
                        var data = *(byte**)(msg + SteamNative.MsgDataOffset);
                        var size = *(int*)(msg + SteamNative.MsgSizeOffset);
                        if (peerType != SteamNative.IdentitySteamIdType || !IsMember(peer) || peer == _self ||
                            size <= 0 || size > _rx.Length || data == null)
                            continue;
                        _rxStream.SetLength(size);   // first: growing the length zero-fills the new range
                        Marshal.Copy((IntPtr)data, _rx, 0, size);
                        _rxStream.Position = 0;
                        Dispatch(peer, channel);
                    }
                    catch (Exception e)
                    {
                        Plugin.L.LogWarning($"Bad packet on channel {channel}: {e.Message}");
                    }
                    finally
                    {
                        SteamNative.SteamAPI_SteamNetworkingMessage_t_Release(messages[i]);
                    }
                }
                if (n < batch)
                    return;
            }
        }

        [HideFromIl2Cpp]
        private void Dispatch(ulong peer, int channel)
        {
            var type = (PacketType)_rxReader.ReadByte();
            if (channel == Packets.ChannelUnreliable)
            {
                if (type != PacketType.PlayerState)
                    return;
                _incoming.Read(_rxReader);
                _incoming.ReceivedAt = Time.realtimeSinceStartup;
                Puppet(peer, Name(peer)).Push(_incoming);
                return;
            }
            if (_handlers.TryGetValue(type, out var handler))
                handler(peer, _rxReader);
            else
                Plugin.L.LogWarning($"No handler for reliable packet {type} from {Name(peer)}");
        }

        [HideFromIl2Cpp]
        private void SendHello(ulong peer) => SendReliable(peer, PacketType.Hello, WriteHello);

        [HideFromIl2Cpp]
        private void WriteHello(BinaryWriter w)
        {
            w.Write(Packets.ProtocolVersion);
            w.Write(Plugin.Version);
            w.Write(Application.version);
            w.Write(_roomHash);
            w.Write(_roomName);
        }

        [HideFromIl2Cpp]
        private void OnHello(ulong peer, BinaryReader r)
        {
            var protocol = r.ReadUInt16();
            var mod = r.ReadString();
            var game = r.ReadString();
            var roomHash = r.ReadUInt32();
            var roomName = r.ReadString();
            Plugin.L.LogInfo($"Hello from {Name(peer)}: protocol {protocol}, mod {mod}, game {game}, room {roomName}");
            if (protocol != Packets.ProtocolVersion || game != Application.version)
                Plugin.L.LogWarning($"{Name(peer)} runs protocol {protocol} / game {game}; this side: {Packets.ProtocolVersion} / {Application.version}");
            if (_peers.TryGetValue(peer, out var info))
            {
                info.RoomName = roomName;
                info.RoomHash = roomHash;
                info.ProtocolVersion = protocol;
                info.GameVersion = game;
                info.ModVersion = mod;
            }
        }

        [HideFromIl2Cpp]
        private void OnChangeRoom(ulong peer, BinaryReader r)
        {
            var hash = r.ReadUInt32();
            var name = r.ReadString();
            if (_peers.TryGetValue(peer, out var info))
            {
                info.RoomHash = hash;
                info.RoomName = name;
            }
            Plugin.L.LogInfo($"{Name(peer)} -> room {name}");
            PeerChangedRoom?.Invoke(peer, hash, name);
        }

        [HideFromIl2Cpp]
        private void OnMonsterDamage(ulong peer, BinaryReader r)
        {
            var damage = MonsterDamage.Read(r);
            if (Plugin.Cfg.SyncEnemies.Value && !_save.SuppressWorldSync)
                _enemySync.OnDamage(peer, damage);
            MonsterDamaged?.Invoke(peer, damage);
        }

        [HideFromIl2Cpp]
        private void OnWorldEntry(ulong peer, BinaryReader r)
        {
            if (!_save.SuppressWorldSync)
                _world.OnEntry(peer, r, _roomHash);
        }

        [HideFromIl2Cpp]
        private void OnSharedInventory(ulong peer, BinaryReader r)
        {
            if (!_save.SuppressWorldSync)
                _world.OnInventory(peer, r, _roomHash);
        }

        [HideFromIl2Cpp]
        private void OnStoryFlag(ulong peer, BinaryReader r)
        {
            if (!_save.SuppressWorldSync)
                _world.OnVariable(peer, r);
        }

        [HideFromIl2Cpp]
        private void OnCutsceneStart(ulong peer, BinaryReader r)
        {
            if (!_save.SuppressWorldSync)
                _cutscenes.OnStart(peer, r, _roomHash);
        }

        // ------------------------------------------------------------------ helpers

        [HideFromIl2Cpp]
        private Puppet Puppet(ulong peer, string label)
        {
            if (!_puppets.TryGetValue(peer, out var puppet))
            {
                puppet = new Puppet(peer, label);
                _puppets[peer] = puppet;
            }
            return puppet;
        }

        [HideFromIl2Cpp]
        private bool IsMember(ulong peer)
        {
            if (_members.Contains(peer))
                return true;
            if (_state != LobbyState.InLobby || _refreshedThisFrame)
                return false;
            _refreshedThisFrame = true;
            RefreshMembers(Time.realtimeSinceStartup);
            return _members.Contains(peer);
        }

        [HideFromIl2Cpp]
        private bool SteamAlive() => _steamReady && SteamNative.SteamAPI_GetHSteamUser() != 0;

        [HideFromIl2Cpp]
        private unsafe void CloseSession(ulong peer)
        {
            var identity = SteamNative.Identity(peer);
            SteamNative.SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_messages, &identity);
        }

        [HideFromIl2Cpp]
        private unsafe void SetLobbyData(string key, string value)
        {
            var k = System.Text.Encoding.UTF8.GetBytes(key + "\0");
            var v = System.Text.Encoding.UTF8.GetBytes((value ?? "") + "\0");
            fixed (byte* pk = k)
            fixed (byte* pv = v)
                SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyData(_matchmaking, _lobby, pk, pv);
        }

        [HideFromIl2Cpp]
        private unsafe string GetLobbyData(string key)
        {
            var k = System.Text.Encoding.UTF8.GetBytes(key + "\0");
            fixed (byte* pk = k)
                return SteamNative.Utf8(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyData(_matchmaking, _lobby, pk));
        }

        [HideFromIl2Cpp]
        private string Name(ulong steamId)
        {
            if (steamId == _self)
                return _selfName;
            if (_peers.TryGetValue(steamId, out var info) && !string.IsNullOrEmpty(info.Name))
                return info.Name;
            if (_link != null)
                return steamId == _link.PeerId ? $"窗口 {3 - _link.Copy}" : steamId.ToString();
            var name = _steamReady ? SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, steamId)) : null;
            return string.IsNullOrEmpty(name) ? steamId.ToString() : name;
        }

        /// <summary>"+connect_lobby &lt;id&gt;" on the command line: Steam starts the game that way when an invite is
        /// accepted while it is not running.</summary>
        [HideFromIl2Cpp]
        private static ulong CommandLineLobby()
        {
            var parts = CommandLineArgs();
            for (var i = 0; i + 1 < parts.Length; i++)
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1].Trim('"'), out var lobby))
                    return lobby;
            return 0;
        }

        [HideFromIl2Cpp]
        private static string[] CommandLineArgs() =>
            s_commandLine ??= (Marshal.PtrToStringUni(SteamNative.GetCommandLineW()) ?? "")
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        [HideFromIl2Cpp]
        private static bool HasLaunchFlag(string flag) => Array.IndexOf(CommandLineArgs(), flag) >= 0;

        /// <summary>"Assets/Scenes/Levels/Outskirts/Outskirts_Diner_Interior.unity" -> "Outskirts_Diner_Interior".</summary>
        [HideFromIl2Cpp]
        private static string ShortRoom(string room)
        {
            if (string.IsNullOrEmpty(room))
                return "?";
            var name = room.Substring(room.LastIndexOf('/') + 1);
            return name.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
        }

        [HideFromIl2Cpp]
        private void UpdateStatus()
        {
            string text;
            if (_link != null)
            {
                var connected = _members.Contains(_link.PeerId);
                text = $"联机（双开测试 · 窗口 {_link.Copy}）：{(connected ? "已连上另一个窗口" : "等待另一个窗口…")}";
                foreach (var pair in _peers)
                {
                    var same = _puppets.TryGetValue(pair.Key, out var p) && p.HasSnapshot && p.RoomHash == _roomHash;
                    text += $"\n{pair.Value.Name}：{(same ? "同一房间" : "在 " + ShortRoom(pair.Value.RoomName))}";
                }
            }
            else if (!_steamReady)
                text = "联机：等待 Steam…";
            else
                switch (_state)
                {
                    case LobbyState.Idle:
                        text = $"联机：未连接　[{Plugin.Cfg.CreateLobbyKey.Value}] 建房";
                        break;
                    case LobbyState.Creating:
                        text = "联机：正在创建大厅…";
                        break;
                    case LobbyState.Joining:
                        text = "联机：正在加入大厅…";
                        break;
                    default:
                        text = $"联机：大厅 {_members.Count}/{Math.Max(2, Plugin.Cfg.MaxPlayers.Value)} 人　" +
                               $"[{Plugin.Cfg.InviteKey.Value}] 邀请　[{Plugin.Cfg.LeaveKey.Value}] 退出";
                        foreach (var pair in _peers)
                        {
                            var same = _puppets.TryGetValue(pair.Key, out var p) && p.HasSnapshot && p.RoomHash == _roomHash;
                            text += $"\n{pair.Value.Name}：{(same ? "同一房间" : "在 " + ShortRoom(pair.Value.RoomName))}";
                        }
                        break;
                }
            if (Connected)
                text += $"\n同步：世界 发{_world.Sent}/收{_world.Applied}　敌人 发{_enemySync.Sent}/收{_enemySync.Applied}（本房间 {_enemySync.Tracked}）";
            if (_save != null && !string.IsNullOrEmpty(_save.Status))
                text += "\n" + _save.Status;
            if (Plugin.Cfg.LoopbackTest.Value)
                text += $"\n本地镜像测试：开（[{Plugin.Cfg.LoopbackKey.Value}] 关闭）　房间 {ShortRoom(_roomName)}";
            _status = text;
        }

        private sealed class PeerInfo
        {
            public string Name;
            public string RoomName;
            public uint RoomHash;
            public ushort ProtocolVersion;
            public string GameVersion;
            public string ModVersion;
        }
    }
}
