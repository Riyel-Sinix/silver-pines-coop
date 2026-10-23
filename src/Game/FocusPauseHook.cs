using System;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;

namespace SilverPinesCoop.Game
{
    /// <summary>
    /// Team17.Suspend.SuspendService.OnFocusChanged (Team17.PostEngagement.dll) suspends the game when its window loses
    /// focus: it counts focus losses and raises the pause-menu event. During co-op that pauses one player's world
    /// whenever they alt-tab (and in the two-copy test, whichever window is not active), so focus losses are dropped
    /// while <see cref="ShouldKeepRunning"/> says so. Focus gains still pass: the service clamps its count at zero.
    /// </summary>
    internal static unsafe class FocusPauseHook
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FocusChangedFn(IntPtr self, byte hasFocus, IntPtr method);

        public static Func<bool> ShouldKeepRunning;

        private static FocusChangedFn s_hook;      // kept alive for the native detour
        private static FocusChangedFn s_original;
        private static INativeDetour s_detour;

        public static void Install()
        {
            if (s_detour != null)
                return;
            var klass = IL2CPP.GetIl2CppClass("Team17.PostEngagement.dll", "Team17.Suspend", "SuspendService");
            var method = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(klass, "OnFocusChanged", 1);
            if (method == IntPtr.Zero)
            {
                Plugin.L.LogWarning("SuspendService.OnFocusChanged not found: the game will still pause when its window loses focus");
                return;
            }
            var code = *(IntPtr*)method;   // MethodInfo.methodPointer
            s_hook = Hook;
            s_detour = INativeDetour.CreateAndApply(code, s_hook, out s_original);
            Plugin.L.LogInfo($"Focus-loss pause hook installed at 0x{code.ToInt64():X}");
        }

        private static void Hook(IntPtr self, byte hasFocus, IntPtr method)
        {
            try
            {
                if (hasFocus == 0 && ShouldKeepRunning != null && ShouldKeepRunning())
                    return;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Focus hook: {e}");
            }
            s_original(self, hasFocus, method);
        }
    }
}
