using Elintria.Editor;
using Elintria.Editor.UI;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using TextCopy;

namespace Elintria.Editor.UI
{
    // =========================================================================
    // ProjectPanel  — Unity-style project file browser
    // =========================================================================
    /// <summary>
    /// Left column: folder tree.
    /// Right column: contents of selected folder (icons + names).
    /// Right-click anywhere → context menu exactly like Unity's.
    /// </summary>
    public class ProjectPanel : Panel
    {
        // ------------------------------------------------------------------
        // Colours
        // ------------------------------------------------------------------
        static readonly Color C_Bg = Color.FromArgb(255, 50, 50, 50);
        static readonly Color C_TreeBg = Color.FromArgb(255, 44, 44, 44);
        static readonly Color C_SelFolder = Color.FromArgb(255, 44, 93, 180);
        static readonly Color C_SelFile = Color.FromArgb(255, 44, 93, 180);
        static readonly Color C_HovRow = Color.FromArgb(80, 70, 95, 150);
        static readonly Color C_Text = Color.FromArgb(255, 200, 200, 200);
        static readonly Color C_Dim = Color.FromArgb(160, 140, 140, 140);
        static readonly Color C_Sep = Color.FromArgb(255, 30, 30, 30);
        static readonly Color C_IconDir = Color.FromArgb(255, 200, 175, 80);
        static readonly Color C_IconFile = Color.FromArgb(255, 150, 180, 220);
        static readonly Color C_IconCS = Color.FromArgb(255, 130, 220, 130);
        static readonly Color C_Toolbar = Color.FromArgb(255, 44, 44, 44);
        static readonly Color C_BreadCrumb = Color.FromArgb(255, 70, 70, 70);

        const float TREE_W = 160f;
        const float ROW_H = 20f;
        const float TOOLBAR_H = 0f;   // DockWindow provides title
        const float ICON_SIZE = 16f;

        // ------------------------------------------------------------------
        private readonly BitmapFont _font;
        private string _rootPath;
        private string _selectedFolder;
        private string _selectedFile;

        // Tree node state
        private readonly Dictionary<string, bool> _expanded = new();

        // Scroll offsets
        private float _treeScrollY = 0f;
        private float _filesScrollY = 0f;

        // Double-click detection
        private string _lastClickFile;
        private float _lastClickTime;
        private const float DOUBLE_CLICK_SEC = 0.35f;

        // Drag state
        private string _dragFilePath;        // file being dragged
        private Vector2 _dragMouseDown;
        private bool _dragStarted;

        // Drop-into-folder highlight
        private string _dropHoverFolder;

        // ------------------------------------------------------------------
        public ProjectPanel(BitmapFont font, string rootPath = "data")
        {
            _font = font;
            _rootPath = Path.GetFullPath(rootPath);
            _selectedFolder = _rootPath;
            BackgroundColor = C_Bg;

            if (!Directory.Exists(_rootPath))
                Directory.CreateDirectory(_rootPath);

            _expanded[_rootPath] = true;
        }

