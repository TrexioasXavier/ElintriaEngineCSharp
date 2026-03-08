using Elintria.Editor;
using Elintria.Editor.UI;
using Elintria.Engine;
using Elintria.Engine.Rendering;
using ElintriaEngineC.WindowCreation;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Drawing;
using System.Linq;

namespace Elintria
{
    // =========================================================================
    // Editor
    // =========================================================================
    public class ElintriaEditor : EWindow
    {
        // ------------------------------------------------------------------
        // Core
        // ------------------------------------------------------------------
        private Shader _shader;
        private Camera _camera;
        private BitmapFont _font;

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------
        private DockingSystem _docking;
        private MenuBar _menuBar;

        private DockWindow _winHierarchy;
        private DockWindow _winScene;
        private DockWindow _winInspector;
        private DockWindow _winProject;

        private HierarchyPanel _hierarchy;
        private SceneViewPanel _sceneView;
        private InspectorPanel _inspector;
        private ProjectPanel _project;

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------
        private GameObject _selected;

        // ------------------------------------------------------------------
        // FPS
        // ------------------------------------------------------------------
        private float _fpsTimer;
        private int _fpsFrames;

        // ------------------------------------------------------------------
        // Play mode
        // ------------------------------------------------------------------
        private bool _isPlaying;
        private bool _isPaused;

        private void EnterPlayMode()
        {
            if (_isPlaying) return;
            _isPlaying = true;
            _isPaused = false;
            Console.WriteLine("[Editor] ▶ Play Mode");
            SceneManager.LoadScene(SceneManager.ActiveScene?.Name ?? "Game");
        }

        private void ExitPlayMode()
        {
            if (!_isPlaying) return;
            _isPlaying = false;
            _isPaused = false;
            Console.WriteLine("[Editor] ■ Edit Mode");
            SceneManager.LoadScene(SceneManager.ActiveScene?.Name ?? "Game");
        }

        private void TogglePause()
        {
            if (!_isPlaying) return;
            _isPaused ^= true;
        }

        private void RunBuild()
        {
            BuildSystem.Build(SceneManager.ActiveScene, "Build/");
        }

        // ------------------------------------------------------------------
        // Mouse tracking — always sourced from OnMouseMove e.Position (logical pixels)
        // ------------------------------------------------------------------
        private Vector2 _mousePos;

        // Logical window size — matches the coordinate space of mouse events.
        // On HiDPI/scaled displays, Size (framebuffer) != ClientSize (logical).
        // ALL UI layout and UIRenderer.Begin() must use logical size.
        // GL.Viewport is the ONLY place that uses physical Size.
        private float _winW, _winH;

        // ------------------------------------------------------------------
        // Layout constants
        // ------------------------------------------------------------------
        const float MENU_H = 22f;
        const float HIER_W_F = 0.17f;
        const float INSP_W_F = 0.22f;
        const float BOT_H_F = 0.25f;

        // Splitter state
        private bool _dragLeft, _dragRight, _dragBot;
        private float _hierW, _inspW, _botH;
        private Vector2 _splitStart;
        private float _splitVal;

        // ------------------------------------------------------------------
        public ElintriaEditor() : base(1280, 800, "Elintria Engine Editor")
        {
            EWindow.Instance = this;
        }

        // ------------------------------------------------------------------
        // GetMousePos — kept for any external callers; panels now use Panel.DispatchMousePos
        // ------------------------------------------------------------------
        public override Vector2 GetMousePos() => _mousePos;

