using System;
using System.Drawing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;
using ElintriaEngine.Rendering.Scene;

namespace ElintriaEngine.UI.Panels
{
    /// <summary>
    /// Combined Scene / Game view panel.
    ///
    /// TABS
    /// ----
    ///   Scene tab — editor camera orbit/pan/zoom, normal operation.
    ///   Game  tab — renders using the game camera (or Scene camera if none is set).
    ///               Activates automatically when play mode starts; returns to Scene on Stop.
    ///               Shows a "PLAYING" indicator and the UI Document overlay.
    ///
    /// RENDERING ARCHITECTURE (unchanged)
    ///   Phase 1  EditorLayout.Render3D()  →  Render3D(winW, winH)   raw GL, before BeginFrame
    ///   Phase 2  EditorLayout.Render2D()  →  OnRender(r)            2-D chrome, inside BeginFrame
    /// </summary>
    public class SceneViewPanel : Panel
    {
        // ── Scene renderer ────────────────────────────────────────────────────
        private readonly SceneRenderer _sceneRenderer = new();
        private Core.Scene? _scene;

        // ── Tab state ─────────────────────────────────────────────────────────
        public enum ViewTab { Scene, Game }
        private ViewTab _activeTab = ViewTab.Scene;

        /// <summary>
        /// Call from EditorLayout when play mode starts / stops.
        /// </summary>
        public ViewTab ActiveTab
        {
            get => _activeTab;
            set { _activeTab = value; }
        }

        // Whether the game is actually playing (drives the "PLAYING" badge)
        public bool IsPlaying { get; set; } = false;
        public bool IsPaused { get; set; } = false;

        // UI Document to overlay on the Game view
        public Core.UIDocument? UIDocument { get; set; }

        // ── Editor camera orbit state ─────────────────────────────────────────
        private bool _orbiting, _panning;
        private PointF _lastMouse;

        // ── Tab bar geometry (cached per render) ──────────────────────────────
        private RectangleF _sceneTabRect;
        private RectangleF _gameTabRect;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color CTabBg = Color.FromArgb(255, 28, 28, 30);
        private static readonly Color CTabActive = Color.FromArgb(255, 38, 38, 42);
        private static readonly Color CTabHover = Color.FromArgb(255, 34, 34, 38);
        private static readonly Color CTabLine = Color.FromArgb(255, 60, 130, 255);
        private static readonly Color CPlaying = Color.FromArgb(255, 55, 195, 85);
        private static readonly Color CPaused = Color.FromArgb(255, 230, 165, 30);
        private static readonly Color CGameBg = Color.FromArgb(255, 10, 10, 12);

        private PointF _mouse;

        // ── Layout constants ──────────────────────────────────────────────────
        private const float TabBarH = 26f;
        private const float TabW = 88f;

        public SceneViewPanel(RectangleF bounds) : base("Scene", bounds)
        {
            MinWidth = 200f;
            MinHeight = 160f;
        }

        public void SetScene(Core.Scene? s) => _scene = s;
        public void SetSelected(GameObject? go) => _sceneRenderer.Selected = go;
        public void Init() => _sceneRenderer.Init();
        public SceneRenderer Renderer => _sceneRenderer;

        // ── Content rect: below header + tab bar ─────────────────────────────
        public RectangleF ViewportRect => new(
            Bounds.X,
            Bounds.Y + HeaderH + TabBarH,
            Bounds.Width,
            Bounds.Height - HeaderH - TabBarH);

        // Legacy alias used by EditorLayout UI-overlay code
        public RectangleF SceneRect => ViewportRect;

        // ── PHASE 1: raw GL render ────────────────────────────────────────────
        public void Render3D(int winW, int winH)
        {
            if (!IsVisible) return;
            _sceneRenderer.Render(ViewportRect, _scene, winW, winH);
        }

        // ── PHASE 2: 2D chrome ────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;

            DrawHeader(r);
            DrawTabBar(r);

