using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace ElintriaEngine.Core
{
    // ── Named editor actions that can be rebound ──────────────────────────────
    public enum EditorAction
    {
        // Transform tools
        MoveTool, RotateTool, ScaleTool,
        // Scene navigation
        FlyForward, FlyBackward, FlyLeft, FlyRight, FlyUp, FlyDown,
        // View shortcuts
        FrameSelected, ViewFront, ViewRight, ViewTop,
        // Scene management
        Save, Undo, Redo,
        // Playback
        Play, Stop, Pause,
        // Edit
        Duplicate, Delete, SelectAll,
    }

    // ── A single key binding ──────────────────────────────────────────────────
    public class Keybind
    {
        public Keys Key { get; set; } = Keys.Unknown;
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }

        [JsonIgnore]
        public string DisplayString
        {
            get
            {
                var s = "";
                if (Ctrl) s += "Ctrl+";
                if (Shift) s += "Shift+";
                if (Alt) s += "Alt+";
                s += Key switch
                {
                    Keys.Unknown => "(unbound)",
                    Keys.Space => "Space",
                    Keys.Backspace => "Backspace",
                    Keys.Delete => "Delete",
                    Keys.Escape => "Escape",
                    Keys.Enter => "Enter",
                    Keys.Tab => "Tab",
                    Keys.Up => "↑",
                    Keys.Down => "↓",
                    Keys.Left => "←",
                    Keys.Right => "→",
                    Keys.D0 => "Num0",
                    Keys.D1 => "Num1",
                    Keys.D2 => "Num2",
                    Keys.D3 => "Num3",
                    Keys.D4 => "Num4",
                    Keys.D5 => "Num5",
                    Keys.D6 => "Num6",
                    Keys.D7 => "Num7",
                    Keys.D8 => "Num8",
                    Keys.D9 => "Num9",
                    _ => Key.ToString()
                };
                return s;
            }
        }

        public bool Matches(Keys key, bool ctrl, bool shift, bool alt) =>
            Key == key && Ctrl == ctrl && Shift == shift && Alt == alt;
    }

    // ── Editor preferences (user-global, stored in AppData) ──────────────────
    public class EditorPreferences
    {
        // ── General ───────────────────────────────────────────────────────────
        public float UiScale { get; set; } = 1.0f;
        public bool AutoSave { get; set; } = true;
        public int AutoSaveInterval { get; set; } = 300; // seconds
        public bool ShowFrameRate { get; set; } = true;
        public bool ShowStats { get; set; } = false;
        public string Theme { get; set; } = "Dark";

        // ── Scene view ────────────────────────────────────────────────────────
        public float MouseSensitivity { get; set; } = 0.35f;
        public float ScrollSpeed { get; set; } = 0.12f;
        public float FlyCamSpeed { get; set; } = 5.0f;
        public bool InvertYAxis { get; set; } = false;
        public bool ShowGrid { get; set; } = true;
        public bool ShowGizmos { get; set; } = true;
        public float GizmoSize { get; set; } = 1.0f;

        // ── Keybinds ──────────────────────────────────────────────────────────
        public Dictionary<EditorAction, Keybind> Keybinds { get; set; } = DefaultKeybinds();

        private static Dictionary<EditorAction, Keybind> DefaultKeybinds() => new()
        {
            [EditorAction.MoveTool] = new() { Key = Keys.W },
            [EditorAction.RotateTool] = new() { Key = Keys.E },
            [EditorAction.ScaleTool] = new() { Key = Keys.R },
            [EditorAction.FlyForward] = new() { Key = Keys.W },
            [EditorAction.FlyBackward] = new() { Key = Keys.S },
            [EditorAction.FlyLeft] = new() { Key = Keys.A },
            [EditorAction.FlyRight] = new() { Key = Keys.D },
            [EditorAction.FlyUp] = new() { Key = Keys.E },
            [EditorAction.FlyDown] = new() { Key = Keys.Q },
            [EditorAction.FrameSelected] = new() { Key = Keys.F },
            [EditorAction.ViewFront] = new() { Key = Keys.Up },
            [EditorAction.ViewRight] = new() { Key = Keys.Right },
            [EditorAction.ViewTop] = new() { Key = Keys.Left },
            [EditorAction.Save] = new() { Key = Keys.S, Ctrl = true },
            [EditorAction.Undo] = new() { Key = Keys.Z, Ctrl = true },
            [EditorAction.Redo] = new() { Key = Keys.Y, Ctrl = true },
            [EditorAction.Play] = new() { Key = Keys.P, Ctrl = true },
            [EditorAction.Stop] = new() { Key = Keys.P, Ctrl = true, Shift = true },
            [EditorAction.Pause] = new() { Key = Keys.Space, Ctrl = true },
            [EditorAction.Duplicate] = new() { Key = Keys.D, Ctrl = true },
            [EditorAction.Delete] = new() { Key = Keys.Delete },
            [EditorAction.SelectAll] = new() { Key = Keys.A, Ctrl = true },
        };

        // ── Persistence ───────────────────────────────────────────────────────
        private static string Path =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ElintriaEngine", "preferences.json");

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static EditorPreferences? _instance;
        public static EditorPreferences Instance => _instance ??= Load();

        public static EditorPreferences Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var p = JsonSerializer.Deserialize<EditorPreferences>(
                                File.ReadAllText(Path), _opts);
                    if (p != null) { _instance = p; EnsureAllActions(p); return p; }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Prefs] Load: {ex.Message}"); }

            var defaults = new EditorPreferences();
            _instance = defaults;
            defaults.Save();
            return defaults;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonSerializer.Serialize(this, _opts));
            }
            catch (Exception ex) { Console.WriteLine($"[Prefs] Save: {ex.Message}"); }
        }

        // Ensure newly added actions get defaults
        private static void EnsureAllActions(EditorPreferences p)
        {
            var defaults = DefaultKeybinds();
            foreach (var (k, v) in defaults)
                if (!p.Keybinds.ContainsKey(k))
                    p.Keybinds[k] = v;
        }

        public Keybind GetKeybind(EditorAction action)
        {
            if (Keybinds.TryGetValue(action, out var kb)) return kb;
            return new Keybind { Key = Keys.Unknown };
        }

        // Nice display names for each action
        public static string ActionDisplayName(EditorAction a) => a switch
        {
            EditorAction.MoveTool => "Move Tool",
            EditorAction.RotateTool => "Rotate Tool",
            EditorAction.ScaleTool => "Scale Tool",
            EditorAction.FlyForward => "Fly Forward",
            EditorAction.FlyBackward => "Fly Backward",
            EditorAction.FlyLeft => "Fly Left",
            EditorAction.FlyRight => "Fly Right",
            EditorAction.FlyUp => "Fly Up",
            EditorAction.FlyDown => "Fly Down",
            EditorAction.FrameSelected => "Frame Selected",
            EditorAction.ViewFront => "View Front",
            EditorAction.ViewRight => "View Right",
            EditorAction.ViewTop => "View Top",
            EditorAction.Save => "Save Scene",
            EditorAction.Undo => "Undo",
            EditorAction.Redo => "Redo",
            EditorAction.Play => "Play",
            EditorAction.Stop => "Stop",
            EditorAction.Pause => "Pause",
            EditorAction.Duplicate => "Duplicate",
            EditorAction.Delete => "Delete",
            EditorAction.SelectAll => "Select All",
            _ => a.ToString(),
        };

        public static string ActionGroup(EditorAction a) => a switch
        {
            EditorAction.MoveTool or EditorAction.RotateTool or EditorAction.ScaleTool
                => "Transform Tools",
            EditorAction.FlyForward or EditorAction.FlyBackward or
            EditorAction.FlyLeft or EditorAction.FlyRight or
            EditorAction.FlyUp or EditorAction.FlyDown
                => "Scene Navigation",
            EditorAction.FrameSelected or EditorAction.ViewFront or
            EditorAction.ViewRight or EditorAction.ViewTop
                => "View",
            EditorAction.Save or EditorAction.Undo or EditorAction.Redo
                => "File",
            EditorAction.Play or EditorAction.Stop or EditorAction.Pause
                => "Playback",
            EditorAction.Duplicate or EditorAction.Delete or EditorAction.SelectAll
                => "Edit",
            _ => "Other",
        };
    }
}