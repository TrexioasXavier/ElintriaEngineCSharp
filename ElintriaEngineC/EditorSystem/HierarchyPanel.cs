using Elintria.Editor;
using Elintria.Editor.UI;
using Elintria.Engine;
using Elintria.Engine.Rendering;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Elintria.Editor.UI
{
    // =========================================================================
    // HierarchyPanel — Unity-style object hierarchy with drag-to-reparent
    // =========================================================================
    public class HierarchyPanel : Panel
    {
        // ------------------------------------------------------------------
        // Colours
        // ------------------------------------------------------------------
        static readonly Color C_Bg = Color.FromArgb(255, 50, 50, 50);
        static readonly Color C_Header = Color.FromArgb(255, 44, 44, 44);
        static readonly Color C_RowEven = Color.Transparent;
        static readonly Color C_RowOdd = Color.FromArgb(20, 255, 255, 255);
        static readonly Color C_Hover = Color.FromArgb(80, 55, 85, 145);
        static readonly Color C_Sel = Color.FromArgb(200, 44, 93, 180);
        static readonly Color C_TextAct = Color.FromArgb(255, 210, 210, 210);
        static readonly Color C_TextInact = Color.FromArgb(160, 150, 150, 150);
        static readonly Color C_Arrow = Color.FromArgb(200, 160, 160, 160);
        static readonly Color C_Drop = Color.FromArgb(200, 255, 165, 0);  // orange
        static readonly Color C_DropLine = Color.FromArgb(255, 255, 165, 0);
        static readonly Color C_Sep = Color.FromArgb(255, 30, 30, 30);
        static readonly Color C_Toolbar = Color.FromArgb(255, 44, 44, 44);
        static readonly Color C_Search = Color.FromArgb(255, 38, 38, 38);

        const float ROW_H = 20f;
        const float INDENT_W = 14f;
        const float ICON_W = 14f;
        const float TOOLBAR_H = 0f;   // removed: DockWindow title bar replaces this
        const float SEARCH_H = 22f;

        // ------------------------------------------------------------------
        private readonly BitmapFont _font;
        private GameObject _selected;
        private readonly Dictionary<GameObject, bool> _expanded = new();

        // Drag-to-reparent state
        private GameObject _dragging;
        private Vector2 _dragOffset;
        private bool _dragStarted;
        private Vector2 _dragMouseDown;
        private GameObject _dropTarget;
        private bool _dropBefore;   // insert before or parent-into

        // Scroll
        private float _scrollY = 0f;

        // Search
        private string _search = "";

        public System.Action<GameObject> OnSelectObject;

        // ------------------------------------------------------------------
        public HierarchyPanel(BitmapFont font)
        {
            _font = font;
            BackgroundColor = C_Bg;
        }

        // ------------------------------------------------------------------
        // Public
        // ------------------------------------------------------------------
        public void SetSelected(GameObject go) { _selected = go; }
        public void Refresh() { /* no-op — we draw live from scene */ }

        // ------------------------------------------------------------------
        // Draw
        // ------------------------------------------------------------------
        public override void Draw()
        {
            if (!Visible) return;
            var abs = GetAbsolutePosition();

            // Background
            UIRenderer.DrawRect(abs.X, abs.Y, Size.X, Size.Y, C_Bg);

            // Toolbar strip
            UIRenderer.DrawRect(abs.X, abs.Y, Size.X, TOOLBAR_H, C_Toolbar);
            UIRenderer.DrawRect(abs.X, abs.Y + TOOLBAR_H - 1, Size.X, 1, C_Sep);
            _font?.DrawText("Hierarchy", abs.X + 6f, abs.Y + 4f, C_TextAct);

            // + button (top-right)
            _font?.DrawText("+", abs.X + Size.X - 16f, abs.Y + 4f, C_TextAct);

            // Search bar
            float searchY = abs.Y + TOOLBAR_H;
            UIRenderer.DrawRect(abs.X + 4, searchY + 2, Size.X - 8, SEARCH_H - 4, C_Search);
            UIRenderer.DrawRectOutline(abs.X + 4, searchY + 2,
                Size.X - 8, SEARCH_H - 4, C_Sep);
            _font?.DrawText(string.IsNullOrEmpty(_search) ? "Search…" : _search,
                abs.X + 8f, searchY + 4f,
                string.IsNullOrEmpty(_search) ?
                    Color.FromArgb(100, 160, 160, 160) : C_TextAct);

            // List body
            float listY = searchY + SEARCH_H;
            float listH = Size.Y - TOOLBAR_H - SEARCH_H;

            var scene = SceneManager.ActiveScene;
            if (scene == null)
            {
                _font?.DrawText("No scene loaded", abs.X + 8f,
                    listY + 8f, C_TextInact);
                return;
            }

            float y = listY - _scrollY;
            int row = 0;

            foreach (var go in scene.RootObjects)
            {
                if (!MatchesSearch(go)) { row++; continue; }
                DrawRow(go, abs.X, ref y, listY, listY + listH, depth: 0, ref row);
            }

            // Drop-target line
            if (_dragStarted && _dropTarget != null)
            {
                // draw orange line where item will be inserted
            }

            // External drag-drop hover: show highlight on the row under mouse
            if (DragDropService.IsDragging && DragDropService.Payload?.AssetType == DragDropAssetType.Script)
            {
                var mp2 = GetMousePosition();
                var abs2 = GetAbsolutePosition();
                float listY2 = abs2.Y + TOOLBAR_H + SEARCH_H;
                var hoverGo = HitTestRow(mp2, listY2);
                if (hoverGo != null)
                {
                    float hy = GetRowY(hoverGo, abs2.Y + TOOLBAR_H + SEARCH_H);
                    UIRenderer.DrawRectOutline(abs2.X + 2f, hy, Size.X - 4f, ROW_H,
                        System.Drawing.Color.FromArgb(220, 90, 160, 255), 2f);
                    _font?.DrawText($"Add to {hoverGo.Name}", abs2.X + 8f, abs2.Y + 4f,
                        System.Drawing.Color.FromArgb(200, 90, 160, 255));
                }
            }

            // Dragged item ghost
            if (_dragStarted && _dragging != null)
            {
                var mp = GetMousePosition();
                UIRenderer.DrawRect(mp.X - _dragOffset.X, mp.Y - _dragOffset.Y,
                    Size.X * 0.6f, ROW_H,
                    Color.FromArgb(160, 44, 93, 180));
                _font?.DrawText(_dragging.Name,
                    mp.X - _dragOffset.X + 6f, mp.Y - _dragOffset.Y + 2f,
                    C_TextAct);
            }

            foreach (var c in Children) c.Draw();
        }

        private void DrawRow(GameObject go, float ax, ref float y,
                             float clipTop, float clipBot, int depth, ref int row)
        {
            bool inView = y + ROW_H >= clipTop && y <= clipBot;
            bool isSel = go == _selected;
            bool isExp = _expanded.TryGetValue(go, out bool exp) && exp;
            bool hasCh = go.GetChildren().Any();
            var mp = GetMousePosition();
            float indent = depth * INDENT_W + 4f;

            if (inView)
            {
                // Row BG
                Color bg = isSel ? C_Sel
                         : (go == _dropTarget && _dragStarted) ? Color.FromArgb(60, 255, 165, 0)
                         : row % 2 == 1 ? C_RowOdd : C_RowEven;
                if (bg != Color.Transparent)
                    UIRenderer.DrawRect(ax, y, Size.X, ROW_H, bg);

                // Hover
                bool hov = mp.X >= ax && mp.X <= ax + Size.X
                        && mp.Y >= y && mp.Y <= y + ROW_H && !isSel;
                if (hov && !_dragStarted)
                    UIRenderer.DrawRect(ax, y, Size.X, ROW_H, C_Hover);

                // Expand arrow
                if (hasCh)
                    _font?.DrawText(isExp ? "▼" : "▶",
                        ax + indent, y + 2f, C_Arrow);

                // Active dot / hidden indicator
                Color tc = go.ActiveSelf ? C_TextAct : C_TextInact;
                _font?.DrawText(go.Name, ax + indent + ICON_W, y + 2f, tc);

                // Drop-before orange line
                if (_dragStarted && go == _dropTarget && _dropBefore)
                    UIRenderer.DrawRect(ax + indent, y - 1f, Size.X - indent, 2f, C_DropLine);
            }

            y += ROW_H;
            row++;

            if (isExp && hasCh)
                foreach (var child in go.GetChildren())
                    DrawRow(child, ax, ref y, clipTop, clipBot, depth + 1, ref row);
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------
        public override bool HandleMouseDown(MouseButtonEventArgs e)
        {
            var mp = GetMousePosition();
            if (!IsPointInside(mp)) return false;

            if (e.Button == MouseButton.Right)
            {
                ContextMenuManager.Open(BuildContextMenu(mp), mp);
                return true;
            }

            if (e.Button == MouseButton.Left)
            {
                var abs = GetAbsolutePosition();
                float listY = abs.Y + TOOLBAR_H + SEARCH_H;

                // Row click
                var go = HitTestRow(mp, listY);
                if (go != null)
                {
                    _dragMouseDown = mp;
                    _dragging = go;

                    // Toggle expand on arrow area
                    float indent = GetDepth(go) * INDENT_W + 4f;
                    if (mp.X < abs.X + indent + ICON_W && go.GetChildren().Any())
                    {
                        bool cur = _expanded.TryGetValue(go, out bool ex) && ex;
                        _expanded[go] = !cur;
                    }
                    else
                    {
                        _selected = go;
                        OnSelectObject?.Invoke(go);
                    }
                    return true;
                }
            }
            return base.HandleMouseDown(e);
        }

        public override bool HandleMouseUp(MouseButtonEventArgs e)
        {
            if (e.Button == MouseButton.Left)
            {
                // ── DragDrop: script/asset from ProjectPanel dropped onto hierarchy ──
                if (DragDropService.IsDragging)
                {
                    var mp = GetMousePosition();
                    if (IsPointInside(mp))
                    {
                        var payload = DragDropService.TryDrop();
                        if (payload != null && payload.AssetType == DragDropAssetType.Script)
                        {
                            // Find which GO the mouse is over
                            var abs = GetAbsolutePosition();
                            float listY = abs.Y + TOOLBAR_H + SEARCH_H;
                            var target = HitTestRow(mp, listY);
                            if (target == null) target = _selected; // fallback to selected
                            if (target != null)
                            {
                                var compType = FindComponentType(payload.FileStem);
                                if (compType != null)
                                {
                                    typeof(GameObject)
                                        .GetMethod(nameof(GameObject.AddComponent))!
                                        .MakeGenericMethod(compType)
                                        .Invoke(target, null);
                                    Console.WriteLine($"[Hierarchy] Added {compType.Name} to {target.Name}");
                                    OnSelectObject?.Invoke(target);
                                }
                                else
                                {
                                    Console.WriteLine($"[Hierarchy] No compiled type for '{payload.FileStem}'. Build first.");
                                }
                            }
                        }
                        return true;
                    }
                    // Not over us — let DragDropService continue
                }

                // ── Internal drag-to-reparent ──
                if (_dragStarted && _dragging != null)
                    FinishDrop();

                _dragStarted = false;
                _dragging = null;
                _dropTarget = null;
            }
            return base.HandleMouseUp(e);
        }

        // Resolve script name → Component Type from all loaded assemblies
        private static System.Type FindComponentType(string typeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.IsAbstract) continue;
                    if (!typeof(Component).IsAssignableFrom(t)) continue;
                    if (string.Equals(t.Name, typeName,
                            System.StringComparison.OrdinalIgnoreCase)) return t;
                }
            }
            return null;
        }

        public override void Update(float dt)
        {
            // Detect drag start (moved >4px while LMB held)
            if (_dragging != null && !_dragStarted)
            {
                var mp = GetMousePosition();
                if ((mp - _dragMouseDown).Length > 4f)
                {
                    _dragStarted = true;
                    _dragOffset = mp - _dragMouseDown;
                }
            }

            // Update drop target while dragging
            if (_dragStarted)
            {
                var mp = GetMousePosition();
                var abs = GetAbsolutePosition();
                float listY = abs.Y + TOOLBAR_H + SEARCH_H;
                _dropTarget = HitTestRow(mp, listY);
                _dropBefore = false;
                if (_dropTarget != null)
                {
                    var abs2 = GetAbsolutePosition();
                    float rowY = GetRowY(_dropTarget, abs2.Y + TOOLBAR_H + SEARCH_H);
                    _dropBefore = mp.Y < rowY + ROW_H * 0.4f;
                }
            }

            base.Update(dt);
        }

        // ------------------------------------------------------------------
        // Drop
        // ------------------------------------------------------------------
        private void FinishDrop()
        {
            if (_dragging == null || _dropTarget == null) return;
            if (_dragging == _dropTarget) return;

            // Parent dragged onto target
            if (!_dropBefore)
            {
                _dragging.Transform.SetParent(_dropTarget.Transform);
                _expanded[_dropTarget] = true;
            }
            else
            {
                // Sibling reorder: set same parent as drop target
                var newParent = _dropTarget.Transform.Parent;
                _dragging.Transform.SetParent(newParent);
            }
        }

        // ------------------------------------------------------------------
        // Hit-testing helpers
        // ------------------------------------------------------------------
        private GameObject HitTestRow(Vector2 mp, float listStartY)
        {
            float y = listStartY - _scrollY;
            var scene = SceneManager.ActiveScene;
            if (scene == null) return null;
            foreach (var go in scene.RootObjects)
            {
                var hit = HitTestRowRec(go, mp, ref y, listStartY, listStartY + Size.Y);
                if (hit != null) return hit;
            }
            return null;
        }

        private GameObject HitTestRowRec(GameObject go, Vector2 mp,
                                         ref float y, float clipTop, float clipBot)
        {
            if (!MatchesSearch(go)) return null;

            var abs = GetAbsolutePosition();
            if (mp.Y >= y && mp.Y < y + ROW_H && y + ROW_H >= clipTop && y <= clipBot)
            {
                y += ROW_H;
                return go;
            }
            y += ROW_H;

            bool isExp = _expanded.TryGetValue(go, out bool exp) && exp;
            if (isExp)
                foreach (var child in go.GetChildren())
                {
                    var hit = HitTestRowRec(child, mp, ref y, clipTop, clipBot);
                    if (hit != null) return hit;
                }
            return null;
        }

        private float GetRowY(GameObject target, float listStartY)
        {
            float y = listStartY - _scrollY;
            var scene = SceneManager.ActiveScene;
            if (scene == null) return listStartY;
            foreach (var go in scene.RootObjects)
            {
                float r = FindRowY(go, target, ref y);
                if (r >= 0) return r;
            }
            return listStartY;
        }

        private float FindRowY(GameObject go, GameObject target, ref float y)
        {
            float cur = y;
            y += ROW_H;
            if (go == target) return cur;
            bool isExp = _expanded.TryGetValue(go, out bool exp) && exp;
            if (isExp)
                foreach (var child in go.GetChildren())
                {
                    float r = FindRowY(child, target, ref y);
                    if (r >= 0) return r;
                }
            return -1f;
        }

        private int GetDepth(GameObject go)
        {
            int d = 0;
            var t = go.Transform.Parent;
            while (t != null) { d++; t = t.Parent; }
            return d;
        }

        private bool MatchesSearch(GameObject go)
        {
            if (string.IsNullOrEmpty(_search)) return true;
            return go.Name.Contains(_search, System.StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Context menu
        // ------------------------------------------------------------------
        private List<ContextMenuItem> BuildContextMenu(Vector2 mp) => new()
        {
            ContextMenuItem.SubMenu("Create Empty", new()
            {
                ContextMenuItem.Item("Empty Object",      () => CreateGameObject()),
                ContextMenuItem.Item("3D Object – Cube",  () => CreateWithMesh("Cube",   "cube")),
                ContextMenuItem.Item("3D Object – Sphere",() => CreateWithMesh("Sphere","sphere")),
                ContextMenuItem.Item("3D Object – Plane", () => CreateWithMesh("Plane", "plane")),
            }),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Rename",                () => { }, disabled: _selected == null),
            ContextMenuItem.Item("Duplicate",             () => { }, disabled: _selected == null),
            ContextMenuItem.Item("Delete",                () => DeleteSelected(),
                                 disabled: _selected == null),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Set as Parent",         () => { }, disabled: true),
            ContextMenuItem.Item("Unparent",              () => _selected?.Transform.SetParent(null),
                                 disabled: _selected?.Transform.Parent == null),
        };

        // ------------------------------------------------------------------
        // GameObject operations
        // ------------------------------------------------------------------
        private void CreateGameObject(string name = "GameObject")
        {
            SceneManager.ActiveScene?.CreateGameObject(name);
        }

        private void CreateWithMesh(string name, string shape)
        {
            var scene = SceneManager.ActiveScene;
            if (scene == null) return;
            var go = scene.CreateGameObject(name);
            var mr = go.AddComponent<MeshRenderer>();
            mr.Mesh = shape switch
            {
                "cube" => Elintria.Engine.Rendering.Mesh.CreateCube(),
                "sphere" => Elintria.Engine.Rendering.Mesh.CreateSphere(),
                "plane" => Elintria.Engine.Rendering.Mesh.CreatePlane(),
                _ => Elintria.Engine.Rendering.Mesh.CreateCube()
            };
        }

        private void DeleteSelected()
        {
            if (_selected == null) return;
            SceneManager.ActiveScene?.Destroy(_selected);
            _selected = null;
            OnSelectObject?.Invoke(null);
        }
    }
}