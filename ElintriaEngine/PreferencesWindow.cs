using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;

namespace ElintriaEngine.UI.Panels
{
    /// <summary>
    /// Edit → Preferences — global editor settings.
    ///
    /// Sections: General | Scene View | Keybinds | Tags & Layers
    ///
    /// Uses an ID-string approach (not ref properties) so all rows are
    /// auto-properties friendly and fully interactive.
    /// </summary>
    public class PreferencesWindow : Panel
    {
        // ── Data ──────────────────────────────────────────────────────────────
        private EditorPreferences _prefs = EditorPreferences.Instance;
        private TagsAndLayers? _tl;

        // ── Section navigation ────────────────────────────────────────────────
        private enum Section { General, SceneView, Keybinds, TagsAndLayers }
        private Section _section = Section.General;

        // ── Layout ────────────────────────────────────────────────────────────
        private const float SideW = 165f;
        private const float TitleH = 32f;
        private const float FootH = 40f;
        private const float RowH = 28f;

        // ── Scroll ────────────────────────────────────────────────────────────
        private float _scroll;
        private float _contentH;

        // ── Interactive element registry (built each frame) ───────────────────
        private enum CtrlType { Toggle, Slider, Dropdown }
        private record Ctrl(CtrlType Type, string Id, RectangleF Bounds,
                            float Min = 0, float Max = 1);
        private readonly List<Ctrl> _ctrls = new();

        // ── Dropdown state ────────────────────────────────────────────────────
        private string? _openDd;
        private string[]? _openDdOpts;
        private RectangleF _openDdOrigin;

        // ── Slider drag state ─────────────────────────────────────────────────
        private string? _dragSlider;
        private RectangleF _dragTrack;

        // ── Keybind rebinding ─────────────────────────────────────────────────
        private EditorAction? _rebinding;
        private float _rebindTimer;

        // ── Tags & Layers editing ─────────────────────────────────────────────
        private enum TLMode { Tags, Layers }
        private TLMode _tlMode = TLMode.Tags;
        private string _tlEditText = "";
        private int _tlEditIdx = -2;   // -2=none, -1=new, ≥0=editing row i
        private bool _tlFocused;

        // ── Colours ────────────────────────────────────────────────────────────
        private static readonly Color CBg = Color.FromArgb(255, 30, 32, 38);
        private static readonly Color CSide = Color.FromArgb(255, 24, 26, 32);
        private static readonly Color CSideHov = Color.FromArgb(255, 38, 42, 56);
        private static readonly Color CSideSel = Color.FromArgb(255, 50, 100, 200);
        private static readonly Color CHead = Color.FromArgb(255, 22, 24, 30);
        private static readonly Color CRow = Color.FromArgb(255, 36, 38, 46);
        private static readonly Color CRowAlt = Color.FromArgb(255, 32, 34, 42);
        private static readonly Color CAccent = Color.FromArgb(255, 60, 130, 255);
        private static readonly Color CText = Color.FromArgb(255, 210, 215, 225);
        private static readonly Color CTextDim = Color.FromArgb(255, 130, 140, 155);
        private static readonly Color CBorder = Color.FromArgb(255, 48, 52, 64);
        private static readonly Color CTogOn = Color.FromArgb(255, 45, 140, 70);
        private static readonly Color CTogOff = Color.FromArgb(255, 55, 55, 68);
        private static readonly Color CBtnGrey = Color.FromArgb(255, 55, 58, 70);
        private static readonly Color CBtnGreen = Color.FromArgb(255, 45, 140, 70);
        private static readonly Color CBtnRed = Color.FromArgb(255, 160, 45, 45);

        private PointF _mouse;
        private int _rowIdx;

        // ─────────────────────────────────────────────────────────────────────
        public PreferencesWindow(RectangleF bounds) : base("Preferences", bounds)
        { IsVisible = false; }

        public void SetTagsAndLayers(TagsAndLayers tl) => _tl = tl;
        public void RefreshPrefs() => _prefs = EditorPreferences.Instance;

        // ══════════════════════════════════════════════════════════════════════
        //  Render
        // ══════════════════════════════════════════════════════════════════════
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            _ctrls.Clear();
            _rowIdx = 0;

            r.FillRect(Bounds, CBg);
            r.DrawRect(Bounds, CBorder, 2f);

