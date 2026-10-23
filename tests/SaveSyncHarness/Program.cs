using SilverPinesCoop.Game;
using SilverPinesCoop.Net;

var tests = new (string Name, Action Run)[]
{
    ("preparation barrier holds both writes", PreparationBarrier),
    ("ordinary save hooks and automatic slot selection", HooksAndSlot),
    ("guest-first save acknowledgements", () => AckOrder(true)),
    ("host-first save acknowledgements", () => AckOrder(false)),
    ("guest death reloads both from checkpoint", DeathLoadsBoth),
    ("native load selects common checkpoint", NativeLoad),
    ("duplicate packets cannot repeat IO", DuplicatePackets),
    ("old session and forged sender are ignored", OldSession),
    ("failed save invalidates checkpoint and ignores late callbacks", FailedSave),
    ("preparation timeout preserves checkpoint", PreparationTimeout),
    ("missing completion acknowledgement blocks transaction", MissingAck),
    ("disconnect clears operation and rejects late callbacks", DisconnectDuringSave),
    ("reconnect restores matching checkpoint", ReconnectMatching),
    ("reconnect discards mismatching checkpoint", ReconnectMismatch),
    ("offline overwrite invalidates shared checkpoint", OfflineOverwrite),
    ("local dual-window test does not coordinate writes", LocalDualWindow),
    ("death before first checkpoint keeps original load available", FirstDeath),
    ("delayed manager initialization preserves persisted checkpoint", ManagerInitialization),
    ("three-member lobby does not intercept ordinary IO", ThreeMemberLobby),
    ("death during save preparation cancels save and reloads checkpoint", DeathDuringPrepare),
    ("success callback waits until game IO is idle", CallbackWhileBusy),
    ("reconnect drains abandoned native IO before new operations", ReconnectWhileDraining),
    ("load timeout retains hold until disconnect", LoadTimeout),
    ("host death after readiness cancels save before execute", () => DeathAfterReady(true)),
    ("guest death after readiness cancels save before execute", () => DeathAfterReady(false)),
    ("older same-session announcement cannot erase committed checkpoint", OldAnnouncement),
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + error); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void PreparationBarrier()
{
    using var world = new World();
    world.Guest.Manager.IsBusy = true;
    world.Host.Run(() => world.Host.Manager.SaveGameInSlot(4));
    for (var i = 0; i < 15; i++) world.Advance();
    Check.That(world.Host.OperationFlag("LocalReady"), "host reaches ready state");
    Check.That(!world.Host.OperationFlag("RemoteReady"), "busy guest has not acknowledged readiness");
    Check.Equal(0, world.Host.Manager.Saves.Count, "host must not write before both ready");
    Check.Equal(0, world.Guest.Manager.Saves.Count, "guest must not write before both ready");
    Check.That(world.Host.Pause.Held && world.Guest.Pause.Held, "both held during preparation");
    world.Guest.Manager.IsBusy = false;
    world.Advance();
    Check.That(world.Host.OperationFlag("RemoteReady"), "guest readiness is delivered");
    Check.Equal(0, world.Host.Manager.Saves.Count, "readiness receipt itself cannot write");
    world.Advance(0.4f);
    Check.Equal(0, world.Host.Manager.Saves.Count, "world-packet drain delay remains in effect");
    world.Until(() => world.Host.Manager.Saves.Count == 1 && world.Guest.Manager.Saves.Count == 1);
    Check.Equal(4, world.Host.Manager.Saves[0], "host requested slot");
    Check.Equal(4, world.Guest.Manager.Saves[0], "guest same slot");
}

static void HooksAndSlot()
{
    using var world = new World();
    world.Host.Manager.AutoSlot = 7;
    world.Host.Run(world.Host.Sync.RequestSave);
    Check.Equal(0, world.Host.Manager.Saves.Count, "request only queues operation");
    world.Until(() => world.Host.Manager.Saves.Count == 1 && world.Guest.Manager.Saves.Count == 1);
    Check.Equal(7, world.Host.Manager.Saves[0], "slot captured from original game selector");
    Check.Equal(7, world.Guest.Manager.Saves[0], "captured slot forwarded");
    world.Host.Run(() => world.Host.Manager.SaveGameInSlot(8));
    world.Guest.Run(() => world.Guest.Manager.SaveGameInSlot(9));
    world.Pump();
    world.FinishSave();
    for (var i = 0; i < 15; i++) world.Advance();
    Check.Equal(1, world.Host.Manager.Saves.Count, "repeated clicks do not queue writes");
    Check.Equal(1, world.Guest.Manager.Saves.Count, "remote repeated click does not queue writes");
}

