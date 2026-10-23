using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SilverPinesCoop.LocalTest
{
    /// <summary>
    /// Puts a two-copy test window on its half of the screen (copy 1 left, copy 2 right), windowed, sized to half the
    /// monitor width with the monitor's aspect ratio, and titles it with the copy number. The game re-applies its own
    /// display settings (DisplaySettings.ApplySettings -> Screen.SetResolution), so the size is checked every few
    /// seconds. Screen.MoveMainWindowTo is stripped from this build: the window is placed with user32 instead.
    /// </summary>
    internal sealed class TestWindow
    {
        private const float CheckInterval = 2f;
        private const float PlaceDelay = 0.5f;   // Screen.SetResolution resizes on a later frame

        private readonly int _copy;
        private readonly int _widthSetting;
        private float _nextCheck;
        private float _placeAt = -1f;
        private int _x;
        private bool _placed;
        private IntPtr _window;

        public TestWindow(int copy, int widthSetting)
        {
            _copy = copy;
            _widthSetting = widthSetting;
        }

        public void Update(float now)
        {
            if (_placeAt >= 0f && now >= _placeAt)
            {
                _placeAt = -1f;
                Place();
            }
            if (now < _nextCheck)
                return;
            _nextCheck = now + CheckInterval;
            var desktop = Screen.currentResolution;   // the monitor's mode while the game is windowed
            if (desktop.width <= 0 || desktop.height <= 0)
                return;
            var width = _widthSetting > 0 ? _widthSetting : desktop.width / 2 - 32;
            var height = width * desktop.height / desktop.width;
            if (width < 320 || height < 180)
                return;
            _x = _copy == 1 ? 0 : desktop.width / 2;
            if (Screen.fullScreenMode == FullScreenMode.Windowed && Screen.width == width && Screen.height == height)
            {
                // already the right size (Unity keeps the size the other copy set): still put it on its own half
                if (!_placed)
                    _placeAt = now;
                return;
            }
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
            _placeAt = now + PlaceDelay;
            Plugin.L.LogInfo($"Two-copy test: window {_copy} set to {width}x{height} at x={_x}");
        }

        private void Place()
        {
            if (_window == IntPtr.Zero || !IsWindow(_window))
                _window = FindOwnWindow();
            if (_window == IntPtr.Zero)
                return;
            _placed = SetWindowPos(_window, IntPtr.Zero, _x, 0, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            SetWindowTextW(_window, $"Silver Pines — 双开测试 窗口 {_copy}");
        }

        /// <summary>This process's visible Unity window (the BepInEx console belongs to the same process too).</summary>
        private static IntPtr FindOwnWindow()
        {
            var pid = (uint)Environment.ProcessId;
            var found = IntPtr.Zero;
            var className = new StringBuilder(64);
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != pid || !IsWindowVisible(window))
                    return true;
                className.Clear();
                GetClassNameW(window, className, className.Capacity);
                if (className.ToString() != "UnityWndClass")
                    return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowTextW(IntPtr window, string text);

        [DllImport("user32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height,
                                                uint flags);
    }
}
