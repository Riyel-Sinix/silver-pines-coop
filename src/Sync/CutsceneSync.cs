using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace SilverPinesCoop.Sync
{
    /// <summary>
    /// "One enters a cutscene, everyone does": the game's cutscenes are Timeline PlayableDirectors with an
    /// EnableCutsceneMode track (GameCutsceneManager.CutsceneActive). When ours starts, the playing directors are sent by
    /// scene + hierarchy path; a player in the same room starts the same director at the same time, so both watch it
    /// (and its signals / persistent flags run on both sides). A player elsewhere just waits: see <see cref="PauseSync"/>.
    /// </summary>
    internal sealed class CutsceneSync
    {
        private const float EchoWindow = 2f;

        private readonly SteamNetManager _net;
        private readonly List<string> _paths = new();
        private bool _wasActive;
        private float _remoteStartedAt = -100f;

        public CutsceneSync(SteamNetManager net)
        {
            _net = net;
        }

        public void Update(float now, uint room, bool connected)
        {
            bool active;
            try
            {
                active = GameCutsceneManager.CutsceneActive;
            }
            catch (Exception)
            {
                return;
            }
            if (active && !_wasActive && connected && Plugin.Cfg.SyncCutscenes.Value && now - _remoteStartedAt > EchoWindow)
                SendPlaying(room);
            _wasActive = active;
        }

        private void SendPlaying(uint room)
        {
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<PlayableDirector>(), FindObjectsSortMode.None))
            {
                var director = obj?.TryCast<PlayableDirector>();
                if (director == null || director.state != PlayState.Playing)
                    continue;
                var path = PathOf(director.gameObject);
                var time = director.time;
                _net.BroadcastReliable(PacketType.CutsceneStart, w =>
                {
                    w.Write(room);
                    w.Write(path);
                    w.Write(time);
                });
                Plugin.L.LogInfo($"Cutscene started here: {path} at {time:0.00}s");
            }
        }

        public void OnStart(ulong sender, BinaryReader r, uint localRoom)
        {
            var room = r.ReadUInt32();
            var path = r.ReadString();
            var time = r.ReadDouble();
            if (!Plugin.Cfg.SyncCutscenes.Value || room != localRoom)
                return;   // not here: PauseSync makes us wait for it instead
            try
            {
                if (GameCutsceneManager.CutsceneActive)
                    return;
                foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<PlayableDirector>(), FindObjectsSortMode.None))
                {
                    var director = obj?.TryCast<PlayableDirector>();
                    if (director == null || PathOf(director.gameObject) != path)
                        continue;
                    if (director.state == PlayState.Playing)
                        return;
                    _remoteStartedAt = Time.realtimeSinceStartup;
                    director.time = time;
                    director.Play();
                    Plugin.L.LogInfo($"Cutscene from {_net.PeerName(sender)}: playing {path} from {time:0.00}s");
                    return;
                }
                Plugin.L.LogInfo($"Cutscene from {_net.PeerName(sender)}: {path} is not active here");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Cutscene {path}: {e.Message}");
            }
        }

        private string PathOf(GameObject go)
        {
            _paths.Clear();
            for (var t = go.transform; t != null; t = t.parent)
                _paths.Add(t.name);
            _paths.Reverse();
            return go.scene.name + ":" + string.Join("/", _paths);
        }
    }
}