        // ------------------------------------------------------------------
        // Draw
        // ------------------------------------------------------------------
        public override void Draw()
        {
            if (!Visible) return;
            var abs = GetAbsolutePosition();

            // Toolbar row
            UIRenderer.DrawRect(abs.X, abs.Y, Size.X, TOOLBAR_H, C_Toolbar);
            UIRenderer.DrawRect(abs.X, abs.Y + TOOLBAR_H - 1, Size.X, 1, C_Sep);
            _font?.DrawText("Project", abs.X + 6f, abs.Y + 4f, C_Text);

            // Breadcrumb
            string rel = Path.GetRelativePath(_rootPath, _selectedFolder);
            string crumb = "Assets" + (rel == "." ? "" : " > " + rel.Replace(Path.DirectorySeparatorChar, ' '));
            _font?.DrawText(crumb, abs.X + 60f, abs.Y + 4f, C_Dim);

            // Tree panel (left)
            float bodyY = abs.Y + TOOLBAR_H;
            float bodyH = Size.Y - TOOLBAR_H;
            UIRenderer.DrawRect(abs.X, bodyY, TREE_W, bodyH, C_TreeBg);
            UIRenderer.DrawRect(abs.X + TREE_W, bodyY, 1, bodyH, C_Sep);

            // File area (right)
            UIRenderer.DrawRect(abs.X + TREE_W + 1, bodyY,
                Size.X - TREE_W - 1, bodyH, C_Bg);

            // Clip drawing to body bounds (approximate via scissor-less approach)
            DrawTree(abs.X, bodyY, TREE_W, bodyH);
            DrawFiles(abs.X + TREE_W + 2, bodyY, Size.X - TREE_W - 2, bodyH);

            // Drop-target highlight when a drag is over a folder
            if (DragDropService.IsDragging && _dropHoverFolder != null)
            {
                var abs2 = GetAbsolutePosition();
                float bodyY2 = abs2.Y + TOOLBAR_H;
                float y2 = bodyY2 + 2f - _treeScrollY;
                HighlightDropFolder(_rootPath, abs2.X, ref y2);
            }

            foreach (var c in Children) c.Draw();
        }

        private void HighlightDropFolder(string path, float ax, ref float y)
        {
            bool sel = path == _dropHoverFolder;
            if (sel)
                UIRenderer.DrawRectOutline(ax, y, TREE_W, ROW_H,
                    System.Drawing.Color.FromArgb(200, 100, 180, 255), 2f);
            y += ROW_H;
            bool exp = _expanded.TryGetValue(path, out bool ex2) && ex2;
            if (exp && Directory.Exists(path))
                foreach (var sub in Directory.GetDirectories(path).OrderBy(d => d))
                    HighlightDropFolder(sub, ax, ref y);
        }

        // ------------------------------------------------------------------
        // Draw folder tree (left column)
        // ------------------------------------------------------------------
        private void DrawTree(float ax, float ay, float w, float h)
        {
            float y = ay + 2f - _treeScrollY;
            DrawTreeNode(_rootPath, "Assets", ax, ref y, ay, ay + h, depth: 0);
        }

        private void DrawTreeNode(string path, string label,
                                  float ax, ref float y,
                                  float clipTop, float clipBot, int depth)
        {
            bool hasChildren = Directory.Exists(path) &&
                               Directory.GetDirectories(path).Length > 0;
            bool expanded = _expanded.TryGetValue(path, out bool ex) && ex;
            bool selected = path == _selectedFolder;

            if (y + ROW_H >= clipTop && y <= clipBot)
            {
                float indent = 6f + depth * 14f;

                if (selected)
                    UIRenderer.DrawRect(ax, y, TREE_W, ROW_H, C_SelFolder);
                else
                {
                    var mp = GetMousePosition();
                    if (mp.X >= ax && mp.X <= ax + TREE_W
                     && mp.Y >= y && mp.Y <= y + ROW_H)
                        UIRenderer.DrawRect(ax, y, TREE_W, ROW_H, C_HovRow);
                }

                // Arrow
                if (hasChildren)
                    _font?.DrawText(expanded ? "▼" : "▶", ax + indent, y + 3f, C_Dim);

                // Folder icon
                UIRenderer.DrawRect(ax + indent + 12f, y + 4f, ICON_SIZE - 4, ICON_SIZE - 6, C_IconDir);
                _font?.DrawText(label, ax + indent + 18f, y + 3f, C_Text);
            }

            y += ROW_H;

            if (expanded && hasChildren)
            {
                foreach (var sub in Directory.GetDirectories(path).OrderBy(d => d))
                    DrawTreeNode(sub, Path.GetFileName(sub), ax, ref y, clipTop, clipBot, depth + 1);
            }
        }

