using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;

namespace ElintriaEngine.UI.Panels
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  UIEditorPanel  –  WYSIWYG UI layout editor
    //
    //  Layout
    //  ─────────────────────────────────────────────────────────────────────────
    //   Left  (PalW)   Palette buttons + element hierarchy list
    //   Center          Canvas with zoom/pan, grid, rulers, snap, resize handles
    //   Right (PropW)   Polished property inspector (sections, sliders, colour pickers)
    //
    //  Controls
    //  ─────────────────────────────────────────────────────────────────────────
    //   Scroll wheel   Zoom in/out (centred on mouse)
    //   Middle drag    Pan canvas
    //   Drag element   Move (with snap when grid snap is on)
    //   Drag handle    Resize (8 handles: corners + edge midpoints)
    //   Arrow keys     Nudge 1 px (Shift = 10 px)
    //   Delete         Remove selected element
    //   G key          Toggle grid snap
    //   Ctrl+D         Duplicate selected
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class UIEditorPanel : Panel
    {
        // ── Layout ────────────────────────────────────────────────────────────
        private const float PalW = 148f;
        private const float PropW = 220f;
        private const float PAD = 8f;
        private const float RowH = 22f;
        private const float BtnH = 30f;
        private const float FieldH = 22f;

        // ── Document ──────────────────────────────────────────────────────────
        private UIDocument _doc = new();
        public UIDocument Document => _doc;
        public void SetDocument(UIDocument doc) { _doc = doc; _selected = null; _fitDone = false; }

        // Called by EditorLayout when a script compilation finishes so the binding
        // panel immediately shows newly added public methods without restart.
        public void NotifyScriptsReloaded() { _cachedScripts = null; }

        // Cached script list — refreshed each frame if null (cleared on compile)
        private List<(string Name, List<string> Methods)>? _cachedScripts;

        // ── Canvas view transform ─────────────────────────────────────────────
        private float _zoom = 1f;       // 0.25 – 4.0
        private PointF _panOffset = new(0f, 0f); // design-space offset of canvas centre
        private bool _fitDone = false;    // auto-fit zoom on first render

        // ── Canvas state ──────────────────────────────────────────────────────
        private RectangleF _canvasRect;  // screen rect of canvas column

        // ── Selection & interaction ───────────────────────────────────────────
        private UIElement? _selected;
        private UIElementType? _pendingType;

        // Drag/move
        private bool _movingElem;
        private PointF _moveStart;       // screen pos at start of move
        private float _moveElemX0, _moveElemY0;

        // Resize
        private bool _resizingElem;
        private int _resizeHandleIdx; // 0-7
        private PointF _resizeStart;
        private float _resizeElemX0, _resizeElemY0, _resizeElemW0, _resizeElemH0;

        // Middle-button pan
        private bool _panning;
        private PointF _panStart;
        private PointF _panOffset0;

        // ── Settings ──────────────────────────────────────────────────────────
        private bool _snapEnabled = false;
        private float _snapSize = 8f;
        private bool _showGrid = true;
        private bool _showRulers = true;

        // ── Live drag info ────────────────────────────────────────────────────
        private string _dragInfo = "";       // shown as tooltip while dragging

        // ── Property field editing ────────────────────────────────────────────
        private string? _propEditId;
        private string _propBuf = "";
        private Action<string>? _propCommit;

        // ── Cached hit-test rects ─────────────────────────────────────────────
        private readonly List<(RectangleF r, UIElementType t)> _palBtns = new();
        private readonly List<(RectangleF r, UIElement e)> _hierRows = new();
        private readonly List<(RectangleF r, int idx)> _handles = new();

        private PointF _mouse;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color CPal = Color.FromArgb(255, 26, 26, 30);
        private static readonly Color CCanvas = Color.FromArgb(255, 18, 18, 20);
        private static readonly Color CDesign = Color.FromArgb(255, 36, 36, 40);
        private static readonly Color CGrid = Color.FromArgb(22, 200, 200, 200);
        private static readonly Color CGridMaj = Color.FromArgb(40, 200, 200, 200);
        private static readonly Color CRuler = Color.FromArgb(255, 24, 24, 28);
        private static readonly Color CRulerTxt = Color.FromArgb(255, 100, 100, 108);
        private static readonly Color CSelBox = Color.FromArgb(255, 65, 145, 255);
        private static readonly Color CHandle = Color.FromArgb(255, 255, 255, 255);
        private static readonly Color CPending = Color.FromArgb(160, 70, 190, 70);
        private static readonly Color CProp = Color.FromArgb(255, 24, 24, 28);
        private static readonly Color CSect = Color.FromArgb(255, 32, 32, 36);
        private static readonly Color CField = Color.FromArgb(255, 30, 30, 34);
        private static readonly Color CFieldEd = Color.FromArgb(255, 22, 42, 72);
        private static readonly Color CSnap = Color.FromArgb(255, 55, 175, 75);

        private const float RulerH = 18f;   // thickness of ruler strips
        private const float HdH = 6f;    // handle half-size

        // ── Constructor ───────────────────────────────────────────────────────
        public UIEditorPanel(RectangleF bounds) : base("UI Editor", bounds)
        {
            MinWidth = 700f;
            MinHeight = 440f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Render
        // ─────────────────────────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            DrawHeader(r);

            var cr = ContentRect;
            r.FillRect(cr, ColBg);
            r.PushClip(cr);

            float cx = cr.X + PalW;
            float propX = cr.Right - PropW;
            float cw = propX - cx;
            _canvasRect = new RectangleF(cx, cr.Y, cw, cr.Height);

            var palRect = new RectangleF(cr.X, cr.Y, PalW, cr.Height);
            var propRect = new RectangleF(propX, cr.Y, PropW, cr.Height);

            DrawPaletteAndHierarchy(r, palRect);
            DrawCanvas(r);
            DrawProperties(r, propRect);

            // Column separators
            Color sep = Color.FromArgb(255, 48, 48, 54);
            r.DrawLine(new PointF(cx, cr.Y), new PointF(cx, cr.Bottom), sep);
            r.DrawLine(new PointF(propX, cr.Y), new PointF(propX, cr.Bottom), sep);

            r.PopClip();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Palette + Hierarchy
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawPaletteAndHierarchy(IEditorRenderer r, RectangleF rect)
        {
            r.FillRect(rect, CPal);
            _palBtns.Clear();
            _hierRows.Clear();

            float bx = rect.X + PAD;
            float bw = rect.Width - PAD * 2;
            float y = rect.Y + PAD;

            // Settings strip
            float sw = (bw - 4f) / 2f;
            DrawToggleBtn(r, bx, y, sw, 20f, "Grid", _showGrid, () => _showGrid = !_showGrid);
            DrawToggleBtn(r, bx + sw + 4f, y, sw, 20f, "Snap", _snapEnabled, () => _snapEnabled = !_snapEnabled);
            y += 24f;

            // Snap size field (only when snap on)
            if (_snapEnabled)
            {
                bool ed = _propEditId == "snapsize";
                var sf = new RectangleF(bx, y, bw, FieldH);
                r.FillRect(sf, ed ? CFieldEd : CField);
                r.DrawRect(sf, ed ? ColAccent : Color.FromArgb(255, 50, 50, 56));
                r.DrawText($"Snap: {_snapSize:F0}px",
                    new PointF(sf.X + 6f, sf.Y + 4f), ColTextDim, 9f);
                if (ed) r.DrawText(_propBuf + "|", new PointF(sf.X + 50f, sf.Y + 4f), ColText, 9f);
                y += FieldH + 4f;
            }

            y += 4f;
            r.DrawLine(new PointF(bx, y), new PointF(rect.Right - PAD, y), Color.FromArgb(255, 45, 45, 50));
            y += 8f;

            r.DrawText("ELEMENTS", new PointF(bx, y), ColTextDim, 8f);
            y += 14f;

            // Palette buttons
            var defs = new[]
            {
                (UIElementType.Text,       "T   Text",      Color.FromArgb(255, 68, 165, 235)),
                (UIElementType.Button,     "□   Button",    Color.FromArgb(255, 68, 135, 215)),
                (UIElementType.TextField,  "▤   TextField", Color.FromArgb(255, 55, 165, 120)),
                (UIElementType.Scrollbar,  "▬   Scrollbar", Color.FromArgb(255, 195, 135, 45)),
            };

            foreach (var (t, label, ac) in defs)
            {
                bool pend = _pendingType == t;
                bool hov = !pend && new RectangleF(bx, y, bw, BtnH).Contains(_mouse);
                var btn = new RectangleF(bx, y, bw, BtnH);

                r.FillRect(btn, pend ? Color.FromArgb(255, ac.R / 3, ac.G / 3, ac.B / 3 + 10)
                               : hov ? Color.FromArgb(255, 42, 42, 48)
                                      : Color.FromArgb(255, 34, 34, 38));
                r.DrawRect(btn, pend ? ac : Color.FromArgb(255, 50, 50, 56));
                if (pend) r.FillRect(new RectangleF(btn.X, btn.Y, 3f, btn.Height), ac);
                r.DrawText(label, new PointF(btn.X + 10f, btn.Y + (BtnH - 11f) / 2f), pend ? ac : ColText, 10f);

                _palBtns.Add((btn, t));
                y += BtnH + 3f;
            }

            y += 6f;
            r.DrawLine(new PointF(bx, y), new PointF(rect.Right - PAD, y), Color.FromArgb(255, 45, 45, 50));
            y += 8f;

            r.DrawText($"HIERARCHY  ({_doc.Elements.Count})", new PointF(bx, y), ColTextDim, 8f);
            y += 14f;

            r.PushClip(new RectangleF(rect.X, y, rect.Width, rect.Bottom - y - PAD));
            foreach (var elem in _doc.Elements)
            {
                bool sel = elem == _selected;
                bool hov = !sel && new RectangleF(bx, y, bw, RowH).Contains(_mouse);
                var row = new RectangleF(rect.X, y, rect.Width, RowH);

                r.FillRect(row, sel ? Color.FromArgb(255, 38, 60, 105)
                               : hov ? Color.FromArgb(255, 36, 36, 42)
                                      : Color.Transparent);

                Color ic = GetTypeColor(elem.ElementType);
                r.FillRect(new RectangleF(bx, y + 8f, 3f, 6f), ic);
                r.DrawText(elem.Name, new PointF(bx + 7f, y + 5f),
                    sel ? Color.White : ColText, 9f);

                _hierRows.Add((row, elem));
                y += RowH;
            }
            r.PopClip();
        }

        private void DrawToggleBtn(IEditorRenderer r, float x, float y, float w, float h,
            string label, bool active, Action toggle)
        {
            bool hov = new RectangleF(x, y, w, h).Contains(_mouse);
            r.FillRect(new RectangleF(x, y, w, h),
                active ? Color.FromArgb(255, 40, 80, 40)
                : hov ? Color.FromArgb(255, 40, 40, 46)
                       : Color.FromArgb(255, 32, 32, 36));
            r.DrawRect(new RectangleF(x, y, w, h),
                active ? Color.FromArgb(255, 55, 160, 55) : Color.FromArgb(255, 50, 50, 56));
            r.DrawText(label, new PointF(x + 5f, y + 3f),
                active ? Color.FromArgb(255, 100, 220, 100) : ColTextDim, 9f);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Canvas
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawCanvas(IEditorRenderer r)
        {
            var cr = _canvasRect;
            r.FillRect(cr, CCanvas);
            r.PushClip(cr);

            // Auto-fit: scale design to fill canvas on first render or when reset
            if (!_fitDone && cr.Width > 10f && cr.Height > 10f)
            {
                float fitZ = Math.Min(
                    (cr.Width - 32f) / _doc.DesignWidth,
                    (cr.Height - 32f) / _doc.DesignHeight);
                _zoom = Math.Clamp(fitZ, 0.1f, 4f);
                _panOffset = new PointF(0, 0);
                _fitDone = true;
            }

            // Origin of the design rect on screen
            float ox = cr.X + cr.Width / 2f + _panOffset.X;
            float oy = cr.Y + cr.Height / 2f + _panOffset.Y;
            float dw = _doc.DesignWidth * _zoom;
            float dh = _doc.DesignHeight * _zoom;
            float dx = ox - dw / 2f;
            float dy = oy - dh / 2f;

            var designRect = new RectangleF(dx, dy, dw, dh);

            // Drop shadow
            r.FillRect(new RectangleF(dx + 5, dy + 5, dw, dh), Color.FromArgb(80, 0, 0, 0));

            // Design area
            r.FillRect(designRect, CDesign);
            r.DrawRect(designRect, Color.FromArgb(255, 65, 65, 72));

            if (_showGrid) DrawGrid(r, designRect);

            // Elements
            r.PushClip(designRect);
            foreach (var e in _doc.Elements)
                if (e.Visible) DrawElement(r, e, dx, dy);
            r.PopClip();

            // Selection
            if (_selected != null)
            {
                var sr = ElemScreen(_selected, dx, dy);
                DrawSelectionBox(r, sr);
                DrawResizeHandles(r, sr);
            }

            // Placement ghost
            if (_pendingType.HasValue && cr.Contains(_mouse))
            {
                var sz = DefaultSize(_pendingType.Value);
                float ex = (_mouse.X - dx) / _zoom - sz.Width / 2f;
                float ey = (_mouse.Y - dy) / _zoom - sz.Height / 2f;
                if (_snapEnabled) { ex = Snap(ex); ey = Snap(ey); }
                var gr = ElemScreen(ex, ey, sz.Width, sz.Height, dx, dy);
                r.FillRect(gr, CPending);
                r.DrawRect(gr, Color.FromArgb(200, 100, 215, 100));
                r.DrawText("Click to place",
                    new PointF(gr.X + 4f, gr.Bottom + 5f), ColTextDim, 9f);
            }

            // Rulers
            if (_showRulers)
            {
                DrawRuler(r, cr, designRect, horizontal: true);
                DrawRuler(r, cr, designRect, horizontal: false);
            }

            // Live drag info tooltip
            if (!string.IsNullOrEmpty(_dragInfo))
            {
                float iw = _dragInfo.Length * 6.5f + 16f;
                float ix = Math.Clamp(_mouse.X + 14f, cr.X, cr.Right - iw - 4f);
                float iy = _mouse.Y - 22f;
                var ir = new RectangleF(ix, iy, iw, 18f);
                r.FillRect(ir, Color.FromArgb(220, 20, 20, 24));
                r.DrawRect(ir, CSelBox);
                r.DrawText(_dragInfo, new PointF(ir.X + 8f, ir.Y + 3f), Color.White, 9f);
            }

            // Zoom label
            r.DrawText($"{_zoom * 100f:F0}%",
                new PointF(cr.Right - 42f, cr.Bottom - 16f), ColTextDim, 9f);

            // Design size label
            r.DrawText($"{_doc.DesignWidth} × {_doc.DesignHeight}",
                new PointF(dx + 4f, dy - 14f), ColTextDim, 8f);

            r.PopClip();
        }

        private void DrawGrid(IEditorRenderer r, RectangleF dr)
        {
            // Minor grid at snapSize, major grid at 5× snapSize
            float minor = _snapSize * _zoom;
            float major = minor * 5f;
            if (minor < 4f) minor = major; // skip minor if too dense

            if (minor >= 4f)
            {
                for (float gx = dr.X; gx <= dr.Right; gx += minor)
                    r.DrawLine(new PointF(gx, dr.Y), new PointF(gx, dr.Bottom), CGrid);
                for (float gy = dr.Y; gy <= dr.Bottom; gy += minor)
                    r.DrawLine(new PointF(dr.X, gy), new PointF(dr.Right, gy), CGrid);
            }

            if (major >= 8f)
            {
                for (float gx = dr.X; gx <= dr.Right; gx += major)
                    r.DrawLine(new PointF(gx, dr.Y), new PointF(gx, dr.Bottom), CGridMaj);
                for (float gy = dr.Y; gy <= dr.Bottom; gy += major)
                    r.DrawLine(new PointF(dr.X, gy), new PointF(dr.Right, gy), CGridMaj);
            }
        }

        private void DrawRuler(IEditorRenderer r, RectangleF cr, RectangleF dr, bool horizontal)
        {
            if (horizontal)
            {
                var ru = new RectangleF(cr.X + RulerH, cr.Y, cr.Width - RulerH, RulerH);
                r.FillRect(ru, CRuler);
                r.DrawLine(new PointF(ru.X, ru.Bottom), new PointF(ru.Right, ru.Bottom),
                    Color.FromArgb(255, 48, 48, 54));

                float step = NiceStep();
                float startD = (float)Math.Floor((ru.X - dr.X) / (_zoom * step)) * step;
                for (float d = startD; d * _zoom + dr.X < ru.Right; d += step)
                {
                    float sx = dr.X + d * _zoom;
                    if (sx < ru.X || sx > ru.Right) continue;
                    r.DrawLine(new PointF(sx, ru.Y + ru.Height - 6f),
                               new PointF(sx, ru.Bottom), CRulerTxt);
                    r.DrawText(((int)d).ToString(), new PointF(sx + 2f, ru.Y + 3f), CRulerTxt, 7f);
                }
            }
            else
            {
                var ru = new RectangleF(cr.X, cr.Y + RulerH, RulerH, cr.Height - RulerH);
                r.FillRect(ru, CRuler);
                r.DrawLine(new PointF(ru.Right, ru.Y), new PointF(ru.Right, ru.Bottom),
                    Color.FromArgb(255, 48, 48, 54));

                float step = NiceStep();
                float startD = (float)Math.Floor((ru.Y - dr.Y) / (_zoom * step)) * step;
                for (float d = startD; d * _zoom + dr.Y < ru.Bottom; d += step)
                {
                    float sy = dr.Y + d * _zoom;
                    if (sy < ru.Y || sy > ru.Bottom) continue;
                    r.DrawLine(new PointF(ru.Right - 6f, sy), new PointF(ru.Right, sy), CRulerTxt);
                    r.DrawText(((int)d).ToString(), new PointF(ru.X + 1f, sy + 2f), CRulerTxt, 7f);
                }
            }

            // Corner square
            var corner = new RectangleF(cr.X, cr.Y, RulerH, RulerH);
            r.FillRect(corner, CRuler);
            r.DrawText("px", new PointF(corner.X + 2f, corner.Y + 4f), CRulerTxt, 7f);
        }

        private float NiceStep()
        {
            float[] candidates = { 1, 2, 5, 10, 20, 50, 100, 200, 500 };
            float target = 50f / _zoom; // aim for ~50px between labels
            foreach (float c in candidates)
                if (c >= target) return c;
            return 500f;
        }

        private void DrawElement(IEditorRenderer r, UIElement e, float ox, float oy)
        {
            var sr = ElemScreen(e, ox, oy);
            bool sel = e == _selected;

            switch (e)
            {
                case UITextElement te:
                    float fs = Math.Max(7f, te.FontSize * _zoom);
                    r.DrawText(te.Text,
                        new PointF(sr.X + 2f, sr.Y + (sr.Height - fs) / 2f), te.Color, fs);
                    // Bounding box only when selected
                    if (sel) r.DrawRect(sr, Color.FromArgb(40, 255, 255, 255));
                    break;

                case UIButtonElement be:
                    r.FillRect(sr, be.BackgroundColor);
                    r.DrawRect(sr, Darken(be.BackgroundColor, 0.55f));
                    float bfs = Math.Max(7f, be.FontSize * _zoom);
                    float tw = be.Text.Length * bfs * 0.54f;
                    r.DrawText(be.Text,
                        new PointF(sr.X + (sr.Width - tw) / 2f, sr.Y + (sr.Height - bfs) / 2f),
                        be.TextColor, bfs);
                    break;

                case UITextFieldElement fe:
                    r.FillRect(sr, fe.BackgroundColor);
                    r.DrawRect(sr, fe.BorderColor);
                    float ffs = Math.Max(7f, fe.FontSize * _zoom);
                    bool empty = string.IsNullOrEmpty(fe.Text);
                    r.PushClip(sr);
                    r.DrawText(empty ? fe.Placeholder : fe.Text,
                        new PointF(sr.X + 5f, sr.Y + (sr.Height - ffs) / 2f),
                        empty ? Color.FromArgb(100, fe.TextColor.R, fe.TextColor.G, fe.TextColor.B)
                              : fe.TextColor, ffs);
                    r.PopClip();
                    break;

                case UIScrollbarElement se:
                    r.FillRect(sr, se.TrackColor);
                    r.DrawRect(sr, Darken(se.TrackColor, 0.5f));
                    float range = se.MaxValue - se.MinValue;
                    float t = range > 0f ? (se.Value - se.MinValue) / range : 0f;
                    RectangleF th;
                    if (se.Orientation == UIScrollbarOrientation.Horizontal)
                    {
                        float tw2 = sr.Width * se.ThumbSize;
                        th = new RectangleF(sr.X + (sr.Width - tw2) * t, sr.Y + 1, tw2, sr.Height - 2);
                    }
                    else
                    {
                        float ht = sr.Height * se.ThumbSize;
                        th = new RectangleF(sr.X + 1, sr.Y + (sr.Height - ht) * t, sr.Width - 2, ht);
                    }
                    r.FillRect(th, se.ThumbColor);
                    break;
            }
        }

        private void DrawSelectionBox(IEditorRenderer r, RectangleF sr)
        {
            // Dashed selection outline
            r.DrawRect(new RectangleF(sr.X - 1f, sr.Y - 1f, sr.Width + 2f, sr.Height + 2f),
                CSelBox, 1.5f);
            // Alignment cross-hairs (centre dot)
            r.FillRect(new RectangleF(sr.X + sr.Width / 2f - 2f, sr.Y + sr.Height / 2f - 2f, 4f, 4f),
                Color.FromArgb(180, CSelBox.R, CSelBox.G, CSelBox.B));
        }

        private void DrawResizeHandles(IEditorRenderer r, RectangleF sr)
        {
            _handles.Clear();
            var pts = HandlePoints(sr);
            for (int i = 0; i < pts.Length; i++)
            {
                var hr = new RectangleF(pts[i].X - HdH, pts[i].Y - HdH, HdH * 2, HdH * 2);
                r.FillRect(hr, CHandle);
                r.DrawRect(hr, CSelBox);
                _handles.Add((hr, i));
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Properties panel
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawProperties(IEditorRenderer r, RectangleF rect)
        {
            r.FillRect(rect, CProp);
            r.PushClip(rect);

            float lx = rect.X + PAD;
            float fw = rect.Width - PAD * 2;
            float y = rect.Y + PAD;

            if (_selected == null)
            {
                r.DrawText("Nothing selected", new PointF(lx, y + 4f), ColTextDim, 10f);
                r.PopClip();
                return;
            }

            UIElement sel = _selected;

            // ── Header ────────────────────────────────────────────────────────
            Color tc = GetTypeColor(sel.ElementType);
            r.FillRect(new RectangleF(rect.X, y, rect.Width, 28f), CSect);
            r.FillRect(new RectangleF(rect.X, y, 3f, 28f), tc);
            r.DrawText(sel.ElementType.ToString().ToUpper(),
                new PointF(lx + 6f, y + 5f), tc, 10f);

            // Delete button (top-right of header)
            var delB = new RectangleF(rect.Right - 26f, y + 4f, 20f, 20f);
            bool dh = delB.Contains(_mouse);
            r.FillRect(delB, dh ? Color.FromArgb(255, 180, 45, 45) : Color.FromArgb(255, 120, 35, 35));
            r.DrawText("✕", new PointF(delB.X + 4f, delB.Y + 3f), Color.White, 9f);
            y += 32f;

            // ── Layout section ────────────────────────────────────────────────
            DrawPropSection(r, rect, "LAYOUT", ref y);
            DrawPropXY(r, lx, fw, sel.Id + "_x", sel.Id + "_y",
                "Position", sel.X, sel.Y,
                vx => sel.X = vx, vy => sel.Y = vy, ref y);
            DrawPropXY(r, lx, fw, sel.Id + "_w", sel.Id + "_h",
                "Size", sel.Width, sel.Height,
                vx => sel.Width = Math.Max(2f, vx),
                vy => sel.Height = Math.Max(2f, vy), ref y);
            DrawPropBool(r, lx, fw, "Visible", sel.Id + "_vis", sel.Visible,
                v => sel.Visible = v, ref y);

            // ── Element-specific section ──────────────────────────────────────
            switch (sel)
            {
                case UITextElement te:
                    DrawPropSection(r, rect, "TEXT", ref y);
                    DrawPropString(r, lx, fw, "Content", te.Id + "_txt", te.Text, v => te.Text = v, ref y);
                    DrawPropSlider(r, lx, fw, "Font Size", te.Id + "_fs", te.FontSize, 4f, 72f, v => te.FontSize = v, ref y);
                    DrawPropColor(r, lx, fw, "Color", te.Id + "_col", te.Color, v => te.Color = v, ref y);
                    DrawPropEnum(r, lx, fw, "Align", te.Id + "_aln",
                        new[] { "Left", "Center", "Right" }, (int)te.Alignment,
                        v => te.Alignment = (UITextAlignment)v, ref y);
                    break;

                case UIButtonElement be:
                    DrawPropSection(r, rect, "BUTTON", ref y);
                    DrawPropString(r, lx, fw, "Label", be.Id + "_txt", be.Text, v => be.Text = v, ref y);
                    DrawPropSlider(r, lx, fw, "Font Size", be.Id + "_fs", be.FontSize, 4f, 36f, v => be.FontSize = v, ref y);
                    DrawPropSection(r, rect, "COLORS", ref y);
                    DrawPropColor(r, lx, fw, "Background", be.Id + "_bg", be.BackgroundColor, v => be.BackgroundColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Text", be.Id + "_tc", be.TextColor, v => be.TextColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Hover", be.Id + "_hov", be.HoverColor, v => be.HoverColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Pressed", be.Id + "_prs", be.PressedColor, v => be.PressedColor = v, ref y);
                    DrawPropSection(r, rect, "EVENTS", ref y);
                    DrawPropString(r, lx, fw, "OnClick", be.Id + "_evt", be.OnClickEvent, v => be.OnClickEvent = v, ref y);
                    DrawPropSection(r, rect, "SCRIPT BINDING", ref y);
                    DrawPropString(r, lx, fw, "Script", be.Id + "_scr", be.TargetScriptName, v => be.TargetScriptName = v, ref y);
                    DrawPropString(r, lx, fw, "Method", be.Id + "_mth", be.TargetMethodName, v => be.TargetMethodName = v, ref y);
                    DrawScriptBindingHints(r, lx, fw, be, ref y);
                    break;

                case UITextFieldElement fe:
                    DrawPropSection(r, rect, "TEXT FIELD", ref y);
                    DrawPropString(r, lx, fw, "Placeholder", fe.Id + "_ph", fe.Placeholder, v => fe.Placeholder = v, ref y);
                    DrawPropString(r, lx, fw, "Value", fe.Id + "_txt", fe.Text, v => fe.Text = v, ref y);
                    DrawPropSlider(r, lx, fw, "Font Size", fe.Id + "_fs", fe.FontSize, 4f, 36f, v => fe.FontSize = v, ref y);
                    DrawPropSection(r, rect, "COLORS", ref y);
                    DrawPropColor(r, lx, fw, "Background", fe.Id + "_bg", fe.BackgroundColor, v => fe.BackgroundColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Text", fe.Id + "_tc", fe.TextColor, v => fe.TextColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Border", fe.Id + "_brc", fe.BorderColor, v => fe.BorderColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Focus", fe.Id + "_fbc", fe.FocusBorderColor, v => fe.FocusBorderColor = v, ref y);
                    break;

                case UIScrollbarElement se:
                    DrawPropSection(r, rect, "SCROLLBAR", ref y);
                    DrawPropEnum(r, lx, fw, "Orientation", se.Id + "_ori",
                        new[] { "Horizontal", "Vertical" }, (int)se.Orientation,
                        v => se.Orientation = (UIScrollbarOrientation)v, ref y);
                    DrawPropSlider(r, lx, fw, "Min", se.Id + "_min", se.MinValue, -1000f, se.MaxValue, v => se.MinValue = v, ref y);
                    DrawPropSlider(r, lx, fw, "Max", se.Id + "_max", se.MaxValue, se.MinValue, 1000f, v => se.MaxValue = v, ref y);
                    DrawPropSlider(r, lx, fw, "Value", se.Id + "_val", se.Value, se.MinValue, se.MaxValue, v => se.Value = v, ref y);
                    DrawPropSlider(r, lx, fw, "Thumb", se.Id + "_ts", se.ThumbSize, 0.05f, 1f, v => se.ThumbSize = v, ref y);
                    DrawPropSection(r, rect, "COLORS", ref y);
                    DrawPropColor(r, lx, fw, "Track", se.Id + "_trc", se.TrackColor, v => se.TrackColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Thumb", se.Id + "_thc", se.ThumbColor, v => se.ThumbColor = v, ref y);
                    break;
            }

            // ── Layer order ───────────────────────────────────────────────────
            DrawPropSection(r, rect, "LAYER", ref y);
            float hw = (fw - 4f) / 2f;
            DrawSmallBtn(r, lx, y, hw, 20f, "▲ Forward",
                () => _doc.BringForward(_selected));
            DrawSmallBtn(r, lx + hw + 4f, y, hw, 20f, "▼ Back",
                () => _doc.SendBackward(_selected));
            y += 24f;

            r.PopClip();
        }

        // ── Section header ─────────────────────────────────────────────────────
        private void DrawPropSection(IEditorRenderer r, RectangleF rect, string title, ref float y)
        {
            r.FillRect(new RectangleF(rect.X, y, rect.Width, 18f), CSect);
            r.DrawText(title, new PointF(rect.X + PAD + 2f, y + 3f), ColTextDim, 8f);
            y += 20f;
        }

        // ── X/Y combined row ──────────────────────────────────────────────────
        private void DrawPropXY(IEditorRenderer r, float lx, float fw,
            string idx, string idy, string label,
            float vx, float vy,
            Action<float> setX, Action<float> setY, ref float y)
        {
            r.DrawText(label, new PointF(lx, y + 5f), ColTextDim, 9f);
            float hw = (fw - 88f - 4f) / 2f;
            float ox = lx + 88f;

            DrawAxisField(r, ox, y, hw, "X", idx, vx, v => setX(v));
            DrawAxisField(r, ox + hw + 4f, y, hw, "Y", idy, vy, v => setY(v));
            y += FieldH + 3f;
        }

        private void DrawAxisField(IEditorRenderer r, float x, float y, float w,
            string axis, string id, float value, Action<float> setter)
        {
            bool ed = _propEditId == id;
            bool hov = !ed && new RectangleF(x, y, w, FieldH).Contains(_mouse);

            // Axis colour bar
            Color axCol = axis == "X" ? Color.FromArgb(255, 185, 48, 48)
                                      : Color.FromArgb(255, 48, 165, 48);
            r.FillRect(new RectangleF(x, y, w, FieldH),
                ed ? CFieldEd : hov ? Color.FromArgb(255, 36, 36, 42) : CField);
            r.DrawRect(new RectangleF(x, y, w, FieldH),
                ed ? ColAccent : hov ? Color.FromArgb(255, 60, 60, 68) : Color.FromArgb(255, 46, 46, 52));
            r.FillRect(new RectangleF(x, y, 4f, FieldH), axCol);
            r.DrawText(ed ? _propBuf + "|" : $"{value:F1}",
                new PointF(x + 8f, y + 4f),
                ed ? Color.White : ColText, 9f);
        }

        private void DrawPropString(IEditorRenderer r, float lx, float fw,
            string label, string id, string value, Action<string> setter, ref float y)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 9f);
            bool ed = _propEditId == id;
            var fr = new RectangleF(lx + 76f, y, fw - 76f, FieldH);
            bool hov = !ed && fr.Contains(_mouse);
            r.FillRect(fr, ed ? CFieldEd : hov ? Color.FromArgb(255, 36, 36, 42) : CField);
            r.DrawRect(fr, ed ? ColAccent : Color.FromArgb(255, 46, 46, 52));
            r.DrawText(ed ? _propBuf + "|" : (value.Length > 0 ? value : " "),
                new PointF(fr.X + 5f, fr.Y + 4f), ed ? Color.White : ColText, 9f);
            y += FieldH + 3f;
        }

        private void DrawPropSlider(IEditorRenderer r, float lx, float fw,
            string label, string id, float value, float min, float max,
            Action<float> setter, ref float y)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 9f);
            float ox = lx + 76f;
            float sw = fw - 76f - 52f;

            // Track
            var track = new RectangleF(ox, y + 7f, sw, 8f);
            r.FillRect(track, Color.FromArgb(255, 35, 35, 40));
            r.DrawRect(track, Color.FromArgb(255, 50, 50, 56));

            // Fill
            float frac = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * frac, track.Height),
                ColAccent);

            // Thumb
            float tx = track.X + track.Width * frac;
            r.FillRect(new RectangleF(tx - 4f, y + 5f, 8f, 12f), Color.White);
            r.DrawRect(new RectangleF(tx - 4f, y + 5f, 8f, 12f), ColAccent);

            // Value text field
            bool ed = _propEditId == id;
            var vf = new RectangleF(ox + sw + 4f, y, 48f, FieldH);
            bool hov = !ed && vf.Contains(_mouse);
            r.FillRect(vf, ed ? CFieldEd : hov ? Color.FromArgb(255, 36, 36, 42) : CField);
            r.DrawRect(vf, ed ? ColAccent : Color.FromArgb(255, 46, 46, 52));
            r.DrawText(ed ? _propBuf + "|" : $"{value:F1}",
                new PointF(vf.X + 4f, vf.Y + 4f), ed ? Color.White : ColText, 9f);
            y += FieldH + 5f;
        }

        private void DrawPropBool(IEditorRenderer r, float lx, float fw,
            string label, string id, bool value, Action<bool> setter, ref float y)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 9f);
            var cb = new RectangleF(lx + 76f, y + 3f, 16f, 16f);
            r.FillRect(cb, value ? Color.FromArgb(255, 45, 145, 45) : CField);
            r.DrawRect(cb, value ? Color.FromArgb(255, 55, 170, 55) : Color.FromArgb(255, 50, 50, 56));
            if (value) r.DrawText("✓", new PointF(cb.X + 2f, cb.Y + 1f), Color.White, 10f);
            y += FieldH + 3f;
        }

        private void DrawPropColor(IEditorRenderer r, float lx, float fw,
            string label, string id, Color value, Action<Color> setter, ref float y)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 9f);
            // Swatch
            r.FillRect(new RectangleF(lx + 76f, y + 2f, 18f, 18f), value);
            r.DrawRect(new RectangleF(lx + 76f, y + 2f, 18f, 18f),
                Color.FromArgb(255, 65, 65, 72));
            // Hex field
            bool ed = _propEditId == id;
            var fr = new RectangleF(lx + 98f, y, fw - 98f, FieldH);
            bool hov = !ed && fr.Contains(_mouse);
            r.FillRect(fr, ed ? CFieldEd : hov ? Color.FromArgb(255, 36, 36, 42) : CField);
            r.DrawRect(fr, ed ? ColAccent : Color.FromArgb(255, 46, 46, 52));
            string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            r.DrawText(ed ? "#" + _propBuf + "|" : hex,
                new PointF(fr.X + 5f, fr.Y + 4f), ed ? Color.White : ColText, 9f);
            y += FieldH + 3f;
        }

        private void DrawPropEnum(IEditorRenderer r, float lx, float fw,
            string label, string id, string[] opts, int cur, Action<int> setter, ref float y)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 9f);
            float ox = lx + 76f;
            float bw = (fw - 76f - (opts.Length - 1) * 2f) / opts.Length;
            for (int i = 0; i < opts.Length; i++)
            {
                bool sel = i == cur;
                var btn = new RectangleF(ox + i * (bw + 2f), y, bw, FieldH);
                r.FillRect(btn, sel ? ColAccent : CField);
                r.DrawRect(btn, sel ? ColAccent : Color.FromArgb(255, 50, 50, 56));
                r.DrawText(opts[i], new PointF(btn.X + 3f, btn.Y + 4f),
                    sel ? Color.White : ColText, 8f);
            }
            y += FieldH + 3f;
        }

        private void DrawSmallBtn(IEditorRenderer r, float x, float y, float w, float h,
            string label, Action onClick)
        {
            bool hov = new RectangleF(x, y, w, h).Contains(_mouse);
            r.FillRect(new RectangleF(x, y, w, h),
                hov ? Color.FromArgb(255, 48, 48, 58) : Color.FromArgb(255, 36, 36, 42));
            r.DrawRect(new RectangleF(x, y, w, h), Color.FromArgb(255, 54, 54, 62));
            r.DrawText(label, new PointF(x + 6f, y + 3f), ColText, 9f);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Input
        // ═══════════════════════════════════════════════════════════════════════
        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;
            if (!IsVisible || !ContainsPoint(pos)) return;

            if (HeaderRect.Contains(pos)) { base.OnMouseDown(e, pos); return; }
            if (_propEditId != null) CommitPropEdit();

            // ── Middle button pan ─────────────────────────────────────────────
            if (e.Button == MouseButton.Middle && _canvasRect.Contains(pos))
            {
                _panning = true;
                _panStart = pos;
                _panOffset0 = _panOffset;
                return;
            }

            if (e.Button != MouseButton.Left) return;

            // ── Palette ───────────────────────────────────────────────────────
            foreach (var (btn, t) in _palBtns)
                if (btn.Contains(pos)) { _pendingType = _pendingType == t ? null : t; return; }

            // ── Hierarchy ─────────────────────────────────────────────────────
            foreach (var (row, elem) in _hierRows)
                if (row.Contains(pos)) { _selected = elem; return; }

            // ── Properties panel ──────────────────────────────────────────────
            if (pos.X > _canvasRect.Right)
            {
                HandlePropClick(pos);
                return;
            }

            // ── Canvas ────────────────────────────────────────────────────────
            if (_canvasRect.Contains(pos))
            {
                GetTransform(out float scale, out float ox, out float oy);

                // Place pending element
                if (_pendingType.HasValue)
                {
                    float dx = (pos.X - ox) / scale;
                    float dy = (pos.Y - oy) / scale;
                    if (_snapEnabled) { dx = Snap(dx); dy = Snap(dy); }
                    PlaceElement(_pendingType.Value, dx, dy, scale);
                    _pendingType = null;
                    return;
                }

                // Resize handle hit?
                foreach (var (hr, idx) in _handles)
                {
                    if (hr.Contains(pos) && _selected != null)
                    {
                        _resizingElem = true;
                        _resizeHandleIdx = idx;
                        _resizeStart = pos;
                        _resizeElemX0 = _selected.X;
                        _resizeElemY0 = _selected.Y;
                        _resizeElemW0 = _selected.Width;
                        _resizeElemH0 = _selected.Height;
                        return;
                    }
                }

                // Select / move
                float ex = (pos.X - ox) / scale;
                float ey = (pos.Y - oy) / scale;

                UIElement? hit = null;
                for (int i = _doc.Elements.Count - 1; i >= 0; i--)
                    if (_doc.Elements[i].Rect.Contains(ex, ey)) { hit = _doc.Elements[i]; break; }

                if (hit != null)
                {
                    _selected = hit;
                    _movingElem = true;
                    _moveStart = pos;
                    _moveElemX0 = hit.X;
                    _moveElemY0 = hit.Y;
                }
                else
                {
                    _selected = null;
                }
            }

            base.OnMouseDown(e, pos);
        }

        public override void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            if (!IsVisible) return;

            if (_panning)
            {
                _panOffset = new PointF(
                    _panOffset0.X + pos.X - _panStart.X,
                    _panOffset0.Y + pos.Y - _panStart.Y);
                return;
            }

            GetTransform(out float scale, out _, out _);

            if (_movingElem && _selected != null)
            {
                float dx = (pos.X - _moveStart.X) / scale;
                float dy = (pos.Y - _moveStart.Y) / scale;
                float nx = _moveElemX0 + dx;
                float ny = _moveElemY0 + dy;
                if (_snapEnabled) { nx = Snap(nx); ny = Snap(ny); }
                _selected.X = nx;
                _selected.Y = ny;
                _dragInfo = $"X: {nx:F0}  Y: {ny:F0}";
            }
            else if (_resizingElem && _selected != null)
            {
                float dx = (pos.X - _resizeStart.X) / scale;
                float dy = (pos.Y - _resizeStart.Y) / scale;
                if (_snapEnabled) { dx = Snap(dx + _resizeElemX0) - _resizeElemX0; dy = Snap(dy + _resizeElemY0) - _resizeElemY0; }
                ApplyHandle(_selected, _resizeHandleIdx, dx, dy,
                    _resizeElemX0, _resizeElemY0, _resizeElemW0, _resizeElemH0);
                _dragInfo = $"W: {_selected.Width:F0}  H: {_selected.Height:F0}";
            }
            else
            {
                _dragInfo = "";
                base.OnMouseMove(pos);
            }
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _movingElem = false;
            _resizingElem = false;
            _panning = false;
            _dragInfo = "";
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseScroll(float delta)
        {
            if (!IsVisible || !_canvasRect.Contains(_mouse)) return;

            // Zoom centred on mouse
            GetTransform(out float oldScale, out float ox, out float oy);

            float oldZoom = _zoom;
            _zoom = Math.Clamp(_zoom * (delta > 0 ? 1.12f : 0.89f), 0.1f, 8f);
            float newZoom = _zoom;

            // Adjust pan so the point under the cursor stays fixed
            float mDesX = (_mouse.X - ox) / oldZoom;
            float mDesY = (_mouse.Y - oy) / oldZoom;
            float newOx = _canvasRect.X + _canvasRect.Width / 2f + _panOffset.X;
            float newOy = _canvasRect.Y + _canvasRect.Height / 2f + _panOffset.Y;
            _panOffset = new PointF(
                _panOffset.X - mDesX * (newZoom - oldZoom),
                _panOffset.Y - mDesY * (newZoom - oldZoom));
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (!IsVisible) return;

            if (_propEditId != null)
            {
                switch (e.Key)
                {
                    case Keys.Enter: CommitPropEdit(); return;
                    case Keys.Escape: _propEditId = null; return;
                    case Keys.Backspace when _propBuf.Length > 0:
                        _propBuf = _propBuf[..^1]; return;
                }
                return;
            }

            // Canvas shortcuts
            if (_selected != null)
            {
                float step = e.Shift ? 10f : (_snapEnabled ? _snapSize : 1f);
                switch (e.Key)
                {
                    case Keys.Delete: _doc.Remove(_selected); _selected = null; return;
                    case Keys.Up: _selected.Y -= step; return;
                    case Keys.Down: _selected.Y += step; return;
                    case Keys.Left: _selected.X -= step; return;
                    case Keys.Right: _selected.X += step; return;
                }

                if (e.Control && e.Key == Keys.D)
                {
                    // Duplicate
                    var clone = _selected.Clone();
                    clone.X += 10f; clone.Y += 10f;
                    clone.Id = Guid.NewGuid().ToString("N")[..8];
                    clone.Name += "_copy";
                    _doc.Add(clone);
                    _selected = clone;
                }
            }

            if (e.Key == Keys.G) _snapEnabled = !_snapEnabled;
            if (e.Key == Keys.F) { _fitDone = false; } // re-fit view
        }

        public override void OnTextInput(TextInputEventArgs e)
        {
            if (_propEditId != null) _propBuf += e.AsString;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Property click handling
        // ─────────────────────────────────────────────────────────────────────
        private void HandlePropClick(PointF pos)
        {
            if (_selected == null) return;

            var cr = ContentRect;
            float lx = cr.Right - PropW + PAD;
            float fw = PropW - PAD * 2;
            float y = cr.Y + PAD;
            UIElement sel = _selected;

            // Delete button
            var delB = new RectangleF(cr.Right - 26f, y + 4f, 20f, 20f);
            if (delB.Contains(pos))
            {
                _doc.Remove(_selected);
                _selected = null;
                return;
            }
            y += 32f;

            // Layout section header + 3 rows
            y += 20f; // section header
            HandleXYClick(pos, lx, fw, sel.Id + "_x", sel.Id + "_y",
                sel.X, sel.Y, v => sel.X = v, v => sel.Y = v, ref y);
            HandleXYClick(pos, lx, fw, sel.Id + "_w", sel.Id + "_h",
                sel.Width, sel.Height,
                v => sel.Width = Math.Max(2f, v), v => sel.Height = Math.Max(2f, v), ref y);

            // Visible bool
            var cb = new RectangleF(lx + 76f, y + 3f, 16f, 16f);
            if (cb.Contains(pos)) { sel.Visible = !sel.Visible; return; }
            y += FieldH + 3f;

            // Type-specific
            switch (sel)
            {
                case UITextElement te:
                    y += 20f; // section
                    TryFieldClick(pos, lx, fw, te.Id + "_txt", te.Text, v => te.Text = v, ref y);
                    TrySliderClick(pos, lx, fw, te.Id + "_fs", te.FontSize, 4f, 72f, v => te.FontSize = v, ref y);
                    TryColorClick(pos, lx, fw, te.Id + "_col", te.Color, v => te.Color = v, ref y);
                    TryEnumClick(pos, lx, fw, te.Id + "_aln", new[] { "Left", "Center", "Right" },
                        (int)te.Alignment, v => te.Alignment = (UITextAlignment)v, ref y);
                    break;
                case UIButtonElement be:
                    y += 20f;
                    TryFieldClick(pos, lx, fw, be.Id + "_txt", be.Text, v => be.Text = v, ref y);
                    TrySliderClick(pos, lx, fw, be.Id + "_fs", be.FontSize, 4f, 36f, v => be.FontSize = v, ref y);
                    y += 20f; // colors section
                    TryColorClick(pos, lx, fw, be.Id + "_bg", be.BackgroundColor, v => be.BackgroundColor = v, ref y);
                    TryColorClick(pos, lx, fw, be.Id + "_tc", be.TextColor, v => be.TextColor = v, ref y);
                    TryColorClick(pos, lx, fw, be.Id + "_hov", be.HoverColor, v => be.HoverColor = v, ref y);
                    TryColorClick(pos, lx, fw, be.Id + "_prs", be.PressedColor, v => be.PressedColor = v, ref y);
                    y += 20f; // events section
                    TryFieldClick(pos, lx, fw, be.Id + "_evt", be.OnClickEvent, v => be.OnClickEvent = v, ref y);
                    y += 20f; // script binding section
                    TryFieldClick(pos, lx, fw, be.Id + "_scr", be.TargetScriptName, v => be.TargetScriptName = v, ref y);
                    TryFieldClick(pos, lx, fw, be.Id + "_mth", be.TargetMethodName, v => be.TargetMethodName = v, ref y);
                    // Script/method hint rows — clickable list
                    TryScriptHintClick(pos, lx, fw, be, ref y);
                    break;
                case UITextFieldElement fe:
                    y += 20f;
                    TryFieldClick(pos, lx, fw, fe.Id + "_ph", fe.Placeholder, v => fe.Placeholder = v, ref y);
                    TryFieldClick(pos, lx, fw, fe.Id + "_txt", fe.Text, v => fe.Text = v, ref y);
                    TrySliderClick(pos, lx, fw, fe.Id + "_fs", fe.FontSize, 4f, 36f, v => fe.FontSize = v, ref y);
                    y += 20f;
                    TryColorClick(pos, lx, fw, fe.Id + "_bg", fe.BackgroundColor, v => fe.BackgroundColor = v, ref y);
                    TryColorClick(pos, lx, fw, fe.Id + "_tc", fe.TextColor, v => fe.TextColor = v, ref y);
                    TryColorClick(pos, lx, fw, fe.Id + "_brc", fe.BorderColor, v => fe.BorderColor = v, ref y);
                    TryColorClick(pos, lx, fw, fe.Id + "_fbc", fe.FocusBorderColor, v => fe.FocusBorderColor = v, ref y);
                    break;
                case UIScrollbarElement se:
                    y += 20f;
                    TryEnumClick(pos, lx, fw, se.Id + "_ori",
                        new[] { "Horizontal", "Vertical" }, (int)se.Orientation,
                        v => se.Orientation = (UIScrollbarOrientation)v, ref y);
                    TrySliderClick(pos, lx, fw, se.Id + "_min", se.MinValue, -1000f, se.MaxValue, v => se.MinValue = v, ref y);
                    TrySliderClick(pos, lx, fw, se.Id + "_max", se.MaxValue, se.MinValue, 1000f, v => se.MaxValue = v, ref y);
                    TrySliderClick(pos, lx, fw, se.Id + "_val", se.Value, se.MinValue, se.MaxValue, v => se.Value = v, ref y);
                    TrySliderClick(pos, lx, fw, se.Id + "_ts", se.ThumbSize, 0.05f, 1f, v => se.ThumbSize = v, ref y);
                    y += 20f;
                    TryColorClick(pos, lx, fw, se.Id + "_trc", se.TrackColor, v => se.TrackColor = v, ref y);
                    TryColorClick(pos, lx, fw, se.Id + "_thc", se.ThumbColor, v => se.ThumbColor = v, ref y);
                    break;
            }

            // Layer buttons
            y += 20f; // layer section
            float hw2 = (fw - 4f) / 2f;
            var upB = new RectangleF(lx, y, hw2, 20f);
            var dnB = new RectangleF(lx + hw2 + 4f, y, hw2, 20f);
            if (upB.Contains(pos) && _selected != null) { _doc.BringForward(_selected); return; }
            if (dnB.Contains(pos) && _selected != null) { _doc.SendBackward(_selected); return; }
        }

        // ── Per-type click testers ─────────────────────────────────────────────
        private void HandleXYClick(PointF pos, float lx, float fw,
            string idx, string idy, float vx, float vy,
            Action<float> sx, Action<float> sy, ref float y)
        {
            float hw = (fw - 88f - 4f) / 2f;
            float ox = lx + 88f;
            if (new RectangleF(ox, y, hw, FieldH).Contains(pos))
                StartEdit(idx, $"{vx:F1}", s => { if (float.TryParse(s, out float v)) sx(v); });
            else if (new RectangleF(ox + hw + 4f, y, hw, FieldH).Contains(pos))
                StartEdit(idy, $"{vy:F1}", s => { if (float.TryParse(s, out float v)) sy(v); });
            y += FieldH + 3f;
        }

        private void TryFieldClick(PointF pos, float lx, float fw,
            string id, string value, Action<string> setter, ref float y)
        {
            if (new RectangleF(lx + 76f, y, fw - 76f, FieldH).Contains(pos))
                StartEdit(id, value, setter);
            y += FieldH + 3f;
        }

        private void TrySliderClick(PointF pos, float lx, float fw,
            string id, float value, float min, float max,
            Action<float> setter, ref float y)
        {
            float ox = lx + 76f;
            float sw = fw - 76f - 52f;
            var track = new RectangleF(ox, y + 7f, sw, 8f);
            if (track.Contains(pos))
            {
                float t = Math.Clamp((pos.X - track.X) / track.Width, 0f, 1f);
                float nv = min + (max - min) * t;
                setter(nv);
            }
            var vf = new RectangleF(ox + sw + 4f, y, 48f, FieldH);
            if (vf.Contains(pos))
                StartEdit(id, $"{value:F1}", s => { if (float.TryParse(s, out float v)) setter(v); });
            y += FieldH + 5f;
        }

        private void TryColorClick(PointF pos, float lx, float fw,
            string id, Color value, Action<Color> setter, ref float y)
        {
            var fr = new RectangleF(lx + 98f, y, fw - 98f, FieldH);
            if (fr.Contains(pos))
                StartEdit(id, $"{value.R:X2}{value.G:X2}{value.B:X2}",
                    s => setter(ParseHex(s, value)));
            y += FieldH + 3f;
        }

        private void TryEnumClick(PointF pos, float lx, float fw,
            string id, string[] opts, int cur, Action<int> setter, ref float y)
        {
            float ox = lx + 76f;
            float bw = (fw - 76f - (opts.Length - 1) * 2f) / opts.Length;
            for (int i = 0; i < opts.Length; i++)
                if (new RectangleF(ox + i * (bw + 2f), y, bw, FieldH).Contains(pos))
                { setter(i); break; }
            y += FieldH + 3f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
        private void GetTransform(out float scale, out float ox, out float oy)
        {
            scale = _zoom;
            ox = _canvasRect.X + _canvasRect.Width / 2f + _panOffset.X;
            oy = _canvasRect.Y + _canvasRect.Height / 2f + _panOffset.Y;
            ox -= _doc.DesignWidth * scale / 2f;
            oy -= _doc.DesignHeight * scale / 2f;
        }

        private RectangleF ElemScreen(UIElement e, float ox, float oy)
            => ElemScreen(e.X, e.Y, e.Width, e.Height, ox, oy);

        private RectangleF ElemScreen(float ex, float ey, float ew, float eh, float ox, float oy)
            => new(ox + ex * _zoom, oy + ey * _zoom, ew * _zoom, eh * _zoom);

        private static PointF[] HandlePoints(RectangleF r)
            => new PointF[]
            {
                new(r.Left,             r.Top),
                new(r.Left+r.Width/2f,  r.Top),
                new(r.Right,            r.Top),
                new(r.Right,            r.Top+r.Height/2f),
                new(r.Right,            r.Bottom),
                new(r.Left+r.Width/2f,  r.Bottom),
                new(r.Left,             r.Bottom),
                new(r.Left,             r.Top+r.Height/2f),
            };

        private static void ApplyHandle(UIElement e, int h, float dx, float dy,
            float x0, float y0, float w0, float h0)
        {
            switch (h)
            {
                case 0: e.X = x0 + dx; e.Y = y0 + dy; e.Width = Math.Max(2, w0 - dx); e.Height = Math.Max(2, h0 - dy); break;
                case 1: e.Y = y0 + dy; e.Height = Math.Max(2, h0 - dy); break;
                case 2: e.Y = y0 + dy; e.Width = Math.Max(2, w0 + dx); e.Height = Math.Max(2, h0 - dy); break;
                case 3: e.Width = Math.Max(2, w0 + dx); break;
                case 4: e.Width = Math.Max(2, w0 + dx); e.Height = Math.Max(2, h0 + dy); break;
                case 5: e.Height = Math.Max(2, h0 + dy); break;
                case 6: e.X = x0 + dx; e.Width = Math.Max(2, w0 - dx); e.Height = Math.Max(2, h0 + dy); break;
                case 7: e.X = x0 + dx; e.Width = Math.Max(2, w0 - dx); break;
            }
        }

        private void PlaceElement(UIElementType type, float dx, float dy, float scale)
        {
            UIElement elem = type switch
            {
                UIElementType.Button => new UIButtonElement(),
                UIElementType.TextField => new UITextFieldElement(),
                UIElementType.Scrollbar => new UIScrollbarElement(),
                _ => new UITextElement(),
            };
            var sz = DefaultSize(type);
            elem.X = dx - sz.Width / 2f;
            elem.Y = dy - sz.Height / 2f;
            if (_snapEnabled) { elem.X = Snap(elem.X); elem.Y = Snap(elem.Y); }
            elem.Name = $"{type}{_doc.Elements.Count + 1}";
            _doc.Add(elem);
            _selected = elem;
        }

        private static SizeF DefaultSize(UIElementType t) => t switch
        {
            UIElementType.Button => new SizeF(120, 36),
            UIElementType.TextField => new SizeF(180, 28),
            UIElementType.Scrollbar => new SizeF(200, 16),
            _ => new SizeF(140, 26),
        };

        private float Snap(float v) => (float)Math.Round(v / _snapSize) * _snapSize;

        private void StartEdit(string id, string initial, Action<string> commit)
        {
            _propEditId = id;
            _propBuf = initial;
            _propCommit = commit;
        }

        private void CommitPropEdit()
        {
            _propCommit?.Invoke(_propBuf);
            _propEditId = null;
        }

        private static Color GetTypeColor(UIElementType t) => t switch
        {
            UIElementType.Button => Color.FromArgb(255, 68, 135, 215),
            UIElementType.TextField => Color.FromArgb(255, 55, 165, 120),
            UIElementType.Scrollbar => Color.FromArgb(255, 195, 135, 45),
            _ => Color.FromArgb(255, 68, 165, 235),
        };

        // ── Script binding hints (shows available scripts from compiled assembly) ──
        private void DrawScriptBindingHints(IEditorRenderer r, float lx, float fw,
            UIButtonElement be, ref float y)
        {
            // Use cached script list (cleared when scripts recompile)
            _cachedScripts ??= BuildScriptList();
            var scriptTypes = _cachedScripts;
            if (scriptTypes.Count == 0)
            {
                r.DrawText("(compile scripts to see options)", new PointF(lx, y + 2f), ColTextDim, 8f);
                y += 14f;
                return;
            }

            // Script list header
            r.DrawText("Available scripts:", new PointF(lx, y + 2f), ColTextDim, 8f);
            y += 14f;

            foreach (var (scriptName, methods) in scriptTypes)
            {
                bool isSel = scriptName == be.TargetScriptName;
                var sBtn = new RectangleF(lx, y, fw, 18f);
                bool hov = sBtn.Contains(_mouse);
                r.FillRect(sBtn, isSel ? Color.FromArgb(255, 35, 55, 95)
                           : hov ? Color.FromArgb(255, 36, 36, 42) : Color.Transparent);
                r.FillRect(new RectangleF(lx, y + 5f, 4f, 8f),
                    isSel ? ColAccent : Color.FromArgb(255, 70, 70, 80));
                r.DrawText(scriptName, new PointF(lx + 8f, y + 3f),
                    isSel ? Color.FromArgb(255, 140, 185, 255) : ColText, 9f);
                y += 20f;

                // Show methods when script is selected
                if (isSel && methods.Count > 0)
                {
                    foreach (var method in methods)
                    {
                        bool mSel = method == be.TargetMethodName;
                        var mBtn = new RectangleF(lx + 12f, y, fw - 12f, 16f);
                        bool mHov = mBtn.Contains(_mouse);
                        r.FillRect(mBtn, mSel ? Color.FromArgb(255, 40, 70, 115)
                                   : mHov ? Color.FromArgb(255, 36, 36, 42) : Color.Transparent);
                        r.DrawText("→ " + method + "()", new PointF(mBtn.X + 4f, mBtn.Y + 2f),
                            mSel ? Color.FromArgb(255, 160, 210, 255) : ColTextDim, 8f);
                        y += 18f;
                    }
                }
            }
        }

        // Returns available compiled script names → public void method names
        // Uses ComponentRegistry.UserAssembly (set by LoadUserScripts on every compile)
        // so the editor always reflects the latest recompile without needing a restart.
        private static List<(string Name, List<string> Methods)> BuildScriptList()
        {
            var result = new List<(string, List<string>)>();
            try
            {
                // Prefer the explicitly tracked user assembly — it's always the freshest
                // compiled version. Fall back to scanning AppDomain only if nothing is loaded yet.
                var assemblies = new List<System.Reflection.Assembly>();
                if (Core.ComponentRegistry.UserAssembly != null)
                    assemblies.Add(Core.ComponentRegistry.UserAssembly);
                else
                {
                    // Fallback: scan domain, skipping all engine/framework assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string n = asm.GetName().Name ?? "";
                        if (n.StartsWith("System") || n.StartsWith("Microsoft") ||
                            n.StartsWith("OpenTK") || n == "ElintriaEngine" ||
                            n.StartsWith("netstandard") || n.StartsWith("mscorlib"))
                            continue;
                        assemblies.Add(asm);
                    }
                }

                foreach (var asm in assemblies)
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (!typeof(Core.Component).IsAssignableFrom(t) || t.IsAbstract) continue;
                        var methods = new List<string>();
                        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public |
                                                        System.Reflection.BindingFlags.Instance |
                                                        System.Reflection.BindingFlags.DeclaredOnly))
                        {
                            // Only include public void Method() — no params, no lifecycle hooks
                            if (m.ReturnType != typeof(void)) continue;
                            if (m.GetParameters().Length != 0) continue;
                            if (m.IsSpecialName) continue;
                            string mn = m.Name;
                            if (mn == "OnStart" || mn == "OnUpdate" || mn == "OnFixedUpdate" ||
                                mn == "Awake" || mn == "OnEnable" || mn == "OnDisable" ||
                                mn == "OnDestroy") continue;
                            methods.Add(mn);
                        }
                        result.Add((t.Name, methods));
                    }
                }
            }
            catch { }
            return result;
        }

        private void TryScriptHintClick(PointF pos, float lx, float fw,
            UIButtonElement be, ref float y)
        {
            _cachedScripts ??= BuildScriptList();
            var scripts = _cachedScripts;
            if (scripts.Count == 0) { y += 14f; return; }

            y += 14f; // header row
            foreach (var (scriptName, methods) in scripts)
            {
                var sBtn = new RectangleF(lx, y, fw, 18f);
                if (sBtn.Contains(pos))
                {
                    be.TargetScriptName = scriptName;
                    if (!methods.Contains(be.TargetMethodName)) be.TargetMethodName = "";
                    return;
                }
                y += 20f;

                if (scriptName == be.TargetScriptName)
                {
                    foreach (var method in methods)
                    {
                        var mBtn = new RectangleF(lx + 12f, y, fw - 12f, 16f);
                        if (mBtn.Contains(pos)) { be.TargetMethodName = method; return; }
                        y += 18f;
                    }
                }
            }
        }

        private static Color Darken(Color c, float f)
            => Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));

        private static Color ParseHex(string s, Color fallback)
        {
            s = s.TrimStart('#');
            if (s.Length == 6)
                try { return Color.FromArgb(255, Convert.ToInt32(s[..2], 16), Convert.ToInt32(s[2..4], 16), Convert.ToInt32(s[4..6], 16)); }
                catch { }
            return fallback;
        }
    }
}