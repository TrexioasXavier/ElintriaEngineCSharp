using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;

namespace ElintriaEngine.UI.Panels
{
    public class HierarchyPanel : Panel
    {
        private Scene? _scene;
        private GameObject? _selected;
        private GameObject? _hovered;
        private GameObject? _renaming;
        private string _renameBuffer = "";

        // Tracks which IDs are collapsed (children hidden)
        private readonly HashSet<int> _collapsed = new();
        // Tracks which objects are manually hidden in the scene view
        private readonly HashSet<int> _hidden = new();

        private ContextMenu? _ctxMenu;
        private bool _showCtx;
        // The object that was right-clicked (used for parenting new objects)
        private GameObject? _ctxTarget;

        private GameObject? _dragGO;
        private GameObject? _dropTarget;
        private bool _isDragging;
        private PointF _dragStart;
        private const float DragThresh = 6f;

        private GameObject? _lastClicked;
        private double _lastClickTime;

        private const float RowH = 22f;
        private const float Indent = 16f;
        private const float EyeW = 20f;   // width of the visibility toggle column

        public event Action<GameObject?>? SelectionChanged;

        public HierarchyPanel(RectangleF bounds) : base("Hierarchy", bounds)
        { MinWidth = 150f; MinHeight = 120f; }

        public void SetScene(Scene s) { _scene = s; _collapsed.Clear(); _hidden.Clear(); }
        public GameObject? Selected => _selected;
        /// <summary>Non-null while the user is dragging a GO out of the hierarchy.</summary>
        public GameObject? ActiveDragGO => _isDragging ? _dragGO : null;
        public event Action<GameObject>? GODragStarted;
        public bool IsHidden(GameObject go) => _hidden.Contains(go.InstanceId);

        // ── Render ─────────────────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            DrawHeader(r);

            var cr = ContentRect;
            r.PushClip(cr);
            r.FillRect(cr, ColBg);

            if (_scene == null)
            { r.DrawText("No scene loaded.", new PointF(cr.X + 8, cr.Y + 8), ColTextDim, 11f); }
            else
            {
                float y = cr.Y - ScrollOffset;
                ContentHeight = 0;
                int rowIndex = 0;
                foreach (var root in _scene.RootObjects)
                    DrawNode(r, root, 0, cr, ref y, ref rowIndex);
            }

            r.PopClip();
            DrawScrollBar(r);

            // Drop line
            if (_isDragging && _dropTarget != null)
            {
                float dy = GetNodeScreenY(_dropTarget);
                if (dy >= 0)
                    r.DrawLine(new PointF(cr.X, dy), new PointF(cr.Right, dy),
                        Color.FromArgb(255, 90, 175, 255), 2f);
            }

            if (_showCtx && _ctxMenu != null)
                _ctxMenu.OnRender(r);
        }

