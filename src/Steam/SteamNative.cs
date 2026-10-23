using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SilverPinesCoop.Steam
{
    /// <summary>
    /// The Steamworks flat API of the steam_api64.dll the game ships (Steamworks.NET 20.1.0 / SDK 1.53a). The game's own
    /// SteamManager initializes Steam (app 2333000) and runs the callbacks; this only binds to the same, already loaded
    /// module, so there is exactly one Steam session in the process.
    /// </summary>
    internal static unsafe class SteamNative
    {
        private const string Lib = "steam_api64";

        public const int CallbackLobbyEnter = 504;                      // LobbyEnter_t
        public const int CallbackLobbyChatUpdate = 506;                 // LobbyChatUpdate_t
        public const int CallbackLobbyCreated = 513;                    // LobbyCreated_t (call result)
        public const int CallbackGameLobbyJoinRequested = 333;          // GameLobbyJoinRequested_t
        public const int CallbackSteamApiCallCompleted = 703;           // SteamAPICallCompleted_t
        public const int CallbackSessionRequest = 1251;                 // SteamNetworkingMessagesSessionRequest_t
        public const int CallbackSessionFailed = 1252;                  // SteamNetworkingMessagesSessionFailed_t

        public const int LobbyTypeFriendsOnly = 1;                      // ELobbyType.k_ELobbyTypeFriendsOnly
        public const uint ChatRoomEnterSuccess = 1;                     // EChatRoomEnterResponse.Success
        public const int ResultOK = 1;                                  // EResult.k_EResultOK

        public const int SendUnreliableNoNagle = 1;                     // k_nSteamNetworkingSend_UnreliableNoNagle
        public const int SendReliable = 8;                              // k_nSteamNetworkingSend_Reliable
        public const int SendAutoRestartBrokenSession = 32;             // k_nSteamNetworkingSend_AutoRestartBrokenSession

        // SteamNetworkingMessage_t (SDK 1.53a, x64): only the leading fields are read.
        public const int MsgDataOffset = 0;                             // void* m_pData
        public const int MsgSizeOffset = 8;                             // int m_cbSize
        public const int MsgPeerOffset = 16;                            // SteamNetworkingIdentity m_identityPeer
        public const int IdentitySteamIdType = 16;                      // k_ESteamNetworkingIdentityType_SteamID

        private static bool s_resolverInstalled;

        /// <summary>Bind every [DllImport("steam_api64")] of this assembly to the module the game already loaded
        /// (Carpenter_Data/Plugins/x86_64), not to a copy found by CoreCLR's probing.</summary>
        public static void InstallResolver()
        {
            if (s_resolverInstalled)
                return;
            NativeLibrary.SetDllImportResolver(typeof(SteamNative).Assembly, Resolve);
            s_resolverInstalled = true;
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Lib && name != Lib + ".dll")
                return IntPtr.Zero;
            var module = GetModuleHandleW("steam_api64.dll");
            if (module == IntPtr.Zero)
                throw new DllNotFoundException("steam_api64.dll is not loaded yet");
            return module;
        }

        public static IntPtr LoadedModule => GetModuleHandleW("steam_api64.dll");

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetCommandLineW();

        // ---- interface accessors
        [DllImport(Lib)] public static extern int SteamAPI_GetHSteamPipe();
        [DllImport(Lib)] public static extern int SteamAPI_GetHSteamUser();
        [DllImport(Lib)] public static extern IntPtr SteamAPI_SteamUser_v021();
        [DllImport(Lib)] public static extern IntPtr SteamAPI_SteamUtils_v010();
        [DllImport(Lib)] public static extern IntPtr SteamAPI_SteamFriends_v017();
        [DllImport(Lib)] public static extern IntPtr SteamAPI_SteamMatchmaking_v009();
        [DllImport(Lib)] public static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        [DllImport(Lib)] public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

        // ---- user / utils / friends
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Lib)] public static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong steamIdLobby);
        [DllImport(Lib)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong steamIdFriend);
        [DllImport(Lib)] public static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);

        // ---- matchmaking
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int lobbyType, int maxMembers);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong steamIdLobby);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong steamIdLobby);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong steamIdLobby);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong steamIdLobby, int member);
        [DllImport(Lib)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong steamIdLobby);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong steamIdLobby, byte* key, byte* value);
        [DllImport(Lib)] public static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong steamIdLobby, byte* key);

        // ---- networking messages
        [DllImport(Lib)] public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, SteamNetworkingIdentity* remote, byte* data, uint size, int sendFlags, int remoteChannel);
        [DllImport(Lib)] public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int localChannel, IntPtr* outMessages, int maxMessages);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, SteamNetworkingIdentity* remote);
        [DllImport(Lib)] public static extern byte SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, SteamNetworkingIdentity* remote);
        [DllImport(Lib)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
        [DllImport(Lib)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
        [DllImport(Lib)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(SteamNetworkingIdentity* self, ulong steamId);

        // ---- manual dispatch (the game's Steamworks.NET CallbackDispatcher drives the pump; see SteamCallbackPump)
        [DllImport(Lib)] public static extern byte SteamAPI_ManualDispatch_GetAPICallResult(int steamPipe, ulong apiCall, byte* callback, int callbackSize, int callbackExpected, byte* failed);

        public static SteamNetworkingIdentity Identity(ulong steamId)
        {
            var identity = default(SteamNetworkingIdentity);
            SteamAPI_SteamNetworkingIdentity_SetSteamID64(&identity, steamId);
            return identity;
        }

        public static string Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    /// <summary>SteamNetworkingIdentity: ESteamNetworkingIdentityType, size, then a 128-byte union (steamID64 first).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    internal struct SteamNetworkingIdentity
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public int Size;
        [FieldOffset(8)] public ulong SteamId64;
    }
}