        // ------------------------------------------------------------------
        protected override void OnLoad()
        {
            base.OnLoad();
            GL.Viewport(0, 0, Size.X, Size.Y);
            GL.Enable(EnableCap.DepthTest);
            GL.ClearColor(0.12f, 0.12f, 0.12f, 1f);
            CursorState = CursorState.Normal;

            _shader = new Shader("data/Shaders/basic.vert", "data/Shaders/basic.frag");
            _font = new BitmapFont("Consolas", 13f);
            _camera = new Camera(new Vector3(0, 2, 8));

            UIRenderer.Init();
            ContextMenuManager.Init(_font);

            // ClientSize is in logical pixels — same space as mouse events.
            // Size is framebuffer (physical) pixels — only used for GL.Viewport.
            _winW = ClientSize.X;
            _winH = ClientSize.Y;
            _hierW = _winW * HIER_W_F;
            _inspW = _winW * INSP_W_F;
            _botH = _winH * BOT_H_F;

            SceneManager.RegisterScene("Game", () => new GameScene
            {
                SharedShader = _shader,
                SharedFont = _font
            }, buildIndex: 0);
            SceneManager.RegisterScene("Empty", () => new Scene(), buildIndex: 1);

            SceneManager.SceneLoaded += (s, _) =>
            {
                _selected = null;
                _inspector?.Select(null);
                _hierarchy?.Refresh();
            };

            SceneManager.LoadScene(0);
            BuildUI();
            TextInput += e => _docking?.HandleTextInput(e);

            // OS drag-and-drop files into the editor window
            FileDrop += OnFileDrop;

            // Initialise mouse pos to window centre — prevents (0,0) before first mouse move
            _mousePos = new Vector2(_winW * 0.5f, _winH * 0.5f);
            ContextMenuManager.ScreenSize = new Vector2(_winW, _winH);
        }

        // ------------------------------------------------------------------
        // BuildUI
        // ------------------------------------------------------------------
        private void BuildUI()
        {
            // ── Menu bar ─────────────────────────────────────────────────
            _menuBar = new MenuBar(_font)
            {
                Position = new Vector2(0, 50),
                Size = new Vector2(_winW, 100)
            };
            _menuBar.OnNewScene = () => SceneManager.LoadScene("Empty");
            _menuBar.OnSaveScene = () => { if (SceneManager.ActiveScene != null) SceneSaver.Save(SceneManager.ActiveScene); };
            _menuBar.OnQuit = () => Close();
            _menuBar.OnPlay = () => { if (_isPlaying) ExitPlayMode(); else EnterPlayMode(); };
            _menuBar.OnPause = () => TogglePause();
            _menuBar.OnBuild = () => RunBuild();
            _menuBar.OnBuildRun = () => RunBuild();
            _menuBar.IsPlaying = () => _isPlaying;
            _menuBar.IsPaused = () => _isPaused;

            // ── Docking ───────────────────────────────────────────────────
            _docking = new DockingSystem(_font);

            _hierarchy = new HierarchyPanel(_font);
            _hierarchy.OnSelectObject += go => SelectObject(go);

            _sceneView = new SceneViewPanel(_font, _camera);
            _sceneView.OnViewportClick += TryPickObject;

            _inspector = new InspectorPanel(_font);

            _project = new ProjectPanel(_font, "data");

            _winHierarchy = _docking.CreateWindow("Hierarchy", _hierarchy);
            _winScene = _docking.CreateWindow("Scene", _sceneView);
            _winInspector = _docking.CreateWindow("Inspector", _inspector);
            _winProject = _docking.CreateWindow("Project", _project);

            _winHierarchy.IsFloating = _winScene.IsFloating =
            _winInspector.IsFloating = _winProject.IsFloating = false;

            ApplyLayout();
        }

