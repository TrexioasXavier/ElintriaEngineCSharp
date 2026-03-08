using Elintria.Engine;
using Elintria.Engine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Drawing;

namespace Elintria.Editor.UI
{
    // =========================================================================
    // SceneViewPanel
    // =========================================================================
    /// <summary>
    /// The 3-D Scene View viewport.
    ///
    ///  • Renders the active scene into an FBO / texture
    ///  • Displays the texture as a full-panel 2-D quad via UIRenderer
    ///  • Overlay toolbar: Wireframe toggle, Shaded button, camera info
    ///  • RMB + WASD fly-camera (only when mouse is inside the viewport)
    ///  • LMB click → raycast pick (delegate to Editor)
    /// </summary>
    public class SceneViewPanel : Panel
    {
        // ------------------------------------------------------------------
        // Colours
        // ------------------------------------------------------------------
        static readonly Color C_Toolbar = Color.FromArgb(220, 44, 44, 44);
        static readonly Color C_BtnNorm = Color.FromArgb(200, 56, 56, 56);
        static readonly Color C_BtnHov = Color.FromArgb(255, 70, 70, 70);
        static readonly Color C_BtnActive = Color.FromArgb(255, 44, 93, 180);
        static readonly Color C_Text = Color.FromArgb(230, 210, 210, 210);
        static readonly Color C_Overlay = Color.FromArgb(160, 20, 20, 20);
        static readonly Color C_Border = Color.FromArgb(255, 26, 26, 26);

        public const float TOOLBAR_H = 24f;
        const float BTN_W = 72f;
        const float BTN_H = 20f;
        const float BTN_PAD = 4f;

        // ------------------------------------------------------------------
        // FBO
        // ------------------------------------------------------------------
        private int _fbo = -1;
        private int _colorTex = -1;
        private int _depthRbo = -1;
        private int _fboW = 0, _fboH = 0;

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------
        public bool WireframeMode { get; set; } = false;
        public bool ShowGrid { get; set; } = true;
        public Camera Camera { get; set; }

        // Fired when user left-clicks in the viewport (Editor subscribes to pick)
        public System.Action<Vector2> OnViewportClick;

        // Camera drag
        private bool _rmbHeld = false;
        private Vector2 _lastMouse;
        private bool _firstMove = true;

        private readonly BitmapFont _font;

        // ------------------------------------------------------------------
        public SceneViewPanel(BitmapFont font, Camera camera)
        {
            _font = font;
            Camera = camera;
            BackgroundColor = Color.FromArgb(255, 38, 38, 38);
        }

        // ------------------------------------------------------------------
        // FBO management
        // ------------------------------------------------------------------
        private void EnsureFBO(int w, int h)
        {
            if (w == _fboW && h == _fboH && _fbo != -1) return;
            DeleteFBO();

            _fboW = w; _fboH = h;

            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            // Colour texture
            _colorTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _colorTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                          w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
                          System.IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _colorTex, 0);

            // Depth renderbuffer
            _depthRbo = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                RenderbufferStorage.Depth24Stencil8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _depthRbo);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private void DeleteFBO()
        {
            if (_fbo != -1) { GL.DeleteFramebuffer(_fbo); _fbo = -1; }
            if (_colorTex != -1) { GL.DeleteTexture(_colorTex); _colorTex = -1; }
            if (_depthRbo != -1) { GL.DeleteRenderbuffer(_depthRbo); _depthRbo = -1; }
            _fboW = _fboH = 0;
        }

        // ------------------------------------------------------------------
        // Render 3-D scene into FBO  (called by Editor before UI pass)
        // ------------------------------------------------------------------
        public void RenderScene(RenderContext ctx, bool wireframe)
        {
            int vw = (int)MathF.Max(1, Size.X);
            int vh = (int)MathF.Max(1, Size.Y - TOOLBAR_H);
            EnsureFBO(vw, vh);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.Viewport(0, 0, vw, vh);

            GL.Enable(EnableCap.DepthTest);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.CullFace);
            GL.ClearColor(0.16f, 0.16f, 0.16f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (wireframe)
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);

