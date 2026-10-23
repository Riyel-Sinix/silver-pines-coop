using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Il2CppInterop.Runtime;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;
using UnityEngine.Events;

namespace SilverPinesCoop.Sync
{
    /// <summary>Only commands and acknowledgements cross the network; each player keeps their own local profile.</summary>
    internal sealed class SaveSync : IDisposable
    {
        private const float Timeout = 90f;
        private enum Kind { Save, Load }
        private enum Phase { Preparing, Executing }
        private sealed class Operation
        {
            public Kind Kind;
            public Phase Phase;
            public uint Id;
            public ulong Token;
            public int Slot;
            public int Profile;
            public float Deadline;
            public float ReadyAt;
            public bool LocalReady, RemoteReady, Started, LocalDone, RemoteDone, CallbackReceived;
        }

        private readonly SteamNetManager _net;
        private readonly PauseSync _pause;
        private readonly HashSet<ulong> _retiredEpochs = new();
        private readonly List<(VoidGameEventChannel Channel, UnityAction Listener)> _listeners = new();
        private readonly List<Action> _actions = new();
        private ulong _peer, _host, _epoch, _checkpoint;
        private int _slot = -1, _profile = -1;
        private uint _nextId, _lastRemoteId;
        private uint _sessionSequence, _lastSessionSequence;
        private bool _ready, _events, _blocked, _dead, _choosingSlot;
        private int? _saveRequest;
        private bool _loadRequest;
        private bool _draining, _drainCallback;
        private Kind _drainKind;
        private int _drainProfile;
        private int _selectedSlot = -1, _result;
        private Operation _op;
        private float _now, _nextSession, _noticeUntil, _nextEventProbe;
        private string _notice;

        public string Status { get; private set; } = "";
        public bool SuppressWorldSync => _blocked || _op?.Kind == Kind.Load;

        public SaveSync(SteamNetManager net, PauseSync pause)
        {
            _net = net;
            _pause = pause;
            SaveLoadHook.InterceptSave = InterceptSave;
            SaveLoadHook.InterceptLoad = _ => InterceptLoad();
            SaveLoadHook.InterceptRecent = InterceptLoad;
            SaveLoadHook.Install();
        }

        public void Update(float now)
        {
            _now = now;
            RegisterEvents();
            EnsureSession();
            if (_draining && _drainCallback && TryManager(out var drainingManager, out _) && !drainingManager.IsBusy)
                _draining = _drainCallback = false;
            if (_peer == 0)
            {
                Status = _net.LocalTestMode ? "双开测试：共同存档关闭（两个窗口共用本地存档）" : "";
                return;
            }
            if (IsHost && _op == null && now >= _nextSession)
                SendSession();
            if (_profile >= 0 && TryManager(out _, out var profile) && profile != _profile)
            {
                Disconnect();
                Show("已切换本地 profile，请重新建立联机后共同存档");
                return;
            }
            if (_op != null && now >= _op.Deadline)
                Fail("操作超时");
            if (_op != null && _result != 0 &&
                (_result < 0 || (TryManager(out var resultManager, out _) && !resultManager.IsBusy)))
            {
                var result = _result;
                _result = 0;
                if (result < 0)
                    Fail("游戏报告存档失败");
                else
                {
                    _op.LocalDone = true;
                    if (!IsHost)
                        SendResult();
                }
            }
            if (_op != null && _op.Phase == Phase.Preparing)
                PrepareLocal();
            if (_op != null && IsHost && _op.Phase == Phase.Preparing &&
                _op.LocalReady && _op.RemoteReady && now >= _op.ReadyAt)
            {
                if (!Send(PacketType.CheckpointExecute, WriteOperationId))
                    Fail("无法向队友发送开始指令");
                else
                {
                    _op.Phase = Phase.Executing;
                    _op.ReadyAt = now + 0.25f;
                }
            }
            if (_op != null && _op.Phase == Phase.Executing && !_op.Started && now >= _op.ReadyAt)
                ExecuteLocal();
            if (_op != null && IsHost && _op.LocalDone && _op.RemoteDone)
                CompleteHost();
            if (_op == null && CanOperate)
            {
                if (_loadRequest)
                {
                    _loadRequest = false;
                    if (_checkpoint != 0)
                        Begin(Kind.Load, _slot);
                }
                else if (_saveRequest.HasValue && !_loadRequest)
                {
                    var slot = _saveRequest.Value;
                    _saveRequest = null;
                    if (IsHost)
                    {
                        if (slot < 0)
                            slot = ChooseGameSlot();
                        if (slot >= 0 && !_dead)
                            Begin(Kind.Save, slot);
                    }
                }
            }
            UpdateStatus();
        }