        // ------------------------------------------------------------------
        // Draw file/folder icons (right column)
        // ------------------------------------------------------------------
        private void DrawFiles(float ax, float ay, float w, float h)
        {
            if (!Directory.Exists(_selectedFolder)) return;

            float y = ay + 4f - _filesScrollY;
            float x = ax + 4f;

            // Sub-folders first
            foreach (var dir in Directory.GetDirectories(_selectedFolder).OrderBy(d => d))
            {
                if (y + ROW_H > ay + h) break;
                DrawFileRow(dir, Path.GetFileName(dir), true, ax, x, y, w);
                y += ROW_H + 2f;
            }

            // Files
            foreach (var file in Directory.GetFiles(_selectedFolder).OrderBy(f => f))
            {
                if (y + ROW_H > ay + h) break;
                DrawFileRow(file, Path.GetFileName(file), false, ax, x, y, w);
                y += ROW_H + 2f;
            }

            if (y <= ay + 40f)   // empty folder hint
                _font?.DrawText("(empty)", ax + 8f, ay + 20f, C_Dim);
        }

        private void DrawFileRow(string path, string name, bool isDir,
                                 float ax, float x, float y, float totalW)
        {
            bool sel = path == _selectedFile || path == _selectedFolder;
            var mp = GetMousePosition();
            bool hov = mp.X >= ax && mp.X <= ax + totalW
                    && mp.Y >= y && mp.Y <= y + ROW_H;

            if (sel) UIRenderer.DrawRect(ax, y, totalW, ROW_H, C_SelFile);
            else if (hov) UIRenderer.DrawRect(ax, y, totalW, ROW_H, C_HovRow);

            Color ic = isDir ? C_IconDir
                     : name.EndsWith(".cs") ? C_IconCS
                     : C_IconFile;

            UIRenderer.DrawRect(x, y + 3f, ICON_SIZE - 2, ICON_SIZE - 4, ic);
            _font?.DrawText(name, x + ICON_SIZE, y + 3f, C_Text);
        }

        // ------------------------------------------------------------------
        // Update — detect drag start after >6px movement; tick double-click timer
        // ------------------------------------------------------------------
        public override void Update(float dt)
        {
            _lastClickTime -= dt;
            base.Update(dt);

            if (_dragFilePath != null && !_dragStarted)
            {
                var mp = GetMousePosition();
                if ((mp - _dragMouseDown).Length > 6f)
                {
                    _dragStarted = true;
                    var payload = new DragDropPayload
                    {
                        FilePath = _dragFilePath,
                        AssetType = DragDropPayload.Classify(_dragFilePath),
                        Source = DragDropSource.ProjectPanel
                    };
                    DragDropService.Begin(payload, _dragMouseDown);
                }
            }

            if (DragDropService.IsDragging)
            {
                DragDropService.UpdatePosition(GetMousePosition());

                // Determine which folder is being hovered for drop highlighting
                var mp2 = GetMousePosition();
                var abs = GetAbsolutePosition();
                float bodyY = abs.Y + TOOLBAR_H;
                if (mp2.X >= abs.X && mp2.X <= abs.X + TREE_W)
                {
                    float ty = bodyY + 2f - _treeScrollY;
                    _dropHoverFolder = HitTestFolder(_rootPath, mp2, abs.X, ref ty);
                }
                else
                    _dropHoverFolder = null;
            }
        }

