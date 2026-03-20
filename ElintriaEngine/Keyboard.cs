using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ElintriaEngine.Core
{
    // =========================================================================
    //  Key  —  platform-independent key codes
    //  Values match Windows Virtual Key codes so they can be fed directly from
    //  WM_KEYDOWN / WM_KEYUP or mapped from GLFW keys.
    // =========================================================================
    public enum Key
    {
        None = 0,

        // ── Letters ──────────────────────────────────────────────────────────
        A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45,
        F = 0x46, G = 0x47, H = 0x48, I = 0x49, J = 0x4A,
        K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E, O = 0x4F,
        P = 0x50, Q = 0x51, R = 0x52, S = 0x53, T = 0x54,
        U = 0x55, V = 0x56, W = 0x57, X = 0x58, Y = 0x59,
        Z = 0x5A,

        // ── Numbers (top row) ─────────────────────────────────────────────────
        Alpha0 = 0x30, Alpha1 = 0x31, Alpha2 = 0x32, Alpha3 = 0x33,
        Alpha4 = 0x34, Alpha5 = 0x35, Alpha6 = 0x36, Alpha7 = 0x37,
        Alpha8 = 0x38, Alpha9 = 0x39,

        // ── Numpad ────────────────────────────────────────────────────────────
        Numpad0 = 0x60, Numpad1 = 0x61, Numpad2 = 0x62, Numpad3 = 0x63,
        Numpad4 = 0x64, Numpad5 = 0x65, Numpad6 = 0x66, Numpad7 = 0x67,
        Numpad8 = 0x68, Numpad9 = 0x69,
        NumpadMultiply = 0x6A, NumpadAdd = 0x6B, NumpadSubtract = 0x6D,
        NumpadDecimal = 0x6E, NumpadDivide = 0x6F,

        // ── Function keys ─────────────────────────────────────────────────────
        F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73,
        F5 = 0x74, F6 = 0x75, F7 = 0x76, F8 = 0x77,
        F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,

        // ── Arrows ────────────────────────────────────────────────────────────
        Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28,

        // ── Control ───────────────────────────────────────────────────────────
        Backspace = 0x08, Tab = 0x09, Enter = 0x0D,
        Escape = 0x1B, Space = 0x20, Delete = 0x2E,
        Insert = 0x2D, Home = 0x24, End = 0x23,
        PageUp = 0x21, PageDown = 0x22,
        PrintScreen = 0x2C, Pause = 0x13, CapsLock = 0x14,
        NumLock = 0x90, ScrollLock = 0x91,

        // ── Modifiers ─────────────────────────────────────────────────────────
        LeftShift = 0xA0, RightShift = 0xA1,
        LeftCtrl = 0xA2, RightCtrl = 0xA3,
        LeftAlt = 0xA4, RightAlt = 0xA5,
        LeftSuper = 0x5B, RightSuper = 0x5C,

        // Shift / Ctrl / Alt without left/right distinction
        Shift = 0x10, Ctrl = 0x11, Alt = 0x12,
    }

    // =========================================================================
    //  Keyboard  —  frame-accurate input state
    //
    //  Three usage modes:
    //
    //  1. Win32 window callback (inside the engine, EWindow.WindowProc):
    //       Keyboard.OnKeyDown(wParam);   // in WM_KEYDOWN
    //       Keyboard.OnKeyUp(wParam);     // in WM_KEYUP
    //       Keyboard.EndFrame();          // at end of each rendered frame
    //
    //  2. GLFW callback (engine or standalone with OpenTK/GLFW):
    //       Keyboard.OnKeyDown(GlfwKeyToKey(key));
    //       Keyboard.OnKeyUp(GlfwKeyToKey(key));
    //       Keyboard.EndFrame();
    //
    //  3. Polling mode (standalone app, no callbacks needed):
    //       Call Keyboard.PollWin32() once per frame — it reads the Windows
    //       GetAsyncKeyState API directly without needing any window message.
    //       Keyboard.PollWin32();    // replaces OnKeyDown/Up + EndFrame
    //
    //  Query methods (same regardless of mode):
    //       Keyboard.IsDown(Key.W)        — held this frame
    //       Keyboard.IsPressed(Key.Space) — went down THIS frame only
    //       Keyboard.IsReleased(Key.E)    — went up THIS frame only
    //       Keyboard.AnyKeyDown           — true if any key is held
    //       Keyboard.AnyKeyPressed        — true if any key was just pressed
    // =========================================================================
    public static class Keyboard
    {
        // ── Internal state (256 virtual-key slots) ────────────────────────────
        private static readonly bool[] _current = new bool[256];
        private static readonly bool[] _previous = new bool[256];
        // Keys that received OnKeyDown this frame before EndFrame was called
        private static readonly bool[] _pressed = new bool[256];
        // Keys that received OnKeyUp this frame before EndFrame was called
        private static readonly bool[] _released = new bool[256];

        // ── Win32 polling support ─────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // Whether PollWin32() has ever been called (chooses state update path)
        private static bool _pollingMode = false;

        // ── Reset / lifecycle ─────────────────────────────────────────────────

        /// <summary>
        /// Clear all key state. Call when your window loses focus.
        /// </summary>
        public static void ClearAll()
        {
            Array.Clear(_current, 0, 256);
            Array.Clear(_previous, 0, 256);
            Array.Clear(_pressed, 0, 256);
            Array.Clear(_released, 0, 256);
        }

        // ── Callback-driven mode (Win32 WM_KEYDOWN / GLFW / custom) ──────────

        /// <summary>
        /// Call from WM_KEYDOWN or equivalent. wParam is the Windows virtual-key code.
        /// </summary>
        public static void OnKeyDown(int vk)
        {
            if ((uint)vk >= 256) return;
            if (!_current[vk])           // only fire pressed on first down, not repeat
                _pressed[vk] = true;
            _current[vk] = true;
        }

        /// <summary>
        /// Convenience overload accepting the Key enum directly.
        /// </summary>
        public static void OnKeyDown(Key key) => OnKeyDown((int)key);

        /// <summary>
        /// Call from WM_KEYUP or equivalent.
        /// </summary>
        public static void OnKeyUp(int vk)
        {
            if ((uint)vk >= 256) return;
            _current[vk] = false;
            _released[vk] = true;
        }

        /// <summary>
        /// Convenience overload accepting the Key enum directly.
        /// </summary>
        public static void OnKeyUp(Key key) => OnKeyUp((int)key);

        /// <summary>
        /// Call ONCE at the end of every frame (after rendering, before polling events).
        /// Advances the pressed/released state so they only report for one frame.
        /// </summary>
        public static void EndFrame()
        {
            Array.Copy(_current, _previous, 256);
            Array.Clear(_pressed, 0, 256);
            Array.Clear(_released, 0, 256);
        }

        // ── Polling mode (no callbacks required) ──────────────────────────────

        /// <summary>
        /// Polls all 256 virtual keys via GetAsyncKeyState each frame.
        /// Use this in a standalone game loop instead of OnKeyDown/OnKeyUp + EndFrame.
        /// Call exactly once per frame.
        /// </summary>
        public static void PollWin32()
        {
            _pollingMode = true;
            Array.Copy(_current, _previous, 256);
            Array.Clear(_pressed, 0, 256);
            Array.Clear(_released, 0, 256);

            for (int vk = 0; vk < 256; vk++)
            {
                bool nowDown = (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (nowDown && !_previous[vk]) _pressed[vk] = true;
                if (!nowDown && _previous[vk]) _released[vk] = true;
                _current[vk] = nowDown;
            }
        }

        // ── Query API ─────────────────────────────────────────────────────────

        /// <summary>True every frame the key is held down.</summary>
        public static bool IsDown(Key key)
        {
            int vk = (int)key;
            return (uint)vk < 256 && _current[vk];
        }

        /// <summary>True on the FIRST frame the key is pressed. False every subsequent frame until released and pressed again.</summary>
        public static bool IsPressed(Key key)
        {
            int vk = (int)key;
            return (uint)vk < 256 && _pressed[vk];
        }

        /// <summary>True on the ONE frame the key is released.</summary>
        public static bool IsReleased(Key key)
        {
            int vk = (int)key;
            return (uint)vk < 256 && _released[vk];
        }

        /// <summary>True while EITHER Shift key is held.</summary>
        public static bool ShiftDown =>
            IsDown(Key.LeftShift) || IsDown(Key.RightShift) || IsDown(Key.Shift);

        /// <summary>True while EITHER Ctrl key is held.</summary>
        public static bool CtrlDown =>
            IsDown(Key.LeftCtrl) || IsDown(Key.RightCtrl) || IsDown(Key.Ctrl);

        /// <summary>True while EITHER Alt key is held.</summary>
        public static bool AltDown =>
            IsDown(Key.LeftAlt) || IsDown(Key.RightAlt) || IsDown(Key.Alt);

        /// <summary>True if any key is currently held.</summary>
        public static bool AnyKeyDown
        {
            get
            {
                for (int i = 0; i < 256; i++)
                    if (_current[i]) return true;
                return false;
            }
        }

        /// <summary>True if any key was just pressed this frame.</summary>
        public static bool AnyKeyPressed
        {
            get
            {
                for (int i = 0; i < 256; i++)
                    if (_pressed[i]) return true;
                return false;
            }
        }

        /// <summary>
        /// Returns the first key that was pressed this frame, or Key.None.
        /// Useful for remapping or text input.
        /// </summary>
        public static Key FirstPressedKey
        {
            get
            {
                for (int i = 1; i < 256; i++)
                    if (_pressed[i]) return (Key)i;
                return Key.None;
            }
        }

        // ── GLFW key mapping ──────────────────────────────────────────────────
        // Map OpenTK / GLFW key codes to Windows VK codes so GLFW users
        // can feed OnKeyDown/OnKeyUp without knowing VK values.
        // Usage (in GLFW KeyCallback):
        //   Keyboard.OnKeyDown(Keyboard.GlfwToKey((int)e.Key));

        /// <summary>
        /// Converts an OpenTK GLFW key integer to the matching Key enum value.
        /// Pass e.Key cast to int from a KeyDown/KeyUp callback.
        /// </summary>
        public static Key GlfwToKey(int glfwKey)
        {
            // OpenTK GLFW key values (Keys enum) — map to our Key enum (Win32 VK)
            return glfwKey switch
            {
                // Letters — GLFW 65-90 = A-Z, same as Win32
                >= 65 and <= 90 => (Key)glfwKey,

                // Numbers — GLFW 48-57, same as Win32
                >= 48 and <= 57 => (Key)glfwKey,

                // Function keys — GLFW 290-301 = F1-F12
                290 => Key.F1,
                291 => Key.F2,
                292 => Key.F3,
                293 => Key.F4,
                294 => Key.F5,
                295 => Key.F6,
                296 => Key.F7,
                297 => Key.F8,
                298 => Key.F9,
                299 => Key.F10,
                300 => Key.F11,
                301 => Key.F12,

                // Arrows
                263 => Key.Left,
                264 => Key.Down,
                265 => Key.Up,
                262 => Key.Right,

                // Control
                256 => Key.Escape,
                257 => Key.Enter,
                258 => Key.Tab,
                259 => Key.Backspace,
                260 => Key.Insert,
                261 => Key.Delete,
                266 => Key.PageUp,
                267 => Key.PageDown,
                268 => Key.Home,
                269 => Key.End,
                32 => Key.Space,

                // Modifiers
                340 => Key.LeftShift,
                344 => Key.RightShift,
                341 => Key.LeftCtrl,
                345 => Key.RightCtrl,
                342 => Key.LeftAlt,
                346 => Key.RightAlt,
                343 => Key.LeftSuper,
                347 => Key.RightSuper,

                // Numpad
                320 => Key.Numpad0,
                321 => Key.Numpad1,
                322 => Key.Numpad2,
                323 => Key.Numpad3,
                324 => Key.Numpad4,
                325 => Key.Numpad5,
                326 => Key.Numpad6,
                327 => Key.Numpad7,
                328 => Key.Numpad8,
                329 => Key.Numpad9,
                331 => Key.NumpadDivide,
                332 => Key.NumpadMultiply,
                333 => Key.NumpadSubtract,
                334 => Key.NumpadAdd,
                335 => Key.Enter,
                330 => Key.NumpadDecimal,

                _ => Key.None
            };
        }
    }
}