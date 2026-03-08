using Elintria.Editor.UI;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Generic;
using System.Drawing;

namespace Elintria.Editor.UI
{
    // =========================================================================
    //  MenuBar  — Unity-style top menu bar
    // =========================================================================
    //  • Horizontal labels for File / Edit / Assets / Component / Build / Help.
    //  • Clicking a label opens the matching dropdown via ContextMenuManager.
    //  • Play / Pause / Stop buttons centred in the bar.
    //  • Hovering across labels while a dropdown is open switches to the new one
    //    (standard desktop menu-bar behaviour).
    // =========================================================================
    public class MenuBar : Panel
    {
        // ── Colours ───────────────────────────────────────────────────────
        static readonly Color C_Bg = Color.FromArgb(255, 50, 50, 55);
        static readonly Color C_Hover = Color.FromArgb(255, 68, 68, 78);
        static readonly Color C_Active = Color.FromArgb(255, 44, 93, 180);
        static readonly Color C_Text = Color.FromArgb(255, 210, 210, 215);
        static readonly Color C_Border = Color.FromArgb(255, 28, 28, 32);

        const float BAR_H = 22f;
        const float PAD_X = 10f;

        // ── Data ──────────────────────────────────────────────────────────
        private readonly BitmapFont _font;
        private int _openIdx = -1;   // which top-level menu is showing a dropdown

        // List of (label, item-factory) pairs.
        private readonly List<(string Label, System.Func<List<ContextMenuItem>> Build)> _menus;

        // Play-button hit rects (rebuilt every Draw, used in HandleMouseDown).
        private readonly List<(float X, float Y, float W, float H, System.Action Act)> _playBtns = new();

        // ── Editor callbacks ──────────────────────────────────────────────
        public System.Action OnNewScene;
        public System.Action OnOpenScene;
        public System.Action OnSaveScene;
        public System.Action OnQuit;
        public System.Action OnToggleWireframe;
        public System.Action OnAbout;
        public System.Action OnPlay;
        public System.Action OnPause;
        public System.Action OnBuild;
        public System.Action OnBuildRun;
        public System.Func<bool> IsPlaying;
        public System.Func<bool> IsPaused;

        // ── Constructor ───────────────────────────────────────────────────
        public MenuBar(BitmapFont font)
        {
            _font = font;
            Size = new Vector2(9999, BAR_H);
            BackgroundColor = C_Bg;

            _menus = new()
            {
                ("File",      BuildFile),
                ("Edit",      BuildEdit),
                ("Assets",    BuildAssets),
                ("Component", BuildComponent),
                ("Build",     BuildBuildMenu),
                ("Help",      BuildHelp),
            };
        }

        // ── Draw ──────────────────────────────────────────────────────────
        public override void Draw()
        {
            if (!Visible) return;
            _playBtns.Clear();

            Vector2 abs = GetAbsolutePosition();
            Vector2 mp = GetMousePosition();

            // Bar background
            UIRenderer.DrawRect(abs.X, abs.Y, Size.X, Size.Y, C_Bg);
            // Bottom hairline
            UIRenderer.DrawRect(abs.X, abs.Y + Size.Y - 1f, Size.X, 1f, C_Border);

            float x = abs.X + 4f;

            for (int i = 0; i < _menus.Count; i++)
            {
                string lbl = _menus[i].Label;
                float tw = _font?.MeasureText(lbl) ?? 50f;
                float bw = tw + PAD_X * 2f;

                bool hovered = mp.X >= x && mp.X < x + bw
                            && mp.Y >= abs.Y && mp.Y < abs.Y + BAR_H;
                bool active = (i == _openIdx);

                // Highlight
                if (active || hovered)
                    UIRenderer.DrawRect(x, abs.Y, bw, BAR_H,
                        active ? C_Active : C_Hover);

                // Switch dropdown if another was already open and user hovers a label
                if (hovered && _openIdx >= 0 && !active)
                    OpenDropdown(i, x);

                if (_font != null)
                    _font.DrawText(lbl, x + PAD_X, abs.Y + (BAR_H - _font.LineH) * 0.5f, C_Text);

                x += bw;
            }

            // ── Play / Pause / Stop ───────────────────────────────────────
            bool playing = IsPlaying?.Invoke() ?? false;
            bool paused = IsPaused?.Invoke() ?? false;
            float btnW = 28f, btnH = 16f;
            float bx = abs.X + Size.X * 0.5f - btnW * 1.5f - 2f;
            float by = abs.Y + (BAR_H - btnH) * 0.5f;

            DrawPlayBtn(bx, by, btnW, btnH, "▶",
                playing && !paused ? C_Active : C_Hover, OnPlay);
            DrawPlayBtn(bx + btnW + 2f, by, btnW, btnH, "‖",
                paused ? C_Active : C_Hover, OnPause);
            DrawPlayBtn(bx + (btnW + 2f) * 2, by, btnW, btnH, "■",
                Color.FromArgb(255, 72, 38, 38),
                () => { if (playing) OnPlay?.Invoke(); });

            DrawChildren(abs);
        }

        private void DrawPlayBtn(float x, float y, float w, float h,
                                 string icon, Color bg, System.Action act)
        {
            _playBtns.Add((x, y, w, h, act));
            UIRenderer.DrawRect(x, y, w, h, bg);
            UIRenderer.DrawRectOutline(x, y, w, h, Color.FromArgb(60, 0, 0, 0));
            if (_font != null)
                _font.DrawText(icon,
                    x + (w - _font.MeasureText(icon)) * 0.5f,
                    y + (h - _font.LineH) * 0.5f,
                    Color.FromArgb(230, 230, 230, 230));
        }