            // Title bar
            var titleBar = new RectangleF(Bounds.X, Bounds.Y, Bounds.Width, TitleH);
            r.FillRect(titleBar, CHead);
            r.DrawText("Preferences", new PointF(Bounds.X + 14f, Bounds.Y + 8f), CText, 13f);
            var closeBtn = new RectangleF(Bounds.Right - 28f, Bounds.Y + 6f, 22f, 20f);
            r.FillRect(closeBtn, closeBtn.Contains(_mouse)
                ? Color.FromArgb(255, 180, 50, 50) : Color.FromArgb(255, 70, 35, 35));
            r.DrawText("✕", new PointF(closeBtn.X + 5f, closeBtn.Y + 4f), Color.White, 9f);
            r.DrawLine(new PointF(Bounds.X, Bounds.Y + TitleH),
                       new PointF(Bounds.Right, Bounds.Y + TitleH), CBorder);

            // Side nav
            DrawSideNav(r);

            // Content
            var content = ContentArea();
            r.FillRect(content, CBg);
            r.PushClip(content);
            DrawContent(r, content);
            r.PopClip();

            // Footer
            var foot = new RectangleF(Bounds.X, Bounds.Bottom - FootH, Bounds.Width, FootH);
            r.FillRect(foot, CHead);
            r.DrawLine(new PointF(foot.X, foot.Y), new PointF(foot.Right, foot.Y), CBorder);

            var saveBtn = new RectangleF(foot.Right - 104f, foot.Y + 8f, 92f, 24f);
            r.FillRect(saveBtn, saveBtn.Contains(_mouse)
                ? Color.FromArgb(255, 80, 160, 255) : CAccent);
            r.DrawText("Save & Close", new PointF(saveBtn.X + 8f, saveBtn.Y + 6f), Color.White, 9f);

            var resetBtn = new RectangleF(foot.X + 12f, foot.Y + 8f, 80f, 24f);
            r.FillRect(resetBtn, resetBtn.Contains(_mouse) ? Color.FromArgb(255, 70, 74, 90) : CBtnGrey);
            r.DrawText("Reset All", new PointF(resetBtn.X + 12f, resetBtn.Y + 6f), CText, 9f);

            // Dropdown popup on top (outside clip region)
            if (_openDd != null) DrawDdPopup(r);

            // Rebind overlay on top of everything
            if (_rebinding.HasValue) DrawRebindOverlay(r);
        }

        private RectangleF ContentArea() => new(
            Bounds.X + SideW, Bounds.Y + TitleH,
            Bounds.Width - SideW, Bounds.Height - TitleH - FootH);

        // ── Side nav ──────────────────────────────────────────────────────────
        private void DrawSideNav(IEditorRenderer r)
        {
            var side = new RectangleF(Bounds.X, Bounds.Y + TitleH, SideW, Bounds.Height - TitleH);
            r.FillRect(side, CSide);
            r.DrawLine(new PointF(side.Right, side.Y), new PointF(side.Right, side.Bottom), CBorder);

            var items = new[] {
                (Section.General,      "⚙  General"),
                (Section.SceneView,    "🎬  Scene View"),
                (Section.Keybinds,     "⌨  Keybinds"),
                (Section.TagsAndLayers,"🏷  Tags & Layers"),
            };

            float y = side.Y + 10f;
            foreach (var (sec, lbl) in items)
            {
                bool sel = _section == sec;
                var itemR = new RectangleF(side.X, y, SideW - 1f, 30f);
                bool hov = !sel && itemR.Contains(_mouse);
                r.FillRect(itemR, sel ? CSideSel : hov ? CSideHov : Color.Transparent);
                if (sel) r.FillRect(new RectangleF(side.X, y, 3f, 30f), CAccent);
                r.DrawText(lbl, new PointF(side.X + 14f, y + 8f),
                    sel ? Color.White : CTextDim, 10f);
                y += 32f;
            }
        }

        // ── Content dispatch ──────────────────────────────────────────────────
        private void DrawContent(IEditorRenderer r, RectangleF a)
        {
            switch (_section)
            {
                case Section.General: DrawGeneral(r, a); break;
                case Section.SceneView: DrawSceneView(r, a); break;
                case Section.Keybinds: DrawKeybinds(r, a); break;
                case Section.TagsAndLayers: DrawTagsLayers(r, a); break;
            }
        }

        // ── General ───────────────────────────────────────────────────────────
        private void DrawGeneral(IEditorRenderer r, RectangleF a)
        {
            float y = a.Y + 8f - _scroll;

            SectionHeader(r, a, ref y, "Autosave");
            Toggle(r, a, ref y, "Auto-Save", "gen.autosave", _prefs.AutoSave);
            SliderInt(r, a, ref y, "Auto-Save Interval (s)", "gen.asint",
                _prefs.AutoSaveInterval, 30, 3600);

            SectionHeader(r, a, ref y, "Display");
            Toggle(r, a, ref y, "Show Frame Rate", "gen.fps", _prefs.ShowFrameRate);
            Toggle(r, a, ref y, "Show Stats Overlay", "gen.stats", _prefs.ShowStats);

            SectionHeader(r, a, ref y, "Theme");
            Dropdown(r, a, ref y, "Color Theme", "gen.theme", _prefs.Theme,
                new[] { "Dark", "Light", "High Contrast" });

            _contentH = y + _scroll - a.Y;
        }