static void AckOrder(bool guestFirst)
{
    using var world = new World();
    world.BeginSave(3, guest: !guestFirst);
    var first = guestFirst ? world.Guest : world.Host;
    var second = guestFirst ? world.Host : world.Guest;
    first.Run(() => first.Manager.CompleteSave());
    world.Advance();
    Check.Equal(0UL, world.Host.State<ulong>("_checkpoint"), "one local success is insufficient");
    Check.Equal(0UL, world.Guest.State<ulong>("_checkpoint"), "guest waits for host completion decision");
    Check.That(world.Host.HasOperation && world.Guest.HasOperation, "both await joint result");
    second.Run(() => second.Manager.CompleteSave());
    world.Advance();
    world.Until(() => !world.Host.HasOperation && !world.Guest.HasOperation);
    var token = world.Host.State<ulong>("_checkpoint");
    Check.That(token != 0, "joint checkpoint is committed");
    Check.Equal(token, world.Guest.State<ulong>("_checkpoint"), "joint checkpoint matches");
    Check.Equal(world.Host.Config.LastCheckpointToken.Value, world.Guest.Config.LastCheckpointToken.Value, "persisted identity matches");
    Check.That(!world.Host.Pause.Held && !world.Guest.Pause.Held, "both holds released");
}

static void DeathLoadsBoth()
{
    using var world = new World();
    world.Checkpoint(5);
    var token = world.Host.State<ulong>("_checkpoint");
    world.Guest.Run(() => world.Guest.Globals.EventChannels.Player.PlayerDead.Raise());
    world.Pump();
    world.Until(() => world.Host.Manager.Loads.Count == 1 && world.Guest.Manager.Loads.Count == 1);
    Check.Equal(5, world.Host.Manager.Loads[0], "living host loads dead guest checkpoint");
    Check.Equal(5, world.Guest.Manager.Loads[0], "dead guest loads same checkpoint");
    world.Guest.Run(world.Guest.Manager.CompleteLoad);
    world.Advance();
    Check.That(world.Host.HasOperation, "guest loading alone cannot finish");
    world.Host.Run(world.Host.Manager.CompleteLoad);
    world.Advance();
    world.Until(() => !world.Host.HasOperation && !world.Guest.HasOperation);
    Check.Equal(token, world.Host.State<ulong>("_checkpoint"), "successful load preserves checkpoint");
    Check.That(!world.Guest.State<bool>("_dead"), "death flag cleared after reload");
}

static void NativeLoad()
{
    using var world = new World();
    world.Checkpoint(5);
    world.Host.Manager.Slots.Add(99);
    world.Host.Run(() => world.Host.Manager.LoadGameFromSlot(99));
    Check.Equal(0, world.Host.Manager.Loads.Count, "ordinary load is intercepted");
    world.Until(() => world.Host.Manager.Loads.Count == 1 && world.Guest.Manager.Loads.Count == 1);
    Check.Equal(5, world.Host.Manager.Loads[0], "ordinary selected slot replaced by common checkpoint");
    world.Host.Run(world.Host.Manager.LoadMostRecentSave);
    world.Guest.Run(world.Guest.Manager.LoadMostRecentSave);
    world.Pump();
    Check.Equal(1, world.Host.Manager.Loads.Count, "most recent load cannot bypass active operation");
    Check.Equal(1, world.Guest.Manager.Loads.Count, "guest most recent load cannot bypass operation");
}

static void DuplicatePackets()
{
    using var world = new World();
    world.BeginSave(4);
    foreach (var type in new[] { PacketType.CheckpointSavePrepare, PacketType.CheckpointReady, PacketType.CheckpointExecute })
    {
        var packet = world.Sent.First(x => x.Type == type);
        world.Replay(packet);
        world.Replay(packet);
    }
    world.Advance();
    Check.Equal(1, world.Host.Manager.Saves.Count, "duplicate packets do not repeat host IO");
    Check.Equal(1, world.Guest.Manager.Saves.Count, "duplicate packets do not repeat guest IO");
    world.FinishSave();
    foreach (var packet in world.Sent.Where(x => x.Type is PacketType.CheckpointSavePrepare or
        PacketType.CheckpointSaveAck or PacketType.CheckpointSaveFinish or PacketType.CheckpointExecute).ToArray())
        world.Replay(packet);
    world.Advance();
    Check.That(!world.Host.HasOperation && !world.Guest.HasOperation, "retired operation stays retired");
    Check.Equal(1, world.Guest.Manager.Saves.Count, "retired prepare does not create a second write");
}