        private bool IsHost => _host != 0 && _host == _net.LocalSteamId;
        private bool CanOperate => _peer != 0 && _ready && !_blocked && !_draining && _events &&
                                   SaveLoadHook.Installed && _net.CheckpointPeerCompatible;
        private bool InSteamCoop => !_net.LocalTestMode && _net.PartnerId != 0;

        public void RequestSave() => RequestSave(-1);

        private void RequestSave(int slot)
        {
            if (!CanOperate || _dead)
            {
                Show(_dead ? "死亡状态无法存档，请先回档" : "共同存档尚未就绪，请查看联机状态");
                return;
            }
            if (_op != null)
                return; // repeated clicks do not queue a second write
            if (IsHost)
                _saveRequest = slot;
            else
                Send(PacketType.CheckpointSaveRequest, w => { w.Write(_epoch); w.Write(slot); });
        }

        private bool InterceptSave(int slot)
        {
            if (_choosingSlot)
            {
                _selectedSlot = slot;
                return true;
            }
            if (!InSteamCoop)
            {
                InvalidateOfflineSave(slot);
                return false;
            }
            RequestSave(slot);
            return true;
        }

        private bool InterceptLoad()
        {
            if (!InSteamCoop)
                return false;
            if (_checkpoint == 0 && _op?.Kind == Kind.Save && _op.Phase == Phase.Preparing &&
                !_blocked && !_draining)
            {
                RequestLoad();
                Show("尚无共同存档，本次按原版读取；共同存档后启用双方回档");
                return false;
            }
            if (_checkpoint == 0 && _op == null && !_blocked && !_draining)
            {
                Show("尚无共同存档，本次按原版读取；共同存档后启用双方回档");
                return false;
            }
            RequestLoad();
            return true;
        }

        private void RequestLoad()
        {
            if (!CanOperate)
            {
                Show("共同回档尚未就绪，请查看联机状态");
                return;
            }
            if (_op?.Kind == Kind.Load)
                return;
            if (_op?.Kind == Kind.Save && _op.Phase == Phase.Preparing)
            {
                if (IsHost)
                {
                    _loadRequest = true;
                    Fail("死亡时取消尚未开始的共同存档");
                }
                else
                {
                    Fail("死亡时取消尚未开始的共同存档");
                    Send(PacketType.CheckpointLoadRequest, w => w.Write(_epoch));
                }
                return;
            }
            if (_op?.Kind == Kind.Save)
            {
                if (IsHost)
                    _loadRequest = true;
                else if (!Send(PacketType.CheckpointLoadRequest, w => w.Write(_epoch)))
                    Show("无法通知房主共同回档");
                return;
            }
            if (_checkpoint == 0)
            {
                Show("尚无共同存档，请从死亡菜单读取后再共同存档");
                return;
            }
            if (IsHost)
                _loadRequest = true;
            else
                Send(PacketType.CheckpointLoadRequest, w => w.Write(_epoch));
        }

        private int ChooseGameSlot()
        {
            if (!TryManager(out var manager, out _) || manager.IsBusy)
            {
                Show("游戏存档服务尚未空闲，请稍后重试");
                return -1;
            }
            _selectedSlot = -1;
            _choosingSlot = true;
            try { manager.SaveGame(); } // native slot hook captures and suppresses the write
            finally { _choosingSlot = false; }
            return _selectedSlot;
        }

        private void Begin(Kind kind, int slot)
        {
            if (!IsHost || !CanOperate || !TryManager(out _, out var profile))
                return;
            _op = NewOperation(kind, ++_nextId, kind == Kind.Save ? NewToken() : _checkpoint, slot, profile);
            Hold();
            var type = kind == Kind.Save ? PacketType.CheckpointSavePrepare : PacketType.CheckpointLoadPrepare;
            if (!Send(type, w =>
            {
                WriteOperationId(w);
                w.Write(_op.Token);
                w.Write(_op.Slot);
            }))
                Fail("无法联系队友");
        }