            var vp = ViewportRect;

            if (_activeTab == ViewTab.Scene)
                DrawSceneChrome(r, vp);
            else
                DrawGameChrome(r, vp);

            r.DrawRect(Bounds, Color.FromArgb(255, 55, 55, 55), 1f);
        }

        private void DrawTabBar(IEditorRenderer r)
        {
            var bar = new RectangleF(Bounds.X, Bounds.Y + HeaderH, Bounds.Width, TabBarH);
            r.FillRect(bar, CTabBg);
            r.DrawLine(new PointF(bar.X, bar.Bottom),
                       new PointF(bar.Right, bar.Bottom),
                       Color.FromArgb(255, 50, 50, 55));

            _sceneTabRect = new RectangleF(bar.X + 4f, bar.Y + 2f, TabW, TabBarH - 4f);
            _gameTabRect = new RectangleF(bar.X + TabW + 10f, bar.Y + 2f, TabW, TabBarH - 4f);

            DrawTab(r, _sceneTabRect, "Scene", _activeTab == ViewTab.Scene,
                Color.FromArgb(255, 100, 165, 255));
            DrawTab(r, _gameTabRect, "Game", _activeTab == ViewTab.Game,
                IsPlaying ? CPlaying : Color.FromArgb(255, 100, 165, 255));

            // Playing / paused badge on the right side of the tab bar
            if (IsPlaying)
            {
                Color badge = IsPaused ? CPaused : CPlaying;
                string label = IsPaused ? "⏸  PAUSED" : "▶  PLAYING";
                float lw = label.Length * 7.2f + 18f;
                var lr = new RectangleF(bar.Right - lw - 10f, bar.Y + 4f, lw, TabBarH - 8f);
                r.FillRect(lr, Color.FromArgb(40, badge.R, badge.G, badge.B));
                r.DrawRect(lr, badge);
                r.DrawText(label, new PointF(lr.X + 8f, lr.Y + 4f), badge, 9f);
            }
        }

        private void DrawTab(IEditorRenderer r, RectangleF tab, string label,
            bool active, Color accentColor)
        {
            bool hov = !active && tab.Contains(_mouse);
            r.FillRect(tab, active ? CTabActive : hov ? CTabHover : Color.Transparent);
            r.DrawText(label, new PointF(tab.X + (tab.Width - label.Length * 6.5f) / 2f,
                tab.Y + (tab.Height - 11f) / 2f),
                active ? Color.FromArgb(255, 220, 220, 225) : Color.FromArgb(255, 140, 140, 148),
                10f);
            // Active underline
            if (active)
                r.FillRect(new RectangleF(tab.X + 4f, tab.Bottom - 2f,
                    tab.Width - 8f, 2f), accentColor);
        }

        private void DrawSceneChrome(IEditorRenderer r, RectangleF vp)
        {
            // Hint overlay when scene is empty
            if (_scene == null || !HasRenderable())
            {
                var hint = new RectangleF(vp.X + vp.Width / 2f - 150f,
                                          vp.Y + vp.Height / 2f - 12f, 300f, 24f);
                r.FillRect(hint, Color.FromArgb(110, 0, 0, 0));
                r.DrawText("Right-click Hierarchy → Create to add objects",
                    new PointF(hint.X + 8f, hint.Y + 6f),
                    Color.FromArgb(200, 200, 200, 200), 10f);
            }

            // Camera info bottom-right
            var cam = _sceneRenderer.Camera;
            string ci = $"Yaw:{cam.Yaw:F0}°  Pitch:{cam.Pitch:F0}°  Dist:{cam.Distance:F1}";
            r.DrawText(ci, new PointF(vp.Right - ci.Length * 5.4f - 8f, vp.Bottom - 16f),
                Color.FromArgb(180, 130, 130, 130), 9f);
        }

