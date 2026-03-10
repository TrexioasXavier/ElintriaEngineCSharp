using System;
using System.Drawing;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Rendering;
using ElintriaEngine.UI;
using ElintriaEngine.Core;

namespace ElintriaEngine
{
    /// <summary>
    /// Main editor window.
    ///
    /// Lifecycle
    /// ---------
    ///  1. OnLoad  - creates EditorRenderer, shows the Project Launcher.
    ///  2. User clicks "Open Project" in the launcher -> LoadEditor(projectRoot).
    ///  3. User clicks File -> Return to Launcher -> UnloadEditor() then back to step 1.
    ///
    /// Render path
    /// -----------
    ///  Launcher mode : full-window 2D panel (no 3D pass).
    ///  Editor mode   : Render3D() + BeginFrame/Render2D()/EndFrame.
    /// </summary>
    public class EditorWindow : GameWindow
    {
        private EditorRenderer? _renderer;
        private ProjectLauncherPanel? _launcher;
        private EditorLayout? _layout;
        private string? _pendingProjectRoot;
        private int _lastW = -1, _lastH = -1;

        public EditorWindow(GameWindowSettings gs, NativeWindowSettings ns,
                            string projectRoot = "")
            : base(gs, ns)
        {
            if (!string.IsNullOrEmpty(projectRoot))
                _pendingProjectRoot = projectRoot;
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            GL.ClearColor(0.08f, 0.08f, 0.09f, 1f);

            string fontPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Fonts", "ProggyClean.ttf");
            _renderer = new EditorRenderer(
                System.IO.File.Exists(fontPath) ? fontPath : null);

            int w = Math.Max(FramebufferSize.X, 800);
            int h = Math.Max(FramebufferSize.Y, 600);
            _lastW = w; _lastH = h;
            GL.Viewport(0, 0, w, h);

            if (_pendingProjectRoot != null)
            {
                LoadEditor(_pendingProjectRoot);
                _pendingProjectRoot = null;
            }
            else
            {
                ShowLauncher();
            }
        }

        protected override void OnUnload()
        {
            base.OnUnload();
            _layout?.Dispose();
            _renderer?.Dispose();
        }

        // ── Mode switching ────────────────────────────────────────────────────

        private void ShowLauncher()
        {
            _layout?.Dispose();
            _layout = null;
            _launcher = new ProjectLauncherPanel();
            _launcher.ProjectOpened += root => _pendingProjectRoot = root;
            Title = "Elintria Engine — Project Manager";
        }

        private void LoadEditor(string projectRoot)
        {
            _launcher = null;
            int w = Math.Max(FramebufferSize.X, 800);
            int h = Math.Max(FramebufferSize.Y, 600);

            _layout = new EditorLayout(w, h, projectRoot);
            _layout.OnResize(w, h);
            _layout.Init();
            _layout.ReturnToLauncher += ShowLauncher;

            string projName = System.IO.Path.GetFileName(
                projectRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                    System.IO.Path.AltDirectorySeparatorChar));
            Title = $"Elintria Engine — {projName}";
        }

        // ── Render ────────────────────────────────────────────────────────────

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            base.OnRenderFrame(args);
            if (_renderer == null) return;

            // Flush pending project-open (set by launcher on the same render thread)
            if (_pendingProjectRoot != null)
            {
                string root = _pendingProjectRoot;
                _pendingProjectRoot = null;
                LoadEditor(root);
            }

            int w = FramebufferSize.X;
            int h = FramebufferSize.Y;
            if (w != _lastW || h != _lastH)
            {
                _lastW = w; _lastH = h;
                GL.Viewport(0, 0, w, h);
                _layout?.OnResize(w, h);
            }

            GL.Viewport(0, 0, w, h);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (_launcher != null)
            {
                GL.Disable(EnableCap.DepthTest);
                GL.Disable(EnableCap.ScissorTest);
                _renderer.BeginFrame(w, h);
                _launcher.Render(_renderer, w, h);
                _renderer.EndFrame();
            }
            else if (_layout != null)
            {
                _layout.Update(args.Time);
                _layout.Render3D();
                GL.Viewport(0, 0, w, h);
                GL.Disable(EnableCap.DepthTest);
                GL.Disable(EnableCap.ScissorTest);
                _renderer.BeginFrame(w, h);
                _layout.Render2D(_renderer);
                _renderer.EndFrame();
            }

            SwapBuffers();
        }

        // ── Resize ────────────────────────────────────────────────────────────

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            int w = Math.Max(FramebufferSize.X, 1);
            int h = Math.Max(FramebufferSize.Y, 1);
            GL.Viewport(0, 0, w, h);
            _layout?.OnResize(w, h);
            _lastW = w; _lastH = h;
        }

        // ── Input ─────────────────────────────────────────────────────────────

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            var pos = ScaledMP();
            _launcher?.OnMouseDown(e, pos);
            _layout?.OnMouseDown(e, pos);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            _layout?.OnMouseUp(e, ScaledMP());
        }

        protected override void OnMouseMove(MouseMoveEventArgs e)
        {
            base.OnMouseMove(e);
            var pos = ScaledMP();
            _launcher?.OnMouseMove(pos);
            _layout?.OnMouseMove(pos);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            _layout?.OnMouseScroll(e.OffsetY);
        }

        protected override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Alt && e.Key == Keys.F4) { Close(); return; }
            _launcher?.OnKeyDown(e);
            _layout?.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyboardKeyEventArgs e)
        {
            base.OnKeyUp(e);
            _layout?.OnKeyUp(e);
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            _launcher?.OnTextInput(e);
            _layout?.OnTextInput(e);
        }

        private PointF ScaledMP()
        {
            float sx = ClientSize.X > 0 ? (float)FramebufferSize.X / ClientSize.X : 1f;
            float sy = ClientSize.Y > 0 ? (float)FramebufferSize.Y / ClientSize.Y : 1f;
            return new PointF(MouseState.X * sx, MouseState.Y * sy);
        }
    }
}