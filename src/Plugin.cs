using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using SilverPinesCoop.Steam;
using UnityEngine;

namespace SilverPinesCoop
{
    /// <summary>Entry point: binds the config, prepares the Steam bindings and puts <see cref="SteamNetManager"/> on
    /// BepInEx's persistent manager GameObject (DontDestroyOnLoad).</summary>
    [BepInPlugin(Guid, DisplayName, Version)]
    public sealed class Plugin : BasePlugin
    {
        public const string Guid = "com.riyelsinix.silverpines.coop";
        public const string DisplayName = "Silver Pines Coop";
        public const string Version = "0.4.0";

        internal static ManualLogSource L;
        internal static CoopConfig Cfg;

        public override void Load()
        {
            L = Log;
            Cfg = new CoopConfig(Config);
            SteamNative.InstallResolver();
            try
            {
                FocusPauseHook.ShouldKeepRunning = () => Cfg.KeepRunningUnfocused.Value && SteamNetManager.Instance != null &&
                                                         SteamNetManager.Instance.KeepRunning;
                FocusPauseHook.Install();
            }
            catch (System.Exception e)
            {
                Log.LogError($"Focus-loss pause hook failed: {e}");
            }
            ClassInjector.RegisterTypeInIl2Cpp<SteamNetManager>();
            AddComponent<SteamNetManager>();
            Log.LogInfo($"{DisplayName} {Version} loaded, protocol {Packets.ProtocolVersion}");
        }
    }

    internal sealed class CoopConfig
    {
        public readonly ConfigEntry<KeyCode> CreateLobbyKey;
        public readonly ConfigEntry<KeyCode> InviteKey;
        public readonly ConfigEntry<KeyCode> LeaveKey;
        public readonly ConfigEntry<KeyCode> LoopbackKey;
        public readonly ConfigEntry<int> MaxPlayers;
        public readonly ConfigEntry<int> TickRate;
        public readonly ConfigEntry<float> InterpolationDelay;
        public readonly ConfigEntry<bool> ShowStatus;
        public readonly ConfigEntry<bool> LoopbackTest;
        public readonly ConfigEntry<float> LoopbackOffsetX;
        public readonly ConfigEntry<bool> SyncWorld;
        public readonly ConfigEntry<bool> SyncStoryFlags;
        public readonly ConfigEntry<bool> SyncStoryNumbers;
        public readonly ConfigEntry<bool> SyncEnemies;
        public readonly ConfigEntry<bool> SyncPause;
        public readonly ConfigEntry<bool> SyncCutscenes;
        public readonly ConfigEntry<KeyCode> SaveCheckpointKey;
        public readonly ConfigEntry<string> LastCheckpointPeer;
        public readonly ConfigEntry<string> LastCheckpointToken;
        public readonly ConfigEntry<int> LastCheckpointSlot;
        public readonly ConfigEntry<int> LastCheckpointProfile;
        public readonly ConfigEntry<bool> KeepRunningUnfocused;
        public readonly ConfigEntry<float> PuppetDepthOffset;
        public readonly ConfigEntry<int> LocalTestPort;
        public readonly ConfigEntry<int> LocalTestWindowWidth;

        public CoopConfig(ConfigFile cfg)
        {
            CreateLobbyKey = cfg.Bind("Keys", "CreateLobby", KeyCode.F5,
                                      "Create a friends-only Steam lobby and open the invite overlay.");
            InviteKey = cfg.Bind("Keys", "Invite", KeyCode.F7, "Open the Steam invite overlay for the current lobby.");
            LeaveKey = cfg.Bind("Keys", "Leave", KeyCode.F6, "Leave the current lobby.");
            LoopbackKey = cfg.Bind("Keys", "ToggleLoopback", KeyCode.F8, "Toggle Debug.LoopbackTest in game.");
            MaxPlayers = cfg.Bind("Lobby", "MaxPlayers", 2, "Lobby size (2 = classic co-op; puppets support more).");
            TickRate = cfg.Bind("Sync", "TickRate", 20, "Player state packets per second on channel 0.");
            InterpolationDelay = cfg.Bind("Sync", "InterpolationDelay", 0.1f,
                                          "Seconds the 2P puppet is rendered behind the newest snapshot.");
            ShowStatus = cfg.Bind("UI", "ShowStatus", true, "Show the co-op status line in the top-left corner.");
            LoopbackTest = cfg.Bind("Debug", "LoopbackTest", false,
                                    "Mirror your own player as a local puppet (no Steam), to test the puppet alone.");
            LoopbackOffsetX = cfg.Bind("Debug", "LoopbackOffsetX", 1.5f, "Loopback puppet offset in world units.");
            SyncWorld = cfg.Bind("World", "SyncWorld", true,
                                 "Share world changes through the save data: doors, picked-up items (gone for everyone), " +
                                 "interactables, triggers, puzzles.");
            SyncStoryFlags = cfg.Bind("World", "SyncStoryFlags", true, "Share the game's persistent yes/no story variables.");
            SyncStoryNumbers = cfg.Bind("World", "SyncStoryNumbers", false,
                                        "Also share persistent number variables (off: some may be per-player counters).");
            SyncEnemies = cfg.Bind("World", "SyncEnemies", true,
                                   "Share damage to enemies in the same room, so a kill on one side is a kill on both.");
            SyncPause = cfg.Bind("World", "SyncPause", true,
                                 "Hold the game while another player has the pause menu open or watches a cutscene.");
            SyncCutscenes = cfg.Bind("World", "SyncCutscenes", true,
                                     "Start a cutscene for players in the same room when one player triggers it.");
            SaveCheckpointKey = cfg.Bind("Keys", "SaveCheckpoint", KeyCode.F9,
                                         "Save a shared co-op checkpoint to both players' local profiles.");
            LastCheckpointPeer = cfg.Bind("Save", "LastCheckpointPeer", "", "Managed by the mod: Steam partner of the committed checkpoint.");
            LastCheckpointToken = cfg.Bind("Save", "LastCheckpointToken", "", "Managed by the mod: identifier confirmed by both players.");
            LastCheckpointSlot = cfg.Bind("Save", "LastCheckpointSlot", -1, "Managed by the mod: committed local slot.");
            LastCheckpointProfile = cfg.Bind("Save", "LastCheckpointProfile", -1, "Managed by the mod: local profile used for that checkpoint.");
            KeepRunningUnfocused = cfg.Bind("World", "KeepRunningUnfocused", true,
                                            "During co-op, do not pause the game when its window loses focus.");
            PuppetDepthOffset = cfg.Bind("Sync", "PuppetDepthOffset", 0.05f,
                                         "How far the 2P puppet sits behind the local player along the camera axis " +
                                         "(stops the two flickering when they overlap).");
            LocalTestPort = cfg.Bind("LocalTest", "BasePort", 27960,
                                     "Two-copy test (launch option --coop-local, coop_local_test.bat): copy 1 uses this " +
                                     "UDP port on 127.0.0.1, copy 2 the next one.");
            LocalTestWindowWidth = cfg.Bind("LocalTest", "WindowWidth", 0,
                                            "Two-copy test window width in pixels: 0 = half the screen, side by side; " +
                                            "-1 = leave the game's window alone.");
        }
    }
}