        // ── Input ─────────────────────────────────────────────────────────
        public override bool HandleMouseDown(MouseButtonEventArgs e)
        {
            if (e.Button != MouseButton.Left) return false;

            Vector2 mp = GetMousePosition();
            Vector2 abs = GetAbsolutePosition();

            // Not in bar area at all
            if (mp.Y < abs.Y || mp.Y >= abs.Y + BAR_H) return false;

            // ── Top-level menu labels ──────────────────────────────────────
            float x = abs.X + 4f;
            for (int i = 0; i < _menus.Count; i++)
            {
                float bw = (_font?.MeasureText(_menus[i].Label) ?? 50f) + PAD_X * 2f;
                if (mp.X >= x && mp.X < x + bw)
                {
                    if (_openIdx == i) CloseDropdown();   // toggle off
                    else OpenDropdown(i, x);
                    return true;
                }
                x += bw;
            }

            // ── Play / Pause / Stop buttons ───────────────────────────────
            foreach (var (bx, by, bw, bh, act) in _playBtns)
            {
                if (mp.X >= bx && mp.X < bx + bw && mp.Y >= by && mp.Y < by + bh)
                {
                    act?.Invoke();
                    return true;
                }
            }

            return false;
        }

        // No per-frame Update needed — ContextMenuManager handles its own update.
        public override void Update(float dt) { }

        // ── Dropdown helpers ──────────────────────────────────────────────
        private void OpenDropdown(int idx, float labelScreenX)
        {
            _openIdx = idx;
            Vector2 abs = GetAbsolutePosition();
            ContextMenuManager.Open(
                _menus[idx].Build(),
                new Vector2(labelScreenX, abs.Y + BAR_H),
                closeCallback: () => _openIdx = -1);
        }

        private void CloseDropdown()
        {
            ContextMenuManager.Close();
            // _openIdx is reset by the closeCallback above
        }

        // ── Menu definitions ──────────────────────────────────────────────
        private List<ContextMenuItem> BuildFile() => new()
        {
            ContextMenuItem.Item("New Scene",       () => OnNewScene?.Invoke()),
            ContextMenuItem.Item("Open Scene…",     () => OnOpenScene?.Invoke()),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Save",            () => OnSaveScene?.Invoke()),
            ContextMenuItem.Item("Save As…",        () => OnSaveScene?.Invoke()),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Build And Run",   () => OnBuildRun?.Invoke()),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Exit",            () => OnQuit?.Invoke()),
        };

        private List<ContextMenuItem> BuildEdit() => new()
        {
            ContextMenuItem.Item("Undo",             null, disabled: true),
            ContextMenuItem.Item("Redo",             null, disabled: true),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Select All",       null, disabled: true),
            ContextMenuItem.Item("Deselect All",     null, disabled: true),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Play",             () => OnPlay?.Invoke()),
            ContextMenuItem.Item("Pause",            () => OnPause?.Invoke()),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Project Settings…", null, disabled: true),
            ContextMenuItem.Item("Preferences…",     null, disabled: true),
        };

        private List<ContextMenuItem> BuildAssets() => new()
        {
            ContextMenuItem.SubMenu("Create", new()
            {
                ContextMenuItem.Item("C# Script",   null, disabled: true),
                ContextMenuItem.Item("Shader",       null, disabled: true),
                ContextMenuItem.Sep(),
                ContextMenuItem.Item("Material",     null, disabled: true),
                ContextMenuItem.Item("Texture",      null, disabled: true),
                ContextMenuItem.Sep(),
                ContextMenuItem.Item("Folder",       null, disabled: true),
            }),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Show in Explorer", null, disabled: true),
            ContextMenuItem.Item("Open",             null, disabled: true),
            ContextMenuItem.Item("Delete",           null, disabled: true),
            ContextMenuItem.Item("Rename",           null, disabled: true),
            ContextMenuItem.Item("Copy Path",        null, disabled: true),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Import New Asset…", null, disabled: true),
            ContextMenuItem.Item("Refresh",          null, disabled: true),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Open C# Project",  null, disabled: true),
        };

        private List<ContextMenuItem> BuildComponent() => new()
        {
            ContextMenuItem.SubMenu("Rendering", new()
            {
                ContextMenuItem.Item("Mesh Renderer",   null, disabled: true),
                ContextMenuItem.Item("Camera",          null, disabled: true),
                ContextMenuItem.Item("Light",           null, disabled: true),
            }),
            ContextMenuItem.SubMenu("Physics", new()
            {
                ContextMenuItem.Item("Rigidbody",       null, disabled: true),
                ContextMenuItem.Item("Box Collider",    null, disabled: true),
                ContextMenuItem.Item("Sphere Collider", null, disabled: true),
            }),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("New Script…",         null, disabled: true),
        };

        private List<ContextMenuItem> BuildBuildMenu() => new()
        {
            ContextMenuItem.Item("Build Game",      () => OnBuild?.Invoke()),
            ContextMenuItem.Item("Build and Run",   () => OnBuildRun?.Invoke()),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Build Settings…", null, disabled: true),
            ContextMenuItem.Item("Player Settings…", null, disabled: true),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Enter Play Mode", () => OnPlay?.Invoke()),
            ContextMenuItem.Item("Pause",           () => OnPause?.Invoke()),
        };

        private List<ContextMenuItem> BuildHelp() => new()
        {
            ContextMenuItem.Item("About Elintria",      () => OnAbout?.Invoke()),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Documentation",       null, disabled: true),
            ContextMenuItem.Item("Scripting Reference", null, disabled: true),
        };
    }
}