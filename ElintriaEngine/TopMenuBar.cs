using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace ElintriaEngine.UI.Panels
{
    public class MenuDropdownItem
    {
        public string Label { get; }
        public string Shortcut { get; init; } = "";
        public Action? Action { get; }
        public bool IsSep { get; init; } = false;
        public bool IsHeader { get; init; } = false;

        public MenuDropdownItem(string label, Action? action) { Label = label; Action = action; }

        public static readonly MenuDropdownItem Sep =
            new("------------------", null) { IsSep = true };
    }

    public class TopMenuBar
    {
        public float Height { get; } = 24f;
        private float _width;
        private PointF _mouse = new(-999, -999);   // starts way off-screen

        private readonly List<(string label, RectangleF bounds, List<MenuDropdownItem> items)> _menus = new();
        private int _openIdx = -1;
        private DropdownPanel? _dropdown;

        private RectangleF _playBtn, _pauseBtn, _stopBtn;
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }

        /// <summary>Set by EditorLayout to show the spinner while scripts compile.</summary>
        public bool IsCompiling { get; set; }
        /// <summary>Set by EditorLayout to show a warning dot when compile failed.</summary>
        public bool IsScriptsDirty { get; set; }

        public  Action? NewScene, OpenScene, SaveScene, SaveSceneAs, Exit;
        public  Action? Undo, Redo, OpenPreferences, OpenProjectSettings;
        public  Action? Play, Pause, Stop;
        public  Action? BuildOnly, BuildAndRun, OpenBuildSettings;
        public  Action<string>? ToggleWindow;

        private static readonly Color CBar = Color.FromArgb(255, 24, 24, 24);
        private static readonly Color CItemN = Color.FromArgb(255, 24, 24, 24);
        private static readonly Color CItemH = Color.FromArgb(255, 52, 90, 168);
        private static readonly Color CItemO = Color.FromArgb(255, 44, 78, 148);
        private static readonly Color CText = Color.FromArgb(255, 212, 212, 212);

        public TopMenuBar(float w) { _width = w; Build(); }
        public void Resize(float w) => _width = w;

        private void Build()
        {
            _menus.Add(("File", default, new List<MenuDropdownItem>
            {
                new("New Scene",       () => NewScene?.Invoke())    { Shortcut="Ctrl+N" },
                new("Open Scene...",   () => OpenScene?.Invoke())   { Shortcut="Ctrl+O" },
                MenuDropdownItem.Sep,
                new("Save Scene",      () => SaveScene?.Invoke())   { Shortcut="Ctrl+S" },
                new("Save Scene As...",() => SaveSceneAs?.Invoke()) { Shortcut="Ctrl+Shift+S" },
                MenuDropdownItem.Sep,
                new("Exit",            () => Exit?.Invoke())        { Shortcut="Alt+F4" },
            }));
            _menus.Add(("Edit", default, new List<MenuDropdownItem>
            {
                new("Undo",                () => Undo?.Invoke())               { Shortcut="Ctrl+Z" },
                new("Redo",                () => Redo?.Invoke())               { Shortcut="Ctrl+Y" },
                MenuDropdownItem.Sep,
                new("Preferences...",      () => OpenPreferences?.Invoke()),
                new("Project Settings...", () => OpenProjectSettings?.Invoke()),
            }));
            _menus.Add(("GameObject", default, new List<MenuDropdownItem>
            {
                new("Create Empty",       null) { Shortcut="Ctrl+Shift+N" },
                MenuDropdownItem.Sep,
                new("[ 3D Objects ]",     null) { IsHeader=true },
                new("  Cube",             null),
                new("  Sphere",           null),
                new("  Plane",            null),
                new("  Capsule",          null),
                new("  Cylinder",         null),
                MenuDropdownItem.Sep,
                new("[ Lights ]",         null) { IsHeader=true },
                new("  Directional Light",null),
                new("  Point Light",      null),
                new("  Spot Light",       null),
                MenuDropdownItem.Sep,
                new("  Camera",           null),
                MenuDropdownItem.Sep,
                new("[ UI ]",             null) { IsHeader=true },
                new("  Canvas",           null),
                new("  Button",           null),
                new("  Text",             null),
            }));
            _menus.Add(("Component", default, new List<MenuDropdownItem>
            {
                new("[ Physics ]",    null) { IsHeader=true },
                new("  Rigidbody",    null),
                new("  Box Collider", null),
                MenuDropdownItem.Sep,
                new("[ Rendering ]",  null) { IsHeader=true },
                new("  Mesh Renderer",null),
                MenuDropdownItem.Sep,
                new("[ Audio ]",      null) { IsHeader=true },
                new("  Audio Source", null),
            }));
            _menus.Add(("Build", default, new List<MenuDropdownItem>
            {
                new("Build Settings...",() => OpenBuildSettings?.Invoke()),
                MenuDropdownItem.Sep,
                new("Build",            () => BuildOnly?.Invoke())   { Shortcut="Ctrl+B" },
                new("Build & Run",      () => BuildAndRun?.Invoke()) { Shortcut="Ctrl+Shift+B" },
            }));
            _menus.Add(("Window", default, new List<MenuDropdownItem>
            {
                new("Hierarchy",  () => ToggleWindow?.Invoke("Hierarchy")),
                new("Inspector",  () => ToggleWindow?.Invoke("Inspector")),
                new("Project",    () => ToggleWindow?.Invoke("Project")),
                new("Scene View", () => ToggleWindow?.Invoke("SceneView")),
                new("Console",    () => ToggleWindow?.Invoke("Console")),
            }));
            _menus.Add(("Help", default, new List<MenuDropdownItem>
            {
                new("Documentation",         null),
                new("About Elintria Engine", null),
            }));
        }

        public void OnRender(IEditorRenderer r)
        {
            r.FillRect(new RectangleF(0, 0, _width, Height), CBar);
            r.DrawLine(new PointF(0, Height - 1), new PointF(_width, Height - 1),
                Color.FromArgb(255, 46, 46, 46));

            // KEY FIX: only apply hover when mouse is physically inside the bar
            bool mouseInBar = _mouse.Y >= 0 && _mouse.Y < Height;

            float x = 0;
            for (int i = 0; i < _menus.Count; i++)
            {
                var (label, _, items) = _menus[i];
                float mw = label.Length * 7.4f + 20f;
                var mb = new RectangleF(x, 0, mw, Height);
                _menus[i] = (label, mb, items);

                bool open = _openIdx == i;
                bool hovered = mouseInBar && mb.Contains(_mouse) && !open;

                r.FillRect(mb, open ? CItemO : hovered ? CItemH : CItemN);
                r.DrawText(label, new PointF(x + 9f, 5f), CText, 11f);
                x += mw;
            }

            // Play controls
            float cx = _width / 2f;
            _playBtn = new RectangleF(cx - 36f, 3f, 20f, 18f);
            _pauseBtn = new RectangleF(cx - 12f, 3f, 20f, 18f);
            _stopBtn = new RectangleF(cx + 12f, 3f, 20f, 18f);

            r.FillRect(_playBtn, (IsPlaying && !IsPaused) ? Color.FromArgb(255, 50, 145, 50) : Color.FromArgb(255, 44, 44, 44));
            r.FillRect(_pauseBtn, IsPaused ? Color.FromArgb(255, 170, 130, 28) : Color.FromArgb(255, 44, 44, 44));
            r.FillRect(_stopBtn, Color.FromArgb(255, 44, 44, 44));

            var btnBorder = Color.FromArgb(255, 68, 68, 68);
            r.DrawRect(_playBtn, btnBorder); r.DrawRect(_pauseBtn, btnBorder); r.DrawRect(_stopBtn, btnBorder);

            r.DrawText(">", new PointF(_playBtn.X + 6f, _playBtn.Y + 4f), Color.White, 10f);
            r.DrawText("||", new PointF(_pauseBtn.X + 4f, _pauseBtn.Y + 4f), Color.White, 10f);
            r.DrawText("[]", new PointF(_stopBtn.X + 3f, _stopBtn.Y + 4f), Color.White, 10f);

            // ── Script compile status indicator (right side of bar) ───────────
            if (IsCompiling)
            {
                // Rotating dots spinner  ⣾⣽⣻⢿⡿⣟⣯⣷
                string[] frames = { "⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷" };
                int frame = (int)(DateTime.UtcNow.Millisecond / 125.0) % frames.Length;
                r.DrawText(frames[frame],
                    new PointF(_width - 120f, 5f),
                    Color.FromArgb(255, 100, 180, 255), 11f);
                r.DrawText("Compiling...",
                    new PointF(_width - 108f, 5f),
                    Color.FromArgb(255, 140, 200, 255), 10f);
            }
            else if (IsScriptsDirty)
            {
                r.DrawText("● Script error",
                    new PointF(_width - 108f, 5f),
                    Color.FromArgb(255, 220, 80, 60), 10f);
            }
            else
            {
                r.DrawText("● Scripts ready",
                    new PointF(_width - 115f, 5f),
                    Color.FromArgb(255, 70, 180, 70), 10f);
            }

            if (_openIdx >= 0 && _dropdown != null)
                _dropdown.OnRender(r);
        }

        // Call with the FULL window mouse position every frame
        public void OnMouseMove(PointF fullPos)
        {
            _mouse = fullPos;
            _dropdown?.OnMouseMove(fullPos);
        }

        public bool OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (_playBtn.Contains(pos)) { TogglePlay(); return true; }
            if (_pauseBtn.Contains(pos)) { TogglePause(); return true; }
            if (_stopBtn.Contains(pos)) { StopPlay(); return true; }

            if (_openIdx >= 0 && _dropdown != null)
            {
                if (_dropdown.ContainsPoint(pos)) { _dropdown.OnMouseDown(pos); Close(); return true; }
                Close(); return false;
            }

            if (pos.Y >= 0 && pos.Y <= Height)
                for (int i = 0; i < _menus.Count; i++)
                    if (_menus[i].bounds.Contains(pos))
                    { if (_openIdx == i) Close(); else Open(i); return true; }

            return false;
        }

        public bool HitTestBar(PointF pos) => pos.Y >= 0 && pos.Y <= Height;

        private void Open(int i) { _openIdx = i; _dropdown = new DropdownPanel(new PointF(_menus[i].bounds.X, Height), _menus[i].items); }
        private void Close() { _openIdx = -1; _dropdown = null; }

        private void TogglePlay() { if (!IsPlaying) { IsPlaying = true; IsPaused = false; Play?.Invoke(); } else StopPlay(); }
        private void TogglePause() { if (IsPlaying) { IsPaused = !IsPaused; Pause?.Invoke(); } }
        private void StopPlay() { IsPlaying = false; IsPaused = false; Stop?.Invoke(); }
    }

    internal class DropdownPanel
    {
        private readonly List<MenuDropdownItem> _items;
        private readonly PointF _origin;
        private int _hov = -1;
        private const float IH = 22f, SH = 6f, MW = 232f;

        private static readonly Color CBg = Color.FromArgb(252, 33, 33, 33);
        private static readonly Color CBd = Color.FromArgb(255, 64, 64, 64);
        private static readonly Color CHov = Color.FromArgb(255, 55, 94, 178);
        private static readonly Color CT = Color.FromArgb(255, 212, 212, 212);
        private static readonly Color CDim = Color.FromArgb(255, 108, 108, 108);
        private static readonly Color CSh = Color.FromArgb(65, 0, 0, 0);

        public DropdownPanel(PointF o, List<MenuDropdownItem> items) { _origin = o; _items = items; }

        private float TotalH() { float h = 4f; foreach (var i in _items) h += i.IsSep ? SH : IH; return h; }
        public RectangleF Bounds => new(_origin.X, _origin.Y, MW, TotalH());
        public bool ContainsPoint(PointF p) => Bounds.Contains(p);

        public void OnRender(IEditorRenderer r)
        {
            var b = Bounds;
            r.FillRect(new RectangleF(b.X + 3, b.Y + 3, b.Width, b.Height), CSh);
            r.FillRect(b, CBg);
            r.DrawRect(b, CBd, 1f);

            float y = _origin.Y + 2f;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.IsSep)
                {
                    r.DrawLine(new PointF(_origin.X + 6, y + SH / 2), new PointF(_origin.X + MW - 6, y + SH / 2),
                        Color.FromArgb(255, 56, 56, 56), 1f);
                    y += SH; continue;
                }
                var row = new RectangleF(_origin.X, y, MW, IH);
                if (i == _hov && !it.IsHeader && it.Action != null) r.FillRect(row, CHov);
                var tc = it.IsHeader ? CDim : (it.Action == null ? CDim : CT);
                r.DrawText(it.Label, new PointF(_origin.X + 8f, y + 5f), tc, 11f);
                if (!string.IsNullOrEmpty(it.Shortcut))
                    r.DrawText(it.Shortcut, new PointF(_origin.X + MW - 68f, y + 5f), CDim, 10f);
                y += IH;
            }
        }

        public void OnMouseDown(PointF pos)
        {
            float y = _origin.Y + 2f;
            foreach (var it in _items)
            {
                float rh = it.IsSep ? SH : IH;
                if (!it.IsSep && !it.IsHeader && it.Action != null)
                    if (new RectangleF(_origin.X, y, MW, rh).Contains(pos)) { it.Action(); return; }
                y += rh;
            }
        }

        public void OnMouseMove(PointF pos)
        {
            _hov = -1;
            float y = _origin.Y + 2f;
            for (int i = 0; i < _items.Count; i++)
            {
                float rh = _items[i].IsSep ? SH : IH;
                if (!_items[i].IsSep && new RectangleF(_origin.X, y, MW, rh).Contains(pos)) { _hov = i; return; }
                y += rh;
            }
        }
    }
}