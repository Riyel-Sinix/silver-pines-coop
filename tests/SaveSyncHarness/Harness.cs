using System.Reflection;
using SilverPinesCoop;
using SilverPinesCoop.Game;
using SilverPinesCoop.Net;
using SilverPinesCoop.Sync;

internal sealed record Message(ulong Sender, ulong Receiver, PacketType Type, byte[] Body);

internal sealed class Node
{
    public Config Config { get; } = new();
    public Globals Globals { get; } = new();
    public SaveDataManager Manager { get; } = new();
    public SteamNetManager Net { get; }
    public PauseSync Pause { get; } = new();
    public SaveSync Sync { get; }
    public object Player { get; set; } = new();
    private readonly Func<int, bool> _save, _load;
    private readonly Func<bool> _recent;

    public Node(World world, ulong id, ulong peer)
    {
        Net = new SteamNetManager { LocalSteamId = id, PartnerId = peer };
        Net.Send = (receiver, type, body) => world.Send(new Message(id, receiver, type, body));
        Activate();
        Sync = new SaveSync(Net, Pause);
        _save = SaveLoadHook.InterceptSave;
        _load = SaveLoadHook.InterceptLoad;
        _recent = SaveLoadHook.InterceptRecent;
    }

    private void Activate()
    {
        Plugin.Cfg = Config;
        SaveDataManager.Instance = Manager;
        GameAccess.Globals = Globals;
        LocalPlayer.Current = Player;
        SaveLoadHook.InterceptSave = _save;
        SaveLoadHook.InterceptLoad = _load;
        SaveLoadHook.InterceptRecent = _recent;
    }

    public void Run(Action action) { Activate(); action(); }
    public T State<T>(string name) => (T)typeof(SaveSync).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Sync)!;
    public bool HasOperation => State<object>("_op") != null;
    public bool OperationFlag(string name)
    {
        var op = State<object>("_op");
        return op != null && (bool)op.GetType().GetField(name)!.GetValue(op)!;
    }

    public void Receive(Message message)
    {
        Run(() =>
        {
            using var stream = new MemoryStream(message.Body, false);
            using var reader = new BinaryReader(stream);
            var sender = message.Sender;
            switch (message.Type)
            {
                case PacketType.CheckpointSession: Sync.OnSession(sender, reader); break;
                case PacketType.CheckpointSessionAck: Sync.OnSessionAck(sender, reader); break;
                case PacketType.CheckpointSaveRequest: Sync.OnSaveRequest(sender, reader); break;
                case PacketType.CheckpointLoadRequest: Sync.OnLoadRequest(sender, reader); break;
                case PacketType.CheckpointDeath: Sync.OnDeathRequest(sender, reader); break;
                case PacketType.CheckpointSavePrepare: Sync.OnSavePrepare(sender, reader); break;
                case PacketType.CheckpointLoadPrepare: Sync.OnLoadPrepare(sender, reader); break;
                case PacketType.CheckpointReady: Sync.OnReady(sender, reader); break;
                case PacketType.CheckpointExecute: Sync.OnExecute(sender, reader); break;
                case PacketType.CheckpointSaveAck: Sync.OnSaveAck(sender, reader); break;
                case PacketType.CheckpointLoadAck: Sync.OnLoadAck(sender, reader); break;
                case PacketType.CheckpointSaveFinish: Sync.OnSaveFinish(sender, reader); break;
                case PacketType.CheckpointLoadFinish: Sync.OnLoadFinish(sender, reader); break;
                case PacketType.CheckpointAbort: Sync.OnAbort(sender, reader); break;
                default: throw new InvalidOperationException("Unhandled test message: " + message.Type);
            }
            Check.Equal(stream.Length, stream.Position, "handler consumes its payload");
        });
    }
}

internal sealed class World : IDisposable
{
    public Node Host { get; }
    public Node Guest { get; }
    public float Time { get; private set; }
    public Queue<Message> Pending { get; } = new();
    public List<Message> Sent { get; } = new();
    public HashSet<PacketType> Drop { get; } = new();

    public World(bool connect = true, bool initialize = true)
    {
        Host = new Node(this, 1, connect ? 2UL : 0UL);
        Guest = new Node(this, 2, connect ? 1UL : 0UL);
        if (initialize) Advance(0);
    }

    public bool Send(Message message)
    {
        Sent.Add(message);
        if (!Drop.Contains(message.Type)) Pending.Enqueue(message);
        return true;
    }
    public void Pump()
    {
        var count = 0;
        while (Pending.TryDequeue(out var message))
        {
            if (++count > 1000) throw new InvalidOperationException("Message loop did not settle");
            (message.Receiver == 1 ? Host : Guest).Receive(message);
        }
    }
    public void Replay(Message message) { Pending.Enqueue(message); Pump(); }
    public void Advance(float delta = 0.1f)
    {
        Time += delta;
        Host.Run(() => Host.Sync.Update(Time));
        Guest.Run(() => Guest.Sync.Update(Time));
        Pump();
    }
    public void Until(Func<bool> predicate, int ticks = 40)
    {
        for (var i = 0; !predicate() && i < ticks; i++) Advance();
        Check.That(predicate(), "condition becomes true within the bounded simulation");
    }
    public void BeginSave(int slot = 2, bool guest = false)
    {
        var node = guest ? Guest : Host;
        var hostCount = Host.Manager.Saves.Count;
        var guestCount = Guest.Manager.Saves.Count;
        node.Run(() => node.Manager.SaveGameInSlot(slot));
        Until(() => Host.Manager.Saves.Count == hostCount + 1 && Guest.Manager.Saves.Count == guestCount + 1);
    }
    public void FinishSave(bool guestFirst = true)
    {
        var first = guestFirst ? Guest : Host;
        var second = guestFirst ? Host : Guest;
        first.Run(() => first.Manager.CompleteSave());
        Advance();
        second.Run(() => second.Manager.CompleteSave());
        Advance();
        Until(() => !Host.HasOperation && !Guest.HasOperation);
    }
    public void Checkpoint(int slot = 2)
    {
        BeginSave(slot);
        FinishSave();
        Check.That(Host.State<ulong>("_checkpoint") != 0, "host confirms checkpoint");
        Check.Equal(Host.State<ulong>("_checkpoint"), Guest.State<ulong>("_checkpoint"), "same checkpoint token");
    }
    public void Disconnect()
    {
        Host.Net.PartnerId = Guest.Net.PartnerId = 0;
        Host.Run(Host.Sync.Disconnect);
        Guest.Run(Guest.Sync.Disconnect);
        Pending.Clear();
    }
    public void Reconnect()
    {
        Host.Net.PartnerId = 2;
        Guest.Net.PartnerId = 1;
        Advance();
        Until(() => Host.State<bool>("_ready") && Guest.State<bool>("_ready"));
    }
    public void Dispose()
    {
        Host.Run(Host.Sync.Dispose);
        Guest.Run(Guest.Sync.Dispose);
    }
}

internal static class Check
{
    public static void That(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    public static void Equal<T>(T expected, T actual, string reason)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{reason}: expected {expected}, actual {actual}");
    }
}