        // ── Scene View ────────────────────────────────────────────────────────
        private void DrawSceneView(IEditorRenderer r, RectangleF a)
        {
            float y = a.Y + 8f - _scroll;

            SectionHeader(r, a, ref y, "Mouse & Navigation");
            SliderFloat(r, a, ref y, "Mouse Sensitivity", "sv.sens", _prefs.MouseSensitivity, 0.05f, 2.0f);
            SliderFloat(r, a, ref y, "Scroll Speed", "sv.scroll", _prefs.ScrollSpeed, 0.01f, 0.5f);
            SliderFloat(r, a, ref y, "Fly-Cam Speed", "sv.fly", _prefs.FlyCamSpeed, 0.5f, 20f);
            Toggle(r, a, ref y, "Invert Y Axis", "sv.invy", _prefs.InvertYAxis);

            SectionHeader(r, a, ref y, "Gizmos & Grid");
            Toggle(r, a, ref y, "Show Grid", "sv.grid", _prefs.ShowGrid);
            Toggle(r, a, ref y, "Show Gizmos", "sv.gizmos", _prefs.ShowGizmos);
            SliderFloat(r, a, ref y, "Gizmo Size", "sv.gsize", _prefs.GizmoSize, 0.2f, 3.0f);

            _contentH = y + _scroll - a.Y;
        }

        // ── Keybinds ──────────────────────────────────────────────────────────
        private void DrawKeybinds(IEditorRenderer r, RectangleF a)
        {
            float y = a.Y + 8f - _scroll;

            // Column headers
            r.DrawText("Action",
                new PointF(a.X + 16f, y + 4f), CTextDim, 9f);
            r.DrawText("Shortcut",
                new PointF(a.Right - 136f, y + 4f), CTextDim, 9f);
            y += 22f;
            r.DrawLine(new PointF(a.X, y), new PointF(a.Right, y), CBorder);
            y += 4f;

            string? lastGroup = null;
            int row = 0;
            foreach (EditorAction action in Enum.GetValues<EditorAction>())
            {
                string group = EditorPreferences.ActionGroup(action);
                if (group != lastGroup)
                {
                    y += 4f;
                    r.FillRect(new RectangleF(a.X, y, a.Width, 22f),
                        Color.FromArgb(255, 26, 28, 38));
                    r.DrawText(group, new PointF(a.X + 10f, y + 4f), CAccent, 10f);
                    y += 22f;
                    lastGroup = group;
                    row = 0;
                }

                bool isRebinding = _rebinding == action;
                var rowR = new RectangleF(a.X, y, a.Width, 24f);
                r.FillRect(rowR, isRebinding
                    ? Color.FromArgb(255, 30, 60, 110)
                    : row % 2 == 0 ? CRow : CRowAlt);

                r.DrawText(EditorPreferences.ActionDisplayName(action),
                    new PointF(a.X + 16f, y + 5f), CText, 10f);

                var pill = new RectangleF(a.Right - 136f, y + 3f, 124f, 18f);
                bool pHov = pill.Contains(_mouse) && _rebinding == null;
                r.FillRect(pill, pHov ? Color.FromArgb(255, 50, 80, 130)
                    : isRebinding ? Color.FromArgb(255, 200, 90, 30)
                    : Color.FromArgb(255, 44, 50, 64));
                r.DrawRect(pill, isRebinding
                    ? Color.FromArgb(255, 255, 140, 60) : CBorder);
                r.DrawText(isRebinding ? "Press a key..." : _prefs.GetKeybind(action).DisplayString,
                    new PointF(pill.X + 6f, pill.Y + 3f),
                    isRebinding ? Color.FromArgb(255, 255, 200, 100) : CText, 9f);

                y += 24f;
                row++;
            }

            r.DrawText("Click any shortcut to rebind.  Escape to cancel.",
                new PointF(a.X + 16f, y + 10f), CTextDim, 9f);

            _contentH = y + 30f - a.Y + _scroll;
        }

