using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Build;
using ElintriaEngine.Core;
using ElintriaEngine.UI.Panels;

namespace ElintriaEngine.UI
{
    /// <summary>
    /// Orchestrates all editor panels.
    ///
    /// RENDER ORDER (must be respected):
    ///   1. Render3D(winW, winH)   — raw GL, called BEFORE BeginFrame
    ///   2. BeginFrame             — starts 2D batch (sets stored viewport)
    ///   3. Render2D(r)            — all 2D panels via IEditorRenderer
    ///   4. EndFrame               — flushes 2D batch using stored viewport
    /// </summary>
    public class EditorLayout : IDisposable
    {
        public TopMenuBar MenuBar { get; }
        public HierarchyPanel Hierarchy { get; }
        public InspectorPanel Inspector { get; }
        public ProjectPanel Project { get; }
        public SceneViewPanel SceneView { get; }
        public BuildSettingsPanel BuildSettings { get; }
        public UI.Panels.UIEditorPanel UIEditor { get; }

        private Core.UIDocument _uiDocument = new();

        private Core.Scene _scene = new();
        private Core.Scene? _savedScene = null;  // editor snapshot, restored on Stop
        private Core.SceneRunner _runner = new();
        private Build.ScriptWatcher? _watcher;
        private volatile bool _scriptsDirty = true;   // starts dirty until first compile
        private volatile bool _scriptsCompiling = false;
        private volatile bool _pendingScriptRefresh = false;  // set on bg thread, read on main
        private PointF _mouse;
        private string _projectRoot = "";
        private int _winW, _winH;

        private const float MenuH = 24f;
        private const float HierW = 220f;
        private const float InspW = 270f;
        private const float ProjH = 200f;

        public EditorLayout(int winW, int winH, string projectRoot)
        {
            _winW = winW; _winH = winH; _projectRoot = projectRoot;

            float cntH = winH - MenuH;
            float midW = winW - HierW - InspW;
            float viewH = cntH - ProjH;

            MenuBar = new TopMenuBar(winW);
            Hierarchy = new HierarchyPanel(new RectangleF(0, MenuH, HierW, cntH));
            Inspector = new InspectorPanel(new RectangleF(winW - InspW, MenuH, InspW, cntH));
            SceneView = new SceneViewPanel(new RectangleF(HierW, MenuH, midW, viewH));
            Project = new ProjectPanel(new RectangleF(HierW, MenuH + viewH, midW, ProjH));

            BuildSettings = new BuildSettingsPanel(
                new RectangleF(winW / 2f - 240f, MenuH + 40f, 480f, 540f),
                new BuildSettings
                {
                    ProjectName = "MyGame",
                    ProjectRoot = projectRoot,
                    OutputDirectory = "Build/Output",
                });
            BuildSettings.IsVisible = false;

            UIEditor = new UI.Panels.UIEditorPanel(
                new RectangleF(winW / 2f - 420f, MenuH + 30f, 860f, 540f));
            UIEditor.SetDocument(_uiDocument);
            UIEditor.IsVisible = false;

            WireEvents(projectRoot);

            // ── Lock all docked panels – they cannot be dragged or resized ────
            Hierarchy.Locked = true;
            Inspector.Locked = true;
            SceneView.Locked = true;
            Project.Locked = true;
            // BuildSettings is a floating dialog – leave it unlocked

            Hierarchy.SetScene(_scene);
            SceneView.SetScene(_scene);
            BuildSettings.SetScene(_scene);

            string assetsDir = System.IO.Path.Combine(projectRoot, "Assets");
            string rootForProject = System.IO.Directory.Exists(assetsDir) ? assetsDir : projectRoot;
            if (System.IO.Directory.Exists(rootForProject))
                Project.SetRootPath(rootForProject);

            // Auto-compile whenever .cs files change in Assets/
            if (!string.IsNullOrEmpty(projectRoot))
            {
                _watcher = new Build.ScriptWatcher(projectRoot);
                _watcher.CompilationStarted += () =>
                {
                    _scriptsCompiling = true;
                    _scriptsDirty = false;
                };
                _watcher.CompilationFinished += ok =>
                {
                    // IMPORTANT: this callback runs on a background thread.
                    // Do NOT touch scene data or UI state here — only set flags.
                    // Update() on the main thread picks these up next frame.
                    _scriptsCompiling = false;
                    _scriptsDirty = !ok;
                    if (ok) _pendingScriptRefresh = true;
                };
                _watcher.Start();
            }
        }