static void OldSession()
{
    using var world = new World();
    world.Checkpoint();
    var previousEpoch = world.Host.State<ulong>("_epoch");
    var oldPackets = world.Sent.ToArray();
    world.Disconnect();
    world.Reconnect();
    Check.That(world.Host.State<ulong>("_epoch") != previousEpoch, "reconnect gets a new epoch");
    foreach (var packet in oldPackets) world.Replay(packet);
    world.Advance();
    Check.That(!world.Host.HasOperation && !world.Guest.HasOperation, "old epoch cannot create operations");
    Check.Equal(1, world.Host.Manager.Saves.Count, "stale traffic does not write host");
    var session = world.Sent.Last(x => x.Type == PacketType.CheckpointSession);
    world.Replay(session with { Sender = 1234 });
    Check.That(world.Guest.State<ulong>("_epoch") != previousEpoch, "forged sender cannot replace session");
}

static void FailedSave()
{
    using var world = new World();
    world.Checkpoint(2);
    world.BeginSave(2);
    world.Guest.Run(() => world.Guest.Manager.CompleteSave(false));
    world.Advance();
    Check.That(world.Host.State<bool>("_blocked") && world.Guest.State<bool>("_blocked"), "partial writes block further operations");
    Check.Equal(0UL, world.Host.State<ulong>("_checkpoint"), "failed overwrite cannot claim old checkpoint");
    Check.Equal("", world.Host.Config.LastCheckpointToken.Value, "host persisted checkpoint cleared");
    Check.Equal("", world.Guest.Config.LastCheckpointToken.Value, "guest persisted checkpoint cleared");
    world.Host.Run(() => world.Host.Manager.CompleteSave());
    world.Guest.Run(() => world.Guest.Manager.CompleteSave());
    world.Advance();
    world.Host.Run(world.Host.Sync.RequestSave);
    world.Advance();
    Check.Equal(0UL, world.Host.State<ulong>("_checkpoint"), "late success cannot resurrect checkpoint");
    Check.That(!world.Host.HasOperation && !world.Guest.HasOperation, "blocked session cannot start a new operation");
    Check.Equal(2, world.Host.Manager.Saves.Count, "no third write after failure");
}

static void PreparationTimeout()
{
    using var world = new World();
    world.Checkpoint(2);
    var token = world.Host.State<ulong>("_checkpoint");
    world.Guest.Manager.IsBusy = true;
    world.Host.Run(world.Host.Sync.RequestSave);
    world.Advance();
    world.Advance();
    world.Advance(91);
    Check.That(!world.Host.HasOperation && !world.Guest.HasOperation, "prepare timeout aborts both");
    Check.That(!world.Host.State<bool>("_blocked") && !world.Guest.State<bool>("_blocked"), "no IO means session can continue");
    Check.Equal(token, world.Host.State<ulong>("_checkpoint"), "prepare timeout preserves host checkpoint");
    Check.Equal(token, world.Guest.State<ulong>("_checkpoint"), "prepare timeout preserves guest checkpoint");
    Check.Equal(1, world.Host.Manager.Saves.Count, "prepare timeout performs no extra write");
    Check.That(!world.Host.Pause.Held && !world.Guest.Pause.Held, "prepare timeout releases holds");
}

static void MissingAck()
{
    using var world = new World();
    world.Drop.Add(PacketType.CheckpointSaveAck);
    world.BeginSave();
    world.Host.Run(() => world.Host.Manager.CompleteSave());
    world.Guest.Run(() => world.Guest.Manager.CompleteSave());
    world.Advance();
    Check.That(world.Host.HasOperation, "host waits for unavailable acknowledgement");
    world.Advance(91);
    Check.That(!world.Host.HasOperation && !world.Guest.HasOperation, "missing acknowledgement times out both");
    Check.That(world.Host.State<bool>("_blocked") && world.Guest.State<bool>("_blocked"), "uncertain commit requires reconnect");
    Check.Equal(0UL, world.Host.State<ulong>("_checkpoint"), "timeout cannot mark a checkpoint");
    Check.Equal(0UL, world.Guest.State<ulong>("_checkpoint"), "guest timeout cannot mark a checkpoint");
}

