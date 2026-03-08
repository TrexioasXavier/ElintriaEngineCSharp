using System;
using System.Collections.Generic;
using System.Drawing;
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

        private Core.Scene _scene = new();
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
                }
                // Always recalculate all panel bounds after any toggle so nothing
                // ends up at a stale or out-of-bounds position.
                OnResize(_winW, _winH);
            };
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

        public void Dispose() => SceneView.Dispose();
    }
}