using System;
using System.Drawing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;
using ElintriaEngine.Rendering.Scene;

namespace ElintriaEngine.UI.Panels
{
    /// <summary>
    /// 3D scene viewport panel.
    ///
    /// RENDERING ARCHITECTURE:
    ///   The 3D render and the 2D UI render are kept strictly separate to avoid
    ///   GL state corruption:
    ///
    ///   1. EditorLayout.Render3D()  →  SceneViewPanel.Render3D(winW, winH)
    ///         Runs raw GL calls (viewport, depth test, shaders…)
    ///         Called BEFORE BeginFrame / any 2D batch calls.
    ///
    ///   2. EditorLayout.Render2D()  →  SceneViewPanel.OnRender(IEditorRenderer r)
    ///         Draws only the 2D chrome (header, toolbar, border) via the batch.
    ///         The 3D content underneath was already rendered in step 1.
    ///         Called INSIDE BeginFrame … EndFrame.
    /// </summary>
    public class SceneViewPanel : Panel
    {
        private readonly SceneRenderer _sceneRenderer = new();
        private Core.Scene? _scene;

        private bool _orbiting, _panning;
        private PointF _lastMouse;

        public SceneViewPanel(RectangleF bounds) : base("Scene", bounds)
        {
            MinWidth = 200f;
            MinHeight = 150f;
        }

        public void SetScene(Core.Scene? s) => _scene = s;
        public void SetSelected(GameObject? go) => _sceneRenderer.Selected = go;

        /// <summary>Call once after GL context is created.</summary>
        public void Init() => _sceneRenderer.Init();

        // ─────────────────────────────────────────────────────────────────────
        // PHASE 1 — pure GL render (called BEFORE 2D batch begins)
        // ─────────────────────────────────────────────────────────────────────
        public void Render3D(int winW, int winH)
        {
            if (!IsVisible) return;
            _sceneRenderer.Render(SceneRect, _scene, winW, winH);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PHASE 2 — 2D chrome only (called inside BeginFrame … EndFrame)
        // ─────────────────────────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;

            // Header
            DrawHeader(r);

            // Toolbar strip
            var toolbar = new RectangleF(Bounds.X, Bounds.Y + HeaderH, Bounds.Width, 22f);
            r.FillRect(toolbar, Color.FromArgb(255, 28, 28, 28));
            r.DrawLine(new PointF(Bounds.X, toolbar.Bottom),
                       new PointF(Bounds.Right, toolbar.Bottom),
                       Color.FromArgb(255, 50, 50, 50));
            r.DrawText("Perspective  |  Shaded",
                new PointF(Bounds.X + 8f, toolbar.Y + 5f),
                Color.FromArgb(255, 175, 175, 175), 10f);

            var cam = _sceneRenderer.Camera;
            string ci = $"Yaw:{cam.Yaw:F0}  Pitch:{cam.Pitch:F0}  Dist:{cam.Distance:F1}";
            r.DrawText(ci, new PointF(Bounds.Right - ci.Length * 5.4f - 6f, toolbar.Y + 5f),
                Color.FromArgb(255, 120, 120, 120), 9f);

            // Hint overlay when scene is empty
            var vp = SceneRect;
            if (_scene == null || !HasRenderable())
            {
                var hint = new RectangleF(vp.X + vp.Width / 2f - 125f,
                                          vp.Y + vp.Height / 2f - 10f, 250f, 22f);
                r.FillRect(hint, Color.FromArgb(110, 0, 0, 0));
                r.DrawText("Right-click Hierarchy > Create to add objects",
                    new PointF(hint.X + 6f, hint.Y + 5f),
                    Color.FromArgb(200, 200, 200, 200), 10f);
            }

            // Panel border on top of the 3D content
            r.DrawRect(Bounds, Color.FromArgb(255, 55, 55, 55), 1f);
        }

        private bool HasRenderable()
        {
            if (_scene == null) return false;
            foreach (var go in _scene.All())
                if (go.GetComponent<MeshFilter>() != null) return true;
            return false;
        }

        public RectangleF SceneRect => new(
            Bounds.X, Bounds.Y + HeaderH + 22f,
            Bounds.Width, Bounds.Height - HeaderH - 22f);

        // ── Mouse ─────────────────────────────────────────────────────────────
        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible || !SceneRect.Contains(pos))
            {
                if (Bounds.Contains(pos)) base.OnMouseDown(e, pos);
                return;
            }
            IsFocused = true;
            _lastMouse = pos;
            if (e.Button == MouseButton.Left) _orbiting = true;
            else if (e.Button == MouseButton.Middle) _panning = true;
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _orbiting = false; _panning = false;
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseMove(PointF pos)
        {
            float dx = pos.X - _lastMouse.X;
            float dy = pos.Y - _lastMouse.Y;
            _lastMouse = pos;

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
            else
            {
                base.OnMouseMove(pos);
            }
        }

        public override void OnMouseScroll(float delta)
        {
            if (!IsVisible) return;
            _sceneRenderer.Camera.Distance =
                Math.Clamp(_sceneRenderer.Camera.Distance * (1f - delta * 0.12f), 0.1f, 2000f);
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (!IsFocused) return;
            var cam = _sceneRenderer.Camera;
            switch (e.Key)
            {
                case Keys.F:
                    cam.Target = OpenTK.Mathematics.Vector3.Zero;
                    cam.Distance = 8f; cam.Yaw = 45f; cam.Pitch = 25f;
                    break;
                case Keys.Up: cam.Yaw = 0f; cam.Pitch = 0f; break;
                case Keys.Down: cam.Yaw = 90f; cam.Pitch = 0f; break;
                case Keys.Left: cam.Yaw = 45f; cam.Pitch = 89f; break;
            }
        }

        public void Dispose() => _sceneRenderer.Dispose();
    }
}