        private void DrawNode(IEditorRenderer r, GameObject go, int depth,
            RectangleF cr, ref float y, ref int rowIndex)
        {
            bool inView = (y + RowH > cr.Y) && (y < cr.Bottom);
            if (inView)
            {
                bool sel = _selected == go;
                bool hov = _hovered == go;
                var row = new RectangleF(cr.X, y, cr.Width, RowH);

                // Row background
                if (sel) r.FillRect(row, ColSelected);
                else if (hov) r.FillRect(row, ColHover);
                else if ((rowIndex & 1) == 1) r.FillRect(row, Color.FromArgb(10, 255, 255, 255));

                // ── Visibility eye toggle (right side) ────────────────────────
                bool isHidden = _hidden.Contains(go.InstanceId);
                var eyeRect = new RectangleF(cr.Right - EyeW - 4f, y + 4f, 14f, 14f);
                r.FillRect(eyeRect, isHidden ? Color.FromArgb(255, 55, 55, 55) : Color.FromArgb(255, 50, 100, 160));
                r.DrawText(isHidden ? "H" : "V", new PointF(eyeRect.X + 2f, eyeRect.Y + 2f), Color.White, 8f);

                float xo = cr.X + 4f + depth * Indent;

                // ── Collapse arrow ────────────────────────────────────────────
                bool hasChildren = go.Children.Count > 0;
                bool isCollapsed = _collapsed.Contains(go.InstanceId);
                if (hasChildren)
                {
                    string arr = isCollapsed ? "+" : "-";
                    r.FillRect(new RectangleF(xo, y + 4f, 14f, 14f), Color.FromArgb(255, 52, 52, 52));
                    r.DrawText(arr, new PointF(xo + 3f, y + 4f), ColTextDim, 9f);
                }

                // ── Object name ───────────────────────────────────────────────
                string label = go == _renaming ? _renameBuffer + "|" : go.Name;
                var nameColor = isHidden ? ColTextDim
                    : go.ActiveSelf ? ColText
                    : Color.FromArgb(255, 120, 120, 120);
                r.DrawText(label, new PointF(xo + 16f, y + 5f), nameColor, 11f);

                // Component hint
                string hint = "";
                if (go.HasComponent("Camera")) hint = " [CAM]";
                if (go.HasComponent("Light")) hint = " [LGT]";
                if (hint.Length > 0)
                    r.DrawText(hint, new PointF(xo + 16f + label.Length * 6.5f, y + 5f),
                        Color.FromArgb(255, 130, 160, 220), 9f);
            }

            y += RowH;
            ContentHeight += RowH;
            rowIndex++;

            // Draw children if not collapsed
            if (go.Children.Count > 0 && !_collapsed.Contains(go.InstanceId))
                foreach (var child in go.Children)
                    DrawNode(r, child, depth + 1, cr, ref y, ref rowIndex);
        }

        private float GetNodeScreenY(GameObject target)
        {
            if (_scene == null) return -1f;
            float y = ContentRect.Y - ScrollOffset;
            return FindNodeY(_scene.RootObjects, target, ref y) ? y : -1f;
        }

        private bool FindNodeY(IEnumerable<GameObject> list, GameObject target, ref float y)
        {
            foreach (var go in list)
            {
                if (go == target) return true;
                y += RowH;
                if (!_collapsed.Contains(go.InstanceId))
                    if (FindNodeY(go.Children, target, ref y)) return true;
            }
            return false;
        }

        // ── Hit test ──────────────────────────────────────────────────────────
        private (GameObject? go, bool onArrow, bool onEye) HitTest(PointF pos)
        {
            if (_scene == null) return (null, false, false);
            float y = ContentRect.Y - ScrollOffset;
            return HitList(_scene.RootObjects, pos, 0, ref y);
        }

        private (GameObject? go, bool onArrow, bool onEye) HitList(
            IEnumerable<GameObject> list, PointF p, int depth, ref float y)
        {
            var cr = ContentRect;
            float xo = cr.X + 4f + depth * Indent;
            foreach (var go in list)
            {
                var row = new RectangleF(cr.X, y, cr.Width, RowH);
                var arrow = new RectangleF(xo, y + 4f, 14f, 14f);
                var eye = new RectangleF(cr.Right - EyeW - 4f, y + 4f, 14f, 14f);
                if (row.Contains(p)) return (go, arrow.Contains(p), eye.Contains(p));
                y += RowH;
                if (!_collapsed.Contains(go.InstanceId))
                {
                    var (child, ca, ce) = HitList(go.Children, p, depth + 1, ref y);
                    if (child != null) return (child, ca, ce);
                }
            }
            return (null, false, false);
        }

