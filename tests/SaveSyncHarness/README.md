# SaveSync state-machine harness

Run without the game or network:

```powershell
dotnet run --project tests/SaveSyncHarness/SaveSyncHarness.csproj -c Release
```

Requires .NET SDK 10. This console project compiles the production `src/Sync/SaveSync.cs` and `src/Net/Packets.cs` directly. Stubs provide two independent profiles, event channels, configurations, game managers, native-hook entry points, and a deterministic reliable-message queue. Each node activates its own static game context before invoking production code.

The checks cover the readiness barrier, ordinary save/load interception, automatic slot selection, both completion orders, joint death reload, death before and after readiness and while a save is executing, duplicate and retired packets, older session announcements, session-sequence reset after epoch change, failed IO and late callbacks, prepare and execution timeouts, disconnect, reconnect with matching or mismatching checkpoints, delayed manager initialization, callback/IO completion ordering, abandoned IO draining, and the shared-disk exclusion for local dual-window mode.

The harness checks coordination and packet behavior. It does not execute native detours, the real game IO, Steam transport, or scene loading; two-PC Steam testing remains necessary for those integrations.
