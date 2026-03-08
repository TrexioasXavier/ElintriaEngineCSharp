using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Generic;
using System.Drawing;

namespace Elintria.Editor.UI
{
    public enum DockSide { Left, Right, Top, Bottom, Center, Float }

    // =========================================================================
    // DockWindow
    // =========================================================================
    public class DockWindow : Panel
    {
        // colours
        static readonly Color C_TitleActive = Color.FromArgb(255, 60, 60, 60);
        static readonly Color C_TitleInact = Color.FromArgb(255, 42, 42, 42);
        static readonly Color C_TitleText = Color.FromArgb(255, 210, 210, 210);
        static readonly Color C_TitleDim = Color.FromArgb(180, 150, 150, 150);
        static readonly Color C_Body = Color.FromArgb(255, 50, 50, 50);
        static readonly Color C_Border = Color.FromArgb(255, 26, 26, 26);
        static readonly Color C_ResizeHot = Color.FromArgb(180, 80, 120, 220);
        static readonly Color C_DropZone = Color.FromArgb(100, 44, 93, 180);

        public const float TITLE_H = 22f;
        public const float RESIZE_SZ = 10f;

        public string Title { get; set; }
        public Panel Content { get; private set; }
        public bool IsFloating { get; set; }
        public bool IsFocused { get; set; }
        public DockSide DockedSide { get; set; } = DockSide.Float;

        // drag / resize
        private bool _dragging, _resizing;
        private Vector2 _dragOffset, _resizeStart, _sizeAtResize;

        // drop-zone highlight
        internal DockSide? HighlightZone;

        public System.Action<DockWindow> OnStartDrag;
        public System.Action<DockWindow, DockSide> OnDropped;

        private readonly BitmapFont _font;
        private readonly DockingSystem _sys;

        // ---------------------------------------------------------------
        public DockWindow(string title, Panel content, BitmapFont font,
                          DockingSystem sys)
        {
            Title = title;
            Content = content;
            _font = font;
            _sys = sys;
            BackgroundColor = C_Body;

            // Content sits below title bar, in LOCAL coordinates
            content.Position = new Vector2(0, TITLE_H);
            AddChild(content);
        }

        // ---------------------------------------------------------------
        // Update
        // ---------------------------------------------------------------
        private Vector2 _lastSize;

        public override void Update(float dt)
        {
            if (Size != _lastSize)
            {
                _lastSize = Size;
                Content.Position = new Vector2(0, TITLE_H);
                Content.Size = new Vector2(Size.X, Size.Y - TITLE_H);
            }

            if (_dragging)
            {
                var mp = GetMousePosition();
                Position = mp - _dragOffset;
            }
            if (_resizing)
            {
                var mp = GetMousePosition();
                Size = Vector2.ComponentMax(_sizeAtResize + (mp - _resizeStart),
                                              new Vector2(120, 80));
            }

            base.Update(dt);
        }

        // ---------------------------------------------------------------
        // Draw
        // ---------------------------------------------------------------
        public override void Draw()
        {
            if (!Visible) return;

            // DockWindows have NO Panel parent — Position is absolute screen-space.
            float ax = Position.X;
            float ay = Position.Y;

            UIRenderer.DrawRect(ax, ay, Size.X, Size.Y, C_Body);

            Color titleBg = IsFocused ? C_TitleActive : C_TitleInact;
            UIRenderer.DrawRect(ax, ay, Size.X, TITLE_H, titleBg);
            _font?.DrawText(Title, ax + 6f, ay + 4f,
                IsFocused ? C_TitleText : C_TitleDim);

            if (IsFloating)
                _font?.DrawText("⊡", ax + Size.X - 18f, ay + 4f,
                    IsFocused ? C_TitleText : C_TitleDim);

            UIRenderer.DrawRectOutline(ax, ay, Size.X, Size.Y, C_Border);

            if (IsFloating && (_resizing || IsInResizeZone(GetMousePosition())))
                UIRenderer.DrawRect(ax + Size.X - RESIZE_SZ,
                                    ay + Size.Y - RESIZE_SZ,
                                    RESIZE_SZ, RESIZE_SZ, C_ResizeHot);

            // drop-zone overlay
            if (HighlightZone.HasValue)
                DrawDropZone(ax, ay, HighlightZone.Value);

            foreach (var c in Children) c.Draw();
        }

