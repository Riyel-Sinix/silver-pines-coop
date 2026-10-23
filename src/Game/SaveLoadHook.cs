using System;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;

namespace SilverPinesCoop.Game
{
    /// <summary>Routes ordinary game save/load requests through the co-op coordinator.</summary>
    internal static unsafe class SaveLoadHook
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SlotFn(IntPtr self, int slot, IntPtr method);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RecentFn(IntPtr self, IntPtr method);

        // Return true only when the coordinator has accepted or deliberately blocked the request.
        public static Func<int, bool> InterceptSave;
        public static Func<int, bool> InterceptLoad;
        public static Func<bool> InterceptRecent;

        [ThreadStatic]
        private static int s_bypassDepth;

        private static SlotFn s_saveHook;
        private static SlotFn s_loadHook;
        private static RecentFn s_recentHook;
        private static SlotFn s_originalSave;
        private static SlotFn s_originalLoad;
        private static RecentFn s_originalRecent;
        private static INativeDetour s_saveDetour;
        private static INativeDetour s_loadDetour;
        private static INativeDetour s_recentDetour;

        public static bool Installed { get; private set; }

        public static void Install()
        {
            if (Installed)
                return;

            var klass = IL2CPP.GetIl2CppClass("CarpenterScripts.dll", "", "SaveDataManager");
            var save = ResolveCode(klass, "SaveGameInSlot", 1);
            var load = ResolveCode(klass, "LoadGameFromSlot", 1);
            var recent = ResolveCode(klass, "LoadMostRecentSave", 0);
            if (save == IntPtr.Zero || load == IntPtr.Zero || recent == IntPtr.Zero)
            {
                Plugin.L.LogWarning("SaveDataManager save/load methods not found: coordinated saves are unavailable");
                return;
            }

            // Resolve every method before applying any detour. Keep both native callbacks and originals alive.
            s_saveHook = SaveHook;
            s_loadHook = LoadHook;
            s_recentHook = RecentHook;
            try
            {
                s_saveDetour = INativeDetour.CreateAndApply(save, s_saveHook, out s_originalSave);
                s_loadDetour = INativeDetour.CreateAndApply(load, s_loadHook, out s_originalLoad);
                s_recentDetour = INativeDetour.CreateAndApply(recent, s_recentHook, out s_originalRecent);
                Installed = true;
                Plugin.L.LogInfo("Save/load coordination hooks installed");
            }
            catch (Exception e)
            {
                Dispose();
                Plugin.L.LogError($"Could not install save/load coordination hooks: {e}");
            }
        }

        private static IntPtr ResolveCode(IntPtr klass, string name, int arguments)
        {
            if (klass == IntPtr.Zero)
                return IntPtr.Zero;
            var method = IL2CPP.il2cpp_class_get_method_from_name(klass, name, arguments);
            return method == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)method;
        }

        /// <summary>Allows one coordinated operation to call the game API without intercepting itself.</summary>
        public static void ExecuteLocal(Action operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            s_bypassDepth++;
            try
            {
                operation();
            }
            finally
            {
                s_bypassDepth--;
            }
        }

        private static void SaveHook(IntPtr self, int slot, IntPtr method)
        {
            try
            {
                if (s_bypassDepth == 0 && InterceptSave != null && InterceptSave(slot))
                    return;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Save interception: {e}");
            }
            s_originalSave(self, slot, method);
        }

        private static void LoadHook(IntPtr self, int slot, IntPtr method)
        {
            try
            {
                if (s_bypassDepth == 0 && InterceptLoad != null && InterceptLoad(slot))
                    return;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Load interception: {e}");
            }
            s_originalLoad(self, slot, method);
        }

        private static void RecentHook(IntPtr self, IntPtr method)
        {
            try
            {
                if (s_bypassDepth == 0 && InterceptRecent != null && InterceptRecent())
                    return;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Recent-save interception: {e}");
            }
            s_originalRecent(self, method);
        }

        public static void Dispose()
        {
            Installed = false;
            DisposeDetour(ref s_recentDetour);
            DisposeDetour(ref s_loadDetour);
            DisposeDetour(ref s_saveDetour);
        }

        private static void DisposeDetour(ref INativeDetour detour)
        {
            if (detour == null)
                return;
            try
            {
                detour.Dispose();
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Could not dispose save/load hook: {e}");
            }
            finally
            {
                detour = null;
            }
        }
    }
}
