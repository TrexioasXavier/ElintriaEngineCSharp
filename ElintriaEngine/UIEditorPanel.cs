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
    //  UIEditorPanel
    //
    //  A floating editor window for building game UI overlays.
    //
    //  Layout (three columns inside content area):
    //    Left  (PalW)  — element palette buttons + layer hierarchy list
    //    Center        — scaled canvas showing the design-resolution preview
    //    Right (PropW) — property fields for the selected element
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class UIEditorPanel : Panel
    {
        // ── Layout constants ──────────────────────────────────────────────────
        private const float PalW = 140f;
        private const float PropW = 210f;
        private const float PAD = 6f;
        private const float RowH = 22f;
        private const float BtnH = 28f;
        private const float HdSize = 10f;  // resize handle half-size

        // ── Document ──────────────────────────────────────────────────────────
        private UIDocument _doc = new();

        // ── Editor state ──────────────────────────────────────────────────────
        private UIElement? _selected;
        private UIElementType? _pendingType;   // waiting for canvas click to place

        // Canvas drag / resize
        private bool _draggingElem;
        private bool _resizingElem;
        private int _resizeHandle;    // 0-7 clockwise from TL, -1=none
        private PointF _dragStart;
        private float _dragElemX0, _dragElemY0, _dragElemW0, _dragElemH0;

        // Property-field inline editing
        private string? _propEditId;
        private string _propBuf = "";
        private Action<string>? _propCommit;

        // Cached rects for hit-testing
        private RectangleF _canvasRect;    // screen rect of the design-space preview
        private RectangleF _designRect;    // actual scaled design area inside canvasRect
        private readonly List<(RectangleF r, UIElementType t)> _palBtns = new();
        private readonly List<(RectangleF r, UIElement e)> _hierRows = new();
        private readonly List<(RectangleF r, int h)> _handles = new();

        // Hover
        private PointF _mouse;

        // Colors
        private static readonly Color CPal = Color.FromArgb(255, 30, 30, 30);
        private static readonly Color CCanvas = Color.FromArgb(255, 22, 22, 22);
        private static readonly Color CDesign = Color.FromArgb(255, 45, 45, 48);
        private static readonly Color CGrid = Color.FromArgb(30, 255, 255, 255);
        private static readonly Color CSel = Color.FromArgb(255, 70, 150, 255);
        private static readonly Color CHandle = Color.FromArgb(255, 255, 255, 255);
        private static readonly Color CPending = Color.FromArgb(180, 80, 180, 80);
        private static readonly Color CProp = Color.FromArgb(255, 28, 28, 28);

        // ── Constructor ───────────────────────────────────────────────────────
        public UIEditorPanel(RectangleF bounds)
            : base("UI Editor", bounds)
        {
            MinWidth = 700f;
            MinHeight = 440f;
        }

        // ── Document access ───────────────────────────────────────────────────
        public UIDocument Document => _doc;
        public void SetDocument(UIDocument doc) { _doc = doc; _selected = null; }

        // ── Render ────────────────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;

            DrawHeader(r);
            var cr = ContentRect;
            r.FillRect(cr, ColBg);
            r.PushClip(cr);

            float palX = cr.X;
            float canvasX = cr.X + PalW;
            float propX = cr.Right - PropW;
            float canvasW = propX - canvasX;

            _canvasRect = new RectangleF(canvasX, cr.Y, canvasW, cr.Height);
            var palRect = new RectangleF(palX, cr.Y, PalW, cr.Height);
            var propRect = new RectangleF(propX, cr.Y, PropW, cr.Height);

            DrawPaletteAndHierarchy(r, palRect);
            DrawCanvas(r, _canvasRect);
            DrawProperties(r, propRect);

            // Column dividers
            r.DrawLine(new PointF(canvasX, cr.Y), new PointF(canvasX, cr.Bottom),
                Color.FromArgb(255, 55, 55, 55));
            r.DrawLine(new PointF(propX, cr.Y), new PointF(propX, cr.Bottom),
                Color.FromArgb(255, 55, 55, 55));

            r.PopClip();
        }

        // ── Left: Palette + Hierarchy ─────────────────────────────────────────
        private void DrawPaletteAndHierarchy(IEditorRenderer r, RectangleF rect)
        {
            r.FillRect(rect, CPal);
            _palBtns.Clear();
            _hierRows.Clear();

            float y = rect.Y + PAD;
            float bw = rect.Width - PAD * 2;
            float bx = rect.X + PAD;

            // Section label
            r.DrawText("ELEMENTS", new PointF(bx, y), ColTextDim, 9f);
            y += 14f;

            // Palette buttons
            var types = new[]
            {
                (UIElementType.Text,       "T  Text",      Color.FromArgb(255, 70,170,230)),
                (UIElementType.Button,     "▣  Button",    Color.FromArgb(255, 70,140,210)),
                (UIElementType.TextField,  "▤  TextField", Color.FromArgb(255, 60,160,120)),
                (UIElementType.Scrollbar,  "▬  Scrollbar", Color.FromArgb(255,180,130, 50)),
            };

            foreach (var (type, label, accentCol) in types)
            {
                var btn = new RectangleF(bx, y, bw, BtnH);
                bool pending = _pendingType == type;
                bool hover = btn.Contains(_mouse);

                Color bg = pending ? accentCol
                         : hover ? Color.FromArgb(255, 55, 55, 55)
                                   : Color.FromArgb(255, 40, 40, 40);
                r.FillRect(btn, bg);
                r.DrawRect(btn, pending ? accentCol : ColBorder);
                r.DrawText(label, new PointF(btn.X + 8f, btn.Y + 7f), ColText, 11f);

                _palBtns.Add((btn, type));
                y += BtnH + 4f;
            }

            y += 6f;
            r.DrawLine(new PointF(bx, y), new PointF(rect.Right - PAD, y),
                Color.FromArgb(255, 55, 55, 55));
            y += 6f;

            r.DrawText("HIERARCHY", new PointF(bx, y), ColTextDim, 9f);
            y += 14f;

            // Element list
            r.PushClip(new RectangleF(rect.X, y, rect.Width, rect.Bottom - y));
            foreach (var elem in _doc.Elements)
            {
                var row = new RectangleF(bx, y, bw, RowH);
                bool sel = elem == _selected;
                bool hover = row.Contains(_mouse);
                r.FillRect(row, sel ? ColSelected
                              : hover ? Color.FromArgb(255, 50, 50, 50)
                                      : Color.Transparent);

                // Type icon color
                Color ic = elem.ElementType switch
                {
                    UIElementType.Button => Color.FromArgb(255, 70, 140, 210),
                    UIElementType.TextField => Color.FromArgb(255, 60, 160, 120),
                    UIElementType.Scrollbar => Color.FromArgb(255, 180, 130, 50),
                    _ => Color.FromArgb(255, 70, 170, 230),
                };
                r.FillRect(new RectangleF(bx, y + 7f, 4f, 8f), ic);
                r.DrawText(elem.Name, new PointF(bx + 8f, y + 5f), ColText, 10f);

                _hierRows.Add((row, elem));
                y += RowH;
            }
            r.PopClip();
        }

        // ── Center: Canvas ────────────────────────────────────────────────────
        private void DrawCanvas(IEditorRenderer r, RectangleF rect)
        {
            r.FillRect(rect, CCanvas);
            r.PushClip(rect);

            // Compute design rect (centered, maintaining aspect)
            float pad = 20f;
            float availW = rect.Width - pad * 2;
            float availH = rect.Height - pad * 2;
            float scaleX = availW / _doc.DesignWidth;
            float scaleY = availH / _doc.DesignHeight;
            float scale = Math.Min(scaleX, scaleY);
            float dw = _doc.DesignWidth * scale;
            float dh = _doc.DesignHeight * scale;
            float dx = rect.X + (rect.Width - dw) / 2f;
            float dy = rect.Y + (rect.Height - dh) / 2f;

            _designRect = new RectangleF(dx, dy, dw, dh);

            // Shadow
            r.FillRect(new RectangleF(dx + 4, dy + 4, dw, dh), Color.FromArgb(80, 0, 0, 0));
            // Design area background
            r.FillRect(_designRect, CDesign);
            r.DrawRect(_designRect, Color.FromArgb(255, 75, 75, 78));

            // Grid
            DrawGrid(r, _designRect, scale);

            // Design resolution label
            r.DrawText($"{_doc.DesignWidth} × {_doc.DesignHeight}",
                new PointF(dx + 4f, dy - 14f), ColTextDim, 9f);

            // Elements
            foreach (var elem in _doc.Elements)
            {
                if (!elem.Visible) continue;
                DrawElementOnCanvas(r, elem, dx, dy, scale);
            }

            // Selection outline + handles
            if (_selected != null)
            {
                var sr = ElemScreenRect(_selected, dx, dy, scale);
                DrawSelectionOutline(r, sr);
                DrawHandles(r, sr);
            }

            // Pending placement ghost
            if (_pendingType.HasValue && _canvasRect.Contains(_mouse))
            {
                var ghost = GetPlaceholderSize(_pendingType.Value);
                var gr = new RectangleF(_mouse.X - ghost.Width / 2f,
                                        _mouse.Y - ghost.Height / 2f,
                                        ghost.Width * scale, ghost.Height * scale);
                r.FillRect(gr, CPending);
                r.DrawRect(gr, Color.FromArgb(200, 100, 220, 100));
                r.DrawText("Click to place", new PointF(gr.X + 4, gr.Bottom + 4), ColTextDim, 9f);
            }

            r.PopClip();
        }

        private void DrawGrid(IEditorRenderer r, RectangleF dr, float scale)
        {
            float step = 50f * scale; // design-space 50px grid
            if (step < 8f) return;

            for (float x = dr.X + step; x < dr.Right; x += step)
                r.DrawLine(new PointF(x, dr.Y), new PointF(x, dr.Bottom), CGrid);
            for (float y = dr.Y + step; y < dr.Bottom; y += step)
                r.DrawLine(new PointF(dr.X, y), new PointF(dr.Right, y), CGrid);
        }

        private void DrawElementOnCanvas(IEditorRenderer r, UIElement elem,
            float ox, float oy, float scale)
        {
            var sr = ElemScreenRect(elem, ox, oy, scale);
            bool sel = elem == _selected;

            switch (elem)
            {
                case UITextElement te:
                    float fs = Math.Max(8f, te.FontSize * scale);
                    r.DrawText(te.Text, new PointF(sr.X + 2, sr.Y + (sr.Height - fs) / 2f),
                        te.Color, fs);
                    break;

                case UIButtonElement be:
                    r.FillRect(sr, be.BackgroundColor);
                    r.DrawRect(sr, DarkenColor(be.BackgroundColor, 0.6f));
                    float bfs = Math.Max(8f, be.FontSize * scale);
                    float tw = be.Text.Length * bfs * 0.55f;
                    r.DrawText(be.Text,
                        new PointF(sr.X + (sr.Width - tw) / 2f, sr.Y + (sr.Height - bfs) / 2f),
                        be.TextColor, bfs);
                    break;

                case UITextFieldElement fe:
                    r.FillRect(sr, fe.BackgroundColor);
                    r.DrawRect(sr, fe.BorderColor);
                    float ffs = Math.Max(7f, fe.FontSize * scale);
                    bool empty = string.IsNullOrEmpty(fe.Text);
                    string txt = empty ? fe.Placeholder : fe.Text;
                    Color col = empty
                        ? Color.FromArgb(100, fe.TextColor.R, fe.TextColor.G, fe.TextColor.B)
                        : fe.TextColor;
                    r.PushClip(sr);
                    r.DrawText(txt, new PointF(sr.X + 4f, sr.Y + (sr.Height - ffs) / 2f), col, ffs);
                    r.PopClip();
                    break;

                case UIScrollbarElement se:
                    r.FillRect(sr, se.TrackColor);
                    r.DrawRect(sr, DarkenColor(se.TrackColor, 0.5f));
                    float range = se.MaxValue - se.MinValue;
                    float t = range > 0f ? (se.Value - se.MinValue) / range : 0f;
                    RectangleF thumb;
                    if (se.Orientation == UIScrollbarOrientation.Horizontal)
                    {
                        float tw2 = sr.Width * se.ThumbSize;
                        thumb = new RectangleF(sr.X + (sr.Width - tw2) * t, sr.Y + 1,
                                               tw2, sr.Height - 2);
                    }
                    else
                    {
                        float th = sr.Height * se.ThumbSize;
                        thumb = new RectangleF(sr.X + 1, sr.Y + (sr.Height - th) * t,
                                               sr.Width - 2, th);
                    }
                    r.FillRect(thumb, se.ThumbColor);
                    r.DrawRect(thumb, DarkenColor(se.ThumbColor, 0.7f));
                    break;
            }
        }

        private void DrawSelectionOutline(IEditorRenderer r, RectangleF sr)
        {
            r.DrawRect(new RectangleF(sr.X - 1, sr.Y - 1, sr.Width + 2, sr.Height + 2),
                CSel, 1.5f);
        }

        private void DrawHandles(IEditorRenderer r, RectangleF sr)
        {
            _handles.Clear();
            var pts = GetHandlePoints(sr);
            for (int i = 0; i < pts.Length; i++)
            {
                var hr = new RectangleF(pts[i].X - HdSize, pts[i].Y - HdSize, HdSize * 2, HdSize * 2);
                r.FillRect(hr, CHandle);
                r.DrawRect(hr, CSel);
                _handles.Add((hr, i));
            }
        }

        // ── Right: Properties ─────────────────────────────────────────────────
        private void DrawProperties(IEditorRenderer r, RectangleF rect)
        {
            r.FillRect(rect, CProp);
            r.PushClip(rect);

            float y = rect.Y + PAD;
            float lx = rect.X + PAD;
            float fw = rect.Width - PAD * 2;

            r.DrawText("PROPERTIES", new PointF(lx, y), ColTextDim, 9f);
            y += 14f;

            if (_selected == null)
            {
                r.DrawText("Nothing selected.", new PointF(lx, y + 4f), ColTextDim, 10f);
                r.PopClip();
                return;
            }

            UIElement sel = _selected;
            r.DrawText(sel.ElementType.ToString().ToUpper(),
                new PointF(lx, y), ColAccent, 10f);
            y += 14f;

            DrawPropDivider(r, rect, ref y);

            // ── Common properties ─────────────────────────────────────────────
            DrawPropString(r, lx, fw, "Name", sel.Id + "_name", sel.Name, v => sel.Name = v, ref y);
            DrawPropFloat(r, lx, fw, "X", sel.Id + "_x", sel.X, v => sel.X = v, ref y);
            DrawPropFloat(r, lx, fw, "Y", sel.Id + "_y", sel.Y, v => sel.Y = v, ref y);
            DrawPropFloat(r, lx, fw, "Width", sel.Id + "_w", sel.Width, v => sel.Width = Math.Max(4f, v), ref y);
            DrawPropFloat(r, lx, fw, "Height", sel.Id + "_h", sel.Height, v => sel.Height = Math.Max(4f, v), ref y);
            DrawPropBool(r, lx, fw, "Visible", sel.Id + "_vis", sel.Visible, v => sel.Visible = v, ref y);

            DrawPropDivider(r, rect, ref y);

            // ── Type-specific properties ──────────────────────────────────────
            switch (sel)
            {
                case UITextElement te:
                    DrawPropString(r, lx, fw, "Text", te.Id + "_txt", te.Text, v => te.Text = v, ref y);
                    DrawPropFloat(r, lx, fw, "Font Size", te.Id + "_fs", te.FontSize, v => te.FontSize = Math.Max(4f, v), ref y);
                    DrawPropColor(r, lx, fw, "Color", te.Id + "_col", te.Color, v => te.Color = v, ref y);
                    DrawPropEnum(r, lx, fw, "Alignment", te.Id + "_aln",
                        new[] { "Left", "Center", "Right" },
                        (int)te.Alignment,
                        v => te.Alignment = (UITextAlignment)v, ref y);
                    break;

                case UIButtonElement be:
                    DrawPropString(r, lx, fw, "Text", be.Id + "_txt", be.Text, v => be.Text = v, ref y);
                    DrawPropFloat(r, lx, fw, "Font Size", be.Id + "_fs", be.FontSize, v => be.FontSize = Math.Max(4f, v), ref y);
                    DrawPropColor(r, lx, fw, "BG Color", be.Id + "_bg", be.BackgroundColor, v => be.BackgroundColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Text Color", be.Id + "_tc", be.TextColor, v => be.TextColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Hover", be.Id + "_hov", be.HoverColor, v => be.HoverColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Pressed", be.Id + "_prs", be.PressedColor, v => be.PressedColor = v, ref y);
                    DrawPropString(r, lx, fw, "OnClick", be.Id + "_evt", be.OnClickEvent, v => be.OnClickEvent = v, ref y);
                    break;

                case UITextFieldElement fe:
                    DrawPropString(r, lx, fw, "Placeholder", fe.Id + "_ph", fe.Placeholder, v => fe.Placeholder = v, ref y);
                    DrawPropString(r, lx, fw, "Text", fe.Id + "_txt", fe.Text, v => fe.Text = v, ref y);
                    DrawPropFloat(r, lx, fw, "Font Size", fe.Id + "_fs", fe.FontSize, v => fe.FontSize = Math.Max(4f, v), ref y);
                    DrawPropInt(r, lx, fw, "Max Length", fe.Id + "_ml", fe.MaxLength, v => fe.MaxLength = Math.Max(1, v), ref y);
                    DrawPropColor(r, lx, fw, "BG Color", fe.Id + "_bg", fe.BackgroundColor, v => fe.BackgroundColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Text Color", fe.Id + "_tc", fe.TextColor, v => fe.TextColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Border", fe.Id + "_brc", fe.BorderColor, v => fe.BorderColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Focus Border", fe.Id + "_fbc", fe.FocusBorderColor, v => fe.FocusBorderColor = v, ref y);
                    break;

                case UIScrollbarElement se:
                    DrawPropEnum(r, lx, fw, "Orientation", se.Id + "_ori",
                        new[] { "Horizontal", "Vertical" },
                        (int)se.Orientation,
                        v => se.Orientation = (UIScrollbarOrientation)v, ref y);
                    DrawPropFloat(r, lx, fw, "Min", se.Id + "_min", se.MinValue, v => se.MinValue = v, ref y);
                    DrawPropFloat(r, lx, fw, "Max", se.Id + "_max", se.MaxValue, v => se.MaxValue = v, ref y);
                    DrawPropFloat(r, lx, fw, "Value", se.Id + "_val", se.Value, v => se.Value = Math.Clamp(v, se.MinValue, se.MaxValue), ref y);
                    DrawPropFloat(r, lx, fw, "Thumb Size", se.Id + "_ts", se.ThumbSize, v => se.ThumbSize = Math.Clamp(v, 0.05f, 1f), ref y);
                    DrawPropColor(r, lx, fw, "Track Color", se.Id + "_trc", se.TrackColor, v => se.TrackColor = v, ref y);
                    DrawPropColor(r, lx, fw, "Thumb Color", se.Id + "_thc", se.ThumbColor, v => se.ThumbColor = v, ref y);
                    break;
            }

            DrawPropDivider(r, rect, ref y);

            // Layer order buttons
            float bw = (fw - 4f) / 2f;
            var upBtn = new RectangleF(lx, y, bw, 20f);
            var dnBtn = new RectangleF(lx + bw + 4f, y, bw, 20f);
            r.FillRect(upBtn, Color.FromArgb(255, 40, 40, 40));
            r.DrawRect(upBtn, ColBorder);
            r.DrawText("▲ Forward", new PointF(upBtn.X + 6f, upBtn.Y + 4f), ColText, 9f);
            r.FillRect(dnBtn, Color.FromArgb(255, 40, 40, 40));
            r.DrawRect(dnBtn, ColBorder);
            r.DrawText("▼ Back", new PointF(dnBtn.X + 6f, dnBtn.Y + 4f), ColText, 9f);
            y += 24f;

            // Delete button
            var delBtn = new RectangleF(lx, y, fw, 20f);
            r.FillRect(delBtn, Color.FromArgb(255, 110, 35, 35));
            r.DrawRect(delBtn, Color.FromArgb(255, 160, 50, 50));
            r.DrawText("✕  Delete Element", new PointF(delBtn.X + 12f, delBtn.Y + 4f), Color.White, 9f);

            r.PopClip();
        }

        // ── Property drawers ──────────────────────────────────────────────────

        private void DrawPropDivider(IEditorRenderer r, RectangleF rect, ref float y)
        {
            r.DrawLine(new PointF(rect.X + PAD, y), new PointF(rect.Right - PAD, y),
                Color.FromArgb(255, 55, 55, 55));
            y += 6f;
        }

        private void DrawPropString(IEditorRenderer r, float lx, float fw,
            string label, string id, string value, Action<string> setter, ref float y)
        {
            DrawPropLabel(r, lx, fw, label, y);
            bool ed = _propEditId == id;
            var fr = new RectangleF(lx + 90f, y, fw - 90f, 18f);
            r.FillRect(fr, ed ? Color.FromArgb(255, 30, 50, 80) : Color.FromArgb(255, 34, 34, 34));
            r.DrawRect(fr, ed ? ColAccent : ColBorder);
            r.DrawText(ed ? _propBuf + "|" : value, new PointF(fr.X + 4f, y + 3f), ColText, 10f);
            y += RowH;
        }

        private void DrawPropFloat(IEditorRenderer r, float lx, float fw,
            string label, string id, float value, Action<float> setter, ref float y)
        {
            DrawPropString(r, lx, fw, label, id, $"{value:F2}",
                s => { if (float.TryParse(s, out float v)) setter(v); }, ref y);
        }

        private void DrawPropInt(IEditorRenderer r, float lx, float fw,
            string label, string id, int value, Action<int> setter, ref float y)
        {
            DrawPropString(r, lx, fw, label, id, value.ToString(),
                s => { if (int.TryParse(s, out int v)) setter(v); }, ref y);
        }

        private void DrawPropBool(IEditorRenderer r, float lx, float fw,
            string label, string id, bool value, Action<bool> setter, ref float y)
        {
            DrawPropLabel(r, lx, fw, label, y);
            var cb = new RectangleF(lx + 90f, y + 2f, 14f, 14f);
            r.FillRect(cb, value ? Color.FromArgb(255, 55, 155, 55) : Color.FromArgb(255, 40, 40, 40));
            r.DrawRect(cb, ColBorder);
            if (value) r.DrawText("✓", new PointF(cb.X + 1f, cb.Y), Color.White, 10f);
            y += RowH;
        }

        private void DrawPropColor(IEditorRenderer r, float lx, float fw,
            string label, string id, Color value, Action<Color> setter, ref float y)
        {
            DrawPropLabel(r, lx, fw, label, y);
            bool ed = _propEditId == id;

            // Color swatch
            var sw = new RectangleF(lx + 90f, y + 2f, 14f, 14f);
            r.FillRect(sw, value);
            r.DrawRect(sw, ColBorder);

            // Hex text field
            string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            var fr = new RectangleF(lx + 108f, y, fw - 108f, 18f);
            r.FillRect(fr, ed ? Color.FromArgb(255, 30, 50, 80) : Color.FromArgb(255, 34, 34, 34));
            r.DrawRect(fr, ed ? ColAccent : ColBorder);
            r.DrawText(ed ? _propBuf + "|" : hex, new PointF(fr.X + 4f, y + 3f), ColText, 10f);
            y += RowH;
        }

        private void DrawPropEnum(IEditorRenderer r, float lx, float fw,
            string label, string id, string[] options, int current, Action<int> setter, ref float y)
        {
            DrawPropLabel(r, lx, fw, label, y);
            float bw = (fw - 90f - (options.Length - 1) * 2f) / options.Length;
            float bx = lx + 90f;
            for (int i = 0; i < options.Length; i++)
            {
                var btn = new RectangleF(bx, y, bw, 18f);
                bool sel = i == current;
                r.FillRect(btn, sel ? ColAccent : Color.FromArgb(255, 40, 40, 40));
                r.DrawRect(btn, sel ? ColAccent : ColBorder);
                r.DrawText(options[i], new PointF(btn.X + 3f, btn.Y + 3f), ColText, 9f);
                bx += bw + 2f;
            }
            y += RowH;
        }

        private static void DrawPropLabel(IEditorRenderer r, float lx, float fw,
            string label, float y)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 9f);
        }

        // ── Input ─────────────────────────────────────────────────────────────

        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;
            if (!IsVisible || !ContainsPoint(pos)) return;

            base.OnMouseDown(e, pos);     // header drag / panel resize via Panel base
            if (HeaderRect.Contains(pos)) return;

            var cr = ContentRect;
            if (!cr.Contains(pos)) return;

            if (e.Button != MouseButton.Left) return;

            // Commit any open property edit
            if (_propEditId != null) { CommitPropEdit(); }

            // ── Canvas interactions ───────────────────────────────────────────
            if (_canvasRect.Contains(pos))
            {
                float scale, ox, oy;
                GetCanvasTransform(out scale, out ox, out oy);

                // Pending placement click
                if (_pendingType.HasValue)
                {
                    PlaceElement(_pendingType.Value, pos, ox, oy, scale);
                    _pendingType = null;
                    return;
                }

                // Resize handle hit?
                foreach (var (hr, h) in _handles)
                {
                    if (hr.Contains(pos) && _selected != null)
                    {
                        _resizingElem = true;
                        _resizeHandle = h;
                        _dragStart = pos;
                        _dragElemX0 = _selected.X;
                        _dragElemY0 = _selected.Y;
                        _dragElemW0 = _selected.Width;
                        _dragElemH0 = _selected.Height;
                        return;
                    }
                }

                // Element selection / drag
                float desX = (pos.X - ox) / scale;
                float desY = (pos.Y - oy) / scale;

                UIElement? hit = null;
                for (int i = _doc.Elements.Count - 1; i >= 0; i--)
                {
                    if (_doc.Elements[i].Rect.Contains(desX, desY))
                    { hit = _doc.Elements[i]; break; }
                }

                _selected = hit;
                if (hit != null)
                {
                    _draggingElem = true;
                    _dragStart = pos;
                    _dragElemX0 = hit.X;
                    _dragElemY0 = hit.Y;
                }
                return;
            }

            // ── Palette button click ──────────────────────────────────────────
            foreach (var (btn, type) in _palBtns)
            {
                if (btn.Contains(pos))
                {
                    _pendingType = _pendingType == type ? null : type;
                    return;
                }
            }

            // ── Hierarchy row click ───────────────────────────────────────────
            foreach (var (row, elem) in _hierRows)
            {
                if (row.Contains(pos)) { _selected = elem; return; }
            }

            // ── Property field clicks ─────────────────────────────────────────
            if (_selected != null)
                HandlePropsClick(pos);
        }

        public override void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            if (!IsVisible) return;
            base.OnMouseMove(pos);

            float scale, ox, oy;
            GetCanvasTransform(out scale, out ox, out oy);

            if (_draggingElem && _selected != null)
            {
                float dx = (pos.X - _dragStart.X) / scale;
                float dy = (pos.Y - _dragStart.Y) / scale;
                _selected.X = _dragElemX0 + dx;
                _selected.Y = _dragElemY0 + dy;
            }
            else if (_resizingElem && _selected != null)
            {
                float dx = (pos.X - _dragStart.X) / scale;
                float dy = (pos.Y - _dragStart.Y) / scale;
                ApplyResizeHandle(_selected, _resizeHandle, dx, dy);
            }
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;
            _draggingElem = false;
            _resizingElem = false;
            base.OnMouseUp(e, pos);
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            // Property text editing
            if (_propEditId != null)
            {
                switch (e.Key)
                {
                    case Keys.Enter: CommitPropEdit(); break;
                    case Keys.Escape: _propEditId = null; break;
                    case Keys.Backspace when _propBuf.Length > 0:
                        _propBuf = _propBuf[..^1]; break;
                }
                return;
            }

            if (_selected == null) return;
            float step = e.Shift ? 10f : 1f;
            switch (e.Key)
            {
                case Keys.Delete:
                    _doc.Remove(_selected);
                    _selected = null;
                    break;
                case Keys.Up: _selected.Y -= step; break;
                case Keys.Down: _selected.Y += step; break;
                case Keys.Left: _selected.X -= step; break;
                case Keys.Right: _selected.X += step; break;
            }
        }

        public override void OnTextInput(TextInputEventArgs e)
        {
            if (_propEditId != null) _propBuf += e.AsString;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void GetCanvasTransform(out float scale, out float ox, out float oy)
        {
            float pad = 20f;
            float availW = _canvasRect.Width - pad * 2;
            float availH = _canvasRect.Height - pad * 2;
            float sx = availW / _doc.DesignWidth;
            float sy = availH / _doc.DesignHeight;
            scale = Math.Min(sx, sy);
            float dw = _doc.DesignWidth * scale;
            float dh = _doc.DesignHeight * scale;
            ox = _canvasRect.X + (_canvasRect.Width - dw) / 2f;
            oy = _canvasRect.Y + (_canvasRect.Height - dh) / 2f;
        }

        private void PlaceElement(UIElementType type, PointF screenPos,
            float ox, float oy, float scale)
        {
            float dx = (screenPos.X - ox) / scale;
            float dy = (screenPos.Y - oy) / scale;

            UIElement elem = type switch
            {
                UIElementType.Button => new UIButtonElement(),
                UIElementType.TextField => new UITextFieldElement(),
                UIElementType.Scrollbar => new UIScrollbarElement(),
                _ => new UITextElement(),
            };

            elem.X = dx - elem.Width / 2f;
            elem.Y = dy - elem.Height / 2f;
            elem.Name = $"{type}{_doc.Elements.Count + 1}";

            _doc.Add(elem);
            _selected = elem;
        }

        private static SizeF GetPlaceholderSize(UIElementType t) => t switch
        {
            UIElementType.Button => new SizeF(120, 34),
            UIElementType.TextField => new SizeF(180, 28),
            UIElementType.Scrollbar => new SizeF(200, 16),
            _ => new SizeF(140, 26),
        };

        private RectangleF ElemScreenRect(UIElement e, float ox, float oy, float scale)
            => new(ox + e.X * scale, oy + e.Y * scale, e.Width * scale, e.Height * scale);

        private static PointF[] GetHandlePoints(RectangleF r)
            => new PointF[]
            {
                new(r.Left,              r.Top),                   // 0 TL
                new(r.Left + r.Width/2f, r.Top),                   // 1 TC
                new(r.Right,             r.Top),                   // 2 TR
                new(r.Right,             r.Top + r.Height/2f),     // 3 MR
                new(r.Right,             r.Bottom),                // 4 BR
                new(r.Left + r.Width/2f, r.Bottom),                // 5 BC
                new(r.Left,              r.Bottom),                // 6 BL
                new(r.Left,              r.Top + r.Height/2f),     // 7 ML
            };

        private static void ApplyResizeHandle(UIElement e, int h, float dx, float dy)
        {
            // Each handle adjusts different combination of X/Y/W/H
            switch (h)
            {
                case 0: e.X += dx; e.Y += dy; e.Width = Math.Max(4, e.Width - dx); e.Height = Math.Max(4, e.Height - dy); break;
                case 1: e.Y += dy; e.Height = Math.Max(4, e.Height - dy); break;
                case 2: e.Y += dy; e.Width = Math.Max(4, e.Width + dx); e.Height = Math.Max(4, e.Height - dy); break;
                case 3: e.Width = Math.Max(4, e.Width + dx); break;
                case 4: e.Width = Math.Max(4, e.Width + dx); e.Height = Math.Max(4, e.Height + dy); break;
                case 5: e.Height = Math.Max(4, e.Height + dy); break;
                case 6: e.X += dx; e.Width = Math.Max(4, e.Width - dx); e.Height = Math.Max(4, e.Height + dy); break;
                case 7: e.X += dx; e.Width = Math.Max(4, e.Width - dx); break;
            }
        }

        private void HandlePropsClick(PointF pos)
        {
            if (_selected == null) return;
            var cr = ContentRect;
            float propX = cr.Right - PropW;
            float lx = propX + PAD;
            float fw = PropW - PAD * 2;
            float y = cr.Y + PAD + 14f + 14f + 6f; // skip header rows

            // Rebuild property hit-test list on click
            // (mirrors DrawProperties layout exactly)
            var props = BuildPropList(_selected);
            foreach (var (id, label, valueStr, isColor, setter) in props)
            {
                float fx = lx + 90f + (isColor ? 18f : 0f);
                float fw2 = fw - 90f - (isColor ? 18f : 0f);
                var fr = new RectangleF(fx, y, fw2, 18f);
                if (fr.Contains(pos))
                {
                    StartPropEdit(id, valueStr, setter);
                    return;
                }

                // Bool toggle
                if (setter == null)
                {
                    var cb = new RectangleF(lx + 90f, y + 2f, 14f, 14f);
                    if (cb.Contains(pos) && _selected != null)
                    {
                        _selected.Visible = !_selected.Visible;
                        return;
                    }
                }
                y += RowH;
            }

            // Delete / layer buttons (approximate)
            float propBottom = cr.Y + cr.Height;
            var delBtn = new RectangleF(lx, propBottom - 56f, fw, 20f);
            if (delBtn.Contains(pos) && _selected != null)
            {
                _doc.Remove(_selected);
                _selected = null;
                return;
            }
            float bw = (fw - 4f) / 2f;
            var upBtn = new RectangleF(lx, propBottom - 80f, bw, 20f);
            var dnBtn = new RectangleF(lx + bw + 4f, propBottom - 80f, bw, 20f);
            if (upBtn.Contains(pos) && _selected != null) { _doc.BringForward(_selected); return; }
            if (dnBtn.Contains(pos) && _selected != null) { _doc.SendBackward(_selected); return; }

            // Enum buttons
            HandleEnumClicks(pos, propX + PAD, fw, _selected);
        }

        private void HandleEnumClicks(PointF pos, float lx, float fw, UIElement sel)
        {
            // Alignment enum for text
            if (sel is UITextElement te)
            {
                float y = GetEnumRowY(sel, "Alignment");
                string[] opts = { "Left", "Center", "Right" };
                float bw = (fw - 90f - 4f) / opts.Length;
                float bx = lx + 90f;
                for (int i = 0; i < opts.Length; i++)
                {
                    if (new RectangleF(bx, y, bw, 18f).Contains(pos))
                    { te.Alignment = (UITextAlignment)i; return; }
                    bx += bw + 2f;
                }
            }
            // Orientation for scrollbar
            if (sel is UIScrollbarElement se)
            {
                float y = GetEnumRowY(sel, "Orientation");
                string[] opts = { "Horizontal", "Vertical" };
                float bw = (fw - 90f - 2f) / opts.Length;
                float bx = lx + 90f;
                for (int i = 0; i < opts.Length; i++)
                {
                    if (new RectangleF(bx, y, bw, 18f).Contains(pos))
                    { se.Orientation = (UIScrollbarOrientation)i; return; }
                    bx += bw + 2f;
                }
            }
        }

        private float GetEnumRowY(UIElement sel, string propName)
        {
            // Count rows before the enum field to compute its Y
            var cr = ContentRect;
            float y = cr.Y + PAD + 14f + 14f + 6f;
            foreach (var (id, label, _, _, _) in BuildPropList(sel))
            {
                if (label == propName) return y;
                y += RowH;
            }
            return y;
        }

        // Returns a flat list of (id, label, displayValue, isColor, setter) tuples
        // matching the same order as DrawProperties — used for hit testing.
        private IEnumerable<(string id, string label, string val, bool isColor, Action<string>? setter)>
            BuildPropList(UIElement sel)
        {
            // Common
            yield return (sel.Id + "_name", "Name", sel.Name, false, v => sel.Name = v);
            yield return (sel.Id + "_x", "X", $"{sel.X:F2}", false, v => { if (float.TryParse(v, out float f)) sel.X = f; });
            yield return (sel.Id + "_y", "Y", $"{sel.Y:F2}", false, v => { if (float.TryParse(v, out float f)) sel.Y = f; });
            yield return (sel.Id + "_w", "Width", $"{sel.Width:F2}", false, v => { if (float.TryParse(v, out float f)) sel.Width = Math.Max(4, f); });
            yield return (sel.Id + "_h", "Height", $"{sel.Height:F2}", false, v => { if (float.TryParse(v, out float f)) sel.Height = Math.Max(4, f); });
            yield return (sel.Id + "_vis", "Visible", sel.Visible ? "true" : "false", false, null);
            // Type-specific
            switch (sel)
            {
                case UITextElement te:
                    yield return (te.Id + "_txt", "Text", te.Text, false, v => te.Text = v);
                    yield return (te.Id + "_fs", "Font Size", $"{te.FontSize:F1}", false, v => { if (float.TryParse(v, out float f)) te.FontSize = Math.Max(4, f); });
                    yield return (te.Id + "_col", "Color", ColorToHex(te.Color), true, v => te.Color = ParseColor(v, te.Color));
                    yield return (te.Id + "_aln", "Alignment", te.Alignment.ToString(), false, null);  // enum, handled separately
                    break;
                case UIButtonElement be:
                    yield return (be.Id + "_txt", "Text", be.Text, false, v => be.Text = v);
                    yield return (be.Id + "_fs", "Font Size", $"{be.FontSize:F1}", false, v => { if (float.TryParse(v, out float f)) be.FontSize = Math.Max(4, f); });
                    yield return (be.Id + "_bg", "BG Color", ColorToHex(be.BackgroundColor), true, v => be.BackgroundColor = ParseColor(v, be.BackgroundColor));
                    yield return (be.Id + "_tc", "Text Color", ColorToHex(be.TextColor), true, v => be.TextColor = ParseColor(v, be.TextColor));
                    yield return (be.Id + "_hov", "Hover", ColorToHex(be.HoverColor), true, v => be.HoverColor = ParseColor(v, be.HoverColor));
                    yield return (be.Id + "_prs", "Pressed", ColorToHex(be.PressedColor), true, v => be.PressedColor = ParseColor(v, be.PressedColor));
                    yield return (be.Id + "_evt", "OnClick", be.OnClickEvent, false, v => be.OnClickEvent = v);
                    break;
                case UITextFieldElement fe:
                    yield return (fe.Id + "_ph", "Placeholder", fe.Placeholder, false, v => fe.Placeholder = v);
                    yield return (fe.Id + "_txt", "Text", fe.Text, false, v => fe.Text = v);
                    yield return (fe.Id + "_fs", "Font Size", $"{fe.FontSize:F1}", false, v => { if (float.TryParse(v, out float f)) fe.FontSize = Math.Max(4, f); });
                    yield return (fe.Id + "_ml", "Max Length", fe.MaxLength.ToString(), false, v => { if (int.TryParse(v, out int i)) fe.MaxLength = Math.Max(1, i); });
                    yield return (fe.Id + "_bg", "BG Color", ColorToHex(fe.BackgroundColor), true, v => fe.BackgroundColor = ParseColor(v, fe.BackgroundColor));
                    yield return (fe.Id + "_tc", "Text Color", ColorToHex(fe.TextColor), true, v => fe.TextColor = ParseColor(v, fe.TextColor));
                    yield return (fe.Id + "_brc", "Border", ColorToHex(fe.BorderColor), true, v => fe.BorderColor = ParseColor(v, fe.BorderColor));
                    yield return (fe.Id + "_fbc", "Focus Border", ColorToHex(fe.FocusBorderColor), true, v => fe.FocusBorderColor = ParseColor(v, fe.FocusBorderColor));
                    break;
                case UIScrollbarElement se:
                    yield return (se.Id + "_ori", "Orientation", se.Orientation.ToString(), false, null);
                    yield return (se.Id + "_min", "Min", $"{se.MinValue:F2}", false, v => { if (float.TryParse(v, out float f)) se.MinValue = f; });
                    yield return (se.Id + "_max", "Max", $"{se.MaxValue:F2}", false, v => { if (float.TryParse(v, out float f)) se.MaxValue = f; });
                    yield return (se.Id + "_val", "Value", $"{se.Value:F2}", false, v => { if (float.TryParse(v, out float f)) se.Value = Math.Clamp(f, se.MinValue, se.MaxValue); });
                    yield return (se.Id + "_ts", "Thumb Size", $"{se.ThumbSize:F2}", false, v => { if (float.TryParse(v, out float f)) se.ThumbSize = Math.Clamp(f, 0.05f, 1f); });
                    yield return (se.Id + "_trc", "Track Color", ColorToHex(se.TrackColor), true, v => se.TrackColor = ParseColor(v, se.TrackColor));
                    yield return (se.Id + "_thc", "Thumb Color", ColorToHex(se.ThumbColor), true, v => se.ThumbColor = ParseColor(v, se.ThumbColor));
                    break;
            }
        }

        private void StartPropEdit(string id, string initial, Action<string>? setter)
        {
            if (setter == null) return;
            _propEditId = id;
            _propBuf = initial.TrimStart('#');
            _propCommit = setter;
        }

        private void CommitPropEdit()
        {
            if (_propCommit != null && _propEditId != null)
            {
                // Re-add # prefix if this is a color field
                string val = _propEditId.Length > 3 && IsColorId(_propEditId)
                    ? "#" + _propBuf.TrimStart('#')
                    : _propBuf;
                _propCommit(val);
            }
            _propEditId = null;
        }

        private bool IsColorId(string id)
            => id.EndsWith("_col") || id.EndsWith("_bg") || id.EndsWith("_tc")
            || id.EndsWith("_hov") || id.EndsWith("_prs") || id.EndsWith("_brc")
            || id.EndsWith("_fbc") || id.EndsWith("_trc") || id.EndsWith("_thc");

        private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color ParseColor(string s, Color fallback)
        {
            s = s.TrimStart('#');
            if (s.Length == 6)
            {
                try
                {
                    int r = Convert.ToInt32(s[..2], 16);
                    int g = Convert.ToInt32(s[2..4], 16);
                    int b = Convert.ToInt32(s[4..6], 16);
                    return Color.FromArgb(255, r, g, b);
                }
                catch { }
            }
            return fallback;
        }

        private static Color DarkenColor(Color c, float f)
            => Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
    }
}