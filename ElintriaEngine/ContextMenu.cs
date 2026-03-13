using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace ElintriaEngine.UI.Panels
{
    // ── Menu item ────────────────────────────────────────────────────────────
    public class ContextMenuItem
    {
        public string Label { get; }
        public Action? Action { get; }
        public string Shortcut { get; init; } = "";
        public bool IsSeparator { get; init; } = false;
        public bool IsDisabled { get; set; } = false;
        public List<ContextMenuItem>? SubItems { get; init; }

        public ContextMenuItem(string label, Action? action)
        { Label = label; Action = action; }

        public static ContextMenuItem Separator =>
            new("", null) { IsSeparator = true };
    }

    // ── Context menu ─────────────────────────────────────────────────────────
    public class ContextMenu
    {
        private readonly List<ContextMenuItem> _items;
        private PointF _pos;
        private int _hovered = -1;

        private const float ItemH = 22f;
        private const float SepH = 8f;
        private const float MenuW = 210f;
        private const float Pad = 8f;

        private static readonly Color CBg = Color.FromArgb(245, 36, 36, 36);
        private static readonly Color CBorder = Color.FromArgb(255, 68, 68, 68);
        private static readonly Color CHover = Color.FromArgb(255, 60, 100, 200);
        private static readonly Color CText = Color.FromArgb(255, 215, 215, 215);
        private static readonly Color CDim = Color.FromArgb(255, 115, 115, 115);
        private static readonly Color CSep = Color.FromArgb(255, 60, 60, 60);
        private static readonly Color CShadow = Color.FromArgb(80, 0, 0, 0);

        public ContextMenu(PointF position, List<ContextMenuItem> items)
        {
            _items = items;
            // Clamp position so the menu never clips off screen edges.
            // We use safe defaults; call Reposition() after construction if you have screen size.
            _pos = position;
        }

        /// <summary>
        /// Call after construction to clamp the menu inside the window.
        /// </summary>
        public void Reposition(float screenW, float screenH)
        {
            float x = _pos.X;
            float y = _pos.Y;
            float h = TotalHeight();
            if (x + MenuW > screenW) x = screenW - MenuW - 4f;
            if (y + h > screenH) y = screenH - h - 4f;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            _pos = new PointF(x, y);
        }

        public float TotalHeight()
        {
            float h = 4f;
            foreach (var i in _items) h += i.IsSeparator ? SepH : ItemH;
            return h;
        }

        public RectangleF Bounds => new(_pos.X, _pos.Y, MenuW, TotalHeight());
        public bool ContainsPoint(PointF p) => Bounds.Contains(p);

        public void OnRender(IEditorRenderer r)
        {
            var b = Bounds;
            // Shadow
            r.FillRect(new RectangleF(b.X + 3, b.Y + 3, b.Width, b.Height), CShadow);
            r.FillRect(b, CBg);
            r.DrawRect(b, CBorder, 1f);

            float y = _pos.Y + 2f;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.IsSeparator)
                {
                    r.DrawLine(new PointF(_pos.X + 4f, y + SepH / 2f),
                               new PointF(_pos.X + MenuW - 4f, y + SepH / 2f), CSep);
                    y += SepH; continue;
                }
                var row = new RectangleF(_pos.X, y, MenuW, ItemH);
                if (i == _hovered && !item.IsDisabled) r.FillRect(row, CHover);
                var tc = item.IsDisabled ? CDim : CText;
                r.DrawText(item.Label, new PointF(_pos.X + Pad, y + 5f), tc, 11f);
                if (!string.IsNullOrEmpty(item.Shortcut))
                    r.DrawText(item.Shortcut,
                        new PointF(_pos.X + MenuW - 58f, y + 5f), CDim, 10f);
                if (item.SubItems != null)
                    r.DrawText("▶", new PointF(_pos.X + MenuW - 16f, y + 5f), CDim, 10f);
                y += ItemH;
            }
        }

        public bool OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            float y = _pos.Y + 2f;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                float rh = item.IsSeparator ? SepH : ItemH;
                if (!item.IsSeparator && !item.IsDisabled)
                {
                    var row = new RectangleF(_pos.X, y, MenuW, rh);
                    if (row.Contains(pos)) { item.Action?.Invoke(); return true; }
                }
                y += rh;
            }
            return false;
        }

        public void OnMouseMove(PointF pos)
        {
            _hovered = -1;
            float y = _pos.Y + 2f;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                float rh = item.IsSeparator ? SepH : ItemH;
                if (!item.IsSeparator)
                {
                    if (new RectangleF(_pos.X, y, MenuW, rh).Contains(pos)) _hovered = i;
                }
                y += rh;
            }
        }
    }
}