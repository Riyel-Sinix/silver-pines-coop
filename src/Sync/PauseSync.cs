using System.Collections.Generic;
using System.IO;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// "One pauses, everyone pauses": each side sends whether its player has the pause (system / options) menu open or
    /// is in a cutscene (timeline cutscene mode or a video). While any other player is, this side's time scale is held
    /// at 0 — what the game itself does for its pausing menus — with a notice on screen; when they continue, the game's
    /// TimeScaleManager restores its own time scale. Only the local player's real menus / cutscenes are sent, never a
    /// freeze caused by someone else, so two players cannot keep each other frozen.
    /// </summary>
    internal sealed class PauseSync
    {
        public const byte FlagPaused = 1;
        public const byte FlagCutscene = 2;
        private const float Heartbeat = 2f;

        private readonly SteamNetManager _net;
        private readonly Dictionary<ulong, byte> _peers = new();
        private byte _local;
        private float _nextSend;
        private bool _frozen;
        private bool _operationHold;
        private string _operationNotice;

        public string Notice { get; private set; }
        public byte LocalFlags => _local;

        public PauseSync(SteamNetManager net)
        {
            _net = net;
        }

        public void Update(float now, bool connected)
        {
            byte local = 0;
            if ((GameAccess.MenuFlags() & (GameAccess.MenuSystem | GameAccess.MenuOptions)) != 0)
                local |= FlagPaused;
            if (GameAccess.InCutscene())
                local |= FlagCutscene;
            if (connected && (local != _local || now >= _nextSend))
            {
                _nextSend = now + Heartbeat;
                _net.BroadcastReliable(PacketType.PauseState, w => w.Write(local));
            }
            _local = local;

            ulong who = 0;
            byte why = 0;
            if (connected && Plugin.Cfg.SyncPause.Value)
                foreach (var pair in _peers)
                    if (pair.Value != 0)
                    {
                        who = pair.Key;
                        why = pair.Value;
                        break;
                    }
            if (why != 0 || _operationHold)
            {
                Time.timeScale = 0f;   // every frame: the game sets it again whenever its own menus change
                _frozen = true;
                Notice = _operationHold ? _operationNotice : (why & FlagCutscene) != 0
                    ? $"{_net.PeerName(who)} 正在看过场动画，等待结束…"
                    : $"{_net.PeerName(who)} 暂停了游戏，等待继续…";
            }
            else if (_frozen)
            {
                _frozen = false;
                Notice = null;
                GameAccess.RestoreTimeScale();
            }
        }

        /// <summary>Tell a player who just arrived what state we are in.</summary>
        public void SendTo(ulong peer) => _net.SendReliable(peer, PacketType.PauseState, w => w.Write(_local));

        public void OnState(ulong sender, BinaryReader r) => _peers[sender] = r.ReadByte();

        public void Forget(ulong peer) => _peers.Remove(peer);

        public void SetOperationHold(bool hold, string notice = null)
        {
            _operationHold = hold;
            _operationNotice = notice;
        }

        public void Release()
        {
            _peers.Clear();
            _operationHold = false;
            _operationNotice = null;
            if (_frozen)
            {
                _frozen = false;
                Notice = null;
                GameAccess.RestoreTimeScale();
            }
        }
    }
}