        private void DrawDropZone(float ax, float ay, DockSide side)
        {
            float r = 0.25f;
            (float zx, float zy, float zw, float zh) = side switch
            {
                DockSide.Left => (ax, ay + TITLE_H, Size.X * r, Size.Y - TITLE_H),
                DockSide.Right => (ax + Size.X * (1 - r), ay + TITLE_H, Size.X * r, Size.Y - TITLE_H),
                DockSide.Top => (ax, ay + TITLE_H, Size.X, Size.Y * r),
                DockSide.Bottom => (ax, ay + Size.Y * (1 - r), Size.X, Size.Y * r),
                _ => (ax, ay, Size.X, Size.Y),
            };
            UIRenderer.DrawRect(zx, zy, zw, zh, C_DropZone);
            UIRenderer.DrawRectOutline(zx, zy, zw, zh,
                Color.FromArgb(200, 60, 110, 220), 2f);
        }

        // ---------------------------------------------------------------
        // Input — handles BOTH left-click (drag) and right-click (pass to content)
        // ---------------------------------------------------------------
        public override bool HandleMouseDown(MouseButtonEventArgs e)
        {
            if (!Visible) return false;

            var mp = GetMousePosition();
            // DockWindow has no Panel parent, so absolute == Position
            float ax = Position.X, ay = Position.Y;

            bool inWindow = mp.X >= ax && mp.X <= ax + Size.X
                         && mp.Y >= ay && mp.Y <= ay + Size.Y;
            if (!inWindow) return false;

            _sys.SetFocus(this);

            // Resize handle (LMB only, floating windows)
            if (e.Button == MouseButton.Left && IsFloating && IsInResizeZone(mp))
            {
                _resizing = true;
                _resizeStart = mp;
                _sizeAtResize = Size;
                return true;
            }

            // Title-bar drag (LMB only)
            bool inTitle = e.Button == MouseButton.Left
                        && mp.Y >= ay && mp.Y <= ay + TITLE_H;
            if (inTitle)
            {
                _dragging = true;
                _dragOffset = mp - new Vector2(ax, ay);
                OnStartDrag?.Invoke(this);
                return true;
            }

            // Pass everything else (including RMB) to content children
            return base.HandleMouseDown(e);
        }

        public override bool HandleMouseUp(MouseButtonEventArgs e)
        {
            if (e.Button == MouseButton.Left)
            {
                if (_dragging)
                {
                    _dragging = false;
                    var drop = _sys.GetDropTarget(this);
                    if (drop.HasValue) OnDropped?.Invoke(this, drop.Value);
                    _sys.EndDrag();
                }
                _resizing = false;
            }
            return base.HandleMouseUp(e);
        }

        // ---------------------------------------------------------------
        private bool IsInResizeZone(Vector2 mp)
        {
            float ax = Position.X, ay = Position.Y;
            return mp.X >= ax + Size.X - RESIZE_SZ && mp.Y >= ay + Size.Y - RESIZE_SZ
                && mp.X <= ax + Size.X && mp.Y <= ay + Size.Y;
        }

        public bool IsDragging => _dragging;

        // Override IsPointInside — DockWindow has no parent, Position IS absolute
        public override bool IsPointInside(Vector2 p)
        {
            return p.X >= Position.X && p.X <= Position.X + Size.X
                && p.Y >= Position.Y && p.Y <= Position.Y + Size.Y;
        }

        // GetAbsolutePosition for DockWindow (no parent)
        public override Vector2 GetAbsolutePosition() => Position;
    }

    // =========================================================================
    // DockingSystem
    // =========================================================================
    public class DockingSystem
    {
        private readonly List<DockWindow> _windows = new();
        private DockWindow _focused;
        private DockWindow _dragging;
        private readonly BitmapFont _font;