        // ── Mouse ──────────────────────────────────────────────────────────────
        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible) return;

            if (_showCtx && _ctxMenu != null)
            {
                if (_ctxMenu.ContainsPoint(pos)) { _ctxMenu.OnMouseDown(e, pos); _showCtx = false; return; }
                _showCtx = false; return;
            }

            if (!Bounds.Contains(pos)) { base.OnMouseDown(e, pos); return; }
            IsFocused = true;

            if (_renaming != null) { CommitRename(); return; }
            if (!ContentRect.Contains(pos)) { base.OnMouseDown(e, pos); return; }

            var (hit, onArrow, onEye) = HitTest(pos);

            // Right-click always shows context menu
            if (e.Button == MouseButton.Right)
            {
                _ctxTarget = hit;
                if (hit != null) { _selected = hit; SelectionChanged?.Invoke(hit); }
                ShowContextMenu(pos, hit);
                return;
            }

            if (hit == null) { _selected = null; SelectionChanged?.Invoke(null); return; }

            // Eye toggle
            if (onEye)
            {
                if (_hidden.Contains(hit.InstanceId)) _hidden.Remove(hit.InstanceId);
                else _hidden.Add(hit.InstanceId);
                return;
            }

            // Collapse arrow
            if (onArrow && hit.Children.Count > 0)
            {
                if (_collapsed.Contains(hit.InstanceId)) _collapsed.Remove(hit.InstanceId);
                else _collapsed.Add(hit.InstanceId);
                return;
            }

            // Double-click to rename
            double now = Environment.TickCount64 / 1000.0;
            if (_lastClicked == hit && now - _lastClickTime < 0.4)
                StartRename(hit);
            else { _lastClicked = hit; _lastClickTime = now; }

            _selected = hit; SelectionChanged?.Invoke(hit);
            _dragGO = hit; _dragStart = pos;
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            if (_isDragging && _dragGO != null && _dropTarget != null
                && _dropTarget != _dragGO
                && !_dropTarget.IsDescendantOf(_dragGO))
            {
                _dragGO.SetParent(_dropTarget);
                // Remove from scene root list if needed
                _scene?.AddGameObject(_dragGO); // AddGameObject checks for duplicates
            }
            _isDragging = false; _dragGO = null; _dropTarget = null;
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseMove(PointF pos)
        {
            base.OnMouseMove(pos);
            _ctxMenu?.OnMouseMove(pos);
            _hovered = ContentRect.Contains(pos) ? HitTest(pos).go : null;

            if (_dragGO != null && !_isDragging)
            {
                float d = MathF.Sqrt(MathF.Pow(pos.X - _dragStart.X, 2) +
                                     MathF.Pow(pos.Y - _dragStart.Y, 2));
                if (d > DragThresh)
                {
                    _isDragging = true;
                    if (_dragGO != null) GODragStarted?.Invoke(_dragGO);
                }
            }
            if (_isDragging) _dropTarget = HitTest(pos).go;
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (_renaming != null)
            {
                switch (e.Key)
                {
                    case Keys.Enter: CommitRename(); break;
                    case Keys.Escape: _renaming = null; break;
                    case Keys.Backspace when _renameBuffer.Length > 0:
                        _renameBuffer = _renameBuffer[..^1]; break;
                }
                return;
            }
            if (_selected == null) return;
            if (e.Key == Keys.Delete) DeleteSelected();
            if (e.Key == Keys.F2) StartRename(_selected);
            if (e.Control && e.Key == Keys.D) DuplicateSelected();
        }

        public override void OnTextInput(TextInputEventArgs e)
        { if (_renaming != null) _renameBuffer += e.AsString; }

        // ── Rename ────────────────────────────────────────────────────────────
        private void StartRename(GameObject go) { _renaming = go; _renameBuffer = go.Name; }
        private void CommitRename()
        {
            if (_renaming != null && _renameBuffer.Trim().Length > 0)
                _renaming.Name = _renameBuffer.Trim();
            _renaming = null;
        }

        // ── Delete / Duplicate ────────────────────────────────────────────────
        private void DeleteSelected()
        {
            if (_selected == null || _scene == null) return;
            _scene.RemoveGameObject(_selected);
            _selected = null; SelectionChanged?.Invoke(null);
        }

        private void DuplicateSelected()
        {
            if (_selected == null || _scene == null) return;
            var dup = _selected.Duplicate();
            if (_selected.Parent != null) dup.SetParent(_selected.Parent);
            else _scene.AddGameObject(dup);
            _selected = dup; SelectionChanged?.Invoke(dup);
        }

        // ── Context menu ──────────────────────────────────────────────────────
        private void ShowContextMenu(PointF pos, GameObject? target)
        {
            // _ctxTarget is what was right-clicked — new objects will be parented to it
            var items = new List<ContextMenuItem>
            {
                new("Create Empty",        () => Create("GameObject", null, _ctxTarget)),
                ContextMenuItem.Separator,
                new("-- 3D Objects --",    null) { IsDisabled = true },
                new("  Cube",              () => Create("Cube",             "MeshFilter,MeshRenderer", _ctxTarget)),
                new("  Sphere",            () => Create("Sphere",           "MeshFilter,MeshRenderer", _ctxTarget)),
                new("  Plane",             () => Create("Plane",            "MeshFilter,MeshRenderer", _ctxTarget)),
                new("  Capsule",           () => Create("Capsule",          "MeshFilter,MeshRenderer", _ctxTarget)),
                new("  Cylinder",          () => Create("Cylinder",         "MeshFilter,MeshRenderer", _ctxTarget)),
                ContextMenuItem.Separator,
                new("-- Lights --",        null) { IsDisabled = true },
                new("  Directional Light", () => Create("Directional Light","Light",                   _ctxTarget)),
                new("  Point Light",       () => Create("Point Light",      "Light",                   _ctxTarget)),
                new("  Spot Light",        () => Create("Spot Light",       "Light",                   _ctxTarget)),
                ContextMenuItem.Separator,
                new("  Camera",            () => Create("Camera",           "Camera",                  _ctxTarget)),
                ContextMenuItem.Separator,
                new("-- UI --",            null) { IsDisabled = true },
                new("  Canvas",            () => Create("Canvas",           "Canvas,CanvasRenderer",   _ctxTarget)),
                new("  Button",            () => Create("Button",           "Canvas,CanvasRenderer,Button", _ctxTarget)),
                new("  Text",              () => Create("Text",             "Canvas,CanvasRenderer,Text", _ctxTarget)),
                ContextMenuItem.Separator,
                new("-- Effects --",       null) { IsDisabled = true },
                new("  Particle System",   () => Create("Particle System",  "ParticleSystem",          _ctxTarget)),
            };

            if (target != null)
            {
                items.Add(ContextMenuItem.Separator);
                items.Add(new("Rename (F2)", () => StartRename(target)));
                items.Add(new("Duplicate (Ctrl+D)", () => { _selected = target; DuplicateSelected(); }));
                items.Add(new("Delete", () => { _selected = target; DeleteSelected(); }));
            }

            _ctxMenu = new ContextMenu(pos, items);
            _showCtx = true;
        }

        private static readonly HashSet<string> _meshShapes =
            new(StringComparer.OrdinalIgnoreCase)
            { "Cube", "Sphere", "Plane", "Capsule", "Cylinder" };

        private void Create(string name, string? comps, GameObject? parent)
        {
            if (_scene == null) return;
            var go = new GameObject(name);
            if (!string.IsNullOrEmpty(comps))
                foreach (var c in comps.Split(','))
                    go.AddComponentByName(c.Trim());

            // Set the MeshName so the SceneRenderer draws the correct primitive
            if (_meshShapes.Contains(name))
            {
                var mf = go.GetComponent<ElintriaEngine.Core.MeshFilter>();
                if (mf != null) mf.MeshName = name;
            }

            if (parent != null)
            {
                go.SetParent(parent);
                _collapsed.Remove(parent.InstanceId);
                // Ensure root ancestor is in scene roots
                var root = parent;
                while (root.Parent != null) root = root.Parent;
                _scene.AddGameObject(root);
            }
            else
            {
                _scene.AddGameObject(go);
            }

            _selected = go; SelectionChanged?.Invoke(go);
            _showCtx = false;
        }
    }
}