        private Operation NewOperation(Kind kind, uint id, ulong token, int slot, int profile) => new()
        {
            Kind = kind, Phase = Phase.Preparing, Id = id, Token = token, Slot = slot, Profile = profile,
            Deadline = _now + Timeout, ReadyAt = _now + 0.5f,
        };

        private void PrepareLocal()
        {
            if (_op.LocalReady || !_events || !TryManager(out var manager, out var profile) || manager.IsBusy)
                return;
            if (profile != _op.Profile || (_op.Kind == Kind.Save && (_dead || LocalPlayer.Find() == null)) ||
                (_op.Kind == Kind.Load && !manager.HasSaveInSlot(_op.Slot)))
            {
                Fail("本地 profile 或存档不符合回档条件");
                return;
            }
            _op.LocalReady = true;
            _op.ReadyAt = _now + 0.5f; // both frozen; let the normal reliable world packets drain first
            if (!IsHost && !Send(PacketType.CheckpointReady, WriteOperationId))
                Fail("无法向房主确认准备状态");
        }

        private void ExecuteLocal()
        {
            if (!TryManager(out var manager, out var profile) || profile != _op.Profile || manager.IsBusy)
                return;
            _op.Started = true;
            _result = 0;
            try
            {
                if (_op.Kind == Kind.Save)
                {
                    ClearCheckpoint(); // a partial overwrite must never remain a valid shared checkpoint
                    SaveLoadHook.ExecuteLocal(() => manager.SaveGameInSlot(_op.Slot));
                }
                else
                    SaveLoadHook.ExecuteLocal(() => GameAccess.Globals.EventChannels.SaveLoad.GameLoad.Raise(_op.Slot));
            }
            catch (Exception e)
            {
                Plugin.L.LogError(e);
                Fail("游戏存读档调用失败");
            }
        }

        private void CompleteHost()
        {
            var type = _op.Kind == Kind.Save ? PacketType.CheckpointSaveFinish : PacketType.CheckpointLoadFinish;
            if (!Send(type, WriteOperationId))
            {
                Fail("无法发送完成确认");
                return;
            }
            Complete();
            SendSession();
        }

        private void Complete()
        {
            if (_op.Kind == Kind.Save)
            {
                _checkpoint = _op.Token;
                _slot = _op.Slot;
                PersistCheckpoint();
            }
            _dead = false;
            _op = null;
            _result = 0;
            _pause.SetOperationHold(false);
            Show("双方共同操作已完成");
        }

        private void Fail(string reason, bool notify = true)
        {
            if (_op == null)
                return;
            var irreversible = _op.Phase == Phase.Executing;
            var rollbackQueued = _loadRequest && _op.Kind == Kind.Save && !irreversible && _checkpoint != 0;
            if (notify)
                Send(PacketType.CheckpointAbort, w => { WriteOperationId(w); w.Write(irreversible); });
            var load = _op.Kind == Kind.Load;
            if (irreversible)
            {
                TrackDrain();
                ClearCheckpoint();
                _blocked = true; // late game callbacks cannot be mistaken for a new operation
                _notice = $"共同{(load ? "回档" : "存档")}未完成：{reason}。按 F6 退出后重新联机";
                _pause.SetOperationHold(true, _notice);
            }
            else
            {
                _pause.SetOperationHold(false);
                Show("共同操作已取消：" + reason);
            }
            Plugin.L.LogWarning("共同操作：" + reason);
            _op = null;
            _result = 0;
            _saveRequest = null;
            _loadRequest = rollbackQueued;
        }

        private void SendResult()
        {
            var type = _op.Kind == Kind.Save ? PacketType.CheckpointSaveAck : PacketType.CheckpointLoadAck;
            if (!Send(type, WriteOperationId))
                Fail("无法发送本地完成确认");
        }

        private void Hold() => _pause.SetOperationHold(true,
            _op.Kind == Kind.Save ? "双方正在共同存档，请稍候…" : "双方正在回到共同存档，请稍候…");

