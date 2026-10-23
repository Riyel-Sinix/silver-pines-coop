using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using UnityEngine.Events;

namespace UnityEngine { }
namespace UnityEngine.Events { public delegate void UnityAction(); }

namespace Il2CppInterop.Runtime
{
    public static class DelegateSupport
    {
        public static T ConvertDelegate<T>(Action action) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), action.Target, action.Method);
    }
}

namespace SilverPinesCoop
{
    internal sealed class Entry<T>(T value) { public T Value { get; set; } = value; }
    internal sealed class Config
    {
        public Entry<string> LastCheckpointPeer { get; } = new("");
        public Entry<int> LastCheckpointProfile { get; } = new(-1);
        public Entry<int> LastCheckpointSlot { get; } = new(-1);
        public Entry<string> LastCheckpointToken { get; } = new("");
        public Entry<string> SaveCheckpointKey { get; } = new("F8");
    }
    internal sealed class Logger
    {
        public void LogInfo(object message) { }
        public void LogWarning(object message) { }
        public void LogError(object message) { }
    }
    internal static class Plugin
    {
        public static Config Cfg;
        public static Logger L { get; } = new();
    }
}

internal sealed class VoidGameEventChannel
{
    private event UnityAction Listeners;
    public void Register(UnityAction action) => Listeners += action;
    public void Unregister(UnityAction action) => Listeners -= action;
    public void Raise() => Listeners?.Invoke();
}

internal sealed class SlotGameEventChannel
{
    public void Raise(int slot) => SaveDataManager.Instance.LoadGameFromSlot(slot);
}

internal sealed class SaveDataManager
{
    public static SaveDataManager Instance;
    public bool IsInitialized { get; set; } = true;
    public bool ProfilesLoaded { get; set; } = true;
    public bool IsBusy { get; set; }
    public ProfileData SharedData { get; } = new();
    public HashSet<int> Slots { get; } = new();
    public List<int> Saves { get; } = new();
    public List<int> Loads { get; } = new();
    public int AutoSlot { get; set; } = 2;

    public bool HasSaveInSlot(int slot) => Slots.Contains(slot);
    public void SaveGame() => SaveGameInSlot(AutoSlot);
    public void SaveGameInSlot(int slot)
    {
        if (SaveLoadHook.ShouldInterceptSave(slot)) return;
        Saves.Add(slot);
        IsBusy = true;
    }
    public void LoadGameFromSlot(int slot)
    {
        if (SaveLoadHook.ShouldInterceptLoad(slot)) return;
        Loads.Add(slot);
        IsBusy = true;
    }
    public void LoadMostRecentSave()
    {
        if (SaveLoadHook.ShouldInterceptRecent()) return;
        LoadGameFromSlot(Slots.Count == 0 ? -1 : Slots.Max());
    }
    public void CompleteSave(bool success = true)
    {
        IsBusy = false;
        if (success && Saves.Count > 0) Slots.Add(Saves[^1]);
        var events = GameAccess.Globals.EventChannels.SaveLoad;
        (success ? events.SaveCompletedSuccesfully : events.SaveCompletedFailed).Raise();
    }
    public void CompleteLoad()
    {
        IsBusy = false;
        GameAccess.Globals.EventChannels.SaveLoad.OnGameReloadedEvent.Raise();
    }
}

internal sealed class ProfileData { public int SaveProfileIndex { get; set; } }

namespace SilverPinesCoop.Game
{
    internal sealed class Globals { public EventChannels EventChannels { get; } = new(); }
    internal sealed class EventChannels
    {
        public SaveLoadEvents SaveLoad { get; } = new();
        public PlayerEvents Player { get; } = new();
    }
    internal sealed class SaveLoadEvents
    {
        public VoidGameEventChannel SaveCompletedSuccesfully { get; } = new();
        public VoidGameEventChannel SaveCompletedFailed { get; } = new();
        public VoidGameEventChannel OnGameReloadedEvent { get; } = new();
        public SlotGameEventChannel GameLoad { get; } = new();
    }
    internal sealed class PlayerEvents { public VoidGameEventChannel PlayerDead { get; } = new(); }
    internal static class GameAccess { public static Globals Globals; }
    internal static class LocalPlayer
    {
        public static object Current;
        public static object Find() => Current;
    }
    internal static class SaveLoadHook
    {
        public static Func<int, bool> InterceptSave;
        public static Func<int, bool> InterceptLoad;
        public static Func<bool> InterceptRecent;
        public static bool Installed { get; private set; }
        private static int _bypass;
        public static void Install() => Installed = true;
        public static void ExecuteLocal(Action action)
        {
            _bypass++;
            try { action(); }
            finally { _bypass--; }
        }
        public static bool ShouldInterceptSave(int slot) => _bypass == 0 && InterceptSave?.Invoke(slot) == true;
        public static bool ShouldInterceptLoad(int slot) => _bypass == 0 && InterceptLoad?.Invoke(slot) == true;
        public static bool ShouldInterceptRecent() => _bypass == 0 && InterceptRecent?.Invoke() == true;
    }
}

namespace SilverPinesCoop.Sync
{
    internal sealed class PauseSync
    {
        public bool Held { get; private set; }
        public string Notice { get; private set; }
        public void SetOperationHold(bool held, string notice = null) { Held = held; Notice = notice; }
    }
}

namespace SilverPinesCoop.Net
{
    internal sealed class SteamNetManager
    {
        public bool LocalTestMode { get; set; }
        public bool Connected { get; set; } = true;
        public ulong LocalSteamId { get; set; }
        public ulong PartnerId { get; set; }
        public ulong HostId { get; set; } = 1;
        public bool CheckpointPeerCompatible { get; set; } = true;
        public Func<ulong, PacketType, byte[], bool> Send;
        public bool SendReliable(ulong peer, PacketType type, Action<BinaryWriter> body)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            body(writer);
            return Send(peer, type, stream.ToArray());
        }
    }
}
