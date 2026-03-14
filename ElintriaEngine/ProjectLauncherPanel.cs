using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;
using ElintriaEngine.UI.Panels;

namespace ElintriaEngine.UI
{
    /// <summary>
    /// Full-window project launcher shown on startup.
    ///
    /// Layout
    /// ──────
    ///  Left sidebar  — logo, action buttons, project list
    ///  Right panel   — tabbed: New Project | Project Info | Settings
    ///
    /// Hit-testing uses cached rects built during Render() so click positions
    /// always match exactly what was drawn.
    /// </summary>
    public sealed class ProjectLauncherPanel
    {
        // ── Events ────────────────────────────────────────────────────────────
        public event Action<string>? ProjectOpened;

        // ── Right panel modes ─────────────────────────────────────────────────
        private enum RightMode { NewProject, ProjectInfo, Settings }
        private RightMode _mode = RightMode.NewProject;

        // ── Data ──────────────────────────────────────────────────────────────
        private List<ProjectManifest> _projects = new();
        private ProjectManifest? _selected;
        private bool _confirmDelete;
        private EngineSettings _settings = ProjectManager.LoadSettings();

        // New-project form
        private string _newName = "MyProject";
        private string _newPath = "";
        private ProjectType _newType = ProjectType.ThreeD;
        private string _newDesc = "";
        private string? _newError;
        private string? _newOk;

        // ── Text-field editing ─────────────────────────────────────────────────
        private string? _editId;
        private string _editBuf = "";
        private Action<string>? _editCommit;

        // ── Hit-test cache (rebuilt each frame) ────────────────────────────────
        private readonly Dictionary<string, (RectangleF Rect, Action Action)> _hitRects = new();
        private readonly Dictionary<string, (RectangleF Rect, Func<string> Getter, Action<string> Setter)> _fieldRects = new();
        private readonly List<(RectangleF Rect, ProjectManifest Proj)> _projRows = new();

        // ── Layout ────────────────────────────────────────────────────────────
        private int _winW, _winH;
        private const float SideW = 280f;
        private const float PAD = 14f;
        private const float RowH = 66f;

        // ── Colors ────────────────────────────────────────────────────────────
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
        private static readonly Color CGear = Color.FromArgb(255, 90, 90, 100);

        private PointF _mouse;

        // ── Constructor ────────────────────────────────────────────────────────
        public ProjectLauncherPanel()
        {
            _newPath = ProjectManager.DefaultProjectsDirectory;

            // Auto-scan on startup if enabled
            if (_settings.AutoScanOnStartup)
                ProjectManager.ScanFolderForProjects(ProjectManager.DefaultProjectsDirectory);

            Refresh();
        }