        private void EnsureSession()
        {
            var peer = _net.LocalTestMode ? 0 : _net.PartnerId;
            var host = peer == 0 ? 0 : _net.HostId;
            if (_peer == peer && _host == host)
            {
                if (_peer != 0 && _profile < 0)
                    RestoreCheckpoint();
                return;
            }
            Disconnect();
            _peer = peer;
            _host = host;
            if (peer == 0)
                return;
            RestoreCheckpoint();
            if (IsHost)
                _epoch = NewToken();
        }

        public void Disconnect()
        {
            TrackDrain();
            if (_epoch != 0)
                _retiredEpochs.Add(_epoch);
            _peer = _host = _epoch = 0;
            _ready = _blocked = false;
            _checkpoint = 0;
            _slot = _profile = -1;
            _op = null;
            _saveRequest = null;
            _loadRequest = false;
            _result = 0;
            _notice = null;
            _nextSession = 0f;
            _lastRemoteId = 0;
            _lastSessionSequence = 0;
            _pause.SetOperationHold(false);
        }

        private void RestoreCheckpoint()
        {
            if (!TryManager(out var manager, out var profile))
                return;
            _profile = profile;
            var cfg = Plugin.Cfg;
            if (cfg.LastCheckpointPeer.Value == _peer.ToString(CultureInfo.InvariantCulture) &&
                cfg.LastCheckpointProfile.Value == profile && cfg.LastCheckpointSlot.Value >= 0 &&
                ulong.TryParse(cfg.LastCheckpointToken.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var token) &&
                token != 0 && manager.HasSaveInSlot(cfg.LastCheckpointSlot.Value))
            {
                _checkpoint = token;
                _slot = cfg.LastCheckpointSlot.Value;
            }
        }

        private void PersistCheckpoint()
        {
            var cfg = Plugin.Cfg;
            cfg.LastCheckpointPeer.Value = _peer.ToString(CultureInfo.InvariantCulture);
            cfg.LastCheckpointProfile.Value = _op.Profile;
            cfg.LastCheckpointSlot.Value = _slot;
            cfg.LastCheckpointToken.Value = _checkpoint.ToString("X16", CultureInfo.InvariantCulture);
        }

        private void ClearCheckpoint()
        {
            _checkpoint = 0;
            _slot = -1;
            Plugin.Cfg.LastCheckpointToken.Value = "";
            Plugin.Cfg.LastCheckpointSlot.Value = -1;
        }

        private void InvalidateOfflineSave(int slot)
        {
            if (slot == Plugin.Cfg.LastCheckpointSlot.Value && TryManager(out _, out var profile) &&
                profile == Plugin.Cfg.LastCheckpointProfile.Value)
                ClearCheckpoint();
        }

        private void SendSession()
        {
            if (!IsHost || _epoch == 0 || !_net.CheckpointPeerCompatible || _profile < 0 || !_events || _draining)
                return;
            _nextSession = _now + 2f;
            _sessionSequence++;
            if (_sessionSequence == 0)
                _sessionSequence++;
            Send(PacketType.CheckpointSession, w =>
            {
                w.Write(_epoch); w.Write(_sessionSequence); w.Write(_checkpoint); w.Write(_slot);
            });
        }

        public void OnSession(ulong sender, BinaryReader r)
        {
            var epoch = r.ReadUInt64();
            var sequence = r.ReadUInt32();
            var token = r.ReadUInt64();
            var slot = r.ReadInt32();
            EnsureSession();
            if (IsHost || sender != _host || sender != _peer || epoch == 0 ||
                _retiredEpochs.Contains(epoch) || !_net.CheckpointPeerCompatible)
                return;
            if (_epoch != 0 && _epoch != epoch)
            {
                _retiredEpochs.Add(_epoch);
                _lastSessionSequence = 0;
                if (_op != null)
                    Fail("房主会话已变更", false);
            }
            if (sequence == 0 || sequence <= _lastSessionSequence)
                return;
            _epoch = epoch;
            _lastSessionSequence = sequence;
            if (_op != null || _blocked)
                return;
            if (_profile < 0)
                RestoreCheckpoint();
            if (_profile < 0 || !_events || _draining)
                return;
            var matched = token == 0 || (token == _checkpoint && slot == _slot);
            if (token == 0)
                ClearCheckpoint();
            _ready = Send(PacketType.CheckpointSessionAck, w =>
            {
                w.Write(_epoch); w.Write(token); w.Write(matched);
            }) && matched;
        }