        public IReadOnlyList<DockWindow> Windows => _windows;

        public DockingSystem(BitmapFont font) { _font = font; }

        // ------------------------------------------------------------------
        public DockWindow CreateWindow(string title, Panel content)
        {
            var w = new DockWindow(title, content, _font, this);
            w.OnStartDrag += StartDrag;
            w.OnDropped += HandleDrop;
            _windows.Add(w);
            if (_focused == null) SetFocus(w);
            return w;
        }

        public void SetFocus(DockWindow w)
        {
            if (_focused != null) _focused.IsFocused = false;
            _focused = w;
            if (_focused != null) _focused.IsFocused = true;
        }

        private void StartDrag(DockWindow w)
        {
            _dragging = w;
            w.IsFloating = true;
        }

        public void EndDrag()
        {
            if (_dragging != null)
                foreach (var win in _windows) win.HighlightZone = null;
            _dragging = null;
        }

        public DockSide? GetDropTarget(DockWindow drag)
        {
            var mp = drag.GetMousePosition();
            foreach (var win in _windows)
            {
                if (win == drag || win.IsFloating) continue;
                if (!win.IsPointInside(mp)) continue;
                float ax = win.Position.X, ay = win.Position.Y;
                float w = win.Size.X, h = win.Size.Y;
                float zw = w * 0.25f, zh = h * 0.25f;
                if (mp.X < ax + zw) return DockSide.Left;
                if (mp.X > ax + w - zw) return DockSide.Right;
                if (mp.Y < ay + zh + DockWindow.TITLE_H) return DockSide.Top;
                if (mp.Y > ay + h - zh) return DockSide.Bottom;
                return DockSide.Center;
            }
            return null;
        }

        private void HandleDrop(DockWindow dropped, DockSide side)
        {
            dropped.IsFloating = false;
            dropped.DockedSide = side;
        }

        public void Update(float dt, Panel _ignored)
        {
            if (_dragging != null)
            {
                foreach (var win in _windows) win.HighlightZone = null;
                var mp = _dragging.GetMousePosition();
                foreach (var win in _windows)
                {
                    if (win == _dragging || win.IsFloating) continue;
                    if (!win.IsPointInside(mp)) continue;
                    float ax = win.Position.X, ay = win.Position.Y;
                    float w = win.Size.X, h = win.Size.Y;
                    float zw = w * 0.25f, zh = h * 0.25f;
                    win.HighlightZone =
                          mp.X < ax + zw ? DockSide.Left
                        : mp.X > ax + w - zw ? DockSide.Right
                        : mp.Y < ay + zh + DockWindow.TITLE_H ? DockSide.Top
                        : mp.Y > ay + h - zh ? DockSide.Bottom
                        : DockSide.Center;
                }
            }
            foreach (var win in _windows) win.Update(dt);
        }

        public void Draw()
        {
            foreach (var win in _windows) if (!win.IsFloating) win.Draw();
            foreach (var win in _windows) if (win.IsFloating) win.Draw();
        }

        public bool HandleMouseDown(MouseButtonEventArgs e)
        {
            // floating first (topmost)
            for (int i = _windows.Count - 1; i >= 0; i--)
                if (_windows[i].IsFloating && _windows[i].HandleMouseDown(e)) return true;
            for (int i = _windows.Count - 1; i >= 0; i--)
                if (!_windows[i].IsFloating && _windows[i].HandleMouseDown(e)) return true;
            return false;
        }

        public bool HandleMouseUp(MouseButtonEventArgs e)
        {
            foreach (var win in _windows) win.HandleMouseUp(e);
            return false;
        }

        public bool HandleKeyDown(KeyboardKeyEventArgs e)
        {
            _focused?.HandleKeyDown(e);
            return false;
        }

        public bool HandleTextInput(TextInputEventArgs e)
        {
            _focused?.HandleTextInput(e);
            return false;
        }

        public void HandleMouseMove(MouseMoveEventArgs e)
        {
            foreach (var w in _windows)
                w.HandleMouseMove(e);
        }

    }

}