        // ── Tags & Layers ──────────────────────────────────────────────────────
        private void DrawTagsLayers(IEditorRenderer a_r, RectangleF a)
        {
            float y = a.Y + 10f;
            if (_tl == null)
            {
                a_r.DrawText("No project loaded.", new PointF(a.X + 20f, y + 10f), CTextDim, 10f);
                return;
            }

            // Tab bar
            var tabT = new RectangleF(a.X + 10f, y, 84f, 26f);
            var tabL = new RectangleF(a.X + 100f, y, 84f, 26f);
            bool tagsActive = _tlMode == TLMode.Tags;
            a_r.FillRect(tabT, tagsActive ? CAccent : CBtnGrey);
            a_r.FillRect(tabL, !tagsActive ? CAccent : CBtnGrey);
            a_r.DrawText("Tags", new PointF(tabT.X + 24f, tabT.Y + 6f), Color.White, 10f);
            a_r.DrawText("Layers", new PointF(tabL.X + 20f, tabL.Y + 6f), Color.White, 10f);
            y += 32f;
            a_r.DrawLine(new PointF(a.X, y), new PointF(a.Right, y), CBorder);
            y += 8f;

            // Count badge
            var list = tagsActive ? _tl.Tags : _tl.Layers;
            string builtIn = tagsActive ? "Untagged" : "Default";
            a_r.DrawText($"{list.Count} {(tagsActive ? "tag" : "layer")}{(list.Count != 1 ? "s" : "")}",
                new PointF(a.Right - 80f, a.Y + 12f), CTextDim, 9f);

            for (int i = 0; i < list.Count; i++)
            {
                bool editing = _tlEditIdx == i && _tlFocused;
                bool isBuiltIn = list[i] == builtIn;
                var rowR = new RectangleF(a.X + 4f, y, a.Width - 8f, 28f);
                a_r.FillRect(rowR, i % 2 == 0 ? CRow : CRowAlt);

                // Index badge
                a_r.FillRect(new RectangleF(rowR.X, rowR.Y, 28f, rowR.Height),
                    Color.FromArgb(255, 28, 30, 40));
                a_r.DrawText(i.ToString(), new PointF(rowR.X + 8f, rowR.Y + 7f), CTextDim, 9f);

                if (editing)
                {
                    var tf = new RectangleF(rowR.X + 32f, rowR.Y + 4f, rowR.Width - 110f, 20f);
                    a_r.FillRect(tf, Color.FromArgb(255, 18, 20, 28));
                    a_r.DrawRect(tf, CAccent, 1.5f);
                    a_r.DrawText(_tlEditText + "│", new PointF(tf.X + 5f, tf.Y + 4f), Color.White, 10f);

                    var ok = new RectangleF(rowR.Right - 72f, rowR.Y + 4f, 34f, 20f);
                    var cx = new RectangleF(rowR.Right - 34f, rowR.Y + 4f, 30f, 20f);
                    a_r.FillRect(ok, ok.Contains(_mouse) ? CBtnGreen : Color.FromArgb(255, 40, 110, 55));
                    a_r.FillRect(cx, cx.Contains(_mouse) ? CBtnRed : Color.FromArgb(255, 110, 40, 40));
                    a_r.DrawText("OK", new PointF(ok.X + 6f, ok.Y + 4f), Color.White, 9f);
                    a_r.DrawText("✕", new PointF(cx.X + 8f, cx.Y + 4f), Color.White, 9f);
                }
                else
                {
                    a_r.DrawText(list[i], new PointF(rowR.X + 36f, rowR.Y + 7f),
                        isBuiltIn ? CTextDim : CText, 10f);
                    if (isBuiltIn)
                        a_r.DrawText("(built-in)", new PointF(rowR.Right - 76f, rowR.Y + 7f), CTextDim, 8f);
                    else
                    {
                        var edit = new RectangleF(rowR.Right - 70f, rowR.Y + 4f, 32f, 20f);
                        var del = new RectangleF(rowR.Right - 34f, rowR.Y + 4f, 30f, 20f);
                        a_r.FillRect(edit, edit.Contains(_mouse)
                            ? Color.FromArgb(255, 60, 100, 180) : CBtnGrey);
                        a_r.FillRect(del, del.Contains(_mouse)
                            ? CBtnRed : Color.FromArgb(255, 100, 40, 40));
                        a_r.DrawText("Edit", new PointF(edit.X + 2f, edit.Y + 4f), Color.White, 8f);
                        a_r.DrawText("✕", new PointF(del.X + 9f, del.Y + 4f), Color.White, 9f);
                    }
                }
                y += 30f;
            }

            // Add new
            y += 8f;
            if (_tlEditIdx == -1 && _tlFocused)
            {
                var tf = new RectangleF(a.X + 4f, y, a.Width - 90f, 26f);
                a_r.FillRect(tf, Color.FromArgb(255, 18, 20, 28));
                a_r.DrawRect(tf, CAccent, 1.5f);
                a_r.DrawText(_tlEditText + "│", new PointF(tf.X + 6f, tf.Y + 6f), Color.White, 10f);
                var addOk = new RectangleF(tf.Right + 4f, y, 44f, 26f);
                a_r.FillRect(addOk, addOk.Contains(_mouse) ? CBtnGreen : Color.FromArgb(255, 40, 110, 55));
                a_r.DrawText("Add", new PointF(addOk.X + 6f, addOk.Y + 6f), Color.White, 9f);
            }
            else
            {
                var addBtn = new RectangleF(a.X + 4f, y, 130f, 26f);
                a_r.FillRect(addBtn, addBtn.Contains(_mouse)
                    ? Color.FromArgb(255, 50, 100, 180) : CAccent);
                a_r.DrawText($"+ Add {(tagsActive ? "Tag" : "Layer")}",
                    new PointF(addBtn.X + 12f, addBtn.Y + 6f), Color.White, 9f);
            }
        }