            SceneManager.Render(ctx);

            if (wireframe)
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

            // World-text billboards
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            WorldText.DrawAll(Camera, ctx.View, ctx.Projection, vw, vh);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        // ------------------------------------------------------------------
        // Draw — display FBO texture + toolbar overlay
        // ------------------------------------------------------------------
        public override void Draw()
        {
            if (!Visible) return;
            var abs = GetAbsolutePosition();

            // FBO texture (the 3-D viewport image)
            if (_colorTex != -1)
            {
                UIRenderer.DrawSceneTexture(
                    abs.X, abs.Y + TOOLBAR_H,
                    Size.X, Size.Y - TOOLBAR_H,
                    0f, 1f, 1f, 0f,   // flip V: FBO origin is bottom-left
                    Color.White, _colorTex);
            }
            else
            {
                UIRenderer.DrawRect(abs.X, abs.Y + TOOLBAR_H,
                    Size.X, Size.Y - TOOLBAR_H,
                    Color.FromArgb(255, 38, 38, 38));
            }

            // Toolbar
            UIRenderer.DrawRect(abs.X, abs.Y, Size.X, TOOLBAR_H, C_Toolbar);
            UIRenderer.DrawRect(abs.X, abs.Y + TOOLBAR_H - 1, Size.X, 1, C_Border);

            DrawToolbar(abs);

            // Border around the viewport
            UIRenderer.DrawRectOutline(abs.X, abs.Y, Size.X, Size.Y, C_Border);

            foreach (var c in Children) c.Draw();
        }

        private void DrawToolbar(Vector2 abs)
        {
            _toolBtns.Clear();
            float x = abs.X + 4f;
            float y = abs.Y + 2f;
            var mp = GetMousePosition();

            // Shaded / Wireframe toggle
            DrawToolBtn(ref x, y, mp, "Shaded",
                !WireframeMode,
                () => WireframeMode = false);

            DrawToolBtn(ref x, y, mp, "Wireframe",
                WireframeMode,
                () => WireframeMode = true);

            // Grid toggle
            x += 6f;
            DrawToolBtn(ref x, y, mp, "Grid",
                ShowGrid,
                () => ShowGrid = !ShowGrid);

            // Camera info on the right
            if (Camera != null)
            {
                var p = Camera.Position;
                string info = $"Pos ({p.X:F1}, {p.Y:F1}, {p.Z:F1})";
                float tw = _font?.MeasureText(info) ?? 0f;
                _font?.DrawText(info, abs.X + Size.X - tw - 6f, abs.Y + 4f,
                    Color.FromArgb(160, 180, 180, 180));
            }
        }

        private void DrawToolBtn(ref float x, float y, Vector2 mp,
                                  string label, bool active,
                                  System.Action onClick)
        {
            float tw = (_font?.MeasureText(label) ?? 50f) + BTN_PAD * 2;
            float bw = MathF.Max(tw, BTN_W - 20f);
            bool hov = mp.X >= x && mp.X <= x + bw
                     && mp.Y >= y && mp.Y <= y + BTN_H;

            Color bg = active ? C_BtnActive : hov ? C_BtnHov : C_BtnNorm;
            UIRenderer.DrawRect(x, y, bw, BTN_H, bg);
            UIRenderer.DrawRectOutline(x, y, bw, BTN_H, Color.FromArgb(80, 0, 0, 0), 1f);
            _font?.DrawText(label, x + BTN_PAD, y + 2f, C_Text);

            // Store for click handling in OnMouseDown
            _toolBtns.Add((x, y, bw, BTN_H, onClick));

            x += bw + 2f;
        }

        // Tool button hit areas collected during Draw (cleared each frame)
        private readonly System.Collections.Generic.List<
            (float x, float y, float w, float h, System.Action action)> _toolBtns = new();