static void DisconnectDuringSave()
{
    using var world = new World();
    world.BeginSave();
    world.Disconnect();
    world.Host.Run(() => world.Host.Manager.CompleteSave());
    world.Guest.Run(() => world.Guest.Manager.CompleteSave());
    world.Advance();
    Check.That(!world.Host.HasOperation && !world.Guest.HasOperation, "disconnect removes operations");
    Check.Equal(0UL, world.Host.State<ulong>("_checkpoint"), "disconnected late completion ignored");
    Check.That(!world.Host.Pause.Held && !world.Guest.Pause.Held, "disconnect clears coordination holds");
    world.Reconnect();
    Check.Equal(0UL, world.Guest.State<ulong>("_checkpoint"), "partial operation is not restored on reconnect");
}

static void ReconnectMatching()
{
    using var world = new World();
    world.Checkpoint(8);
    var token = world.Host.State<ulong>("_checkpoint");
    world.Disconnect();
    world.Reconnect();
    Check.Equal(token, world.Host.State<ulong>("_checkpoint"), "host checkpoint restored");
    Check.Equal(token, world.Guest.State<ulong>("_checkpoint"), "matching guest checkpoint restored");
    Check.Equal(8, world.Host.State<int>("_slot"), "restored slot");
}

static void ReconnectMismatch()
{
    using var world = new World();
    world.Checkpoint(8);
    world.Disconnect();
    world.Guest.Config.LastCheckpointToken.Value = "BAD";
    world.Reconnect();
    Check.Equal(0UL, world.Host.State<ulong>("_checkpoint"), "mismatching host checkpoint discarded");
    Check.Equal(0UL, world.Guest.State<ulong>("_checkpoint"), "mismatching guest checkpoint discarded");
    Check.Equal("", world.Host.Config.LastCheckpointToken.Value, "host mismatch persisted invalidation");
    Check.Equal("", world.Guest.Config.LastCheckpointToken.Value, "guest mismatch persisted invalidation");
}

static void OfflineOverwrite()
{
    using var world = new World();
    world.Checkpoint(3);
    world.Disconnect();
    world.Host.Run(() => world.Host.Manager.SaveGameInSlot(3));
    Check.Equal(2, world.Host.Manager.Saves.Count, "offline save proceeds normally");
    Check.Equal("", world.Host.Config.LastCheckpointToken.Value, "offline overwrite invalidates saved shared identity");
    world.Host.Run(() => world.Host.Manager.CompleteSave());
    world.Reconnect();
    Check.Equal(0UL, world.Guest.State<ulong>("_checkpoint"), "reconnect propagates invalidation");
}

static void LocalDualWindow()
{
    using var world = new World(false);
    world.Host.Net.LocalTestMode = world.Guest.Net.LocalTestMode = true;
    world.Host.Net.PartnerId = 2;
    world.Guest.Net.PartnerId = 1;
    world.Advance();
    world.Host.Run(() => world.Host.Manager.SaveGameInSlot(2));
    Check.Equal(1, world.Host.Manager.Saves.Count, "ordinary local save passes through");
    Check.Equal(0, world.Guest.Manager.Saves.Count, "other local window must not write shared disk");
    Check.Equal(0, world.Sent.Count, "local dual-window does not send checkpoint commands");
}

static void FirstDeath()
{
    using var world = new World();
    world.Host.Manager.Slots.Add(6);
    world.Host.Run(() => world.Host.Globals.EventChannels.Player.PlayerDead.Raise());
    world.Host.Run(() => world.Host.Manager.LoadGameFromSlot(6));
    Check.Equal(1, world.Host.Manager.Loads.Count, "death-menu original load remains available before common checkpoint");
    Check.Equal(6, world.Host.Manager.Loads[0], "original death-menu slot is preserved");
    Check.Equal(0, world.Guest.Manager.Loads.Count, "no shared checkpoint means no coordinated load");
    Check.That(!world.Host.HasOperation && !world.Guest.HasOperation, "fallback load creates no checkpoint transaction");
}