        // ------------------------------------------------------------------
        // ApplyLayout — called after any splitter move or resize
        // ------------------------------------------------------------------
        // ------------------------------------------------------------------
        // OS file drop — files dragged from Windows Explorer into the window
        // ------------------------------------------------------------------
        private void OnFileDrop(FileDropEventArgs e)
        {
            string assetsRoot = System.IO.Path.GetFullPath("data");

            foreach (string srcPath in e.FileNames)
            {
                if (!System.IO.File.Exists(srcPath) && !System.IO.Directory.Exists(srcPath))
                    continue;

                var assetType = DragDropPayload.Classify(srcPath);
                string destDir = assetType switch
                {
                    DragDropAssetType.Script => System.IO.Path.Combine(assetsRoot, "Scripts"),
                    DragDropAssetType.Texture => System.IO.Path.Combine(assetsRoot, "Textures"),
                    DragDropAssetType.Mesh => System.IO.Path.Combine(assetsRoot, "Models"),
                    DragDropAssetType.Material => System.IO.Path.Combine(assetsRoot, "Materials"),
                    DragDropAssetType.Shader => System.IO.Path.Combine(assetsRoot, "Shaders"),
                    DragDropAssetType.Scene => System.IO.Path.Combine(assetsRoot, "Scenes"),
                    DragDropAssetType.Audio => System.IO.Path.Combine(assetsRoot, "Audio"),
                    DragDropAssetType.Font => System.IO.Path.Combine(assetsRoot, "Fonts"),
                    _ => System.IO.Path.Combine(assetsRoot, "Assets"),
                };

                System.IO.Directory.CreateDirectory(destDir);

                if (System.IO.File.Exists(srcPath))
                {
                    string dest = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(srcPath));
                    // Don't overwrite unless different
                    if (!System.IO.File.Exists(dest))
                        System.IO.File.Copy(srcPath, dest);
                    Console.WriteLine($"[Editor] Imported {System.IO.Path.GetFileName(srcPath)} → {dest}");
                }
                else if (System.IO.Directory.Exists(srcPath))
                {
                    // Copy entire folder
                    CopyDirectory(srcPath,
                        System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(srcPath)));
                }
            }
        }

        private static void CopyDirectory(string src, string dst)
        {
            System.IO.Directory.CreateDirectory(dst);
            foreach (var f in System.IO.Directory.GetFiles(src))
                System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), false);
            foreach (var d in System.IO.Directory.GetDirectories(src))
                CopyDirectory(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
        }

        private void ApplyLayout()
        {
            if (_winHierarchy == null) return;

            float totalW = _winW;
            float totalH = _winH - MENU_H;
            float topH = totalH - _botH;

            _hierW = MathHelper.Clamp(_hierW, 100f, totalW * 0.4f);
            _inspW = MathHelper.Clamp(_inspW, 120f, totalW * 0.4f);
            _botH = MathHelper.Clamp(_botH, 80f, totalH * 0.5f);

            float sceneW = totalW - _hierW - _inspW;

            // DockWindow positions are in screen-space (no uiRoot parent now)
            _winHierarchy.Position = new Vector2(0, MENU_H);
            _winHierarchy.Size = new Vector2(_hierW, topH);

            _winScene.Position = new Vector2(_hierW, MENU_H);
            _winScene.Size = new Vector2(sceneW, topH);

            _winInspector.Position = new Vector2(_hierW + sceneW, MENU_H);
            _winInspector.Size = new Vector2(_inspW, topH);

            _winProject.Position = new Vector2(0, MENU_H + topH);
            _winProject.Size = new Vector2(totalW, _botH);

            // Content panels live inside DockWindows which have no Panel parent
            // so content.GetAbsolutePosition = DockWindow.pos + content.local_pos(0,TITLE_H)
            _hierarchy.Size = new Vector2(_hierW, topH - DockWindow.TITLE_H);
            _sceneView.Size = new Vector2(sceneW, topH - DockWindow.TITLE_H);
            _inspector.Size = new Vector2(_inspW, topH - DockWindow.TITLE_H);
            _project.Size = new Vector2(totalW, _botH - DockWindow.TITLE_H);

            _menuBar.Size = new Vector2(totalW, MENU_H);
        }

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------
        private void SelectObject(GameObject go)
        {
            _selected = go;
            _inspector?.Select(go);
            _hierarchy?.SetSelected(go);
        }

        private void TryPickObject(Vector2 screenPos)
        {
            var scene = SceneManager.ActiveScene;
            if (scene == null || _winScene == null) return;

            float vpX = _winScene.Position.X;
            float vpY = _winScene.Position.Y + DockWindow.TITLE_H + SceneViewPanel.TOOLBAR_H;
            float vpW = _winScene.Size.X;
            float vpH = _winScene.Size.Y - DockWindow.TITLE_H - SceneViewPanel.TOOLBAR_H;

            float relX = screenPos.X - vpX;
            float relY = screenPos.Y - vpY;
            if (relX < 0 || relY < 0 || relX > vpW || relY > vpH) return;

            var ctx = _sceneView.BuildContext(0f);
            var ray = Raycast.ScreenPointToRay(new Vector2(relX, relY),
                ctx.View, ctx.Projection, vpW, vpH);
            var hit = Raycast.AgainstScene(ray, scene);
            SelectObject(hit.Hit ? hit.GameObject : null);
        }

        // ------------------------------------------------------------------
        // Mouse events
        // ------------------------------------------------------------------
        protected override void OnMouseMove(MouseMoveEventArgs e)
        {
            base.OnMouseMove(e);

            // Always first — one write, every panel reads this.
            _mousePos = e.Position;
            Panel.DispatchMousePos = _mousePos;

            // Propagate to panels for hover state (IsHovered, sub-menu tracking).
            _menuBar?.HandleMouseMove(e);
            _docking?.HandleMouseMove(e);

            // Splitter dragging
            if (_dragLeft) { _hierW = _splitVal + (e.X - _splitStart.X); ApplyLayout(); }
            if (_dragRight) { _inspW = _splitVal - (e.X - _splitStart.X); ApplyLayout(); }
            if (_dragBot) { _botH = _splitVal - (e.Y - _splitStart.Y); ApplyLayout(); }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);

            // Always set dispatch pos first — before any hit-test or dispatch.
            _mousePos = new Vector2(MouseState.Position.X, MouseState.Position.Y);
            Panel.DispatchMousePos = _mousePos;
            var mp = _mousePos;

            // ── 1. Context menu gets absolute priority ─────────────────────
            // If open: consumed = true means click was inside (handled).
            //          consumed = false means click was outside (menu closed,
            //          but the click still needs to reach the panel below).
            if (ContextMenuManager.IsOpen)
            {
                bool consumed = ContextMenuManager.HandleMouseDown(e);
                if (consumed) return;
                // fall through — the click closed the menu AND should reach panels
            }

            // ── 2. Splitter dragging (LMB only) ───────────────────────────
            if (e.Button == MouseButton.Left && _winHierarchy != null)
            {
                float topH = _winH - MENU_H - _botH;
                float leftX = _hierW;
                float rightX = _winW - _inspW;
                float botY = MENU_H + topH;
                const float SW = 5f;

                if (Math.Abs(mp.X - leftX) < SW && mp.Y > MENU_H && mp.Y < botY)
                { _dragLeft = true; _splitStart = mp; _splitVal = _hierW; return; }
                if (Math.Abs(mp.X - rightX) < SW && mp.Y > MENU_H && mp.Y < botY)
                { _dragRight = true; _splitStart = mp; _splitVal = _inspW; return; }
                if (Math.Abs(mp.Y - botY) < SW)
                { _dragBot = true; _splitStart = mp; _splitVal = _botH; return; }
            }

            // ── 3. MenuBar ────────────────────────────────────────────────
            if (_menuBar?.HandleMouseDown(e) ?? false) return;

            // ── 4. Docking windows ────────────────────────────────────────
            _docking?.HandleMouseDown(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);

            // Let panels handle mouse-up FIRST so they can call DragDropService.TryDrop().
            // DragDropService.End() is the fallback cleanup if no panel consumed the drop.
            _docking?.HandleMouseUp(e);

            if (e.Button == MouseButton.Left)
            {
                DragDropService.End();   // no-op if a panel already called TryDrop()
                _dragLeft = _dragRight = _dragBot = false;
            }
        }

        protected override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Context menu Escape handling has highest priority
            if (ContextMenuManager.HandleKeyDown(e)) return;

            // Focused input field gets keyboard next
            _docking?.HandleKeyDown(e);

            // Ctrl+S = Save
            if (e.Key == Keys.S && (KeyboardState.IsKeyDown(Keys.LeftControl) || KeyboardState.IsKeyDown(Keys.RightControl)))
            {
                if (SceneManager.ActiveScene != null) SceneSaver.Save(SceneManager.ActiveScene);
                return;
            }

            switch (e.Key)
            {
                case Keys.Escape:
                    if (_isPlaying) ExitPlayMode(); else SelectObject(null);
                    break;
                case Keys.F5:
                    if (_isPlaying) ExitPlayMode(); else EnterPlayMode();
                    break;
                case Keys.F when _selected != null:
                    _camera.Position = _selected.Transform.Position + new Vector3(0, 1.5f, 4f);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Resize
        // ------------------------------------------------------------------
        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            // GL.Viewport takes physical (framebuffer) pixels
            GL.Viewport(0, 0, e.Width, e.Height );

            // All layout uses logical pixels (ClientSize = same space as mouse)
            _winW = ClientSize.X;
            _winH = ClientSize.Y;
            _hierW = _winW * HIER_W_F;
            _inspW = _winW * INSP_W_F;
            _botH = _winH * BOT_H_F;
            ContextMenuManager.ScreenSize = new Vector2(_winW, _winH);
            ApplyLayout();
        }

        // ------------------------------------------------------------------
        // Update
        // ------------------------------------------------------------------
        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);
            if (!IsFocused) return;

            float dt = (float)e.Time;

            _fpsFrames++;
            _fpsTimer += dt;
            if (_fpsTimer >= 0.5f)
            {
                string tag = _isPlaying ? (_isPaused ? " [PAUSED]" : " [PLAYING]") : "";
                Title = $"Elintria Engine Editor{tag}  —  FPS: {_fpsFrames / _fpsTimer:F0}";
                _fpsTimer = _fpsFrames = 0;
            }

            _sceneView?.HandleCameraKeys(KeyboardState, dt);

            if (!(_isPlaying && _isPaused))
                SceneManager.Update(dt);

            ContextMenuManager.Update(dt);
            // Update ghost position each frame (mouse may move while no Move event fires)
            if (DragDropService.IsDragging)
                DragDropService.UpdatePosition(_mousePos);
            _menuBar?.Update(dt);
            _docking?.Update(dt, null);
        }

        // ------------------------------------------------------------------
        // Render
        // ------------------------------------------------------------------
        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);

            // 1. Render 3-D scene into SceneViewPanel's FBO
            if (_sceneView != null)
            {
                var ctx = _sceneView.BuildContext((float)e.Time);
                _sceneView.RenderScene(ctx, _sceneView.WireframeMode);
            }

            // 2. Restore window FB and prepare for UI pass
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, Size.X, Size.Y);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.ClearColor(0.12f, 0.12f, 0.12f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            // 3. UI pass
            UIRenderer.Begin(_winW, _winH);

            // Splitters drawn first (bottom layer)
            DrawSplitters();

            // Docked windows
            _docking?.Draw();

            // MenuBar on top of everything so it is never obscured
            _menuBar?.Draw();

            // Context menus — always topmost
            ContextMenuManager.Draw();
            DragDropService.DrawGhost(_font);

            UIRenderer.End();

            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);

            SwapBuffers();
        }

        // ------------------------------------------------------------------
        // Splitters
        // ------------------------------------------------------------------
        private void DrawSplitters()
        {
            if (_winHierarchy == null) return;

            var mp = _mousePos;
            float topH = _winH - MENU_H - _botH;
            float lx = _hierW;
            float rx = _winW - _inspW;
            float by = MENU_H + topH;
            const float SW = 3f;

            Color sc = Color.FromArgb(255, 26, 26, 26);
            Color sh = Color.FromArgb(255, 55, 85, 150);

            bool hl = (Math.Abs(mp.X - lx) < 6f && mp.Y > MENU_H && mp.Y < by) || _dragLeft;
            bool hr = (Math.Abs(mp.X - rx) < 6f && mp.Y > MENU_H && mp.Y < by) || _dragRight;
            bool hb = Math.Abs(mp.Y - by) < 6f || _dragBot;

            UIRenderer.DrawRect(lx - SW * .5f, MENU_H, SW, topH, hl ? sh : sc);
            UIRenderer.DrawRect(rx - SW * .5f, MENU_H, SW, topH, hr ? sh : sc);
            UIRenderer.DrawRect(0, by - 1, _winW, SW, hb ? sh : sc);
        }

        // ------------------------------------------------------------------
        // Unload
        // ------------------------------------------------------------------
        protected override void OnUnload()
        {
            foreach (var s in SceneManager.LoadedScenes.ToArray())
                SceneManager.UnloadScene(s);

            _sceneView?.Dispose();
            _shader?.Dispose();
            _font?.Dispose();
            UIRenderer.Dispose();
            base.OnUnload();
        }
    }
}