        public void OnSessionAck(ulong sender, BinaryReader r)
        {
            var epoch = r.ReadUInt64();
            var token = r.ReadUInt64();
            var matched = r.ReadBoolean();
            if (!IsHost || !Valid(sender, epoch) || _op != null || _blocked || token != _checkpoint)
                return;
            if (!matched)
            {
                ClearCheckpoint();
                SendSession();
                return;
            }
            _ready = true;
        }

        public void OnSaveRequest(ulong sender, BinaryReader r)
        {
            var epoch = r.ReadUInt64();
            var slot = r.ReadInt32();
            if (IsHost && Valid(sender, epoch))
                RequestSave(slot);
        }

        public void OnLoadRequest(ulong sender, BinaryReader r)
        {
            var epoch = r.ReadUInt64();
            if (IsHost && Valid(sender, epoch))
                RequestLoad();
        }

        public void OnDeathRequest(ulong sender, BinaryReader r) => OnLoadRequest(sender, r);
        public void OnSavePrepare(ulong sender, BinaryReader r) => OnPrepare(sender, r, Kind.Save);
        public void OnLoadPrepare(ulong sender, BinaryReader r) => OnPrepare(sender, r, Kind.Load);

        private void OnPrepare(ulong sender, BinaryReader r, Kind kind)
        {
            var epoch = r.ReadUInt64();
            var id = r.ReadUInt32();
            var token = r.ReadUInt64();
            var slot = r.ReadInt32();
            if (IsHost || !Valid(sender, epoch) || !CanOperate || id == 0 || token == 0 || slot < 0 ||
                id <= _lastRemoteId || _op != null || !TryManager(out _, out var profile))
                return;
            if (kind == Kind.Load && (token != _checkpoint || slot != _slot))
                return;
            _lastRemoteId = id;
            _op = NewOperation(kind, id, token, slot, profile);
            Hold();
        }

        public void OnReady(ulong sender, BinaryReader r)
        {
            if (!ReadOperation(sender, r) || !IsHost || _op.Phase != Phase.Preparing)
                return;
            _op.RemoteReady = true;
            _op.ReadyAt = _now + 0.5f;
        }

        public void OnExecute(ulong sender, BinaryReader r)
        {
            if (!ReadOperation(sender, r) || IsHost || _op.Phase != Phase.Preparing || !_op.LocalReady)
                return;
            _op.Phase = Phase.Executing; // game API runs later in Update, never inside a packet handler
        }

        public void OnSaveAck(ulong sender, BinaryReader r) => OnResult(sender, r, Kind.Save);
        public void OnLoadAck(ulong sender, BinaryReader r) => OnResult(sender, r, Kind.Load);
        private void OnResult(ulong sender, BinaryReader r, Kind kind)
        {
            if (ReadOperation(sender, r) && IsHost && _op.Kind == kind && _op.Phase == Phase.Executing)
                _op.RemoteDone = true;
        }

        public void OnSaveFinish(ulong sender, BinaryReader r) => OnFinish(sender, r, Kind.Save);
        public void OnLoadFinish(ulong sender, BinaryReader r) => OnFinish(sender, r, Kind.Load);
        private void OnFinish(ulong sender, BinaryReader r, Kind kind)
        {
            if (ReadOperation(sender, r) && !IsHost && _op.Kind == kind && _op.LocalDone)
                Complete();
        }

        public void OnAbort(ulong sender, BinaryReader r)
        {
            var valid = ReadOperation(sender, r);
            var irreversible = r.ReadBoolean();
            if (!valid)
                return;
            if (irreversible)
                _op.Phase = Phase.Executing;
            Fail("队友未能完成操作", false);
        }

        private bool ReadOperation(ulong sender, BinaryReader r)
        {
            var epoch = r.ReadUInt64();
            var id = r.ReadUInt32();
            return Valid(sender, epoch) && _op != null && _op.Id == id;
        }

        private bool Valid(ulong sender, ulong epoch) => sender == _peer && epoch != 0 && epoch == _epoch &&
                                                        _net.CheckpointPeerCompatible;
        private bool Send(PacketType type, Action<BinaryWriter> body) => _peer != 0 && _net.SendReliable(_peer, type, body);
        private void WriteOperationId(BinaryWriter w) { w.Write(_epoch); w.Write(_op.Id); }
        private static ulong NewToken()
        {
            var token = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
            return token == 0 ? 1UL : token;
        }