        // ── Rebind overlay ────────────────────────────────────────────────────
        private void DrawRebindOverlay(IEditorRenderer r)
        {
            r.FillRect(Bounds, Color.FromArgb(120, 0, 0, 0));
            float bw = 340f, bh = 88f;
            var box = new RectangleF(
                Bounds.X + (Bounds.Width - bw) / 2f,
                Bounds.Y + (Bounds.Height - bh) / 2f, bw, bh);
            r.FillRect(box, Color.FromArgb(255, 28, 32, 44));
            r.DrawRect(box, Color.FromArgb(255, 255, 140, 60), 2f);
            r.DrawText($"Rebinding: {EditorPreferences.ActionDisplayName(_rebinding!.Value)}",
                new PointF(box.X + 14f, box.Y + 14f), Color.White, 11f);
            r.DrawText("Press a key combination  —  Escape to cancel",
                new PointF(box.X + 14f, box.Y + 38f), CTextDim, 9f);
            int secs = Math.Max(0, 5 - (int)_rebindTimer);
            r.DrawText($"Auto-cancels in {secs}s",
                new PointF(box.X + 14f, box.Y + 58f), Color.FromArgb(180, 200, 120, 60), 8f);
        }

        // ── Dropdown popup ────────────────────────────────────────────────────
        private void DrawDdPopup(IEditorRenderer r)
        {
            if (_openDd == null || _openDdOpts == null) return;
            float itemH = 22f;
            float ph = _openDdOpts.Length * itemH + 4f;
            var popup = new RectangleF(_openDdOrigin.X, _openDdOrigin.Bottom,
                Math.Max(_openDdOrigin.Width, 160f), ph);
            r.FillRect(popup, Color.FromArgb(255, 28, 32, 42));
            r.DrawRect(popup, CAccent, 1.5f);
            for (int i = 0; i < _openDdOpts.Length; i++)
            {
                var ir = new RectangleF(popup.X, popup.Y + 2f + i * itemH,
                    popup.Width, itemH);
                bool hov = ir.Contains(_mouse);
                bool sel = GetDdValue(_openDd) == _openDdOpts[i];
                if (hov || sel) r.FillRect(ir, sel
                    ? Color.FromArgb(255, 50, 100, 200)
                    : Color.FromArgb(255, 42, 48, 64));
                r.DrawText(_openDdOpts[i], new PointF(ir.X + 8f, ir.Y + 4f),
                    sel ? Color.White : CText, 10f);
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Row helpers (ID-based, no ref properties)
        // ════════════════════════════════════════════════════════════════════════
        private RectangleF RowBg(IEditorRenderer r, RectangleF a, float y)
        {
            var rr = new RectangleF(a.X, y, a.Width, RowH);
            r.FillRect(rr, _rowIdx++ % 2 == 0 ? CRow : CRowAlt);
            return rr;
        }

        private void SectionHeader(IEditorRenderer r, RectangleF a, ref float y, string title)
        {
            y += 6f;
            r.FillRect(new RectangleF(a.X, y, a.Width, 22f), Color.FromArgb(255, 26, 28, 38));
            r.DrawText(title, new PointF(a.X + 12f, y + 4f), CAccent, 10f);
            y += 22f;
        }

        private void Toggle(IEditorRenderer r, RectangleF a, ref float y,
                            string label, string id, bool value)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(a.X + 16f, y + 7f), CText, 10f);
            var tog = new RectangleF(rr.Right - 46f, y + 6f, 36f, 16f);
            r.FillRect(tog, value ? CTogOn : CTogOff);
            r.DrawRect(tog, CBorder);
            r.FillRect(new RectangleF(value ? tog.Right - 14f : tog.X + 2f, tog.Y + 2f, 12f, 12f),
                Color.White);
            r.DrawText(value ? "ON" : "OFF", new PointF(rr.Right - 60f, y + 8f), CTextDim, 8f);
            _ctrls.Add(new Ctrl(CtrlType.Toggle, id, rr));
            y += RowH;
        }