        private void DrawGameChrome(IEditorRenderer r, RectangleF vp)
        {
            if (!IsPlaying)
            {
                // Not in play mode — draw a dark overlay with instruction
                r.FillRect(vp, Color.FromArgb(200, 10, 10, 12));
                string msg = "Press ▶ Play to enter Game View";
                float mw = msg.Length * 6.8f;
                r.DrawText(msg, new PointF(vp.X + (vp.Width - mw) / 2f,
                    vp.Y + vp.Height / 2f - 8f),
                    Color.FromArgb(180, 160, 160, 165), 11f);
                return;
            }

            // Draw UI Document overlay on top of the game view
            if (UIDocument != null && UIDocument.Elements.Count > 0)
                Rendering.UIDocumentRenderer.Render(r, UIDocument, vp);

            // Thin border to mark game bounds
            r.DrawRect(vp, Color.FromArgb(80, CPlaying.R, CPlaying.G, CPlaying.B));
        }

        private bool HasRenderable()
        {
            if (_scene == null) return false;
            foreach (var go in _scene.All())
                if (go.GetComponent<MeshFilter>() != null) return true;
            return false;
        }

        // ── Mouse ─────────────────────────────────────────────────────────────
        public override void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            float dx = pos.X - _lastMouse.X;
            float dy = pos.Y - _lastMouse.Y;
            _lastMouse = pos;

            if (_activeTab == ViewTab.Scene)
            {
                var cam = _sceneRenderer.Camera;
                if (_orbiting)
                {
                    cam.Yaw += dx * 0.35f;
                    cam.Pitch = Math.Clamp(cam.Pitch - dy * 0.35f, -89f, 89f);
                }
                else if (_panning)
                {
                    var view = cam.GetViewMatrix();
                    var right = new OpenTK.Mathematics.Vector3(view.Row0.X, view.Row0.Y, view.Row0.Z);
                    var up = new OpenTK.Mathematics.Vector3(view.Row1.X, view.Row1.Y, view.Row1.Z);
                    float spd = cam.Distance * 0.002f;
                    cam.Target -= right * (dx * spd);
                    cam.Target += up * (dy * spd);
                }
                else base.OnMouseMove(pos);
            }
            else base.OnMouseMove(pos);
        }

        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;

            // Tab clicks
            if (_sceneTabRect.Contains(pos)) { _activeTab = ViewTab.Scene; return; }
            if (_gameTabRect.Contains(pos)) { _activeTab = ViewTab.Game; return; }

            if (!IsVisible) return;
            if (HeaderRect.Contains(pos)) { base.OnMouseDown(e, pos); return; }

            if (_activeTab == ViewTab.Scene && ViewportRect.Contains(pos))
            {
                IsFocused = true;
                _lastMouse = pos;
                if (e.Button == MouseButton.Left) _orbiting = true;
                else if (e.Button == MouseButton.Middle) _panning = true;
            }
            else
            {
                base.OnMouseDown(e, pos);
            }
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _orbiting = false; _panning = false;
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseScroll(float delta)
        {
            if (!IsVisible) return;
            if (_activeTab == ViewTab.Scene)
            {
                _sceneRenderer.Camera.Distance =
                    Math.Clamp(_sceneRenderer.Camera.Distance * (1f - delta * 0.12f), 0.1f, 2000f);
            }
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (!IsFocused || _activeTab != ViewTab.Scene) return;
            var cam = _sceneRenderer.Camera;
            switch (e.Key)
            {
                case Keys.F:
                    cam.Target = OpenTK.Mathematics.Vector3.Zero;
                    cam.Distance = 8f; cam.Yaw = 45f; cam.Pitch = 25f;
                    break;
                case Keys.Up: cam.Yaw = 0f; cam.Pitch = 0f; break;
                case Keys.Right: cam.Yaw = 90f; cam.Pitch = 0f; break;
                case Keys.Left: cam.Yaw = 45f; cam.Pitch = 89f; break;
            }
        }

        public void Dispose() => _sceneRenderer.Dispose();
    }
}