        private void Refresh()
        {
            _projects = ProjectManager.GetRecentProjects();
            _newPath = ProjectManager.DefaultProjectsDirectory;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  RENDER — rebuilds all hit-test caches while drawing
        // ═══════════════════════════════════════════════════════════════════════
        public void Render(IEditorRenderer r, int winW, int winH)
        {
            _winW = winW; _winH = winH;
            _hitRects.Clear();
            _fieldRects.Clear();
            _projRows.Clear();

            r.FillRect(new RectangleF(0, 0, winW, winH), CBg);
            DrawSidebar(r, winH);

            var right = new RectangleF(SideW, 0, winW - SideW, winH);
            r.FillRect(right, CBg);
            r.PushClip(right);

            switch (_mode)
            {
                case RightMode.NewProject: DrawNewForm(r, right); break;
                case RightMode.ProjectInfo when _selected != null:
                    DrawInfoPanel(r, right, _selected); break;
                case RightMode.Settings: DrawSettingsPanel(r, right); break;
                default: DrawNewForm(r, right); break;
            }

            r.PopClip();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Sidebar
        // ─────────────────────────────────────────────────────────────────────
        private void DrawSidebar(IEditorRenderer r, int winH)
        {
            var sb = new RectangleF(0, 0, SideW, winH);
            r.FillRect(sb, CSide);
            r.DrawLine(new PointF(SideW, 0), new PointF(SideW, winH), CBorder);

            float y = PAD;

            // Logo
            var logo = new RectangleF(PAD, y, SideW - PAD * 2, 52f);
            r.FillRect(logo, Color.FromArgb(255, 30, 30, 34));
            r.DrawRect(logo, CBorder);
            r.FillRect(new RectangleF(PAD, y, 4f, 52f), CAccent);
            r.DrawText("ELINTRIA ENGINE", new PointF(PAD + 14f, y + 8f), CAccent, 12f);
            r.DrawText("Project Manager", new PointF(PAD + 14f, y + 28f), CDim, 10f);
            y += 62f;

            // Action buttons
            DrawSideBtn(r, "btn_new", "＋  New Project", PAD, y, SideW - PAD * 2, 32f,
                _mode == RightMode.NewProject ? CAccent : Color.FromArgb(255, 42, 42, 50),
                _mode == RightMode.NewProject ? Color.White : CText,
                () => { _mode = RightMode.NewProject; _selected = null; _confirmDelete = false; _newError = _newOk = null; });
            y += 36f;

            DrawSideBtn(r, "btn_scan", "⟳  Scan Default Folder", PAD, y, SideW - PAD * 2, 32f,
                Color.FromArgb(255, 36, 36, 44), CText,
                () => { ScanAndRefresh(); });
            y += 36f;

            DrawSideBtn(r, "btn_settings", "⚙  Settings", PAD, y, SideW - PAD * 2, 32f,
                _mode == RightMode.Settings ? Color.FromArgb(255, 50, 50, 60) : Color.FromArgb(255, 36, 36, 44),
                _mode == RightMode.Settings ? CText : CGear,
                () => { _mode = RightMode.Settings; _selected = null; });
            y += 44f;

            // Divider + count
            r.DrawLine(new PointF(PAD, y), new PointF(SideW - PAD, y), CBorder);
            y += 8f;
            r.DrawText($"PROJECTS  ({_projects.Count})", new PointF(PAD + 4f, y), CDim, 8f);
            y += 18f;

            // Project rows
            r.PushClip(new RectangleF(0, y, SideW, winH - y - 4f));
            if (_projects.Count == 0)
            {
                r.DrawText("No projects found.", new PointF(PAD + 6f, y + 10f), CDim, 9f);
                r.DrawText("Create one or scan your projects folder.", new PointF(PAD + 6f, y + 26f), CDim, 8f);
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
                r.DrawText(proj.Name, new PointF(rc.X + 34f, rc.Y + 5f), CText, 11f);
                r.DrawText(TruncPath(proj.RootPath, 30), new PointF(rc.X + 6f, rc.Y + 26f), CDim, 8f);
                r.DrawText(RelDate(proj.LastOpenedAt), new PointF(rc.X + 6f, rc.Y + 42f), CDim, 8f);

                // Scene/script count badges
                if (proj.SceneCount > 0)
                    r.DrawText($"{proj.SceneCount}sc", new PointF(rc.Right - 50f, rc.Y + 42f), CDim, 8f);

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

        // ─────────────────────────────────────────────────────────────────────
        //  New Project form
        // ─────────────────────────────────────────────────────────────────────
        private void DrawNewForm(IEditorRenderer r, RectangleF panel)
        {
            float cx = panel.X + panel.Width / 2f;
            float fw = Math.Min(560f, panel.Width - PAD * 4);
            float x0 = cx - fw / 2f;
            float y = panel.Y + PAD * 2;

            r.DrawText("Create New Project", new PointF(x0, y), CText, 17f);
            y += 28f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 18f;

            // Project Name
            r.DrawText("Project Name", new PointF(x0, y), CDim, 9f);
            y += 16f;
            DrawField(r, "f_name", x0, y, fw, _newName,
                v => {
                    _newName = v;
                    _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory, v);
                });
            y += 30f;

            // Save Location — shown as final path (DefaultDir / name)
            r.DrawText("Project Folder  (inside your default projects directory)", new PointF(x0, y), CDim, 9f);
            y += 16f;
            DrawField(r, "f_path", x0, y, fw - 96f, _newPath, v => _newPath = v);
            var browseBtn = new RectangleF(x0 + fw - 92f, y, 88f, 26f);
            bool bh = browseBtn.Contains(_mouse);
            r.FillRect(browseBtn, bh ? Color.FromArgb(255, 50, 50, 58) : Color.FromArgb(255, 38, 38, 44));
            r.DrawRect(browseBtn, CBorder);
            r.DrawText("Browse…", new PointF(browseBtn.X + 10f, browseBtn.Y + 6f), CText, 9f);
            _hitRects["browse"] = (browseBtn, BrowseFolder);
            y += 30f;

            // Default folder hint
            r.DrawText($"Default folder: {TruncPath(ProjectManager.DefaultProjectsDirectory, 48)}",
                new PointF(x0, y), CDim, 8f);
            y += 20f;

            // Description
            r.DrawText("Description  (optional)", new PointF(x0, y), CDim, 9f);
            y += 16f;
            DrawField(r, "f_desc", x0, y, fw, _newDesc, v => _newDesc = v);
            y += 34f;

            // Project Type cards
            r.DrawText("Project Type", new PointF(x0, y), CDim, 9f);
            y += 16f;
            float tw = (fw - 10f) / 2f;
            DrawTypeCard(r, "tc_2d", x0, y, tw, 82f,
                "2D", "Orthographic camera\nFlat physics\nSprite renderer",
                _newType == ProjectType.TwoD, CTag2D, () => _newType = ProjectType.TwoD);
            DrawTypeCard(r, "tc_3d", x0 + tw + 10f, y, tw, 82f,
                "3D", "Perspective camera\n3D physics\nMesh renderer",
                _newType == ProjectType.ThreeD, CTag3D, () => _newType = ProjectType.ThreeD);
            y += 96f;

            // Messages
            if (_newError != null)
            {
                r.FillRect(new RectangleF(x0, y, fw, 26f), Color.FromArgb(55, 200, 50, 50));
                r.DrawRect(new RectangleF(x0, y, fw, 26f), CDanger);
                r.DrawText("⚠  " + _newError, new PointF(x0 + 10f, y + 6f), CDanger, 10f);
                y += 34f;
            }
            if (_newOk != null)
            {
                r.FillRect(new RectangleF(x0, y, fw, 26f), Color.FromArgb(55, 50, 180, 80));
                r.DrawRect(new RectangleF(x0, y, fw, 26f), CSuccess);
                r.DrawText("✓  " + _newOk, new PointF(x0 + 10f, y + 6f), CSuccess, 10f);
                y += 34f;
            }

            // Create button
            var cr = new RectangleF(x0 + fw - 155f, y, 155f, 38f);
            bool ch = cr.Contains(_mouse);
            r.FillRect(cr, ch ? CAccentH : CAccent);
            r.DrawRect(cr, Color.FromArgb(255, 38, 88, 178));
            r.DrawText("Create Project  →", new PointF(cr.X + 14f, cr.Y + 11f), Color.White, 11f);
            _hitRects["create"] = (cr, TryCreate);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Project Info panel
        // ─────────────────────────────────────────────────────────────────────
        private void DrawInfoPanel(IEditorRenderer r, RectangleF panel, ProjectManifest proj)
        {
            float cx = panel.X + panel.Width / 2f;
            float fw = Math.Min(560f, panel.Width - PAD * 4);
            float x0 = cx - fw / 2f;
            float y = panel.Y + PAD * 2;

            r.DrawText(proj.Name, new PointF(x0, y), CText, 19f);
            Color tc = proj.Type == ProjectType.TwoD ? CTag2D : CTag3D;
            DrawPill(r, proj.Type == ProjectType.TwoD ? "2D" : "3D", tc, x0 + fw - 32f, y + 4f);
            y += 32f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 14f;

            void Row(string lbl, string val)
            {
                r.DrawText(lbl + ":", new PointF(x0, y + 2f), CDim, 9f);
                r.DrawText(val, new PointF(x0 + 116f, y + 2f), CText, 9f);
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
            y += 18f;

            // Action buttons
            float bh = 42f;
            var openR = new RectangleF(x0, y, fw - 202f, bh);
            bool oh = openR.Contains(_mouse);
            r.FillRect(openR, oh ? CAccentH : CAccent);
            r.DrawRect(openR, Color.FromArgb(255, 38, 88, 178));
            r.DrawText("▶  Open Project", new PointF(openR.X + 16f, openR.Y + 13f), Color.White, 12f);
            _hitRects["open_proj"] = (openR, () => DoOpenProject(proj));

            var remR = new RectangleF(x0 + fw - 194f, y, 92f, bh);
            bool rh = remR.Contains(_mouse);
            r.FillRect(remR, rh ? Lighten(CCard) : CCard);
            r.DrawRect(remR, CBorder);
            r.DrawText("Remove", new PointF(remR.X + 12f, remR.Y + 9f), CText, 10f);
            r.DrawText("(keep files)", new PointF(remR.X + 6f, remR.Y + 23f), CDim, 8f);
            _hitRects["remove"] = (remR, () =>
            {
                ProjectManager.RemoveFromRegistry(proj);
                _selected = null; _mode = RightMode.NewProject; Refresh();
            }
            );

            bool cf = _confirmDelete;
            var delR = new RectangleF(x0 + fw - 96f, y, 96f, bh);
            bool dh = delR.Contains(_mouse) || cf;
            r.FillRect(delR, dh ? CDangerH : CDanger);
            r.DrawRect(delR, Color.FromArgb(255, 138, 33, 33));
            if (cf)
                r.DrawText("⚠ Confirm?", new PointF(delR.X + 8f, delR.Y + 13f), Color.White, 9f);
            else
            {
                r.DrawText("🗑 Delete", new PointF(delR.X + 10f, delR.Y + 9f), Color.White, 10f);
                r.DrawText("(all files)", new PointF(delR.X + 8f, delR.Y + 23f),
                    Color.FromArgb(255, 255, 175, 175), 8f);
            }
            _hitRects["delete"] = (delR, () =>
            {
                if (_confirmDelete)
                { ProjectManager.DeleteProject(proj, true); _selected = null; _mode = RightMode.NewProject; _confirmDelete = false; Refresh(); }
                else _confirmDelete = true;
            }
            );

            if (cf)
            {
                y += bh + 8f;
                r.DrawText("⚠  All project files will be permanently deleted. Click Delete again to confirm.",
                    new PointF(x0, y), CDanger, 9f);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Settings panel
        // ─────────────────────────────────────────────────────────────────────
        private void DrawSettingsPanel(IEditorRenderer r, RectangleF panel)
        {
            float cx = panel.X + panel.Width / 2f;
            float fw = Math.Min(560f, panel.Width - PAD * 4);
            float x0 = cx - fw / 2f;
            float y = panel.Y + PAD * 2;

            r.DrawText("Settings", new PointF(x0, y), CText, 17f);
            y += 28f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 18f;

            // ── Default Projects Folder ───────────────────────────────────────
            DrawSectionHeader(r, x0, fw, "PROJECT STORAGE", ref y);

            r.DrawText("Default Projects Folder", new PointF(x0, y), CDim, 9f);
            y += 16f;
            r.DrawText("All new projects are created as subfolders inside this directory.",
                new PointF(x0, y), CDim, 8f);
            y += 14f;

            string curDir = ProjectManager.DefaultProjectsDirectory;
            DrawField(r, "s_dir", x0, y, fw - 170f, curDir,
                v => { ProjectManager.DefaultProjectsDirectory = v; _newPath = v; _settings = ProjectManager.LoadSettings(); });

            // Browse button — opens native folder picker
            var browseBtn2 = new RectangleF(x0 + fw - 166f, y, 46f, 26f);
            bool bb2h = browseBtn2.Contains(_mouse);
            r.FillRect(browseBtn2, bb2h ? Lighten(CCard) : CCard);
            r.DrawRect(browseBtn2, CBorder);
            r.DrawText("Browse", new PointF(browseBtn2.X + 4f, browseBtn2.Y + 6f), CText, 9f);
            _hitRects["s_browse"] = (browseBtn2, () =>
            {
                string? chosen = Core.NativeDialog.SelectFolder(
                    "Choose Default Projects Folder",
                    ProjectManager.DefaultProjectsDirectory);
                if (!string.IsNullOrEmpty(chosen))
                {
                    ProjectManager.DefaultProjectsDirectory = chosen;
                    _newPath = chosen;
                    _settings = ProjectManager.LoadSettings();
                    Refresh();
                }
            }
            );

            var setBtn = new RectangleF(x0 + fw - 116f, y, 56f, 26f);
            bool sbh = setBtn.Contains(_mouse);
            r.FillRect(setBtn, sbh ? CAccentH : CAccent);
            r.DrawRect(setBtn, Color.FromArgb(255, 38, 88, 178));
            r.DrawText("Apply", new PointF(setBtn.X + 9f, setBtn.Y + 6f), Color.White, 9f);
            _hitRects["s_apply"] = (setBtn, () =>
            {
                if (_fieldRects.TryGetValue("s_dir", out var fe) && _editId == "s_dir")
                    CommitEdit();
                _newOk = null;
                Refresh();
            }
            );

            var scanBtn = new RectangleF(x0 + fw - 56f, y, 52f, 26f);
            bool scbh = scanBtn.Contains(_mouse);
            r.FillRect(scanBtn, scbh ? Lighten(CCard) : CCard);
            r.DrawRect(scanBtn, CBorder);
            r.DrawText("Scan", new PointF(scanBtn.X + 10f, scanBtn.Y + 6f), CText, 9f);
            _hitRects["s_scan"] = (scanBtn, ScanAndRefresh);
            y += 34f;

            // Show what's currently in the folder
            bool exists = Directory.Exists(curDir);
            if (exists)
            {
                r.DrawText($"✓  Folder exists", new PointF(x0, y), CSuccess, 9f);
                // Count sub-projects
                int cnt = 0;
                try
                {
                    foreach (var sub in Directory.GetDirectories(curDir))
                        if (File.Exists(Path.Combine(sub, "project.elintria"))) cnt++;
                    if (File.Exists(Path.Combine(curDir, "project.elintria"))) cnt++;
                }
                catch { }
                if (cnt > 0)
                    r.DrawText($"  {cnt} project folder(s) detected", new PointF(x0 + 120f, y), CDim, 9f);
            }
            else
            {
                r.DrawText("⚠  Folder does not exist yet (will be created on first project)",
                    new PointF(x0, y), Color.FromArgb(255, 200, 155, 40), 9f);
            }
            y += 24f;

            // Create folder button
            if (!exists)
            {
                var mkBtn = new RectangleF(x0, y, 180f, 28f);
                bool mkh = mkBtn.Contains(_mouse);
                r.FillRect(mkBtn, mkh ? Lighten(CCard) : CCard);
                r.DrawRect(mkBtn, CBorder);
                r.DrawText("Create Folder Now", new PointF(mkBtn.X + 10f, mkBtn.Y + 7f), CText, 9f);
                _hitRects["s_mkdir"] = (mkBtn, () =>
                {
                    try { Directory.CreateDirectory(curDir); }
                    catch (Exception ex) { Console.WriteLine($"[Settings] mkdir: {ex.Message}"); }
                }
                );
                y += 36f;
            }

            y += 8f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 16f;

            // ── Auto-scan ─────────────────────────────────────────────────────
            DrawSectionHeader(r, x0, fw, "STARTUP BEHAVIOUR", ref y);

            bool autoScan = _settings.AutoScanOnStartup;
            var asRect = new RectangleF(x0, y, 18f, 18f);
            r.FillRect(asRect, autoScan ? CAccent : CField);
            r.DrawRect(asRect, autoScan ? CAccent : CBorder);
            if (autoScan) r.DrawText("✓", new PointF(asRect.X + 3f, asRect.Y + 1f), Color.White, 10f);
            r.DrawText("Auto-scan default folder on startup",
                new PointF(x0 + 24f, y + 2f), CText, 10f);
            r.DrawText("(finds new projects automatically without manually clicking Scan)",
                new PointF(x0 + 24f, y + 16f), CDim, 8f);
            _hitRects["s_autoscan"] = (new RectangleF(x0, y, fw, 32f), () =>
            {
                _settings.AutoScanOnStartup = !_settings.AutoScanOnStartup;
                ProjectManager.SaveSettings(_settings);
            }
            );
            y += 42f;

            y += 8f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 16f;

            // ── Known Projects ────────────────────────────────────────────────
            DrawSectionHeader(r, x0, fw, $"KNOWN PROJECTS  ({_projects.Count})", ref y);

            if (_projects.Count == 0)
            {
                r.DrawText("No projects registered yet.", new PointF(x0, y), CDim, 9f);
                y += 18f;
            }

            foreach (var proj in _projects)
            {
                bool exists2 = File.Exists(proj.ManifestPath);
                Color lc = exists2 ? CText : CDanger;
                r.DrawText(exists2 ? "●" : "✕", new PointF(x0, y + 3f),
                    exists2 ? CSuccess : CDanger, 8f);
                r.DrawText(proj.Name, new PointF(x0 + 14f, y + 3f), lc, 9f);
                r.DrawText(TruncPath(proj.RootPath, 52),
                    new PointF(x0 + 14f, y + 15f), CDim, 7f);
                y += 30f;
                if (y > panel.Y + panel.Height - 20f) break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Drawing helpers
        // ─────────────────────────────────────────────────────────────────────
        private void DrawField(IEditorRenderer r, string id, float x, float y, float w,
            string value, Action<string> setter)
        {
            bool ed = _editId == id;
            bool hov = !ed && new RectangleF(x, y, w, 26f).Contains(_mouse);
            var fr = new RectangleF(x, y, w, 26f);
            r.FillRect(fr, ed ? CFieldEd : hov ? CFieldH : CField);
            r.DrawRect(fr, ed ? CAccent : hov ? CBorder : Color.FromArgb(255, 44, 44, 52));
            string disp = ed ? _editBuf + "|" : (value.Length > 0 ? value : " ");
            r.DrawText(disp, new PointF(fr.X + 8f, fr.Y + 6f), ed ? Color.White : CText, 10f);
            _fieldRects[id] = (fr, () => value, setter);
        }

        private void DrawTypeCard(IEditorRenderer r, string id, float x, float y, float w, float h,
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

        private static void DrawSectionHeader(IEditorRenderer r, float x0, float fw,
            string title, ref float y)
        {
            r.DrawText(title, new PointF(x0, y), CDim, 8f);
            y += 16f;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Input — reads from cached hit rects only
        // ═══════════════════════════════════════════════════════════════════════
        public void OnMouseMove(PointF pos) => _mouse = pos;

        public void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (e.Button != MouseButton.Left) return;

            // ── Project rows ──────────────────────────────────────────────────
            foreach (var (rect, proj) in _projRows)
            {
                if (!rect.Contains(pos)) continue;
                if (proj == _selected && _mode == RightMode.ProjectInfo)
                    DoOpenProject(proj);
                else
                {
                    CommitIfActive(null);
                    _selected = proj;
                    _mode = RightMode.ProjectInfo;
                    _confirmDelete = false;
                }
                return;
            }

            // ── Text fields ───────────────────────────────────────────────────
            foreach (var (id, entry) in _fieldRects)
            {
                if (!entry.Rect.Contains(pos)) continue;
                if (_editId != null && _editId != id) CommitEdit();
                if (_editId != id) StartEdit(id, entry.Getter(), entry.Setter);
                return;
            }

            // Clicking outside a field commits the edit
            if (_editId != null) CommitEdit();

            // ── General hit rects ─────────────────────────────────────────────
            foreach (var (id, entry) in _hitRects)
            {
                if (!entry.Rect.Contains(pos)) continue;
                if (id != "delete") _confirmDelete = false;
                entry.Action();
                return;
            }

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

        // ═══════════════════════════════════════════════════════════════════════
        //  Actions
        // ═══════════════════════════════════════════════════════════════════════
        private void TryCreate()
        {
            _newError = _newOk = null;
            if (string.IsNullOrWhiteSpace(_newName))
            { _newError = "Project name cannot be empty."; return; }

            // Always put the project in its own named subfolder
            string baseDir = ProjectManager.DefaultProjectsDirectory;
            string final = _newPath.TrimEnd(Path.DirectorySeparatorChar,
                                               Path.AltDirectorySeparatorChar);

            // If the user hasn't changed the path, ensure it includes the project name
            if (!Path.GetFileName(final).Equals(_newName, StringComparison.OrdinalIgnoreCase))
                final = Path.Combine(string.IsNullOrWhiteSpace(_newPath) ? baseDir : final, _newName);

            if (Directory.Exists(final) &&
                Directory.GetFileSystemEntries(final).Length > 0 &&
                !File.Exists(Path.Combine(final, "project.elintria")))
            { _newError = "Directory exists and is not empty."; return; }

            var m = ProjectManager.CreateProject(_newName, final, _newType, _newDesc);
            if (m == null) { _newError = "Failed to create project. Check the path."; return; }

            _newOk = $"Created: {final}";
            _newName = "MyProject";
            _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory, _newName);
            _newDesc = "";
            Refresh();
            _selected = m;
            _mode = RightMode.ProjectInfo;
        }

        private void DoOpenProject(ProjectManifest proj)
        {
            var opened = ProjectManager.OpenProject(proj.ManifestPath);
            if (opened != null) ProjectOpened?.Invoke(opened.RootPath);
        }

        private void ScanAndRefresh()
        {
            int found = ProjectManager.ScanFolderForProjects(ProjectManager.DefaultProjectsDirectory);
            Refresh();
            if (found == 0) Console.WriteLine("[Launcher] No new projects found during scan.");
        }

        private void BrowseFolder()
        {
            string? chosen = Core.NativeDialog.SelectFolder(
                "Choose Project Location",
                ProjectManager.DefaultProjectsDirectory);
            if (!string.IsNullOrEmpty(chosen))
            {
                string name = string.IsNullOrWhiteSpace(_newName) ? "NewProject" : _newName;
                _newPath = Path.Combine(chosen, name);
            }
            else
            {
                // Fallback: use default dir + name
                string name = string.IsNullOrWhiteSpace(_newName) ? "NewProject" : _newName;
                _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory, name);
            }
        }

        // ── Edit helpers ──────────────────────────────────────────────────────
        private void StartEdit(string id, string initial, Action<string> commit)
        { _editId = id; _editBuf = initial; _editCommit = commit; }

        private void CommitEdit()
        { _editCommit?.Invoke(_editBuf); _editId = null; }

        private void CommitIfActive(string? keepId)
        { if (_editId != null && _editId != keepId) CommitEdit(); }

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

        private static Color Lighten(Color c) =>
            Color.FromArgb(c.A,
                Math.Min(255, c.R + 18),
                Math.Min(255, c.G + 18),
                Math.Min(255, c.B + 18));
    }
}