        private string HitTestFolder(string path, Vector2 mp, float ax, ref float y)
        {
            bool hit = mp.Y >= y && mp.Y <= y + ROW_H;
            y += ROW_H;
            if (hit) return path;
            bool exp = _expanded.TryGetValue(path, out bool ex) && ex;
            if (exp && Directory.Exists(path))
                foreach (var sub in Directory.GetDirectories(path).OrderBy(d => d))
                {
                    var r = HitTestFolder(sub, mp, ax, ref y);
                    if (r != null) return r;
                }
            return null;
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------
        public override bool HandleMouseDown(MouseButtonEventArgs e)
        {
            var mp = GetMousePosition();
            var abs = GetAbsolutePosition();
            if (!IsPointInside(mp)) return false;

            float bodyY = abs.Y + TOOLBAR_H;

            // Right-click → context menu
            if (e.Button == MouseButton.Right)
            {
                ContextMenuManager.Open(BuildContextMenu(), mp);
                return true;
            }

            // Left-click in tree
            if (e.Button == MouseButton.Left && mp.X <= abs.X + TREE_W)
            {
                float y = bodyY + 2f - _treeScrollY;
                HandleTreeClick(_rootPath, mp, abs.X, ref y);
                return true;
            }

            // Left-click in files area
            if (e.Button == MouseButton.Left && mp.X > abs.X + TREE_W)
            {
                HandleFilesClick(mp, abs.X + TREE_W + 2, bodyY);
                // Record as potential drag source
                if (_selectedFile != null)
                {
                    _dragFilePath = _selectedFile;
                    _dragMouseDown = mp;
                    _dragStarted = false;
                }
                return true;
            }

            return base.HandleMouseDown(e);
        }

        public override bool HandleMouseUp(OpenTK.Windowing.Common.MouseButtonEventArgs e)
        {
            if (e.Button == OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left)
            {
                if (_dragStarted && DragDropService.IsDragging)
                {
                    var mp = GetMousePosition();
                    var abs = GetAbsolutePosition();
                    // If mouse released inside our own tree area → folder-move
                    bool overTree = IsPointInside(mp) && mp.X >= abs.X && mp.X <= abs.X + TREE_W;
                    if (overTree && _dropHoverFolder != null && _dragFilePath != null
                        && _dropHoverFolder != Path.GetDirectoryName(_dragFilePath))
                    {
                        string dest = Path.Combine(_dropHoverFolder, Path.GetFileName(_dragFilePath));
                        if (!File.Exists(dest))
                        {
                            File.Move(_dragFilePath, dest);
                            _selectedFile = dest;
                        }
                        DragDropService.End();   // consumed here
                    }
                    // If released OUTSIDE (over Inspector/Hierarchy), leave IsDragging = true
                    // so those panels can call TryDrop() in their own HandleMouseUp.
                    // Editor.OnMouseUp() calls DragDropService.End() as a final fallback.
                }
                _dragFilePath = null;
                _dragStarted = false;
                _dropHoverFolder = null;
            }
            return base.HandleMouseUp(e);
        }

        private void HandleTreeClick(string path, Vector2 mp,
                                     float ax, ref float y)
        {
            float indent = 0f; // (depth handled inside; just check click y band)
            bool hit = mp.Y >= y && mp.Y <= y + ROW_H;
            if (hit)
            {
                _selectedFolder = path;
                _filesScrollY = 0f;
                // Toggle expand
                bool cur = _expanded.TryGetValue(path, out bool e) && e;
                _expanded[path] = !cur;
            }
            y += ROW_H;

            bool expanded = _expanded.TryGetValue(path, out bool ex) && ex;
            if (expanded && Directory.Exists(path))
                foreach (var sub in Directory.GetDirectories(path).OrderBy(d => d))
                    HandleTreeClick(sub, mp, ax, ref y);
        }

        private void HandleFilesClick(Vector2 mp, float ax, float ay)
        {
            if (!Directory.Exists(_selectedFolder)) return;
            float y = ay + 4f - _filesScrollY;

            foreach (var dir in Directory.GetDirectories(_selectedFolder).OrderBy(d => d))
            {
                if (mp.Y >= y && mp.Y <= y + ROW_H)
                {
                    if (_lastClickFile == dir && _lastClickTime > 0f)
                    {
                        // Double-click folder → navigate into it
                        _selectedFolder = dir; _filesScrollY = 0f;
                    }
                    else
                    {
                        _selectedFolder = dir; _filesScrollY = 0f;
                    }
                    _lastClickFile = dir;
                    _lastClickTime = DOUBLE_CLICK_SEC;
                    return;
                }
                y += ROW_H + 2f;
            }
            foreach (var file in Directory.GetFiles(_selectedFolder).OrderBy(f => f))
            {
                if (mp.Y >= y && mp.Y <= y + ROW_H)
                {
                    bool isDouble = (_lastClickFile == file && _lastClickTime > 0f);
                    _lastClickFile = file;
                    _lastClickTime = DOUBLE_CLICK_SEC;
                    _selectedFile = file;

                    if (isDouble)
                    {
                        // Double-click: open scripts in Visual Studio via .sln
                        // Any other file type: open with OS default
                        if (file.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
                            Elintria.Editor.ScriptCreator.OpenInEditor(file);
                        else
                            OpenSelected();
                    }
                    return;
                }
                y += ROW_H + 2f;
            }
        }

        // ------------------------------------------------------------------
        // Context menu (right-click in project window)
        // ------------------------------------------------------------------
        private List<ContextMenuItem> BuildContextMenu() => new()
        {
            ContextMenuItem.SubMenu("Create", new()
            {
                ContextMenuItem.Item("Folder",         () => CreateFolder()),
                ContextMenuItem.Sep(),
                ContextMenuItem.Item("C# Script",      () => {
                    string path = ScriptCreator.CreateScript("NewScript");
                    if (path != null) ScriptCreator.OpenInEditor(path);
                }),
                ContextMenuItem.Item("Material",       () => CreateFile("NewMaterial.mat", "")),
                ContextMenuItem.Item("Shader",         () => CreateFile("NewShader.glsl", "")),
                ContextMenuItem.Item("Scene",          () => CreateFile("NewScene.scene", "")),
            }),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Show in Explorer",   () => OpenInExplorer()),
            ContextMenuItem.Item("Open",               () => OpenSelected(),
                disabled: _selectedFile == null),
            ContextMenuItem.Item("Delete",             () => DeleteSelected(),
                disabled: _selectedFile == null && _selectedFolder == _rootPath),
            ContextMenuItem.Item("Rename",             () => { /* TODO rename dialog */ }),
            ContextMenuItem.Item("Copy Path",          () =>
            {
                string p = _selectedFile ?? _selectedFolder;
                CopyToClipboard(p);
            }),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Import New Asset…",  () => { }),
            ContextMenuItem.Item("Refresh",            () => { }),
            ContextMenuItem.Item("Reimport",           () => { }),
            ContextMenuItem.Item("Reimport All",       () => { }),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Open C# Project",    () => { }),
            ContextMenuItem.Item("Find References In Scene", () => { }, disabled: true),
            ContextMenuItem.Item("Select Dependencies",() => { }, disabled: true),
            ContextMenuItem.Sep(),
            ContextMenuItem.Item("Properties…",        () => { }, disabled: true),
        };

        // ------------------------------------------------------------------
        // File operations
        // ------------------------------------------------------------------
        private void CreateFolder()
        {
            string p = Path.Combine(_selectedFolder, "New Folder");
            int n = 0;
            while (Directory.Exists(p + (n > 0 ? $" {n}" : ""))) n++;
            Directory.CreateDirectory(p + (n > 0 ? $" {n}" : ""));
        }

        private void CreateFile(string name, string contents)
        {
            string p = Path.Combine(_selectedFolder, name);
            if (!File.Exists(p)) File.WriteAllText(p, contents);
        }

        private void DeleteSelected()
        {
            if (_selectedFile != null && File.Exists(_selectedFile))
            { File.Delete(_selectedFile); _selectedFile = null; }
        }

        private void OpenSelected()
        {
            if (_selectedFile == null) return;
            if (_selectedFile.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
                Elintria.Editor.ScriptCreator.OpenInEditor(_selectedFile);
            else
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = _selectedFile, UseShellExecute = true });
                }
                catch { }
        }

        private void OpenInExplorer()
        {
            string p = _selectedFile != null
                ? Path.GetDirectoryName(_selectedFile) : _selectedFolder;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = p, UseShellExecute = true });
            }
            catch { }
        }

        private static void CopyToClipboard(string text)
        {
            try
            {
                if (System.OperatingSystem.IsWindows())
                {
                    var t = new System.Threading.Thread(() =>
                        ClipboardService.SetText(text));
                    t.SetApartmentState(System.Threading.ApartmentState.STA);
                    t.Start(); t.Join();
                }
            }
            catch { }
        }
    }
}