        public void Init() => SceneView.Init();

        // ── Wire events ────────────────────────────────────────────────────────
        private void WireEvents(string projectRoot)
        {
            Hierarchy.SelectionChanged += go =>
            {
                Inspector.Inspect(go);
                SceneView.SetSelected(go);
            };

            Project.DragStarted += file =>
            {
                if (file.Type == AssetType.Script)
                    Inspector.SetDropHighlight(Inspector.IsVisible && Inspector.ContainsPoint(_mouse));
            };

            MenuBar.NewScene += () => { _scene = new Core.Scene(); Hierarchy.SetScene(_scene); SceneView.SetScene(_scene); Inspector.Inspect(null); BuildSettings.SetScene(_scene); };
            MenuBar.SaveScene += SaveScene;
            MenuBar.OpenScene += OpenScene;
            MenuBar.Exit += () => System.Environment.Exit(0);
            MenuBar.BuildOnly += () => { BuildSettings.IsVisible = true; BuildSettings.StartBuild(false); };
            MenuBar.BuildAndRun += () => { BuildSettings.IsVisible = true; BuildSettings.StartBuild(true); };
            MenuBar.OpenBuildSettings += () => BuildSettings.IsVisible = !BuildSettings.IsVisible;

            MenuBar.Play += EnterPlayMode;
            MenuBar.Pause += () => _runner.IsPaused = !_runner.IsPaused;
            MenuBar.Stop += ExitPlayMode;

            MenuBar.ToggleWindow += name =>
            {
                switch (name)
                {
                    case "Hierarchy":
                        Hierarchy.IsVisible = !Hierarchy.IsVisible;
                        break;
                    case "Inspector":
                        Inspector.IsVisible = !Inspector.IsVisible;
                        break;
                    case "Project":
                        Project.IsVisible = !Project.IsVisible;
                        break;
                    case "SceneView":
                        SceneView.IsVisible = !SceneView.IsVisible;
                        break;
                    case "UIEditor":
                        UIEditor.IsVisible = !UIEditor.IsVisible;
                        break;
                }
                OnResize(_winW, _winH);
            };
        }

        private bool _compilingScripts = false;  // true while dotnet build is running
        private bool _pendingPlayStart = false;  // set when compilation finishes, Start on next Update

