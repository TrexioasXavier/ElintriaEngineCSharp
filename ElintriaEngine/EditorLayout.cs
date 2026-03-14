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
        public PreferencesWindow Preferences { get; }
        public ProjectSettingsWindow ProjectSettings { get; }

        private Core.UIDocument _uiDocument = new();

        private Core.Scene _scene = new();
        private Core.Scene? _savedScene = null;  // editor snapshot, restored on Stop
        private Core.SceneRunner _runner = new();
        private Build.ScriptWatcher? _watcher;
        private volatile bool _scriptsDirty = true;
        private volatile bool _scriptsCompiling = false;
        private volatile bool _pendingScriptRefresh = false;
        private PointF _mouse;
        private Core.GameObject? _goDragActive;  // GO being dragged from hierarchy
        private string _projectRoot = "";
        private int _winW, _winH;

        // ── Scene picker popup (File → Open Scene) ─────────────────────────────
        private bool _showScenePicker;
        private List<string> _sceneFiles = new();
        private int _scenePickerHover = -1;

        /// <summary>Fired when the user clicks File → Return to Launcher.</summary>
        public event Action? ReturnToLauncher;

        private const float MenuH = 24f;
        private const float HierW = 220f;
        private const float InspW = 270f;
        private const float ProjH = 200f;

        // ── Docking ────────────────────────────────────────────────────────────
        private DockManager _dock = null!;

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
            Inspector.SceneView = SceneView;
            Inspector.Scene = _scene;
            Inspector.ProjectRoot = projectRoot;
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

            // ── Floating editor dialogs ────────────────────────────────────────
            Preferences = new PreferencesWindow(
                new RectangleF(winW / 2f - 300f, MenuH + 40f, 600f, 520f));
            Preferences.IsVisible = false;

            ProjectSettings = new ProjectSettingsWindow(
                new RectangleF(winW / 2f - 360f, MenuH + 30f, 720f, 580f));
            ProjectSettings.IsVisible = false;

            // Load per-project tags/layers and project settings
            Core.TagsAndLayers.LoadForProject(projectRoot);
            Core.ProjectSettings.LoadForProject(projectRoot);
            Preferences.SetTagsAndLayers(Core.TagsAndLayers.Instance);
            ProjectSettings.Load(projectRoot);

            WireEvents(projectRoot);

            // ── Unlock all docked panels – DockManager handles bounds ─────────
            Hierarchy.Locked = false;
            Inspector.Locked = false;
            SceneView.Locked = false;
            Project.Locked = false;
            UIEditor.Locked = false;

            // ── Build the initial dock tree ───────────────────────────────────
            // Layout: [Hierarchy | [SceneView / Project] | Inspector]
            float dockAreaX = 0f;
            float dockAreaY = MenuH;
            float dockAreaW = winW;
            float dockAreaH = winH - MenuH;

            var sceneProj = new SplitNode(false,
                ratio: (dockAreaH - ProjH) / dockAreaH,
                new LeafNode(SceneView),
                new LeafNode(Project));

            var midRight = new SplitNode(true,
                ratio: (dockAreaW - HierW - InspW) / (dockAreaW - HierW),
                sceneProj,
                new LeafNode(Inspector));

            var root = (DockNode)new SplitNode(true,
                ratio: HierW / dockAreaW,
                new LeafNode(Hierarchy),
                midRight);

            _dock = new DockManager(root,
                new RectangleF(dockAreaX, dockAreaY, dockAreaW, dockAreaH));

            Hierarchy.SetScene(_scene);
            SceneView.SetScene(_scene);
            BuildSettings.SetScene(_scene);
            BuildSettings.SetUIDocument(_uiDocument);

            string assetsDir = System.IO.Path.Combine(projectRoot, "Assets");
            string rootForProject = System.IO.Directory.Exists(assetsDir) ? assetsDir : projectRoot;
            if (System.IO.Directory.Exists(rootForProject))
                Project.SetRootPath(rootForProject);

            // Give panels window size for context menu clamping
            Hierarchy.ScreenSize = (winW, winH);
            Project.ScreenSize = (winW, winH);

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

        public void Init()
        {
            SceneView.Init();
            AutoLoadLastScene();
        }

        private void AutoLoadLastScene()
        {
            try
            {
                string lastPath = Core.ProjectManager.LoadLastScene(_projectRoot);
                if (!string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath))
                {
                    LoadSceneFromFile(lastPath);
                    Console.WriteLine($"[Editor] Auto-loaded last scene: {lastPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Editor] Auto-load scene failed: {ex.Message}");
            }
        }

        private void LoadSceneFromFile(string path)
        {
            var loaded = Core.SceneSerializer.Load(path);
            _scene = loaded;
            Hierarchy.SetScene(_scene);
            SceneView.SetScene(_scene);
            BuildSettings.SetScene(_scene);
            Inspector.Scene = _scene;
            Inspector.Inspect(null);
            // Re-resolve scripts so hot-reloaded types work immediately
            Core.SceneRunner.LoadUserScripts(_projectRoot);
            Core.SceneRunner.ResolveEditorScripts(_scene);
            // Load companion UI document if it exists
            string uiPath = System.IO.Path.ChangeExtension(path, ".uidoc");
            if (System.IO.File.Exists(uiPath))
                _uiDocument = Core.UIDocumentSerializer.LoadFromFile(uiPath) ?? new Core.UIDocument();
            // Remember this as the last-opened scene so it auto-loads next session
            Core.ProjectManager.SaveLastScene(_projectRoot, path);
        }

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

            Hierarchy.GODragStarted += go => { _goDragActive = go; };

            MenuBar.NewScene += () => { _scene = new Core.Scene(); Hierarchy.SetScene(_scene); SceneView.SetScene(_scene); Inspector.Inspect(null); Inspector.Scene = _scene; BuildSettings.SetScene(_scene); };
            MenuBar.SaveScene += SaveScene;
            MenuBar.SaveSceneAs += SaveSceneAs;
            MenuBar.OpenScene += OpenSceneDialog;
            MenuBar.Exit += () => System.Environment.Exit(0);
            MenuBar.ReturnToLauncher += () => ReturnToLauncher?.Invoke();
            MenuBar.RegenerateScripts += ForceRegenerateScripts;
            MenuBar.BuildOnly += () => { BuildSettings.IsVisible = true; BuildSettings.StartBuild(false); };
            MenuBar.BuildAndRun += () => { BuildSettings.IsVisible = true; BuildSettings.StartBuild(true); };
            MenuBar.OpenBuildSettings += () => BuildSettings.IsVisible = !BuildSettings.IsVisible;
            MenuBar.OpenPreferences += () => Preferences.IsVisible = !Preferences.IsVisible;
            MenuBar.OpenProjectSettings += () => ProjectSettings.IsVisible = !ProjectSettings.IsVisible;

            MenuBar.Play += EnterPlayMode;
            MenuBar.Pause += () => { _runner.IsPaused = !_runner.IsPaused; SceneView.IsPaused = _runner.IsPaused; };
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
                        if (UIEditor.IsVisible)
                        {
                            // Dock it beside SceneView by default if not already in tree
                            if (!_dock.ContainsPanel(UIEditor))
                                _dock.AddPanel(UIEditor, SceneView, DockZone.Right);
                        }
                        else
                        {
                            _dock.RemovePanel(UIEditor);
                        }
                        break;
                }
                OnResize(_winW, _winH);
            };
        }

        private bool _compilingScripts = false;  // true while dotnet build is running
        private bool _pendingPlayStart = false;  // set when compilation finishes, Start on next Update

        // ── Play mode (powered by SceneRunner) ────────────────────────────────
        private void ForceRegenerateScripts()
        {
            if (_scriptsCompiling) return;

            _scriptsCompiling = true;
            _scriptsDirty = false;
            Console.WriteLine("[Editor] Force-regenerating scripts...");

            var rootSnapshot = _projectRoot;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await Build.BuildSystem.CompileScriptsAsync(rootSnapshot);
                // Flag main thread to reload user scripts + refresh UI
                _scriptsCompiling = false;
                _pendingScriptRefresh = true;
                Console.WriteLine("[Editor] Script regeneration complete.");
            });
        }

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
            SceneView.IsPlaying = true;
            SceneView.IsPaused = false;
            SceneView.UIDocument = _uiDocument;
            // Don't force Game tab — let the user stay on whichever tab they're on.
            // Scene tab continues to use the editor camera (navigable while playing).
            // Game tab shows the runtime/game camera view.
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
            SceneView.IsPlaying = false;
            SceneView.ActiveTab = SceneViewPanel.ViewTab.Scene;
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

                // 1. Load the newly compiled DLL and register all types
                Core.SceneRunner.LoadUserScripts(_projectRoot);

                // 2. Upgrade every DynamicScript placeholder AND every stale real-component
                //    instance (from a previous compile) to a fresh instance from the new assembly.
                //    This is exactly the same code path as "remove script + re-add script".
                Core.SceneRunner.ResolveEditorScripts(_scene);

                // 3. Re-draw whatever the inspector is currently showing.
                //    ForceRefresh re-inspects _target directly so it works even when
                //    nothing is selected in the hierarchy (Hierarchy.Selected could be null).
                Inspector.ForceRefresh();

                UIEditor.NotifyScriptsReloaded();
                Console.WriteLine("[Editor] Scripts reloaded — all components upgraded, inspector refreshed.");
            }

            // ── Start runner once compilation finishes ────────────────────────
            if (_pendingPlayStart)
            {
                _pendingPlayStart = false;
                Console.WriteLine("[Editor] Starting SceneRunner...");
                _runner.UIDocument = _uiDocument;
                _runner.Start(_scene, _projectRoot);
            }

            _runner.Tick(dt);
            SceneView.OnUpdate(dt);
            Preferences.OnUpdate(dt);
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
            if (UIEditor.IsVisible) UIEditor.OnRender(r);

            if (BuildSettings.IsVisible) BuildSettings.OnRender(r);
            if (Preferences.IsVisible) Preferences.OnRender(r);
            if (ProjectSettings.IsVisible) ProjectSettings.OnRender(r);

            // Push live compile state into the menu bar so the indicator updates
            MenuBar.IsCompiling = _scriptsCompiling || (_watcher?.IsCompiling ?? false);
            MenuBar.IsScriptsDirty = _scriptsDirty;
            MenuBar.OnRender(r);

            // Drag ghost
            if (_goDragActive != null)
            {
                var g = new RectangleF(_mouse.X + 12, _mouse.Y + 6, 140f, 18f);
                r.FillRect(g, Color.FromArgb(220, 30, 35, 55));
                r.DrawRect(g, Color.FromArgb(255, 80, 130, 255));
                r.DrawText("GO: " + _goDragActive.Name, new PointF(g.X + 5, g.Y + 4), Color.FromArgb(255, 180, 210, 255), 10f);
            }
            if (Inspector.ActiveDragComponent != null)
            {
                var comp = Inspector.ActiveDragComponent;
                string lbl = $"{comp.GetType().Name}  ({comp.GameObject?.Name ?? "?"})";
                var g = new RectangleF(_mouse.X + 12, _mouse.Y + 6, Math.Max(120f, lbl.Length * 6.2f + 10f), 18f);
                r.FillRect(g, Color.FromArgb(220, 35, 55, 35));
                r.DrawRect(g, Color.FromArgb(255, 90, 200, 100));
                r.DrawText(lbl, new PointF(g.X + 5, g.Y + 4), Color.FromArgb(255, 160, 240, 165), 9f);
            }
            if (Project.ActiveDrag != null)
            {
                var g = new RectangleF(_mouse.X + 12, _mouse.Y + 6, 120f, 18f);
                r.FillRect(g, Color.FromArgb(190, 40, 40, 90));
                r.DrawRect(g, Color.FromArgb(255, 100, 130, 220));
                r.DrawText(Project.ActiveDrag.Name, new PointF(g.X + 5, g.Y + 4), Color.White, 10f);
            }

            // ── Dock overlay (dividers + drop zones) ─────────────────────────
            _dock.DrawOverlay(r, _mouse);

            // ── Context menus — rendered last so they always appear on top ────
            // Collect from all dockable panels plus floating ones
            var allPanelsForCtx = new Panel[]
                { Hierarchy, Project, Inspector, SceneView, UIEditor };
            foreach (var p in allPanelsForCtx)
            {
                var ctx = p.GetActiveContextMenu();
                if (ctx != null) ctx.OnRender(r);
            }

            // Ref-field picker popup — floats above everything
            Inspector.DrawRefPickerIfOpen(r);

            // Scene picker popup
            if (_showScenePicker)
                DrawScenePicker(r);
        }

        // ── Input routing ──────────────────────────────────────────────────────
        public void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            _mouse = pos;

            // Scene picker swallows all clicks when visible
            if (_showScenePicker) { HandleScenePickerClick(pos); return; }

            if (MenuBar.OnMouseDown(e, pos)) return;

            if (BuildSettings.IsVisible && BuildSettings.ContainsPoint(pos))
            { BuildSettings.OnMouseDown(e, pos); return; }
            if (Preferences.IsVisible && Preferences.ContainsPoint(pos))
            { Preferences.OnMouseDown(e, pos); return; }
            if (ProjectSettings.IsVisible && ProjectSettings.ContainsPoint(pos))
            { ProjectSettings.OnMouseDown(e, pos); return; }

            // DockManager gets first crack at dividers; panel header drag is shared
            bool dockConsumed = _dock.OnMouseDown(e, pos);
            if (dockConsumed) return;

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

            // DockManager gets first crack at completing a dock drag/divider
            if (_dock.OnMouseUp(e, pos)) return;

            // ── Script drop ───────────────────────────────────────────────────
            if (Project.ActiveDrag != null
                && Project.ActiveDrag.Type == AssetType.Script
                && Inspector.IsVisible
                && Inspector.ContainsPoint(pos))
            {
                Inspector.AcceptScriptDrop(Project.ActiveDrag.FullPath, _projectRoot);
                Inspector.SetDropHighlight(false);
                foreach (var p in PanelZOrder()) p.OnMouseUp(e, pos);
                return;
            }

            // ── Prefab drop onto SceneView or Hierarchy → instantiate ─────────
            if (Project.ActiveDrag != null
                && Project.ActiveDrag.Type == AssetType.Prefab
                && _scene != null)
            {
                bool onScene = SceneView.IsVisible && SceneView.ContainsPoint(pos);
                bool onHier = Hierarchy.IsVisible && Hierarchy.ContainsPoint(pos);
                if (onScene || onHier)
                {
                    var go = Core.SceneSerializer.LoadPrefab(Project.ActiveDrag.FullPath);
                    if (go != null)
                    {
                        _scene.AddGameObject(go);
                        Hierarchy.SetScene(_scene);
                        Inspector.Inspect(go);
                        SceneView.SetSelected(go);
                        Hierarchy.ForceSelect(go);
                        Console.WriteLine($"[Editor] Prefab instantiated: {go.Name}");
                    }
                }
            }

            // ── GO drop onto inspector object-ref field ────────────────────────
            bool droppedOnInspector = false;
            if (_goDragActive != null && Inspector.IsVisible && Inspector.ContainsPoint(pos))
            {
                string? fid = Inspector.GetObjectRefFieldAt(pos);
                if (fid != null)
                {
                    Inspector.AcceptGODrop(fid, _goDragActive);
                    Inspector.FlushGODrops();   // apply immediately — before panels' OnMouseUp fires
                    droppedOnInspector = true;  // suppress hierarchy SelectionChanged below
                }
            }

            // ── GO drop onto Project panel → create prefab ────────────────────
            if (_goDragActive != null && Project.IsVisible && Project.ContainsPoint(pos))
            {
                string prefabName = _goDragActive.Name;
                string prefabPath = System.IO.Path.Combine(
                    Project.CurrentPath,
                    prefabName + ".prefab");
                // Unique name if file already exists
                int n = 1;
                while (System.IO.File.Exists(prefabPath))
                    prefabPath = System.IO.Path.Combine(
                        Project.CurrentPath, $"{prefabName} ({n++}).prefab");

                try
                {
                    Core.SceneSerializer.SavePrefab(_goDragActive, prefabPath);
                    Project.Refresh();
                    Console.WriteLine($"[Editor] Prefab created: {System.IO.Path.GetFileName(prefabPath)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Editor] Prefab creation failed: {ex.Message}");
                }
            }

            _goDragActive = null;
            Inspector.HoveredDropFieldId = null;
            Project.PrefabDropHighlight = false;
            SceneView.PrefabDropHighlight = false;
            Hierarchy.PrefabDropHighlight = false;
            Inspector.SetDropHighlight(false);

            if (Preferences.IsVisible) Preferences.OnMouseUp(e, pos);
            if (ProjectSettings.IsVisible) ProjectSettings.OnMouseUp(e, pos);
            foreach (var p in PanelZOrder())
            {
                // If we already flushed a GO drop onto the inspector, skip the hierarchy's
                // OnMouseUp — otherwise it fires SelectionChanged(droppedGO) and switches
                // the inspector away from the target GO.
                if (droppedOnInspector && p == Hierarchy) continue;
                p.OnMouseUp(e, pos);
            }
        }

        public void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            UpdateScenePickerHover(pos);

            // DockManager handles divider and panel-drag mouse move
            _dock.OnMouseMove(pos);
            // If the dock is actively dragging a panel, suppress panel mouse-move
            // so panels don't react to mouseover during the drag
            if (_dock.IsDragging) return;

            // Highlight inspector object-ref field under cursor during hierarchy GO drag
            if (_goDragActive != null && Inspector.IsVisible && Inspector.ContainsPoint(pos))
            {
                Inspector.HoveredDropFieldId = Inspector.GetObjectRefFieldAt(pos);
            }
            else
            {
                Inspector.HoveredDropFieldId = null;
            }

            // Highlight project panel as prefab drop target during GO drag
            Project.PrefabDropHighlight = _goDragActive != null
                && Project.IsVisible
                && Project.ContainsPoint(pos);

            // Highlight SceneView/Hierarchy as prefab instantiation targets
            bool draggingPrefab = Project.ActiveDrag?.Type == AssetType.Prefab;
            SceneView.PrefabDropHighlight = draggingPrefab && SceneView.IsVisible && SceneView.ContainsPoint(pos);
            Hierarchy.PrefabDropHighlight = draggingPrefab && Hierarchy.IsVisible && Hierarchy.ContainsPoint(pos);

            MenuBar.OnMouseMove(pos);

            // Update drop highlight while dragging a script
            if (Project.ActiveDrag?.Type == AssetType.Script)
                Inspector.SetDropHighlight(Inspector.IsVisible && Inspector.ContainsPoint(pos));

            foreach (var p in PanelZOrder()) p.OnMouseMove(pos);
            if (Preferences.IsVisible) Preferences.OnMouseMove(pos);
            if (ProjectSettings.IsVisible) ProjectSettings.OnMouseMove(pos);
        }

        public void OnMouseScroll(float delta)
        {
            if (Preferences.IsVisible && Preferences.ContainsPoint(_mouse))
            { Preferences.OnMouseScroll(delta); return; }
            if (ProjectSettings.IsVisible && ProjectSettings.ContainsPoint(_mouse))
            { ProjectSettings.OnMouseScroll(delta); return; }
            foreach (var p in PanelZOrder())
                if (p.IsPointInContent(_mouse)) { p.OnMouseScroll(delta); return; }
        }

        public void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (e.Control && e.Key == Keys.S) { SaveScene(); return; }
            if (e.Control && e.Key == Keys.Z) { MenuBar.Undo?.Invoke(); return; }
            if (e.Control && e.Key == Keys.Y) { MenuBar.Redo?.Invoke(); return; }
            if (Preferences.IsVisible) { Preferences.OnKeyDown(e); return; }
            if (ProjectSettings.IsVisible) { ProjectSettings.OnKeyDown(e); return; }
            foreach (var p in PanelZOrder()) if (p.IsFocused) { p.OnKeyDown(e); return; }
        }

        public void OnKeyUp(KeyboardKeyEventArgs e)
        {
            if (Preferences.IsVisible) { Preferences.OnKeyUp(e); return; }
            if (ProjectSettings.IsVisible) { ProjectSettings.OnKeyUp(e); return; }
            foreach (var p in PanelZOrder()) if (p.IsFocused) { p.OnKeyUp(e); return; }
        }

        public void OnTextInput(TextInputEventArgs e)
        {
            if (Preferences.IsVisible) { Preferences.OnTextInput(e); return; }
            if (ProjectSettings.IsVisible) { ProjectSettings.OnTextInput(e); return; }
            foreach (var p in PanelZOrder()) if (p.IsFocused) { p.OnTextInput(e); return; }
        }

        public void OnResize(int w, int h)
        {
            _winW = w; _winH = h;
            MenuBar.Resize(w);

            // Update the dock area and let the tree re-layout all panels
            _dock.SetArea(new RectangleF(0f, MenuH, w, h - MenuH));

            // Keep context menu clamping up to date
            Hierarchy.ScreenSize = (w, h);
            Project.ScreenSize = (w, h);

            // Floating dialogs stay centered
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
                string path = System.IO.Path.Combine(_projectRoot, "Assets", "Scenes",
                                                     _scene.Name + ".scene");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                _scene.FilePath = path;
            }
            SceneSerializer.Save(_scene, _scene.FilePath);
            Console.WriteLine($"[Editor] Saved scene → {_scene.FilePath}");

            // Remember this scene so it auto-loads next time the project is opened
            Core.ProjectManager.SaveLastScene(_projectRoot, _scene.FilePath);

            // Save UIDocument alongside the scene (.uidoc file)
            string uiPath = System.IO.Path.ChangeExtension(_scene.FilePath, ".uidoc");
            Core.UIDocumentSerializer.SaveToFile(_uiDocument, uiPath);
            Console.WriteLine($"[Editor] Saved UI → {uiPath}");
        }

        private void OpenScene()
        {
            // Scan the project's Scenes folder and show a picker overlay
            string scenesDir = System.IO.Path.Combine(_projectRoot, "Assets", "Scenes");
            _sceneFiles.Clear();
            if (System.IO.Directory.Exists(scenesDir))
            {
                foreach (var f in System.IO.Directory.GetFiles(scenesDir, "*.scene",
                             System.IO.SearchOption.AllDirectories))
                    _sceneFiles.Add(f);
            }

            if (_sceneFiles.Count == 0)
            {
                Console.WriteLine("[Editor] No .scene files found in Assets/Scenes/");
                return;
            }

            _showScenePicker = true;
            _scenePickerHover = -1;
        }

        private void SaveSceneAs()
        {
            string initDir = string.IsNullOrEmpty(_scene.FilePath)
                ? System.IO.Path.Combine(_projectRoot, "Assets", "Scenes")
                : System.IO.Path.GetDirectoryName(_scene.FilePath)!;
            string defName = string.IsNullOrEmpty(_scene.FilePath)
                ? _scene.Name + ".scene"
                : System.IO.Path.GetFileName(_scene.FilePath);

            string? path = Core.NativeDialog.SaveFile(
                "Save Scene As",
                "Scene files (*.scene)|*.scene|All files (*.*)|*.*",
                defName,
                initDir);

            if (string.IsNullOrEmpty(path)) return;
            if (!path.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
                path += ".scene";

            _scene.FilePath = path;
            _scene.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            SaveScene();
        }

        private void OpenSceneDialog()
        {
            string initDir = System.IO.Path.Combine(_projectRoot, "Assets", "Scenes");
            string? path = Core.NativeDialog.OpenFile(
                "Open Scene",
                "Scene files (*.scene)|*.scene|All files (*.*)|*.*",
                System.IO.Directory.Exists(initDir) ? initDir : _projectRoot);

            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                LoadSceneFromFile(path);
        }

        // ── Scene picker overlay ───────────────────────────────────────────────
        private void DrawScenePicker(IEditorRenderer r)
        {
            // Centre the dialog in the window
            float pw = Math.Min(500f, _winW - 80f);
            float ph = Math.Min(60f + _sceneFiles.Count * 24f + 40f, _winH - 100f);
            float px = (_winW - pw) / 2f;
            float py = (_winH - ph) / 2f;
            var dlg = new RectangleF(px, py, pw, ph);

            // Dim background
            r.FillRect(new RectangleF(0, 0, _winW, _winH), Color.FromArgb(140, 0, 0, 0));

            // Dialog box
            r.FillRect(dlg, Color.FromArgb(255, 28, 30, 36));
            r.DrawRect(dlg, Color.FromArgb(255, 60, 130, 255), 2f);

            // Title bar
            var title = new RectangleF(dlg.X, dlg.Y, dlg.Width, 28f);
            r.FillRect(title, Color.FromArgb(255, 38, 42, 58));
            r.DrawText("Open Scene", new PointF(title.X + 10f, title.Y + 7f),
                Color.FromArgb(255, 200, 215, 255), 12f);

            // Close button
            var closeBtn = new RectangleF(dlg.Right - 26f, dlg.Y + 4f, 22f, 20f);
            r.FillRect(closeBtn, Color.FromArgb(255, 120, 40, 40));
            r.DrawText("X", new PointF(closeBtn.X + 6f, closeBtn.Y + 4f), Color.White, 9f);

            // Scene rows
            float ry = dlg.Y + 34f;
            for (int i = 0; i < _sceneFiles.Count; i++)
            {
                var row = ScenePickerRowRect(dlg, i);
                bool hov = i == _scenePickerHover;
                r.FillRect(row, hov ? Color.FromArgb(255, 50, 80, 130) : Color.FromArgb(255, 35, 37, 44));
                r.DrawRect(row, Color.FromArgb(255, 50, 55, 70));

                string name = System.IO.Path.GetFileNameWithoutExtension(_sceneFiles[i]);
                string rel = TryMakeRelative(_sceneFiles[i], _projectRoot);
                r.DrawText(name, new PointF(row.X + 8f, row.Y + 4f),
                    Color.FromArgb(255, 210, 225, 255), 11f);
                r.DrawText(rel, new PointF(row.X + 8f, row.Y + 16f),
                    Color.FromArgb(180, 130, 140, 160), 8f);
                ry += 24f;
            }

            // Cancel button
            var cancelBtn = new RectangleF(dlg.X + (dlg.Width - 80f) / 2f, dlg.Bottom - 32f, 80f, 22f);
            r.FillRect(cancelBtn, Color.FromArgb(255, 55, 55, 65));
            r.DrawRect(cancelBtn, Color.FromArgb(255, 80, 80, 95));
            r.DrawText("Cancel", new PointF(cancelBtn.X + 16f, cancelBtn.Y + 5f),
                Color.FromArgb(255, 190, 190, 200), 10f);
        }

        private RectangleF ScenePickerRowRect(RectangleF dlg, int i)
        {
            float rowH = 24f;
            return new RectangleF(dlg.X + 6f, dlg.Y + 34f + i * rowH, dlg.Width - 12f, rowH - 2f);
        }

        private static string TryMakeRelative(string path, string root)
        {
            try
            {
                var rel = System.IO.Path.GetRelativePath(root, path);
                return rel.Length < path.Length ? rel : path;
            }
            catch { return path; }
        }

        private bool HandleScenePickerClick(PointF pos)
        {
            if (!_showScenePicker) return false;

            float pw = Math.Min(500f, _winW - 80f);
            float ph = Math.Min(60f + _sceneFiles.Count * 24f + 40f, _winH - 100f);
            float px = (_winW - pw) / 2f;
            float py = (_winH - ph) / 2f;
            var dlg = new RectangleF(px, py, pw, ph);

            // Close button
            var closeBtn = new RectangleF(dlg.Right - 26f, dlg.Y + 4f, 22f, 20f);
            if (closeBtn.Contains(pos)) { _showScenePicker = false; return true; }

            // Cancel button
            var cancelBtn = new RectangleF(dlg.X + (dlg.Width - 80f) / 2f, dlg.Bottom - 32f, 80f, 22f);
            if (cancelBtn.Contains(pos)) { _showScenePicker = false; return true; }

            // Scene rows
            for (int i = 0; i < _sceneFiles.Count; i++)
            {
                var row = ScenePickerRowRect(dlg, i);
                if (row.Contains(pos))
                {
                    _showScenePicker = false;
                    LoadSceneFromFile(_sceneFiles[i]);
                    Core.ProjectManager.SaveLastScene(_projectRoot, _sceneFiles[i]);
                    Console.WriteLine($"[Editor] Loaded scene: {_sceneFiles[i]}");
                    return true;
                }
            }

            // Clicking outside the dialog closes it
            if (!dlg.Contains(pos)) { _showScenePicker = false; return true; }
            return true; // swallow all clicks while picker is open
        }

        private void UpdateScenePickerHover(PointF pos)
        {
            if (!_showScenePicker) return;
            float pw = Math.Min(500f, _winW - 80f);
            float py = (_winH - Math.Min(60f + _sceneFiles.Count * 24f + 40f, _winH - 100f)) / 2f;
            float px = (_winW - pw) / 2f;
            var dlg = new RectangleF(px, py, pw, 1f); // height unused for hover
            _scenePickerHover = -1;
            for (int i = 0; i < _sceneFiles.Count; i++)
            {
                var row = ScenePickerRowRect(new RectangleF(px, py, pw, 999f), i);
                if (row.Contains(pos)) { _scenePickerHover = i; break; }
            }
        }

        public void Dispose() { _watcher?.Dispose(); _runner.Dispose(); SceneView.Dispose(); }
    }
}