        private static bool TryManager(out SaveDataManager manager, out int profile)
        {
            manager = SaveDataManager.Instance;
            profile = -1;
            if (manager == null || !manager.IsInitialized || !manager.ProfilesLoaded || manager.SharedData == null)
                return false;
            profile = manager.SharedData.SaveProfileIndex;
            return profile >= 0;
        }

        private void RegisterEvents()
        {
            if (_events || _now < _nextEventProbe)
                return;
            _nextEventProbe = _now + 2f;
            var channels = GameAccess.Globals?.EventChannels;
            if (channels?.SaveLoad == null || channels.Player == null)
                return;
            try
            {
                Listen(channels.SaveLoad.SaveCompletedSuccesfully, () => Result(Kind.Save, 1));
                Listen(channels.SaveLoad.SaveCompletedFailed, () => Result(Kind.Save, -1));
                Listen(channels.SaveLoad.OnGameReloadedEvent, () =>
                {
                    _dead = false;
                    Result(Kind.Load, 1);
                });
                Listen(channels.Player.PlayerDead, () =>
                {
                    _dead = true;
                    if (InSteamCoop)
                        RequestLoad();
                });
                _events = true;
                Plugin.L.LogInfo("共同存档和死亡回档事件已注册");
            }
            catch (Exception e)
            {
                UnregisterEvents();
                Plugin.L.LogWarning("共同存档事件注册失败：" + e.Message);
            }
        }

        private void Result(Kind kind, int result)
        {
            if (_draining && _drainKind == kind && TryManager(out _, out var drainProfile) && drainProfile == _drainProfile)
            {
                _drainCallback = true;
                return;
            }
            if (_op?.Kind == kind && _op.Started && !_op.LocalDone &&
                TryManager(out _, out var profile) && profile == _op.Profile)
            {
                _op.CallbackReceived = true;
                _result = result;
            }
        }

        private void TrackDrain()
        {
            if (_op == null || !_op.Started || _op.LocalDone)
                return;
            _draining = true;
            _drainKind = _op.Kind;
            _drainProfile = _op.Profile;
            _drainCallback = _op.CallbackReceived;
        }

        private void Listen(VoidGameEventChannel channel, Action action)
        {
            if (channel == null)
                throw new InvalidOperationException("缺少游戏存档事件");
            var listener = DelegateSupport.ConvertDelegate<UnityAction>(action);
            channel.Register(listener);
            _actions.Add(action);
            _listeners.Add((channel, listener));
        }

        private void UnregisterEvents()
        {
            foreach (var (channel, listener) in _listeners)
            {
                try { channel.Unregister(listener); }
                catch (Exception e) { Plugin.L.LogWarning(e.Message); }
            }
            _listeners.Clear();
            _actions.Clear();
            _events = false;
        }

        public void Dispose()
        {
            Disconnect();
            UnregisterEvents();
            SaveLoadHook.InterceptSave = null;
            SaveLoadHook.InterceptLoad = null;
            SaveLoadHook.InterceptRecent = null;
        }

        private void Show(string notice)
        {
            _notice = notice;
            _noticeUntil = _now + 6f;
        }

        private void UpdateStatus()
        {
            if (_blocked) Status = _notice;
            else if (_draining) Status = "共同存档：等待前次本地存读档结束…";
            else if (!SaveLoadHook.Installed || !_events) Status = "共同存档：游戏接口尚未就绪";
            else if (!_net.CheckpointPeerCompatible) Status = "共同存档：双方需使用相同的游戏与模组版本";
            else if (!_ready) Status = "共同存档：等待双方确认会话…";
            else if (_op != null) Status = _op.Kind == Kind.Save ? "共同存档：等待双方写盘确认…" : "共同存档：等待双方回档完成…";
            else if (_now < _noticeUntil) Status = _notice;
            else Status = _checkpoint == 0 ? $"共同存档：尚无共同存档　[{Plugin.Cfg.SaveCheckpointKey.Value}] 保存" :
                $"共同存档：槽位索引 {_slot} 已确认　[{Plugin.Cfg.SaveCheckpointKey.Value}] 保存";
        }
    }
}