        // ── Play mode (powered by SceneRunner) ────────────────────────────────
        private void EnterPlayMode()
        {
            if (_runner.IsRunning || _compilingScripts) return;

            // Snapshot + deep-clone the editor scene so runtime changes
            // don't corrupt it. Restore on Stop.
            _savedScene = _scene;
            try
            {
                string json = Core.SceneSerializer.ToJson(_scene);
                _scene = Core.SceneSerializer.FromJson(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Editor] Scene clone failed, using live scene: {ex.Message}");
                _scene = _savedScene;
            }

            Hierarchy.SetScene(_scene);
            SceneView.SetScene(_scene);
            Inspector.Inspect(null);

            // If the watcher is currently compiling, wait for it to finish
            // rather than starting a second redundant compile.
            if (_scriptsCompiling)
            {
                Console.WriteLine("[Editor] Waiting for background script compile to finish...");
                _compilingScripts = true;
                var sceneRef = _scene;
                _watcher!.CompilationFinished += WaitForWatcher;
                void WaitForWatcher(bool _)
                {
                    _watcher!.CompilationFinished -= WaitForWatcher;
                    _compilingScripts = false;
                    _pendingPlayStart = true;
                }
                return;
            }

            // If the watcher already produced a fresh DLL (no dirty flag), skip
            // the compile entirely and go straight to play.
            if (!_scriptsDirty)
            {
                Console.WriteLine("[Editor] Scripts are up-to-date, entering play mode.");
                _pendingPlayStart = true;
                return;
            }

            // Scripts are dirty — compile now on a background thread, then start.
            _compilingScripts = true;
            Console.WriteLine("[Editor] Compiling scripts...");
            var sceneSnapshot = _scene;
            var rootSnapshot = _projectRoot;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await Build.BuildSystem.CompileScriptsAsync(rootSnapshot);
                _scene = sceneSnapshot;
                _compilingScripts = false;
                _pendingPlayStart = true;
                Console.WriteLine("[Editor] Compilation done — starting play mode on next frame.");
            });
        }

        private void ExitPlayMode()
        {
            if (!_runner.IsRunning) return;

            // SceneRunner calls OnDisable → OnDestroy on everything
            _runner.Stop();

            // Restore the clean editor scene
            if (_savedScene != null)
            {
                _scene = _savedScene;
                _savedScene = null;
            }

            Hierarchy.SetScene(_scene);
            SceneView.SetScene(_scene);
            Inspector.Inspect(null);
        }

        /// <summary>
        /// Called every frame by EditorWindow on the main/render thread.
        /// Starts the runner after compilation completes, then ticks it each frame.
        /// </summary>
        public void Update(double dt)
        {
            // ── Post-compilation refresh (main thread only) ───────────────────
            // _pendingScriptRefresh is set by the background compile thread.
            // We load user scripts and refresh the inspector here, on the main
            // thread, so there are no data races with the render loop.
            if (_pendingScriptRefresh)
            {
                _pendingScriptRefresh = false;
                Core.SceneRunner.LoadUserScripts(_projectRoot);
                // Re-inspect so the inspector re-reads TryGetType with the newly
                // registered types and shows the real public fields immediately.
                Inspector.Inspect(Hierarchy.Selected);
                Console.WriteLine("[Editor] Script types loaded — inspector refreshed.");
            }

            // ── Start runner once compilation finishes ────────────────────────
            if (_pendingPlayStart)
            {
                _pendingPlayStart = false;
                Console.WriteLine("[Editor] Starting SceneRunner...");
                _runner.Start(_scene, _projectRoot);
            }

            _runner.Tick(dt);
        }

        // ── PHASE 1: 3D render — call BEFORE BeginFrame ────────────────────────
        public void Render3D()
        {
            SceneView.Render3D(_winW, _winH);
        }

        // ── PHASE 2: 2D render — call inside BeginFrame…EndFrame ───────────────
        public void Render2D(IEditorRenderer r)
        {
            // IMPORTANT: Do NOT fill the entire window here.
            // The 3D scene was rendered by Render3D() directly to the framebuffer.
            // Filling the whole window would paint over it.
            // Each panel draws its own background inside its bounds.

            // SceneView chrome only (header + toolbar border drawn on top of 3D)
            SceneView.OnRender(r);

            Hierarchy.OnRender(r);
            Inspector.OnRender(r);
            Project.OnRender(r);

            if (BuildSettings.IsVisible) BuildSettings.OnRender(r);
            if (UIEditor.IsVisible) UIEditor.OnRender(r);

            // Render UIDocument elements as a game overlay inside the scene view
            if (_uiDocument.Elements.Count > 0)
            {
                var sv = SceneView.ContentRect;
                Rendering.UIDocumentRenderer.Render(r, _uiDocument, sv);
            }

            // Push live compile state into the menu bar so the indicator updates
            MenuBar.IsCompiling = _scriptsCompiling || (_watcher?.IsCompiling ?? false);
            MenuBar.IsScriptsDirty = _scriptsDirty;
            MenuBar.OnRender(r);

            // Drag ghost
            if (Project.ActiveDrag != null)
            {
                var g = new RectangleF(_mouse.X + 12, _mouse.Y + 6, 120f, 18f);
                r.FillRect(g, Color.FromArgb(190, 40, 40, 90));
                r.DrawRect(g, Color.FromArgb(255, 100, 130, 220));
                r.DrawText(Project.ActiveDrag.Name, new PointF(g.X + 5, g.Y + 4), Color.White, 10f);
            }
        }

        // ── Input routing ──────────────────────────────────────────────────────
        public void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;
            if (MenuBar.OnMouseDown(e, pos)) return;

            if (BuildSettings.IsVisible && BuildSettings.ContainsPoint(pos))
            { BuildSettings.OnMouseDown(e, pos); return; }

            foreach (var p in PanelZOrder())
            {
                if (p.ContainsPoint(pos))
                {
                    foreach (var other in PanelZOrder()) if (other != p) other.IsFocused = false;
                    p.OnMouseDown(e, pos);
                    return;
                }
            }
        }

        public void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;

            // ── Script drop: fires on RELEASE (drag completes on MouseUp) ──────
            if (Project.ActiveDrag != null
                && Project.ActiveDrag.Type == AssetType.Script
                && Inspector.IsVisible
                && Inspector.ContainsPoint(pos))
            {
                Inspector.AcceptScriptDrop(Project.ActiveDrag.FullPath, _projectRoot);
                Inspector.SetDropHighlight(false);
                // Let the project panel clear ActiveDrag
                foreach (var p in PanelZOrder()) p.OnMouseUp(e, pos);
                return;
            }

            Inspector.SetDropHighlight(false);
            foreach (var p in PanelZOrder()) p.OnMouseUp(e, pos);
        }

        public void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            MenuBar.OnMouseMove(pos);

            // Update drop highlight while dragging a script
            if (Project.ActiveDrag?.Type == AssetType.Script)
                Inspector.SetDropHighlight(Inspector.IsVisible && Inspector.ContainsPoint(pos));

            foreach (var p in PanelZOrder()) p.OnMouseMove(pos);
        }

        public void OnMouseScroll(float delta)
        {
            foreach (var p in PanelZOrder())
                if (p.IsPointInContent(_mouse)) { p.OnMouseScroll(delta); return; }
        }

        public void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (e.Control && e.Key == Keys.S) { SaveScene(); return; }
            if (e.Control && e.Key == Keys.Z) { MenuBar.Undo?.Invoke(); return; }
            if (e.Control && e.Key == Keys.Y) { MenuBar.Redo?.Invoke(); return; }
            foreach (var p in PanelZOrder()) if (p.IsFocused) { p.OnKeyDown(e); return; }
        }

        public void OnKeyUp(KeyboardKeyEventArgs e)
        { foreach (var p in PanelZOrder()) if (p.IsFocused) { p.OnKeyUp(e); return; } }

        public void OnTextInput(TextInputEventArgs e)
        { foreach (var p in PanelZOrder()) if (p.IsFocused) { p.OnTextInput(e); return; } }

        public void OnResize(int w, int h)
        {
            _winW = w; _winH = h;
            MenuBar.Resize(w);

            float cntH = h - MenuH;
            float midW = w - HierW - InspW;
            float viewH = cntH - ProjH;

            Hierarchy.Bounds = new RectangleF(0, MenuH, HierW, cntH);
            Inspector.Bounds = new RectangleF(w - InspW, MenuH, InspW, cntH);
            SceneView.Bounds = new RectangleF(HierW, MenuH, midW, viewH);
            Project.Bounds = new RectangleF(HierW, MenuH + viewH, midW, ProjH);
            BuildSettings.Bounds = new RectangleF(w / 2f - 240f, MenuH + 40f, 480f, 540f);
        }

        private IEnumerable<Panel> PanelZOrder()
        {
            if (UIEditor.IsVisible) yield return UIEditor;
            if (BuildSettings.IsVisible) yield return BuildSettings;
            yield return Inspector;
            yield return Hierarchy;
            yield return Project;
            yield return SceneView;
        }

        private void SaveScene()
        {
            if (string.IsNullOrEmpty(_scene.FilePath))
            {
                // Default location
                string path = System.IO.Path.Combine(_projectRoot, "Assets", "Scenes", _scene.Name + ".scene");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                _scene.FilePath = path;
            }
            SceneSerializer.Save(_scene, _scene.FilePath);
            Console.WriteLine($"[Editor] Saved scene → {_scene.FilePath}");
        }

        private void OpenScene()
        {
            Console.WriteLine("[Editor] OpenScene: use file dialog (not yet implemented)");
        }

        public void Dispose() { _watcher?.Dispose(); _runner.Dispose(); SceneView.Dispose(); }
    }
}