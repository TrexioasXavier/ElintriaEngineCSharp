using System;
using System.Drawing;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;
using ElintriaEngine.Rendering.Scene;

namespace ElintriaEngine.UI.Panels
{
    /// <summary>
    /// NAVIGATION (Scene tab)
    ///   Right-drag            = orbit / look
    ///   Right-held + WASD     = fly forward/back/left/right
    ///   Right-held + Q/E      = fly down/up
    ///   Middle-drag           = pan
    ///   Scroll                = zoom
    ///   Left-click/drag       = move or rotate selected object via gizmo handles
    ///   F                     = frame selected object (or reset view)
    ///   W / E                 = switch Move / Rotate tool (when NOT flying)
    /// </summary>
    public class SceneViewPanel : Panel
    {
        private readonly SceneRenderer _sceneRenderer = new();
        private Core.Scene? _scene;

        // ── Tab state ──────────────────────────────────────────────────────────
        public enum ViewTab { Scene, Game }
        private ViewTab _activeTab = ViewTab.Scene;
        public ViewTab ActiveTab { get => _activeTab; set => _activeTab = value; }

        // ── Play state ─────────────────────────────────────────────────────────
        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set { _isPlaying = value; _sceneRenderer.IsPlayMode = value; }
        }
        public bool IsPaused { get; set; }
        public Core.UIDocument? UIDocument { get; set; }

        private GizmoRenderer Gizmos => _sceneRenderer.Gizmos;

        // ── Navigation ─────────────────────────────────────────────────────────
        private bool _rightHeld;
        private bool _panning;
        private PointF _lastMouse;

        // Fly-cam keys (active only while _rightHeld)
        private bool _flyW, _flyS, _flyA, _flyD, _flyQ, _flyE;

        // ── Transform handle drag ──────────────────────────────────────────────
        private bool _handleDragging;
        private int _handleAxis = -1;
        private Vector3 _dragCamRight;
        private Vector3 _dragCamForward;

        // ── Toolbar rects ──────────────────────────────────────────────────────
        private const float ToolbarH = 24f;
        private RectangleF _toolbarRect;
        private RectangleF _btnMove, _btnRotate;
        private RectangleF _btnGizmoAll, _btnCam, _btnLight, _btnCollider, _btnAudio;
        private RectangleF _sceneTabRect, _gameTabRect;

        // ── Colours ────────────────────────────────────────────────────────────
        private static readonly Color CTabBg = Color.FromArgb(255, 28, 28, 30);
        private static readonly Color CTabActive = Color.FromArgb(255, 38, 38, 42);
        private static readonly Color CTabHover = Color.FromArgb(255, 34, 34, 38);
        private static readonly Color CPlaying = Color.FromArgb(255, 55, 195, 85);
        private static readonly Color CPaused = Color.FromArgb(255, 230, 165, 30);
        private static readonly Color CBtn = Color.FromArgb(255, 44, 44, 50);
        private static readonly Color CBtnOn = Color.FromArgb(255, 60, 130, 255);
        private static readonly Color CBtnHov = Color.FromArgb(255, 55, 55, 62);

        private const float TabBarH = 26f;
        private const float TabW = 88f;
        private PointF _mouse;

        public SceneViewPanel(RectangleF bounds) : base("Scene", bounds)
        { MinWidth = 200f; MinHeight = 160f; }

        public void SetScene(Core.Scene? s) => _scene = s;
        public void SetSelected(GameObject? go) => _sceneRenderer.Selected = go;
        public void Init() => _sceneRenderer.Init();
        public SceneRenderer Renderer => _sceneRenderer;

        private float TopBarsH => HeaderH + TabBarH + ToolbarH;
        public RectangleF ViewportRect => new(
            Bounds.X, Bounds.Y + TopBarsH,
            Bounds.Width, Bounds.Height - TopBarsH);
        public RectangleF SceneRect => ViewportRect;