        public override void Update(float dt)
        {

            if (Camera == null) return;

            var abs = GetAbsolutePosition();
            var mp = GetMousePosition();
            bool insideViewport = mp.X >= abs.X && mp.X <= abs.X + Size.X
                               && mp.Y >= abs.Y + TOOLBAR_H
                               && mp.Y <= abs.Y + Size.Y;

            if (_rmbHeld && insideViewport)
            {
                if (_firstMove) { _lastMouse = mp; _firstMove = false; }
                float dx = mp.X - _lastMouse.X;
                float dy = mp.Y - _lastMouse.Y;
                _lastMouse = mp;
                Camera.Yaw += dx * Camera.Sensitivity;
                Camera.Pitch -= dy * Camera.Sensitivity;
                Camera.Pitch = MathHelper.Clamp(Camera.Pitch, -89f, 89f);
            }

            base.Update(dt);
        }

        public override bool HandleMouseDown(MouseButtonEventArgs e)
        {
            var mp = GetMousePosition();
            var abs = GetAbsolutePosition();
            if (!IsPointInside(mp)) return false;

            if (e.Button == MouseButton.Right)
            {
                _rmbHeld = true;
                _firstMove = true;
                return true;
            }

            if (e.Button == MouseButton.Left)
            {
                // Check toolbar buttons
                foreach (var (bx, by, bw, bh, action) in _toolBtns)
                {
                    if (mp.X >= bx && mp.X <= bx + bw &&
                        mp.Y >= by && mp.Y <= by + bh)
                    {
                        action?.Invoke();
                        return true;
                    }
                }

                // Click in viewport → pass screen pos to Editor for picking
                bool inVP = mp.Y > abs.Y + TOOLBAR_H;
                if (inVP)
                {
                    OnViewportClick?.Invoke(mp);
                    return true;
                }
            }

            return base.HandleMouseDown(e);
        }

        public override bool HandleMouseUp(MouseButtonEventArgs e)
        {
            if (e.Button == MouseButton.Right)
            {
                _rmbHeld = false;
                _firstMove = true;
            }
            return base.HandleMouseUp(e);
        }

        // ------------------------------------------------------------------
        // Camera keyboard (called by Editor when viewport is focused)
        // ------------------------------------------------------------------
        public void HandleCameraKeys(KeyboardState kb, float dt)
        {
            if (!_rmbHeld || Camera == null) return;
            float spd = Camera.Speed * dt;
            if (kb.IsKeyDown(Keys.W)) Camera.Position += Camera.Front * spd;
            if (kb.IsKeyDown(Keys.S)) Camera.Position -= Camera.Front * spd;
            if (kb.IsKeyDown(Keys.A)) Camera.Position -= Camera.Right * spd;
            if (kb.IsKeyDown(Keys.D)) Camera.Position += Camera.Right * spd;
            if (kb.IsKeyDown(Keys.E) || kb.IsKeyDown(Keys.Space))
                Camera.Position += Vector3.UnitY * spd;
            if (kb.IsKeyDown(Keys.Q) || kb.IsKeyDown(Keys.LeftShift))
                Camera.Position -= Vector3.UnitY * spd;
        }

        // ------------------------------------------------------------------
        // Build a RenderContext from the current camera and panel size
        // ------------------------------------------------------------------
        public RenderContext BuildContext(float dt)
        {
            int vw = (int)MathF.Max(1, Size.X);
            int vh = (int)MathF.Max(1, Size.Y - TOOLBAR_H);
            float aspect = vw / (float)vh;
            return new RenderContext
            {
                View = Camera?.GetViewMatrix() ?? Matrix4.Identity,
                Projection = Matrix4.CreatePerspectiveFieldOfView(
                    MathHelper.DegreesToRadians(Camera?.Fov ?? 60f),
                    aspect, 0.1f, 500f),
                CameraPos = Camera?.Position ?? Vector3.Zero,
                DeltaTime = dt
            };
        }

        // ------------------------------------------------------------------
        // Dispose
        // ------------------------------------------------------------------
        public void Dispose() => DeleteFBO();
    }
}