static void ManagerInitialization()
{
    using var world = new World(initialize: false);
    const string token = "000000000000A123";
    foreach (var node in new[] { world.Host, world.Guest })
    {
        node.Config.LastCheckpointPeer.Value = node.Net.PartnerId.ToString();
        node.Config.LastCheckpointProfile.Value = 0;
        node.Config.LastCheckpointSlot.Value = 3;
        node.Config.LastCheckpointToken.Value = token;
        node.Manager.Slots.Add(3);
        node.Manager.IsInitialized = false;
    }
    world.Advance();
    Check.Equal(0, world.Sent.Count, "host cannot advertise empty checkpoint before manager initialization");
    Check.Equal(token, world.Host.Config.LastCheckpointToken.Value, "initial delay preserves host persisted identity");
    world.Host.Manager.IsInitialized = true;
    world.Advance();
    Check.That(!world.Host.State<bool>("_ready"), "host waits for uninitialized guest");
    Check.Equal(token, world.Guest.Config.LastCheckpointToken.Value, "guest delay cannot erase persisted checkpoint");
    world.Guest.Manager.IsInitialized = true;
    world.Advance(2.1f);
    world.Until(() => world.Host.State<bool>("_ready") && world.Guest.State<bool>("_ready"));
    Check.Equal(0xA123UL, world.Host.State<ulong>("_checkpoint"), "host restores checkpoint after initialization");
    Check.Equal(0xA123UL, world.Guest.State<ulong>("_checkpoint"), "guest restores checkpoint after initialization");
}

static void ThreeMemberLobby()
{
    using var world = new World(false);
    // Production SteamNetManager.PartnerId returns zero unless membership is exactly two.
    world.Host.Net.Connected = world.Guest.Net.Connected = true;
    world.Host.Run(() => world.Host.Manager.SaveGameInSlot(3));
    Check.Equal(1, world.Host.Manager.Saves.Count, "connected lobby without exactly one partner keeps original saves");
    world.Host.Run(() => world.Host.Manager.CompleteSave());
    world.Host.Run(() => world.Host.Manager.LoadGameFromSlot(3));
    Check.Equal(1, world.Host.Manager.Loads.Count, "connected lobby without exactly one partner keeps original loads");
    Check.Equal(0, world.Sent.Count, "no joint checkpoint traffic in three-member lobby");
}

static void DeathDuringPrepare()
{
    using var world = new World();
    world.Checkpoint(6);
    var token = world.Host.State<ulong>("_checkpoint");
    world.Host.Run(world.Host.Sync.RequestSave);
    world.Advance();
    Check.That(world.Host.HasOperation && world.Guest.HasOperation, "save preparation exists on both nodes");
    world.Guest.Run(() => world.Guest.Globals.EventChannels.Player.PlayerDead.Raise());
    world.Pump();
    world.Until(() => world.Host.Manager.Loads.Count == 1 && world.Guest.Manager.Loads.Count == 1);
    Check.Equal(1, world.Host.Manager.Saves.Count, "death before execute cancels host new save");
    Check.Equal(1, world.Guest.Manager.Saves.Count, "death before execute cancels guest new save");
    Check.Equal(6, world.Host.Manager.Loads[0], "host reloads previous common slot");
    Check.Equal(6, world.Guest.Manager.Loads[0], "guest reloads previous common slot");
    Check.Equal(token, world.Host.State<ulong>("_checkpoint"), "cancelled preparation preserves rollback target");
}

static void CallbackWhileBusy()
{
    using var world = new World();
    world.BeginSave();
    world.Host.Run(() => world.Host.Globals.EventChannels.SaveLoad.SaveCompletedSuccesfully.Raise());
    world.Guest.Run(() => world.Guest.Globals.EventChannels.SaveLoad.SaveCompletedSuccesfully.Raise());
    world.Advance();
    Check.That(!world.Host.OperationFlag("LocalDone") && !world.Guest.OperationFlag("LocalDone"), "success event cannot outrun game busy state");
    Check.That(!world.Sent.Any(x => x.Type == PacketType.CheckpointSaveAck), "busy guest cannot acknowledge completion");
    world.Host.Manager.IsBusy = world.Guest.Manager.IsBusy = false;
    world.Advance();
    world.Until(() => !world.Host.HasOperation && !world.Guest.HasOperation);
    Check.That(world.Host.State<ulong>("_checkpoint") != 0, "idle state releases pending success result");
}

