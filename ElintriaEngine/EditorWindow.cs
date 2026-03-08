using System;
using System.Drawing;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Rendering;
using ElintriaEngine.UI;

namespace ElintriaEngine
{
    /// <summary>
    /// Editor OpenTK window.
    ///
    /// WHY WE RE-CHECK SIZE EVERY FRAME:
    ///   OpenTK fires OnResize before OnLoad, so the layout misses the first event.
    ///   DPI scaling also makes ClientSize (logical) differ from FramebufferSize
    ///   (physical pixels). We always use FramebufferSize for GL and layout so
    ///   input hit-testing matches what is actually rendered on screen.
    /// </summary>
    public class EditorWindow : GameWindow
    {
        private EditorRenderer? _uiRenderer;
        private EditorLayout? _layout;
        private readonly string _projectRoot;
        private int _lastW = -1, _lastH = -1;

        public EditorWindow(GameWindowSettings gs, NativeWindowSettings ns,
                            string projectRoot = "")
            : base(gs, ns) => _projectRoot = projectRoot;

        protected override void OnLoad()
        {
            base.OnLoad();

            GL.ClearColor(0.12f, 0.12f, 0.12f, 1f);

            string fontPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Fonts", "ProggyClean.ttf");
            _uiRenderer = new EditorRenderer(
                System.IO.File.Exists(fontPath) ? fontPath : null);

            // Use FramebufferSize (physical pixels). Guard against 0 during startup.
            int w = Math.Max(FramebufferSize.X, 800);
            int h = Math.Max(FramebufferSize.Y, 600);

            _layout = new EditorLayout(w, h, _projectRoot);

            // OnResize fires before OnLoad and is silently dropped (_layout was null).
            // Force the correct layout NOW with the real size.
            GL.Viewport(0, 0, w, h);
            _layout.OnResize(w, h);
            _lastW = w; _lastH = h;

            _layout.Init();
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            base.OnRenderFrame(args);
            if (_uiRenderer == null || _layout == null) return;

            int w = FramebufferSize.X;
            int h = FramebufferSize.Y;

            // Auto-correct if the framebuffer changed since last frame
            if (w != _lastW || h != _lastH)
            {
                _lastW = w; _lastH = h;
                GL.Viewport(0, 0, w, h);
                _layout.OnResize(w, h);
            }

            GL.Viewport(0, 0, w, h);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            _layout.Render3D();

            GL.Viewport(0, 0, w, h);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.ScissorTest);

            _uiRenderer.BeginFrame(w, h);
            _layout.Render2D(_uiRenderer);
            _uiRenderer.EndFrame();

            SwapBuffers();
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            if (_layout == null) return;  // fired before OnLoad – handled there

            int w = Math.Max(FramebufferSize.X, 1);
            int h = Math.Max(FramebufferSize.Y, 1);
            GL.Viewport(0, 0, w, h);
            _layout.OnResize(w, h);
            _lastW = w; _lastH = h;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        { base.OnMouseDown(e); _layout?.OnMouseDown(e, ScaledMP()); }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        { base.OnMouseUp(e); _layout?.OnMouseUp(e, ScaledMP()); }

        protected override void OnMouseMove(MouseMoveEventArgs e)
        { base.OnMouseMove(e); _layout?.OnMouseMove(ScaledMP()); }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        { base.OnMouseWheel(e); _layout?.OnMouseScroll(e.OffsetY); }

        protected override void OnKeyDown(KeyboardKeyEventArgs e)
        { base.OnKeyDown(e); if (e.Alt && e.Key == Keys.F4) Close(); _layout?.OnKeyDown(e); }

        protected override void OnKeyUp(KeyboardKeyEventArgs e)
        { base.OnKeyUp(e); _layout?.OnKeyUp(e); }

        protected override void OnTextInput(TextInputEventArgs e)
        { base.OnTextInput(e); _layout?.OnTextInput(e); }

        protected override void OnUnload()
        { base.OnUnload(); _layout?.Dispose(); _uiRenderer?.Dispose(); }

        /// Mouse position in physical (framebuffer) pixels.
        /// MouseState is in logical pixels; scale if DPI != 1.
        private PointF ScaledMP()
        {
            float sx = ClientSize.X > 0 ? (float)FramebufferSize.X / ClientSize.X : 1f;
            float sy = ClientSize.Y > 0 ? (float)FramebufferSize.Y / ClientSize.Y : 1f;
            return new PointF(MouseState.X * sx, MouseState.Y * sy);
        }
    }
}