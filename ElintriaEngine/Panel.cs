using System;
using System.Drawing;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace ElintriaEngine.UI.Panels
{
    // ── Renderer abstraction ──────────────────────────────────────────────────
    public interface IEditorRenderer
    {
        void FillRect(RectangleF rect, Color color);
        void DrawRect(RectangleF rect, Color color, float thickness = 1f);
        void DrawLine(PointF from, PointF to, Color color, float thickness = 1f);
        void DrawText(string text, PointF position, Color color, float size = 12f);
        void DrawImage(string texturePath, RectangleF dest, Color tint);
        void PushClip(RectangleF rect);
        void PopClip();
        Vector2 MeasureText(string text, float size);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Panel – base class for every editor panel
    // ═══════════════════════════════════════════════════════════════════════════
    public abstract class Panel
    {
        // ── Identity / layout ─────────────────────────────────────────────────
        public string Title { get; set; }
        public RectangleF Bounds { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsFocused { get; set; } = false;
        public float MinWidth { get; protected set; } = 100f;
        public float MinHeight { get; protected set; } = 60f;

        // ── Scroll ────────────────────────────────────────────────────────────
        protected float ScrollOffset = 0f;
        protected float ContentHeight = 0f;

        /// <summary>When true the panel cannot be dragged or resized – use for all docked panels.</summary>
        public bool Locked { get; set; } = false;

        // ── Drag / resize ─────────────────────────────────────────────────────
        private bool _dragging;
        private bool _resizing;
        private PointF _dragOriginMouse;
        private RectangleF _dragOriginBounds;
        private ResizeEdge _resizeEdge;

        [Flags]
        private enum ResizeEdge { None = 0, L = 1, R = 2, T = 4, B = 8 }
        private const float ResizeHit = 5f;
        private const float ScrollBarW = 8f;

        // ── Theme ─────────────────────────────────────────────────────────────
        protected static readonly Color ColBg = Color.FromArgb(255, 38, 38, 38);
        protected static readonly Color ColHeader = Color.FromArgb(255, 28, 28, 28);
        protected static readonly Color ColBorder = Color.FromArgb(255, 58, 58, 58);
        protected static readonly Color ColText = Color.FromArgb(255, 210, 210, 210);
        protected static readonly Color ColTextDim = Color.FromArgb(255, 130, 130, 130);
        protected static readonly Color ColAccent = Color.FromArgb(255, 70, 130, 230);
        protected static readonly Color ColHover = Color.FromArgb(50, 255, 255, 255);
        protected static readonly Color ColSelected = Color.FromArgb(255, 55, 90, 185);
        protected const float HeaderH = 22f;

        // ── Constructor ───────────────────────────────────────────────────────
        protected Panel(string title, RectangleF bounds)
        {
            Title = title;
            Bounds = bounds;
        }

        // ── Abstract render ───────────────────────────────────────────────────
        public abstract void OnRender(IEditorRenderer r);
        public virtual void OnUpdate(double dt) { }

        // ── Rect helpers ──────────────────────────────────────────────────────
        public RectangleF HeaderRect => new(Bounds.X, Bounds.Y, Bounds.Width, HeaderH);
        public RectangleF ContentRect => new(Bounds.X, Bounds.Y + HeaderH,
                                             Bounds.Width - ScrollBarW,
                                             Bounds.Height - HeaderH);

        public bool ContainsPoint(PointF p) => IsVisible && Bounds.Contains(p);
        public bool IsPointInContent(PointF p) => IsVisible && ContentRect.Contains(p);

        protected PointF ToContentLocal(PointF world)
        {
            var cr = ContentRect;
            return new PointF(world.X - cr.X, world.Y - cr.Y + ScrollOffset);
        }

        // ── Default header draw ───────────────────────────────────────────────
        protected void DrawHeader(IEditorRenderer r)
        {
            r.FillRect(HeaderRect, IsFocused ? ColAccent : ColHeader);
            r.DrawText(Title, new PointF(Bounds.X + 6f, Bounds.Y + 4f), ColText, 11f);
            r.DrawRect(Bounds, ColBorder, 1f);
        }

        // ── Scroll bar ────────────────────────────────────────────────────────
        protected void DrawScrollBar(IEditorRenderer r)
        {
            var cr = ContentRect;
            if (ContentHeight <= cr.Height) return;

            var track = new RectangleF(cr.Right, cr.Y, ScrollBarW, cr.Height);
            r.FillRect(track, Color.FromArgb(255, 28, 28, 28));

            float ratio = cr.Height / ContentHeight;
            float thumbH = Math.Max(16f, cr.Height * ratio);
            float maxOff = ContentHeight - cr.Height;
            float thumbY = cr.Y + (maxOff > 0 ? ScrollOffset / maxOff : 0f) * (cr.Height - thumbH);
            r.FillRect(new RectangleF(track.X + 1f, thumbY, ScrollBarW - 2f, thumbH),
                Color.FromArgb(255, 80, 80, 80));
        }

        // ── Input ─────────────────────────────────────────────────────────────
        public virtual void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible) return;
            if (Locked) return;   // docked panels are never dragged or resized
            if (HeaderRect.Contains(pos) && e.Button == MouseButton.Left)
            {
                _dragging = true;
                _dragOriginMouse = pos;
                _dragOriginBounds = Bounds;
                return;
            }
            _resizeEdge = GetEdge(pos);
            if (_resizeEdge != ResizeEdge.None && e.Button == MouseButton.Left)
            {
                _resizing = true;
                _dragOriginMouse = pos;
                _dragOriginBounds = Bounds;
            }
        }

        public virtual void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _dragging = false;
            _resizing = false;
            _resizeEdge = ResizeEdge.None;
        }

        public virtual void OnMouseMove(PointF pos)
        {
            if (!IsVisible || Locked) return;
            if (_dragging)
            {
                float dx = pos.X - _dragOriginMouse.X;
                float dy = pos.Y - _dragOriginMouse.Y;
                Bounds = new RectangleF(
                    _dragOriginBounds.X + dx, _dragOriginBounds.Y + dy,
                    Bounds.Width, Bounds.Height);
                return;
            }
            if (_resizing) ApplyResize(pos);
        }

        public virtual void OnMouseScroll(float delta)
        {
            if (!IsVisible) return;
            float max = Math.Max(0f, ContentHeight - ContentRect.Height);
            ScrollOffset = Math.Clamp(ScrollOffset - delta * 24f, 0f, max);
        }

        public virtual void OnKeyDown(KeyboardKeyEventArgs e) { }
        public virtual void OnKeyUp(KeyboardKeyEventArgs e) { }
        public virtual void OnTextInput(TextInputEventArgs e) { }

        // ── Resize ────────────────────────────────────────────────────────────
        private ResizeEdge GetEdge(PointF p)
        {
            var b = Bounds;
            var e = ResizeEdge.None;
            if (p.X <= b.Left + ResizeHit) e |= ResizeEdge.L;
            if (p.X >= b.Right - ResizeHit) e |= ResizeEdge.R;
            if (p.Y <= b.Top + ResizeHit) e |= ResizeEdge.T;
            if (p.Y >= b.Bottom - ResizeHit) e |= ResizeEdge.B;
            return e;
        }

        private void ApplyResize(PointF p)
        {
            float dx = p.X - _dragOriginMouse.X;
            float dy = p.Y - _dragOriginMouse.Y;
            float x = _dragOriginBounds.X, y = _dragOriginBounds.Y;
            float w = _dragOriginBounds.Width, h = _dragOriginBounds.Height;

            if (_resizeEdge.HasFlag(ResizeEdge.R)) w = Math.Max(MinWidth, w + dx);
            if (_resizeEdge.HasFlag(ResizeEdge.B)) h = Math.Max(MinHeight, h + dy);
            if (_resizeEdge.HasFlag(ResizeEdge.L)) { float nw = Math.Max(MinWidth, w - dx); x += w - nw; w = nw; }
            if (_resizeEdge.HasFlag(ResizeEdge.T)) { float nh = Math.Max(MinHeight, h - dy); y += h - nh; h = nh; }

            Bounds = new RectangleF(x, y, w, h);
        }
    }
}