static void ReconnectWhileDraining()
{
    using var world = new World();
    world.BeginSave();
    world.Disconnect();
    world.Host.Net.PartnerId = 2;
    world.Guest.Net.PartnerId = 1;
    world.Advance();
    world.Host.Run(world.Host.Sync.RequestSave);
    for (var i = 0; i < 10; i++) world.Advance();
    Check.That(world.Host.State<bool>("_draining") && world.Guest.State<bool>("_draining"), "abandoned game operations remain draining");
    Check.Equal(1, world.Host.Manager.Saves.Count, "host cannot start fresh IO while old callback outstanding");
    Check.Equal(1, world.Guest.Manager.Saves.Count, "guest cannot start fresh IO while old callback outstanding");
    world.Host.Run(() => world.Host.Manager.CompleteSave());
    world.Advance();
    Check.That(!world.Host.State<bool>("_draining"), "host callback drains its old operation");
    Check.That(world.Guest.State<bool>("_draining"), "guest still awaits its own callback");
    Check.That(!world.Host.State<bool>("_ready"), "host cannot operate before guest drain and handshake");
    world.Guest.Run(() => world.Guest.Manager.CompleteSave());
    world.Advance(2.1f);
    world.Advance(2.1f);
    world.Until(() => world.Host.State<bool>("_ready") && world.Guest.State<bool>("_ready"));
    Check.Equal(0UL, world.Host.State<ulong>("_checkpoint"), "drained abandoned success cannot become checkpoint");
    world.BeginSave();
    Check.Equal(2, world.Host.Manager.Saves.Count, "fresh coordinated operation works after drain");
}

static void LoadTimeout()
{
    using var world = new World();
    world.Checkpoint();
    world.Guest.Run(() => world.Guest.Globals.EventChannels.Player.PlayerDead.Raise());
    world.Pump();
    world.Until(() => world.Host.Manager.Loads.Count == 1 && world.Guest.Manager.Loads.Count == 1);
    world.Advance(91);
    Check.That(world.Host.State<bool>("_blocked") && world.Guest.State<bool>("_blocked"), "incomplete load blocks both");
    Check.That(world.Host.Pause.Held && world.Guest.Pause.Held, "load uncertainty holds both worlds");
    Check.That(world.Host.Sync.SuppressWorldSync && world.Guest.Sync.SuppressWorldSync, "load uncertainty suppresses world updates");
    world.Disconnect();
    Check.That(!world.Host.Pause.Held && !world.Guest.Pause.Held, "F6 disconnect releases stuck load holds");
}

static void DeathAfterReady(bool host)
{
    using var world = new World();
    world.Checkpoint(6);
    world.Host.Run(world.Host.Sync.RequestSave);
    world.Until(() => world.Host.OperationFlag("LocalReady") && world.Host.OperationFlag("RemoteReady"));
    var dead = host ? world.Host : world.Guest;
    dead.Run(() => dead.Globals.EventChannels.Player.PlayerDead.Raise());
    world.Pump();
    world.Advance(0.6f);
    world.Advance();
    Check.Equal(1, world.Host.Manager.Saves.Count, "death after readiness must prevent host save execution");
    Check.Equal(1, world.Guest.Manager.Saves.Count, "death after readiness must prevent guest save execution");
    world.Until(() => world.Host.Manager.Loads.Count == 1 && world.Guest.Manager.Loads.Count == 1);
    Check.Equal(6, world.Host.Manager.Loads[0], "rollback uses previous common checkpoint");
}

static void OldAnnouncement()
{
    using var world = new World();
    var emptyAnnouncement = world.Sent.First(x => x.Type == PacketType.CheckpointSession);
    world.Checkpoint(6);
    var token = world.Guest.State<ulong>("_checkpoint");
    world.Replay(emptyAnnouncement);
    Check.Equal(token, world.Guest.State<ulong>("_checkpoint"), "old zero-token announcement cannot erase a committed checkpoint");
    Check.That(world.Guest.Config.LastCheckpointToken.Value.Length != 0, "old announcement cannot clear persisted checkpoint");
}