        private void SliderFloat(IEditorRenderer r, RectangleF a, ref float y,
                                  string label, string id, float value, float min, float max)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(a.X + 16f, y + 7f), CText, 10f);

            float tw = 150f;
            var track = new RectangleF(rr.Right - tw - 52f, y + 10f, tw, 8f);
            r.FillRect(track, Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(track, _dragSlider == id ? CAccent : CBorder);
            float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * t, track.Height), CAccent);
            r.FillRect(new RectangleF(track.X + track.Width * t - 4f, track.Y - 2f, 8f, 12f), Color.White);

            var valBox = new RectangleF(rr.Right - 48f, y + 4f, 44f, 20f);
            r.FillRect(valBox, Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(valBox, CBorder);
            r.DrawText(value.ToString("F2"), new PointF(valBox.X + 3f, valBox.Y + 4f), CText, 9f);

            _ctrls.Add(new Ctrl(CtrlType.Slider, id, track, min, max));
            y += RowH;
        }

        private void SliderInt(IEditorRenderer r, RectangleF a, ref float y,
                               string label, string id, int value, int min, int max) =>
            SliderFloat(r, a, ref y, label, id, value, min, max);

        private void Dropdown(IEditorRenderer r, RectangleF a, ref float y,
                              string label, string id, string value, string[] opts)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(a.X + 16f, y + 7f), CText, 10f);

            var dd = new RectangleF(rr.Right - 148f, y + 4f, 136f, 20f);
            bool open = _openDd == id;
            r.FillRect(dd, open || dd.Contains(_mouse)
                ? Color.FromArgb(255, 50, 60, 80) : Color.FromArgb(255, 38, 42, 54));
            r.DrawRect(dd, open ? CAccent : CBorder);
            r.DrawText(value, new PointF(dd.X + 6f, dd.Y + 4f), CText, 9f);
            r.DrawText("▾", new PointF(dd.Right - 14f, dd.Y + 4f), CTextDim, 9f);

            _ctrls.Add(new Ctrl(CtrlType.Dropdown, id, dd));
            y += RowH;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Apply changes by ID
        // ════════════════════════════════════════════════════════════════════════
        private void ApplyToggle(string id)
        {
            switch (id)
            {
                case "gen.autosave": _prefs.AutoSave = !_prefs.AutoSave; break;
                case "gen.fps": _prefs.ShowFrameRate = !_prefs.ShowFrameRate; break;
                case "gen.stats": _prefs.ShowStats = !_prefs.ShowStats; break;
                case "sv.invy": _prefs.InvertYAxis = !_prefs.InvertYAxis; break;
                case "sv.grid": _prefs.ShowGrid = !_prefs.ShowGrid; break;
                case "sv.gizmos": _prefs.ShowGizmos = !_prefs.ShowGizmos; break;
            }
        }

        private void ApplySlider(string id, float t)
        {
            static float Lerp(float a, float b, float x) => a + (b - a) * Math.Clamp(x, 0, 1);
            switch (id)
            {
                case "gen.asint": _prefs.AutoSaveInterval = (int)Lerp(30, 3600, t); break;
                case "sv.sens": _prefs.MouseSensitivity = Lerp(0.05f, 2.0f, t); break;
                case "sv.scroll": _prefs.ScrollSpeed = Lerp(0.01f, 0.5f, t); break;
                case "sv.fly": _prefs.FlyCamSpeed = Lerp(0.5f, 20f, t); break;
                case "sv.gsize": _prefs.GizmoSize = Lerp(0.2f, 3.0f, t); break;
            }
        }

        private void ApplyDropdown(string id, string value)
        {
            switch (id)
            {
                case "gen.theme": _prefs.Theme = value; break;
            }
        }

        private string GetDdValue(string id) => id switch
        {
            "gen.theme" => _prefs.Theme,
            _ => ""
        };

        private string[] GetDdOptions(string id) => id switch
        {
            "gen.theme" => new[] { "Dark", "Light", "High Contrast" },
            _ => Array.Empty<string>()
        };

        // ════════════════════════════════════════════════════════════════════════
        //  Mouse
        // ════════════════════════════════════════════════════════════════════════
        public override void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            // Slider drag
            if (_dragSlider != null)
            {
                float t = Math.Clamp((pos.X - _dragTrack.X) / _dragTrack.Width, 0f, 1f);
                ApplySlider(_dragSlider, t);
            }
        }

        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible) return;
            _mouse = pos;

            // Dropdown popup intercepts everything
            if (_openDd != null)
            {
                if (_openDdOpts != null)
                {
                    float itemH = 22f;
                    float ph = _openDdOpts.Length * itemH + 4f;
                    var popup = new RectangleF(_openDdOrigin.X, _openDdOrigin.Bottom,
                        Math.Max(_openDdOrigin.Width, 160f), ph);
                    if (popup.Contains(pos))
                    {
                        int i = (int)((pos.Y - popup.Y - 2f) / itemH);
                        if (i >= 0 && i < _openDdOpts.Length)
                            ApplyDropdown(_openDd, _openDdOpts[i]);
                    }
                }
                _openDd = null;
                return;
            }

            // Cancel rebind
            if (_rebinding.HasValue) { _rebinding = null; return; }

            // Title bar close
            var closeBtn = new RectangleF(Bounds.Right - 28f, Bounds.Y + 6f, 22f, 20f);
            if (closeBtn.Contains(pos)) { IsVisible = false; return; }

            // Footer
            var foot = new RectangleF(Bounds.X, Bounds.Bottom - FootH, Bounds.Width, FootH);
            if (new RectangleF(foot.Right - 104f, foot.Y + 8f, 92f, 24f).Contains(pos))
            { _prefs.Save(); IsVisible = false; return; }
            if (new RectangleF(foot.X + 12f, foot.Y + 8f, 80f, 24f).Contains(pos))
            { /* reset - could implement later */ return; }

            // Side nav
            var side = new RectangleF(Bounds.X, Bounds.Y + TitleH, SideW, Bounds.Height - TitleH);
            if (side.Contains(pos))
            {
                var secs = new[] {
                    Section.General, Section.SceneView,
                    Section.Keybinds, Section.TagsAndLayers };
                float y = side.Y + 10f;
                foreach (var sec in secs)
                {
                    if (new RectangleF(side.X, y, SideW - 1f, 30f).Contains(pos))
                    { _section = sec; _scroll = 0; _tlFocused = false; _tlEditIdx = -2; return; }
                    y += 32f;
                }
                return;
            }

            // Tags & Layers section
            if (_section == Section.TagsAndLayers)
            { HandleTLClick(pos); return; }

            // Keybinds section
            if (_section == Section.Keybinds)
            { HandleKeybindClick(pos); return; }

            // Controls registered this frame
            foreach (var ctrl in _ctrls)
            {
                if (!ctrl.Bounds.Contains(pos)) continue;
                switch (ctrl.Type)
                {
                    case CtrlType.Toggle:
                        ApplyToggle(ctrl.Id);
                        return;
                    case CtrlType.Slider:
                        _dragSlider = ctrl.Id;
                        _dragTrack = ctrl.Bounds;
                        float t = Math.Clamp((pos.X - ctrl.Bounds.X) / ctrl.Bounds.Width, 0f, 1f);
                        ApplySlider(ctrl.Id, t);
                        return;
                    case CtrlType.Dropdown:
                        _openDd = ctrl.Id;
                        _openDdOrigin = ctrl.Bounds;
                        _openDdOpts = GetDdOptions(ctrl.Id);
                        return;
                }
            }
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            if (_dragSlider != null)
            { _prefs.Save(); _dragSlider = null; }
        }

        // ── Keybind click ──────────────────────────────────────────────────────
        private void HandleKeybindClick(PointF pos)
        {
            var a = ContentArea();
            float y = a.Y + 8f - _scroll;
            y += 22f + 4f; // headers

            string? lastGroup = null;
            foreach (EditorAction action in Enum.GetValues<EditorAction>())
            {
                string group = EditorPreferences.ActionGroup(action);
                if (group != lastGroup) { y += 4f + 22f; lastGroup = group; }
                var pill = new RectangleF(a.Right - 136f, y + 3f, 124f, 18f);
                if (pill.Contains(pos)) { _rebinding = action; _rebindTimer = 0f; return; }
                y += 24f;
            }
        }

        // ── Tags & Layers click ────────────────────────────────────────────────
        private void HandleTLClick(PointF pos)
        {
            if (_tl == null) return;
            var a = ContentArea();
            float y = a.Y + 10f;

            var tabT = new RectangleF(a.X + 10f, y, 84f, 26f);
            var tabL = new RectangleF(a.X + 100f, y, 84f, 26f);
            if (tabT.Contains(pos)) { _tlMode = TLMode.Tags; _tlEditIdx = -2; _tlFocused = false; return; }
            if (tabL.Contains(pos)) { _tlMode = TLMode.Layers; _tlEditIdx = -2; _tlFocused = false; return; }
            y += 32f + 8f;

            var list = _tlMode == TLMode.Tags ? _tl.Tags : _tl.Layers;
            string builtIn = _tlMode == TLMode.Tags ? "Untagged" : "Default";

            for (int i = 0; i < list.Count; i++)
            {
                bool editing = _tlEditIdx == i && _tlFocused;
                bool isBuiltIn = list[i] == builtIn;
                var rowR = new RectangleF(a.X + 4f, y, a.Width - 8f, 28f);

                if (editing)
                {
                    var ok = new RectangleF(rowR.Right - 72f, rowR.Y + 4f, 34f, 20f);
                    var cx = new RectangleF(rowR.Right - 34f, rowR.Y + 4f, 30f, 20f);
                    if (ok.Contains(pos)) { CommitTLEdit(list, i); return; }
                    if (cx.Contains(pos)) { CancelTLEdit(); return; }
                }
                else if (!isBuiltIn)
                {
                    var edit = new RectangleF(rowR.Right - 70f, rowR.Y + 4f, 32f, 20f);
                    var del = new RectangleF(rowR.Right - 34f, rowR.Y + 4f, 30f, 20f);
                    if (edit.Contains(pos)) { _tlEditIdx = i; _tlEditText = list[i]; _tlFocused = true; return; }
                    if (del.Contains(pos)) { DeleteTLItem(list[i]); return; }
                }
                y += 30f;
            }

            y += 8f;
            if (_tlEditIdx == -1 && _tlFocused)
            {
                var addOk = new RectangleF(a.X + 4f + (a.Width - 90f), y, 44f, 26f);
                if (addOk.Contains(pos)) { CommitNewTLItem(); return; }
            }
            else
            {
                var addBtn = new RectangleF(a.X + 4f, y, 130f, 26f);
                if (addBtn.Contains(pos)) { _tlEditIdx = -1; _tlEditText = ""; _tlFocused = true; }
            }
        }

        private void CommitTLEdit(List<string> list, int i)
        {
            string n = _tlEditText.Trim();
            if (!string.IsNullOrEmpty(n) && n != list[i])
            {
                if (_tlMode == TLMode.Tags) _tl!.RenameTag(list[i], n);
                else _tl!.RenameLayer(list[i], n);
            }
            CancelTLEdit();
        }

        private void CommitNewTLItem()
        {
            string n = _tlEditText.Trim();
            if (!string.IsNullOrEmpty(n))
            {
                if (_tlMode == TLMode.Tags) _tl!.AddTag(n);
                else _tl!.AddLayer(n);
            }
            CancelTLEdit();
        }

        private void DeleteTLItem(string name)
        {
            if (_tlMode == TLMode.Tags) _tl!.RemoveTag(name);
            else _tl!.RemoveLayer(name);
        }

        private void CancelTLEdit() { _tlEditIdx = -2; _tlFocused = false; _tlEditText = ""; }

        // ════════════════════════════════════════════════════════════════════════
        //  Keyboard
        // ════════════════════════════════════════════════════════════════════════
        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (!IsVisible) return;

            // Capture rebind
            if (_rebinding.HasValue)
            {
                if (e.Key == Keys.Escape) { _rebinding = null; return; }
                bool ctrl = e.Modifiers.HasFlag(KeyModifiers.Control);
                bool shift = e.Modifiers.HasFlag(KeyModifiers.Shift);
                bool alt = e.Modifiers.HasFlag(KeyModifiers.Alt);
                _prefs.Keybinds[_rebinding.Value] = new Keybind
                { Key = e.Key, Ctrl = ctrl, Shift = shift, Alt = alt };
                _prefs.Save();
                _rebinding = null;
                return;
            }

            // Tags & Layers text editing
            if (_tlFocused && _section == Section.TagsAndLayers)
            {
                if (e.Key == Keys.Escape) { CancelTLEdit(); return; }
                if (e.Key == Keys.Enter)
                {
                    var list = _tlMode == TLMode.Tags ? _tl!.Tags : _tl!.Layers;
                    if (_tlEditIdx >= 0 && _tlEditIdx < list.Count) CommitTLEdit(list, _tlEditIdx);
                    else CommitNewTLItem();
                    return;
                }
                if (e.Key == Keys.Backspace && _tlEditText.Length > 0)
                { _tlEditText = _tlEditText[..^1]; return; }
            }
        }

        public override void OnTextInput(TextInputEventArgs e)
        {
            if (!IsVisible || !_tlFocused || _section != Section.TagsAndLayers) return;
            _tlEditText += e.AsString;
        }

        public override void OnMouseScroll(float delta)
        {
            if (!IsVisible || _section == Section.TagsAndLayers) return;
            _scroll = Math.Clamp(_scroll - delta * 30f, 0f, Math.Max(0f, _contentH - 400f));
        }

        public override void OnUpdate(double dt)
        {
            if (!IsVisible) return;
            if (_rebinding.HasValue)
            {
                _rebindTimer += (float)dt;
                if (_rebindTimer >= 5f) _rebinding = null;
            }
        }
    }
}