        // ══════════════════════════════════════════════════════════════════════
        //  Render
        // ══════════════════════════════════════════════════════════════════════
        public void Render3D(int winW, int winH)
        {
            if (!IsVisible) return;
            _sceneRenderer.Render(ViewportRect, _scene, winW, winH);
        }

        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            DrawHeader(r);
            DrawTabBar(r);
            DrawToolbar(r);
            var vp = ViewportRect;
            if (_activeTab == ViewTab.Scene) DrawSceneChrome(r, vp);
            else DrawGameChrome(r, vp);
            r.DrawRect(Bounds, Color.FromArgb(255, 55, 55, 55), 1f);
        }

        private void DrawTabBar(IEditorRenderer r)
        {
            var bar = new RectangleF(Bounds.X, Bounds.Y + HeaderH, Bounds.Width, TabBarH);
            r.FillRect(bar, CTabBg);
            r.DrawLine(new PointF(bar.X, bar.Bottom), new PointF(bar.Right, bar.Bottom),
                       Color.FromArgb(255, 50, 50, 55));

            _sceneTabRect = new RectangleF(bar.X + 4f, bar.Y + 2f, TabW, TabBarH - 4f);
            _gameTabRect = new RectangleF(bar.X + TabW + 10f, bar.Y + 2f, TabW, TabBarH - 4f);
            DrawTab(r, _sceneTabRect, "Scene", _activeTab == ViewTab.Scene, Color.FromArgb(255, 100, 165, 255));
            DrawTab(r, _gameTabRect, "Game", _activeTab == ViewTab.Game,
                    IsPlaying ? CPlaying : Color.FromArgb(255, 100, 165, 255));

            if (IsPlaying)
            {
                var badge = IsPaused ? CPaused : CPlaying;
                var lbl = IsPaused ? "⏸  PAUSED" : "▶  PLAYING";
                float lw = lbl.Length * 7.2f + 18f;
                var lr = new RectangleF(bar.Right - lw - 10f, bar.Y + 4f, lw, TabBarH - 8f);
                r.FillRect(lr, Color.FromArgb(40, badge.R, badge.G, badge.B));
                r.DrawRect(lr, badge);
                r.DrawText(lbl, new PointF(lr.X + 8f, lr.Y + 4f), badge, 9f);
            }
        }

        private void DrawTab(IEditorRenderer r, RectangleF tab, string lbl, bool active, Color accent)
        {
            bool hov = !active && tab.Contains(_mouse);
            r.FillRect(tab, active ? CTabActive : hov ? CTabHover : Color.Transparent);
            r.DrawText(lbl,
                new PointF(tab.X + (tab.Width - lbl.Length * 6.5f) / 2f,
                           tab.Y + (tab.Height - 11f) / 2f),
                active ? Color.FromArgb(255, 220, 220, 225)
                       : Color.FromArgb(255, 140, 140, 148), 10f);
            if (active)
                r.FillRect(new RectangleF(tab.X + 4f, tab.Bottom - 2f, tab.Width - 8f, 2f), accent);
        }

        private void DrawToolbar(IEditorRenderer r)
        {
            _toolbarRect = new RectangleF(
                Bounds.X, Bounds.Y + HeaderH + TabBarH, Bounds.Width, ToolbarH);
            r.FillRect(_toolbarRect, Color.FromArgb(255, 32, 32, 36));
            r.DrawLine(new PointF(_toolbarRect.X, _toolbarRect.Bottom),
                       new PointF(_toolbarRect.Right, _toolbarRect.Bottom),
                       Color.FromArgb(255, 45, 45, 50));

            float x = _toolbarRect.X + 4f, y = _toolbarRect.Y + 2f, h = ToolbarH - 4f;

            _btnMove = new RectangleF(x, y, 46f, h); x += 50f;
            _btnRotate = new RectangleF(x, y, 46f, h); x += 54f;
            DrawToolBtn(r, _btnMove, "Move", Gizmos.ActiveTool == GizmoRenderer.TransformTool.Move);
            DrawToolBtn(r, _btnRotate, "Rotate", Gizmos.ActiveTool == GizmoRenderer.TransformTool.Rotate);

            r.DrawLine(new PointF(x + 1f, y + 2f), new PointF(x + 1f, y + h - 2f), Color.FromArgb(255, 55, 55, 60));
            x += 6f;

            _btnGizmoAll = new RectangleF(x, y, 52f, h); x += 56f;
            DrawToolBtn(r, _btnGizmoAll, "Gizmos", Gizmos.ShowAll);

            if (Gizmos.ShowAll)
            {
                _btnCam = new RectangleF(x, y, 38f, h); x += 42f;
                _btnLight = new RectangleF(x, y, 38f, h); x += 42f;
                _btnCollider = new RectangleF(x, y, 50f, h); x += 54f;
                _btnAudio = new RectangleF(x, y, 40f, h);
                DrawToolBtn(r, _btnCam, "Cam", Gizmos.ShowCameras);
                DrawToolBtn(r, _btnLight, "Light", Gizmos.ShowLights);
                DrawToolBtn(r, _btnCollider, "Collide", Gizmos.ShowColliders);
                DrawToolBtn(r, _btnAudio, "Audio", Gizmos.ShowAudio);
            }
        }

        private void DrawToolBtn(IEditorRenderer r, RectangleF b, string lbl, bool on)
        {
            bool hov = b.Contains(_mouse);
            r.FillRect(b, on ? CBtnOn : hov ? CBtnHov : CBtn);
            r.DrawRect(b, on ? Color.FromArgb(255, 80, 155, 255) : Color.FromArgb(255, 55, 55, 62));
            float tw = lbl.Length * 5.5f;
            r.DrawText(lbl, new PointF(b.X + (b.Width - tw) / 2f, b.Y + (b.Height - 10f) / 2f),
                on ? Color.White : Color.FromArgb(255, 175, 175, 180), 9f);
        }

        private void DrawSceneChrome(IEditorRenderer r, RectangleF vp)
        {
            if (_scene == null || !HasRenderable())
            {
                var hint = new RectangleF(vp.X + vp.Width / 2f - 165f, vp.Y + vp.Height / 2f - 12f, 330f, 24f);
                r.FillRect(hint, Color.FromArgb(110, 0, 0, 0));
                r.DrawText("Right-click Hierarchy → Create to add objects",
                    new PointF(hint.X + 8f, hint.Y + 6f), Color.FromArgb(200, 200, 200, 200), 10f);
            }

            DrawGizmoLabels(r, vp);

            var cam = _sceneRenderer.Camera;
            string ci = $"Yaw:{cam.Yaw:F0}°  Pitch:{cam.Pitch:F0}°  Dist:{cam.Distance:F1}";
            r.DrawText(ci, new PointF(vp.Right - ci.Length * 5.4f - 8f, vp.Bottom - 16f),
                Color.FromArgb(180, 130, 130, 130), 9f);

            string nav = _rightHeld
                ? "RMB: look  WASD=fly  Q/E=down/up"
                : (_sceneRenderer.Selected != null
                    ? (Gizmos.ActiveTool == GizmoRenderer.TransformTool.Move
                        ? "W=Move  E=Rotate  drag arrows to move"
                        : "W=Move  E=Rotate  drag rings to rotate")
                    : "RMB+drag=look  hold RMB+WASD=fly  MMB=pan");
            r.DrawText(nav, new PointF(vp.X + 6f, vp.Bottom - 16f), Color.FromArgb(140, 180, 180, 180), 9f);
        }

        private void DrawGizmoLabels(IEditorRenderer r, RectangleF vp)
        {
            if (!Gizmos.ShowAll || _scene == null) return;
            var view = _sceneRenderer.Camera.GetViewMatrix();
            var proj = _sceneRenderer.Camera.GetProjectionMatrix(vp.Width / Math.Max(vp.Height, 1f));

            foreach (var go in _scene.All())
            {
                if (!go.ActiveSelf) continue;
                bool hasCam = Gizmos.ShowCameras && go.GetComponent<Core.Camera>() != null;
                bool hasDL = Gizmos.ShowLights && go.GetComponent<Core.DirectionalLight>() != null;
                bool hasSL = Gizmos.ShowLights && go.GetComponent<Core.SpotLight>() != null;
                bool hasAu = Gizmos.ShowAudio && go.GetComponent<Core.AudioSource>() != null;
                string? icon = hasCam ? "[Cam]" : hasDL ? "[Sun]" : hasSL ? "[Spot]" : hasAu ? "[Audio]" : null;
                if (icon == null) continue;
                var scr = GizmoRenderer.WorldToScreen(go.Transform.LocalPosition, view, proj, vp);
                if (scr.X < vp.X || scr.X > vp.Right || scr.Y < vp.Y || scr.Y > vp.Bottom) continue;
                r.DrawText(icon, new PointF(scr.X + 8f, scr.Y - 6f), Color.FromArgb(220, 255, 230, 100), 9f);
            }
        }

        private void DrawGameChrome(IEditorRenderer r, RectangleF vp)
        {
            if (!IsPlaying)
            {
                r.FillRect(vp, Color.FromArgb(200, 10, 10, 12));
                string msg = "Press ▶ Play to enter Game View";
                float mw = msg.Length * 6.8f;
                r.DrawText(msg, new PointF(vp.X + (vp.Width - mw) / 2f, vp.Y + vp.Height / 2f - 8f),
                    Color.FromArgb(180, 160, 160, 165), 11f);
                return;
            }
            if (UIDocument?.Elements.Count > 0)
                Rendering.UIDocumentRenderer.Render(r, UIDocument, vp);
            r.DrawRect(vp, Color.FromArgb(80, CPlaying.R, CPlaying.G, CPlaying.B));
        }

        private bool HasRenderable()
        {
            if (_scene == null) return false;
            foreach (var go in _scene.All())
                if (go.GetComponent<MeshFilter>() != null) return true;
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Update — fly-cam
        // ══════════════════════════════════════════════════════════════════════
        public override void OnUpdate(double dt)
        {
            if (!_rightHeld || _activeTab != ViewTab.Scene) return;
            if (!(_flyW || _flyS || _flyA || _flyD || _flyQ || _flyE)) return;

            var cam = _sceneRenderer.Camera;
            var view = cam.GetViewMatrix();
            var right = new Vector3(view.Row0.X, view.Row0.Y, view.Row0.Z);
            var forward = Vector3.Normalize(cam.Target - cam.Position);
            float speed = Math.Max(cam.Distance * 1.5f, 0.5f) * (float)dt;

            var move = Vector3.Zero;
            if (_flyW) move += forward;
            if (_flyS) move -= forward;
            if (_flyD) move += right;
            if (_flyA) move -= right;
            if (_flyE) move += Vector3.UnitY;
            if (_flyQ) move -= Vector3.UnitY;

            if (move.LengthSquared > 0.0001f)
                cam.Target += Vector3.Normalize(move) * speed;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Mouse
        // ══════════════════════════════════════════════════════════════════════
        public override void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            float dx = pos.X - _lastMouse.X;
            float dy = pos.Y - _lastMouse.Y;
            _lastMouse = pos;

            if (_activeTab != ViewTab.Scene) { base.OnMouseMove(pos); return; }

            if (_handleDragging && Gizmos.HandleTarget != null)
            { ApplyHandleDrag(dx, dy); return; }

            var cam = _sceneRenderer.Camera;

            if (_rightHeld)
            {
                cam.Yaw += dx * 0.35f;
                cam.Pitch = Math.Clamp(cam.Pitch - dy * 0.35f, -89f, 89f);
                return;
            }
            if (_panning)
            {
                var view = cam.GetViewMatrix();
                var right = new Vector3(view.Row0.X, view.Row0.Y, view.Row0.Z);
                var up = new Vector3(view.Row1.X, view.Row1.Y, view.Row1.Z);
                float spd = cam.Distance * 0.002f;
                cam.Target -= right * (dx * spd);
                cam.Target += up * (dy * spd);
                return;
            }
            base.OnMouseMove(pos);
        }

        private void ApplyHandleDrag(float dx, float dy)
        {
            var go = Gizmos.HandleTarget!;
            var t = go.Transform;

            if (Gizmos.ActiveTool == GizmoRenderer.TransformTool.Move)
            {
                float speed = _sceneRenderer.Camera.Distance * 0.006f;
                var pos = t.LocalPosition;
                switch (_handleAxis)
                {
                    case 0: // X axis
                        float xSign = Vector3.Dot(_dragCamRight, Vector3.UnitX) >= 0 ? 1f : -1f;
                        t.LocalPosition = new Vector3(pos.X + dx * speed * xSign, pos.Y, pos.Z);
                        break;
                    case 1: // Y axis (screen up = world up)
                        t.LocalPosition = new Vector3(pos.X, pos.Y - dy * speed, pos.Z);
                        break;
                    case 2: // Z axis
                        float zSign = Vector3.Dot(_dragCamRight, Vector3.UnitZ) >= 0 ? 1f : -1f;
                        t.LocalPosition = new Vector3(pos.X, pos.Y, pos.Z + dx * speed * zSign);
                        break;
                    case 3: // XZ plane
                        t.LocalPosition = pos
                            + _dragCamRight * (dx * speed)
                            - _dragCamForward * (dy * speed);
                        break;
                }
            }
            else if (Gizmos.ActiveTool == GizmoRenderer.TransformTool.Rotate)
            {
                float spd = 1.5f;
                var rot = t.LocalEulerAngles;
                switch (_handleAxis)
                {
                    case 0: t.LocalEulerAngles = rot with { Y = rot.Y + dx * spd }; break;
                    case 1: t.LocalEulerAngles = rot with { X = rot.X + dy * spd }; break;
                    case 2: t.LocalEulerAngles = rot with { Z = rot.Z + dx * spd }; break;
                    default:
                        t.LocalEulerAngles = new Vector3(rot.X + dy * spd, rot.Y + dx * spd, rot.Z);
                        break;
                }
            }
        }

        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;

            if (_toolbarRect.Contains(pos) && e.Button == MouseButton.Left)
            { HandleToolbarClick(pos); return; }
            if (_sceneTabRect.Contains(pos)) { _activeTab = ViewTab.Scene; return; }
            if (_gameTabRect.Contains(pos)) { _activeTab = ViewTab.Game; return; }
            if (!IsVisible) return;
            if (HeaderRect.Contains(pos)) { base.OnMouseDown(e, pos); return; }

            if (_activeTab == ViewTab.Scene && ViewportRect.Contains(pos))
            {
                IsFocused = true;
                _lastMouse = pos;

                if (e.Button == MouseButton.Left)
                    TryStartHandleDrag(pos);
                else if (e.Button == MouseButton.Right)
                    _rightHeld = true;
                else if (e.Button == MouseButton.Middle)
                    _panning = true;
            }
            else base.OnMouseDown(e, pos);
        }

        private void HandleToolbarClick(PointF pos)
        {
            if (_btnMove.Contains(pos)) { Gizmos.ActiveTool = GizmoRenderer.TransformTool.Move; return; }
            if (_btnRotate.Contains(pos)) { Gizmos.ActiveTool = GizmoRenderer.TransformTool.Rotate; return; }
            if (_btnGizmoAll.Contains(pos)) { Gizmos.ShowAll = !Gizmos.ShowAll; return; }
            if (!Gizmos.ShowAll) return;
            if (_btnCam.Contains(pos)) { Gizmos.ShowCameras = !Gizmos.ShowCameras; return; }
            if (_btnLight.Contains(pos)) { Gizmos.ShowLights = !Gizmos.ShowLights; return; }
            if (_btnCollider.Contains(pos)) { Gizmos.ShowColliders = !Gizmos.ShowColliders; return; }
            if (_btnAudio.Contains(pos)) { Gizmos.ShowAudio = !Gizmos.ShowAudio; return; }
        }

        private bool TryStartHandleDrag(PointF pos)
        {
            if (Gizmos.HandleTarget == null || Gizmos.LastHandles.Count == 0) return false;

            const float HitRadius = 18f;   // generous hit radius in screen pixels
            int bestAxis = -1;
            float bestDist = float.MaxValue;

            foreach (var h in Gizmos.LastHandles)
            {
                float ddx = pos.X - h.ScreenTip.X;
                float ddy = pos.Y - h.ScreenTip.Y;
                float d2 = ddx * ddx + ddy * ddy;
                if (d2 < HitRadius * HitRadius && d2 < bestDist)
                { bestDist = d2; bestAxis = h.Axis; }
            }

            if (bestAxis < 0) return false;

            _handleDragging = true;
            _handleAxis = bestAxis;

            // Cache camera basis once at drag start
            var view = _sceneRenderer.Camera.GetViewMatrix();
            _dragCamRight = new Vector3(view.Row0.X, view.Row0.Y, view.Row0.Z);
            // Row2 is the -forward in view space; negate it and flatten to ground
            _dragCamForward = new Vector3(-view.Row2.X, 0f, -view.Row2.Z);
            if (_dragCamForward.LengthSquared > 0.0001f)
                _dragCamForward = Vector3.Normalize(_dragCamForward);

            return true;
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            if (e.Button == MouseButton.Left) _handleDragging = false;
            if (e.Button == MouseButton.Right) { _rightHeld = false; ClearFlyKeys(); }
            if (e.Button == MouseButton.Middle) _panning = false;
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseScroll(float delta)
        {
            if (!IsVisible || _activeTab != ViewTab.Scene) return;
            _sceneRenderer.Camera.Distance =
                Math.Clamp(_sceneRenderer.Camera.Distance * (1f - delta * 0.12f), 0.05f, 2000f);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Keyboard
        // ══════════════════════════════════════════════════════════════════════
        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (!IsFocused || _activeTab != ViewTab.Scene) return;

            // Always track fly keys so they work once right is pressed
            switch (e.Key)
            {
                case Keys.W: _flyW = true; break;
                case Keys.S: _flyS = true; break;
                case Keys.A: _flyA = true; break;
                case Keys.D: _flyD = true; break;
                case Keys.Q: _flyQ = true; break;
                case Keys.E: _flyE = true; break;
            }

            // Tool / view shortcuts only when NOT holding right-click (not flying)
            if (!_rightHeld)
            {
                var cam = _sceneRenderer.Camera;
                switch (e.Key)
                {
                    case Keys.W: Gizmos.ActiveTool = GizmoRenderer.TransformTool.Move; break;
                    case Keys.E: Gizmos.ActiveTool = GizmoRenderer.TransformTool.Rotate; break;
                    case Keys.F:
                        if (_sceneRenderer.Selected != null)
                        { cam.Target = _sceneRenderer.Selected.Transform.LocalPosition; cam.Distance = 6f; }
                        else
                        { cam.Target = Vector3.Zero; cam.Distance = 8f; cam.Yaw = 45f; cam.Pitch = 25f; }
                        break;
                    case Keys.Up: cam.Yaw = 0f; cam.Pitch = 0f; break;
                    case Keys.Right: cam.Yaw = 90f; cam.Pitch = 0f; break;
                    case Keys.Left: cam.Yaw = 45f; cam.Pitch = 89f; break;
                }
            }
        }

        public override void OnKeyUp(KeyboardKeyEventArgs e)
        {
            switch (e.Key)
            {
                case Keys.W: _flyW = false; break;
                case Keys.S: _flyS = false; break;
                case Keys.A: _flyA = false; break;
                case Keys.D: _flyD = false; break;
                case Keys.Q: _flyQ = false; break;
                case Keys.E: _flyE = false; break;
            }
        }

        private void ClearFlyKeys() => _flyW = _flyS = _flyA = _flyD = _flyQ = _flyE = false;

        public void Dispose() => _sceneRenderer.Dispose();
    }
}