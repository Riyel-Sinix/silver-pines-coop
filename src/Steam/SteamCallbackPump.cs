using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;

namespace SilverPinesCoop.Steam
{
    /// <summary>A Steam callback or call result copied out of the dispatch loop, handled later on the main thread.</summary>
    internal readonly struct SteamCallback
    {
        public readonly int Id;
        public readonly byte[] Data;
        public readonly ulong ApiCall;   // non-zero: the result of one of our own API calls
        public readonly bool IoFailure;

        public SteamCallback(int id, byte[] data, ulong apiCall, bool ioFailure)
        {
            Id = id;
            Data = data;
            ApiCall = apiCall;
            IoFailure = ioFailure;
        }
    }

    /// <summary>
    /// The game's Steamworks.NET runs Steam in manual-dispatch mode: SteamManager.Update -> CallbackDispatcher.RunFrame
    /// drains every callback with SteamAPI_ManualDispatch_GetNextCallback and drops the ones it has no handler for. The
    /// IL2CPP build contains no Callback&lt;T&gt; for the lobby / session callbacks, so they cannot be registered there.
    /// Instead GetNextCallback is detoured: each callback is copied here when it is one of ours and then passed on
    /// unchanged, so the game's own handlers keep working. Our call results (CreateLobby, JoinLobby) are fetched from the
    /// SteamAPICallCompleted_t notification the same way Steamworks.NET fetches its own.
    /// </summary>
    internal static unsafe class SteamCallbackPump
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte GetNextCallbackFn(int steamPipe, IntPtr callbackMsg);

        // CallbackMsg_t (pack 8): HSteamUser m_hSteamUser, int m_iCallback, uint8* m_pubParam, int m_cubParam
        private const int MsgCallbackIdOffset = 4;
        private const int MsgParamOffset = 8;
        private const int MsgParamSizeOffset = 16;

        private static readonly HashSet<int> Wanted = new()
        {
            SteamNative.CallbackGameLobbyJoinRequested, SteamNative.CallbackLobbyEnter,
            SteamNative.CallbackLobbyChatUpdate, SteamNative.CallbackSessionRequest, SteamNative.CallbackSessionFailed,
        };

        // Steamworks.NET pumps callbacks from SteamManager.Update (main thread), but nothing guarantees it: lock anyway
        private static readonly object Sync = new();
        private static readonly Queue<SteamCallback> Pending = new();
        private static readonly Dictionary<ulong, int> PendingCalls = new();   // API call handle -> expected callback id
        private static GetNextCallbackFn s_hook;                              // kept alive for the native detour
        private static GetNextCallbackFn s_original;
        private static INativeDetour s_detour;
        private static bool s_hookFaulted;

        public static bool Installed => s_detour != null;

        /// <summary>Detours GetNextCallback once steam_api64.dll is loaded; false while it is not.</summary>
        public static bool TryInstall()
        {
            if (s_detour != null)
                return true;
            var module = SteamNative.LoadedModule;
            if (module == IntPtr.Zero)
                return false;
            var target = SteamNative.GetProcAddress(module, "SteamAPI_ManualDispatch_GetNextCallback");
            if (target == IntPtr.Zero)
                throw new EntryPointNotFoundException("SteamAPI_ManualDispatch_GetNextCallback");
            s_hook = Hook;
            s_detour = INativeDetour.CreateAndApply(target, s_hook, out s_original);
            Plugin.L.LogInfo($"Steam callback hook installed at 0x{target.ToInt64():X}");
            return true;
        }

        /// <summary>Have the result of an API call (e.g. CreateLobby -> LobbyCreated_t) delivered through the queue.</summary>
        public static void ExpectCallResult(ulong apiCall, int callbackId)
        {
            if (apiCall == 0)
                return;
            lock (Sync)
                PendingCalls[apiCall] = callbackId;
        }

        public static bool TryDequeue(out SteamCallback callback)
        {
            lock (Sync)
                return Pending.TryDequeue(out callback);
        }

        private static byte Hook(int steamPipe, IntPtr callbackMsg)
        {
            var got = s_original(steamPipe, callbackMsg);
            if (got == 0 || s_hookFaulted)
                return got;
            try
            {
                var msg = (byte*)callbackMsg;
                var id = *(int*)(msg + MsgCallbackIdOffset);
                var param = *(byte**)(msg + MsgParamOffset);
                var size = *(int*)(msg + MsgParamSizeOffset);
                if (id == SteamNative.CallbackSteamApiCallCompleted)
                    TakeCallResult(steamPipe, param, size);
                else if (Wanted.Contains(id) && param != null && size > 0)
                {
                    var copy = new byte[size];
                    Marshal.Copy((IntPtr)param, copy, 0, size);
                    lock (Sync)
                        Pending.Enqueue(new SteamCallback(id, copy, 0, false));
                }
            }
            catch (Exception e)
            {
                // never let an exception unwind into the game's dispatcher; stop observing instead
                s_hookFaulted = true;
                Plugin.L.LogError($"Steam callback hook disabled after an error: {e}");
            }
            return got;
        }

        private static void TakeCallResult(int steamPipe, byte* param, int size)
        {
            // SteamAPICallCompleted_t: SteamAPICall_t m_hAsyncCall, int m_iCallback, uint32 m_cubParam
            if (param == null || size < 16)
                return;
            var call = *(ulong*)param;
            var callbackId = *(int*)(param + 8);
            var resultSize = *(int*)(param + 12);
            lock (Sync)
            {
                if (!PendingCalls.TryGetValue(call, out var expected) || expected != callbackId)
                    return;
                PendingCalls.Remove(call);
            }
            if (resultSize <= 0 || resultSize > 4096)
                return;
            var result = new byte[resultSize];
            byte failed = 0;
            fixed (byte* buffer = result)
            {
                if (SteamNative.SteamAPI_ManualDispatch_GetAPICallResult(steamPipe, call, buffer, resultSize, callbackId,
                                                                         &failed) == 0)
                    failed = 1;
            }
            lock (Sync)
                Pending.Enqueue(new SteamCallback(callbackId, result, call, failed != 0));
        }
    }
}
