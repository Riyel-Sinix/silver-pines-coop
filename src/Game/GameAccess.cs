using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SilverPinesCoop.Game
{
    /// <summary>The game's global state the co-op layer reads: menus, cutscenes, time scale, the save data store.</summary>
    internal static class GameAccess
    {
        // GameMenuState.GameMenu flags (CarpenterScripts)
        public const int MenuSystem = 64;
        public const int MenuVideoCutscene = 256;
        public const int MenuOptions = 8192;

        private static TimeScaleManager s_timeScale;
        private static float s_nextTimeScaleSearch;

        /// <summary>GlobalReferences, or null while it is not loaded. GlobalReferences.Instance would load the asset
        /// synchronously when called too early, so it is only touched once Loaded says it is there.</summary>
        public static GlobalReferences Globals
        {
            get
            {
                try
                {
                    return GlobalReferences.Loaded ? GlobalReferences.Instance : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>GameMenuState.ActiveMenuState as flags; 0 when unknown.</summary>
        public static int MenuFlags()
        {
            try
            {
                var state = Globals?.GameMenuState;
                return state == null ? 0 : (int)state.ActiveMenuState;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>A timeline cutscene (GameCutsceneManager, set by the EnableCutsceneMode track) or a video cutscene.</summary>
        public static bool InCutscene()
        {
            try
            {
                return GameCutsceneManager.CutsceneActive || (MenuFlags() & MenuVideoCutscene) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static PersistentDataStore DataStore
        {
            get
            {
                try
                {
                    return Globals?.DataStore;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static TimeScaleManager TimeScale
        {
            get
            {
                if (s_timeScale != null)
                    return s_timeScale;
                var now = Time.realtimeSinceStartup;
                if (now < s_nextTimeScaleSearch)
                    return null;
                s_nextTimeScaleSearch = now + 2f;
                s_timeScale = Object.FindAnyObjectByType(Il2CppType.Of<TimeScaleManager>())?.TryCast<TimeScaleManager>();
                return s_timeScale;
            }
        }

        /// <summary>Lets the game's TimeScaleManager put back the time scale it wants (after a co-op freeze).</summary>
        public static void RestoreTimeScale()
        {
            var manager = TimeScale;
            if (manager != null)
                manager.UpdateTimeScale();
            else
                Time.timeScale = 1f;
        }
    }
}
