using System.Collections.Generic;
using System.IO;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// Each side sends whether its player has the pause menu open or is in a cutscene. Remote activity is displayed
    /// above that player's puppet; it never stops this side's simulation. Operation holds remain for coordinated IO.
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

        public string Activity(ulong peer)
        {
            if (!Plugin.Cfg.SyncPause.Value || !_peers.TryGetValue(peer, out var state))
                return null;
            if ((state & FlagCutscene) != 0)
                return "正在看过场…";
            return (state & FlagPaused) != 0 ? "正在暂停…" : null;
        }

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

            if (_operationHold)
            {
                Time.timeScale = 0f;   // every frame: the game sets it again whenever its own menus change
                _frozen = true;
                Notice = _operationNotice;
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
