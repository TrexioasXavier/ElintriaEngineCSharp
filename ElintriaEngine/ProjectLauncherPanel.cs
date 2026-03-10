using ElintriaEngine.Core;
using ElintriaEngine.UI.Panels;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace ElintriaEngine.UI
{
    /// <summary>
    /// Full-window project launcher.
    ///
    /// BUG FIX NOTES:
    ///   All interactive rects (text fields, buttons, cards) are CACHED during
    ///   Render() into _hitRects and _fieldRects. OnMouseDown reads from those
    ///   caches rather than recomputing y-positions independently, which was
    ///   causing misaligned hit areas and broken field interactivity.
    ///
    ///   CommitEdit() is no longer called blindly at the top of OnMouseDown.
    ///   Instead it is only called when clicking something OTHER than the
    ///   currently-active field, preventing the "Open Project" click from being
    ///   swallowed by an edit commit.
    /// </summary>
    public sealed class ProjectLauncherPanel
    {
        public event Action<string>? ProjectOpened;

        // ── Mode ─────────────────────────────────────────────────────────────
        private enum RightMode { NewProject, ProjectInfo }
        private RightMode _mode = RightMode.NewProject;

        // ── Data ─────────────────────────────────────────────────────────────
        private List<ProjectManifest> _projects = new();
        private ProjectManifest? _selected;
        private bool _confirmDelete;

        // New-project form values
        private string _newName = "MyProject";
        private string _newPath = "";
        private ProjectType _newType = ProjectType.ThreeD;
        private string _newDesc = "";
        private string? _newError;
        private string? _newSuccess;

        // ── Text field editing ────────────────────────────────────────────────
        private string? _editId;
        private string _editBuf = "";
        private Action<string>? _editCommit;

        // ── Hit-test cache (rebuilt every Render call) ────────────────────────
        // Generic clickable: id -> (rect, action)
        private readonly Dictionary<string, (RectangleF Rect, Action Action)> _hitRects = new();
        // Text fields: id -> (rect, currentValue, setter)
        private readonly Dictionary<string, (RectangleF Rect, Func<string> Getter, Action<string> Setter)> _fieldRects = new();
        // Project card rows: index -> (rect, manifest)
        private readonly List<(RectangleF Rect, ProjectManifest Proj)> _projRows = new();

        // ── Layout ───────────────────────────────────────────────────────────
        private int _winW, _winH;
        private const float SideW = 276f;
        private const float PAD = 14f;
        private const float RowH = 62f;

        // ── Colors ───────────────────────────────────────────────────────────
        private static readonly Color CBg = Color.FromArgb(255, 20, 20, 22);
        private static readonly Color CSide = Color.FromArgb(255, 27, 27, 30);
        private static readonly Color CCard = Color.FromArgb(255, 32, 32, 36);
        private static readonly Color CCardSel = Color.FromArgb(255, 36, 56, 98);
        private static readonly Color CBorder = Color.FromArgb(255, 48, 48, 54);
        private static readonly Color CAccent = Color.FromArgb(255, 70, 135, 255);
        private static readonly Color CAccentH = Color.FromArgb(255, 95, 158, 255);
        private static readonly Color CDanger = Color.FromArgb(255, 188, 46, 46);
        private static readonly Color CDangerH = Color.FromArgb(255, 218, 63, 63);
        private static readonly Color CText = Color.FromArgb(255, 208, 208, 213);
        private static readonly Color CDim = Color.FromArgb(255, 118, 118, 126);
        private static readonly Color CSuccess = Color.FromArgb(255, 52, 172, 90);
        private static readonly Color CTag2D = Color.FromArgb(255, 56, 172, 128);
        private static readonly Color CTag3D = Color.FromArgb(255, 72, 118, 208);
        private static readonly Color CField = Color.FromArgb(255, 28, 28, 34);
        private static readonly Color CFieldH = Color.FromArgb(255, 36, 36, 44);
        private static readonly Color CFieldEd = Color.FromArgb(255, 22, 40, 70);

        private PointF _mouse;

        // ── Constructor ───────────────────────────────────────────────────────
        public ProjectLauncherPanel()
        {
            _newPath = ProjectManager.DefaultProjectsDirectory;
            Refresh();
        }

        private void Refresh() => _projects = ProjectManager.GetRecentProjects();

        // ═════════════════════════════════════════════════════════════════════
        //  RENDER — builds hit caches while drawing
        // ═════════════════════════════════════════════════════════════════════
        public void Render(IEditorRenderer r, int winW, int winH)
        {
            _winW = winW; _winH = winH;

            // Clear caches every frame
            _hitRects.Clear();
            _fieldRects.Clear();
            _projRows.Clear();

            r.FillRect(new RectangleF(0, 0, winW, winH), CBg);

            DrawSidebar(r, winH);

            var right = new RectangleF(SideW, 0, winW - SideW, winH);
            r.FillRect(right, CBg);
            r.PushClip(right);
            if (_mode == RightMode.NewProject)
                DrawNewForm(r, right);
            else if (_mode == RightMode.ProjectInfo && _selected != null)
                DrawInfoPanel(r, right, _selected);
            r.PopClip();
        }

        // ── Sidebar ───────────────────────────────────────────────────────────
        private void DrawSidebar(IEditorRenderer r, int winH)
        {
            var sb = new RectangleF(0, 0, SideW, winH);
            r.FillRect(sb, CSide);
            r.DrawLine(new PointF(SideW, 0), new PointF(SideW, winH), CBorder);

            float y = PAD;

            // Logo block
            var logo = new RectangleF(PAD, y, SideW - PAD * 2, 50f);
            r.FillRect(logo, Color.FromArgb(255, 30, 30, 34));
            r.DrawRect(logo, CBorder);
            r.FillRect(new RectangleF(PAD, y, 4f, 50f), CAccent);
            r.DrawText("ELINTRIA ENGINE", new PointF(PAD + 12f, y + 8f), CAccent, 12f);
            r.DrawText("Project Manager", new PointF(PAD + 12f, y + 28f), CDim, 10f);
            y += 58f;

            // New Project button
            DrawSideBtn(r, "new_project", "＋  New Project", PAD, y,
                SideW - PAD * 2, 32f,
                _mode == RightMode.NewProject ? CAccent : Color.FromArgb(255, 42, 42, 48),
                _mode == RightMode.NewProject ? Color.White : CText,
                () =>
                {
                    _mode = RightMode.NewProject;
                    _selected = null;
                    _confirmDelete = false;
                    _newError = _newSuccess = null;
                });
            y += 36f;

            DrawSideBtn(r, "open_disk", "📂  Open from disk", PAD, y,
                SideW - PAD * 2, 32f,
                Color.FromArgb(255, 38, 38, 44), CText, OpenFromDisk);
            y += 40f;

            r.DrawLine(new PointF(PAD, y), new PointF(SideW - PAD, y), CBorder);
            y += 8f;
            r.DrawText($"RECENT  ({_projects.Count})", new PointF(PAD + 4f, y), CDim, 8f);
            y += 16f;

            // Project rows — push clip so they don't overflow sidebar
            r.PushClip(new RectangleF(0, y, SideW, winH - y));
            if (_projects.Count == 0)
            {
                r.DrawText("No projects yet.", new PointF(PAD + 4f, y + 10f), CDim, 10f);
                r.DrawText("Click + New Project to start.", new PointF(PAD + 4f, y + 28f), CDim, 9f);
            }
            foreach (var proj in _projects)
            {
                bool sel = proj == _selected;
                bool hov = !sel && new RectangleF(4, y, SideW - 8f, RowH - 4f).Contains(_mouse);
                var rc = new RectangleF(4f, y, SideW - 8f, RowH - 4f);
                r.FillRect(rc, sel ? CCardSel : hov ? Color.FromArgb(255, 34, 34, 40) : CCard);
                r.DrawRect(rc, sel ? CAccent : CBorder);

                Color tc = proj.Type == ProjectType.TwoD ? CTag2D : CTag3D;
                DrawPill(r, proj.Type == ProjectType.TwoD ? "2D" : "3D", tc, rc.X + 6f, rc.Y + 6f);
                r.DrawText(proj.Name, new PointF(rc.X + 32f, rc.Y + 5f), CText, 11f);
                r.DrawText(TruncPath(proj.RootPath, 28), new PointF(rc.X + 6f, rc.Y + 26f), CDim, 8f);
                r.DrawText(RelDate(proj.LastOpenedAt), new PointF(rc.X + 6f, rc.Y + 42f), CDim, 8f);

                _projRows.Add((rc, proj));
                y += RowH;
            }
            r.PopClip();
        }

        private void DrawSideBtn(IEditorRenderer r, string id, string label,
            float x, float y, float w, float h, Color bg, Color fg, Action action)
        {
            bool hov = new RectangleF(x, y, w, h).Contains(_mouse);
            r.FillRect(new RectangleF(x, y, w, h), hov ? Lighten(bg) : bg);
            r.DrawRect(new RectangleF(x, y, w, h), CBorder);
            r.DrawText(label, new PointF(x + 10f, y + (h - 11f) / 2f), fg, 10f);
            _hitRects[id] = (new RectangleF(x, y, w, h), action);
        }

        // ── New project form ───────────────────────────────────────────────────
        private void DrawNewForm(IEditorRenderer r, RectangleF panel)
        {
            float cx = panel.X + panel.Width / 2f;
            float fw = Math.Min(560f, panel.Width - PAD * 4);
            float x0 = cx - fw / 2f;
            float y = panel.Y + PAD * 2;

            r.DrawText("Create New Project", new PointF(x0, y), CText, 17f);
            y += 28f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 16f;

            // Project name
            r.DrawText("Project Name", new PointF(x0, y), CDim, 9f);
            y += 16f;
            DrawField(r, "f_name", x0, y, fw, _newName,
                v => { _newName = v; _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory, v); });
            y += 30f;

            // Save location
            r.DrawText("Save Location", new PointF(x0, y), CDim, 9f);
            y += 16f;
            DrawField(r, "f_path", x0, y, fw - 96f, _newPath, v => _newPath = v);
            // Browse btn
            var brow = new RectangleF(x0 + fw - 92f, y, 88f, 26f);
            bool bhov = brow.Contains(_mouse);
            r.FillRect(brow, bhov ? Color.FromArgb(255, 50, 50, 58) : Color.FromArgb(255, 38, 38, 44));
            r.DrawRect(brow, CBorder);
            r.DrawText("Browse…", new PointF(brow.X + 10f, brow.Y + 6f), CText, 9f);
            _hitRects["browse"] = (brow, BrowseFolder);
            y += 30f;

            // Description
            r.DrawText("Description  (optional)", new PointF(x0, y), CDim, 9f);
            y += 16f;
            DrawField(r, "f_desc", x0, y, fw, _newDesc, v => _newDesc = v);
            y += 34f;

            // Type cards
            r.DrawText("Project Type", new PointF(x0, y), CDim, 9f);
            y += 16f;
            float tw = (fw - 10f) / 2f;
            DrawTypeCard(r, "tc_2d", x0, y, tw, 80f,
                "2D", "Orthographic camera\nFlat physics\nSprite renderer",
                _newType == ProjectType.TwoD, CTag2D,
                () => _newType = ProjectType.TwoD);
            DrawTypeCard(r, "tc_3d", x0 + tw + 10f, y, tw, 80f,
                "3D", "Perspective camera\n3D physics\nMesh renderer",
                _newType == ProjectType.ThreeD, CTag3D,
                () => _newType = ProjectType.ThreeD);
            y += 94f;

            // Messages
            if (_newError != null)
            {
                r.FillRect(new RectangleF(x0, y, fw, 26f), Color.FromArgb(55, 200, 50, 50));
                r.DrawRect(new RectangleF(x0, y, fw, 26f), CDanger);
                r.DrawText("⚠  " + _newError, new PointF(x0 + 10f, y + 6f), CDanger, 10f);
                y += 34f;
            }
            if (_newSuccess != null)
            {
                r.FillRect(new RectangleF(x0, y, fw, 26f), Color.FromArgb(55, 50, 180, 80));
                r.DrawRect(new RectangleF(x0, y, fw, 26f), CSuccess);
                r.DrawText("✓  " + _newSuccess, new PointF(x0 + 10f, y + 6f), CSuccess, 10f);
                y += 34f;
            }

            // Create button
            var cr = new RectangleF(x0 + fw - 155f, y, 155f, 36f);
            bool chov = cr.Contains(_mouse);
            r.FillRect(cr, chov ? CAccentH : CAccent);
            r.DrawRect(cr, Color.FromArgb(255, 38, 88, 178));
            r.DrawText("Create Project  →", new PointF(cr.X + 14f, cr.Y + 10f), Color.White, 11f);
            _hitRects["create"] = (cr, TryCreate);
        }

        // ── Info panel ─────────────────────────────────────────────────────────
        private void DrawInfoPanel(IEditorRenderer r, RectangleF panel, ProjectManifest proj)
        {
            float cx = panel.X + panel.Width / 2f;
            float fw = Math.Min(560f, panel.Width - PAD * 4);
            float x0 = cx - fw / 2f;
            float y = panel.Y + PAD * 2;

            // Title row
            r.DrawText(proj.Name, new PointF(x0, y), CText, 19f);
            Color tc = proj.Type == ProjectType.TwoD ? CTag2D : CTag3D;
            DrawPill(r, proj.Type == ProjectType.TwoD ? "2D" : "3D", tc, x0 + fw - 30f, y + 4f);
            y += 32f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 12f;

            void Row(string lbl, string val)
            {
                r.DrawText(lbl + ":", new PointF(x0, y + 2f), CDim, 9f);
                r.DrawText(val, new PointF(x0 + 112f, y + 2f), CText, 9f);
                y += 22f;
            }

            Row("Location", proj.RootPath);
            Row("Type", proj.Type == ProjectType.TwoD ? "2D" : "3D");
            Row("Created", proj.CreatedAt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt"));
            Row("Last Opened", proj.LastOpenedAt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt"));
            Row("Engine", proj.EngineVersion);
            Row("Scenes", proj.SceneCount.ToString());
            Row("Scripts", proj.ScriptCount.ToString());
            if (!string.IsNullOrEmpty(proj.Description)) Row("Description", proj.Description);
            y += 8f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 16f;

            // Action buttons
            float bh = 40f;

            // Open
            var openR = new RectangleF(x0, y, fw - 202f, bh);
            bool oh = openR.Contains(_mouse);
            r.FillRect(openR, oh ? CAccentH : CAccent);
            r.DrawRect(openR, Color.FromArgb(255, 38, 88, 178));
            r.DrawText("▶  Open Project", new PointF(openR.X + 16f, openR.Y + 11f), Color.White, 12f);
            _hitRects["open_proj"] = (openR, () => DoOpenProject(proj));

            // Remove
            var remR = new RectangleF(x0 + fw - 194f, y, 90f, bh);
            bool rh = remR.Contains(_mouse);
            r.FillRect(remR, rh ? Color.FromArgb(255, 50, 50, 58) : Color.FromArgb(255, 38, 38, 44));
            r.DrawRect(remR, CBorder);
            r.DrawText("Remove", new PointF(remR.X + 12f, remR.Y + 8f), CText, 10f);
            r.DrawText("(keep files)", new PointF(remR.X + 6f, remR.Y + 22f), CDim, 8f);
            _hitRects["remove"] = (remR, () =>
            {
                ProjectManager.RemoveFromRegistry(proj);
                _selected = null; _mode = RightMode.NewProject; Refresh();
            }
            );

            // Delete
            bool cf = _confirmDelete;
            var delR = new RectangleF(x0 + fw - 96f, y, 96f, bh);
            bool dh2 = delR.Contains(_mouse) || cf;
            r.FillRect(delR, dh2 ? CDangerH : CDanger);
            r.DrawRect(delR, Color.FromArgb(255, 138, 33, 33));
            if (cf)
            {
                r.DrawText("⚠ Confirm?", new PointF(delR.X + 8f, delR.Y + 12f), Color.White, 9f);
            }
            else
            {
                r.DrawText("🗑 Delete", new PointF(delR.X + 10f, delR.Y + 8f), Color.White, 10f);
                r.DrawText("(all files)", new PointF(delR.X + 8f, delR.Y + 22f),
                    Color.FromArgb(255, 255, 175, 175), 8f);
            }
            _hitRects["delete"] = (delR, () =>
            {
                if (_confirmDelete)
                {
                    ProjectManager.DeleteProject(proj, deleteFiles: true);
                    _selected = null; _mode = RightMode.NewProject; _confirmDelete = false; Refresh();
                }
                else _confirmDelete = true;
            }
            );

            if (cf)
            {
                y += bh + 6f;
                r.DrawText("⚠  All project files will be permanently deleted. Click Delete again to confirm.",
                    new PointF(x0, y), CDanger, 9f);
            }
        }

        // ── Field helpers ─────────────────────────────────────────────────────
        private void DrawField(IEditorRenderer r, string id, float x, float y, float w,
            string value, Action<string> setter)
        {
            bool ed = _editId == id;
            bool hov = !ed && new RectangleF(x, y, w, 26f).Contains(_mouse);
            var fr = new RectangleF(x, y, w, 26f);
            r.FillRect(fr, ed ? CFieldEd : hov ? CFieldH : CField);
            r.DrawRect(fr, ed ? CAccent : hov ? CBorder : Color.FromArgb(255, 44, 44, 52));
            r.DrawText(ed ? _editBuf + "|" : (value.Length > 0 ? value : " "),
                new PointF(fr.X + 8f, fr.Y + 6f), ed ? Color.White : CText, 10f);

            _fieldRects[id] = (fr, () => value, setter);
        }

        private void DrawTypeCard(IEditorRenderer r, string id,
            float x, float y, float w, float h,
            string title, string bullets, bool selected, Color ac, Action action)
        {
            bool hov = !selected && new RectangleF(x, y, w, h).Contains(_mouse);
            r.FillRect(new RectangleF(x, y, w, h),
                selected ? Color.FromArgb(28, ac.R, ac.G, ac.B)
                : hov ? Color.FromArgb(255, 34, 34, 40)
                         : Color.FromArgb(255, 30, 30, 36));
            r.DrawRect(new RectangleF(x, y, w, h), selected ? ac : CBorder);
            r.FillRect(new RectangleF(x, y, 4f, h), selected ? ac : Color.FromArgb(70, ac.R, ac.G, ac.B));
            r.DrawText(title, new PointF(x + 12f, y + 8f), selected ? ac : CText, 15f);
            float by = y + 32f;
            foreach (var line in bullets.Split('\n'))
            { r.DrawText("• " + line, new PointF(x + 12f, by), CDim, 9f); by += 14f; }
            _hitRects[id] = (new RectangleF(x, y, w, h), action);
        }

        private static void DrawPill(IEditorRenderer r, string text, Color col, float x, float y)
        {
            var pill = new RectangleF(x, y, text.Length * 7f + 10f, 15f);
            r.FillRect(pill, Color.FromArgb(38, col.R, col.G, col.B));
            r.DrawRect(pill, col);
            r.DrawText(text, new PointF(pill.X + 5f, pill.Y + 1f), col, 8f);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Input — reads from cached rects, no y recomputation
        // ═════════════════════════════════════════════════════════════════════
        public void OnMouseMove(PointF pos) => _mouse = pos;

        public void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (e.Button != MouseButton.Left) return;

            // ── Project list (sidebar) ────────────────────────────────────────
            foreach (var (rect, proj) in _projRows)
            {
                if (rect.Contains(pos))
                {
                    // Clicking the currently selected project opens it directly
                    if (proj == _selected && _mode == RightMode.ProjectInfo)
                        DoOpenProject(proj);
                    else
                    {
                        _selected = proj;
                        _mode = RightMode.ProjectInfo;
                        _confirmDelete = false;
                        CommitIfNotSelf(null); // commit any open edit
                    }
                    return;
                }
            }

            // ── Text fields ───────────────────────────────────────────────────
            foreach (var (id, entry) in _fieldRects)
            {
                if (entry.Rect.Contains(pos))
                {
                    // Commit previous edit (different field) then start new
                    if (_editId != null && _editId != id) CommitEdit();
                    if (_editId != id) StartEdit(id, entry.Getter(), entry.Setter);
                    return;
                }
            }

            // Click outside any field → commit any open edit
            if (_editId != null) CommitEdit();

            // ── General hit rects (buttons, type cards, etc.) ─────────────────
            // Dispatch in order: check _confirmDelete reset for non-delete clicks
            foreach (var (id, entry) in _hitRects)
            {
                if (entry.Rect.Contains(pos))
                {
                    // Any non-delete click cancels delete confirmation
                    if (id != "delete") _confirmDelete = false;
                    entry.Action();
                    return;
                }
            }

            // Click on blank area: cancel delete confirmation
            _confirmDelete = false;
        }

        public void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (_editId == null) return;
            switch (e.Key)
            {
                case Keys.Enter: CommitEdit(); break;
                case Keys.Escape: _editId = null; break;
                case Keys.Backspace when _editBuf.Length > 0:
                    _editBuf = _editBuf[..^1]; break;
            }
        }

        public void OnTextInput(TextInputEventArgs e)
        {
            if (_editId != null) _editBuf += e.AsString;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Actions
        // ═════════════════════════════════════════════════════════════════════
        private void TryCreate()
        {
            _newError = _newSuccess = null;
            if (string.IsNullOrWhiteSpace(_newName))
            { _newError = "Project name cannot be empty."; return; }
            if (string.IsNullOrWhiteSpace(_newPath))
            { _newError = "Save location cannot be empty."; return; }

            string final = _newPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Path.GetFileName(final).Equals(_newName, StringComparison.OrdinalIgnoreCase))
                final = Path.Combine(final, _newName);

            if (Directory.Exists(final) && Directory.GetFileSystemEntries(final).Length > 0)
            { _newError = "Directory already exists and is not empty."; return; }

            var m = ProjectManager.CreateProject(_newName, final, _newType, _newDesc);
            if (m == null) { _newError = "Failed to create project. Check the path."; return; }

            _newSuccess = $"Created at {final}";
            Refresh();
            _selected = m;
            _mode = RightMode.ProjectInfo;
        }

        private void DoOpenProject(ProjectManifest proj)
        {
            var opened = ProjectManager.OpenProject(proj.ManifestPath);
            if (opened != null) ProjectOpened?.Invoke(opened.RootPath);
        }

        private void OpenFromDisk()
        {
            string dir = ProjectManager.DefaultProjectsDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var mf in Directory.GetFiles(dir, "project.elintria", SearchOption.AllDirectories))
                ProjectManager.ImportProject(mf);
            Refresh();
        }

        private void BrowseFolder()
        {
            _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory,
                string.IsNullOrWhiteSpace(_newName) ? "NewProject" : _newName);
        }

        // ── Edit helpers ──────────────────────────────────────────────────────
        private void StartEdit(string id, string initial, Action<string> commit)
        {
            _editId = id; _editBuf = initial; _editCommit = commit;
        }

        private void CommitEdit()
        {
            _editCommit?.Invoke(_editBuf);
            _editId = null;
        }

        private void CommitIfNotSelf(string? keepId)
        {
            if (_editId != null && _editId != keepId) CommitEdit();
        }

        // ── Formatting ────────────────────────────────────────────────────────
        private static string RelDate(DateTime dt)
        {
            var d = DateTime.UtcNow - dt;
            if (d.TotalMinutes < 2) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
            return dt.ToLocalTime().ToString("MMM d, yyyy");
        }

        private static string TruncPath(string p, int max)
            => p.Length <= max ? p : "…" + p[^(max - 1)..];

        private static Color Lighten(Color c)
            => Color.FromArgb(c.A, Math.Min(255, c.R + 18), Math.Min(255, c.G + 18), Math.Min(255, c.B + 18));
    }
}