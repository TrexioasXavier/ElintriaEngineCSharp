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
    // ═══════════════════════════════════════════════════════════════════════════
    //  ProjectLauncherPanel
    //
    //  Full-window project manager UI rendered before the editor loads.
    //
    //  Layout:
    //    Left sidebar  (SideW)  — logo, New button, Open button, project list
    //    Right area            — tabbed between:
    //       • "new project"  form  (name, location, type, description)
    //       • "project info" card  (stats, open / delete / remove)
    //
    //  Call Render() every frame; wire keyboard and mouse from EditorWindow.
    //  Subscribe to ProjectOpened to get the root path when the user clicks Open.
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class ProjectLauncherPanel
    {
        // ── Events ────────────────────────────────────────────────────────────
        public event Action<string>? ProjectOpened;   // fires with project root path

        // ── State ─────────────────────────────────────────────────────────────
        private enum RightMode { None, NewProject, ProjectInfo }
        private RightMode _mode = RightMode.NewProject;

        private List<ProjectManifest> _projects = new();
        private ProjectManifest? _selected;
        private bool _confirmDelete = false;  // second-click guard

        // New-project form
        private string _newName = "MyProject";
        private string _newPath = ProjectManager.DefaultProjectsDirectory;
        private ProjectType _newType = ProjectType.ThreeD;
        private string _newDesc = "";
        private string? _newError = null;
        private string? _newSuccess = null;

        // Text editing
        private string? _editId;
        private string _editBuf = "";
        private Action<string>? _editCommit;

        // Layout (set every frame from window size)
        private int _winW, _winH;
        private const float SideW = 280f;
        private const float PAD = 14f;
        private const float RowH = 64f;   // project list row height
        private const float HeaderH = 56f;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color CBg = Color.FromArgb(255, 22, 22, 24);
        private static readonly Color CSide = Color.FromArgb(255, 28, 28, 31);
        private static readonly Color CCard = Color.FromArgb(255, 34, 34, 38);
        private static readonly Color CCardSel = Color.FromArgb(255, 38, 58, 100);
        private static readonly Color CBorder = Color.FromArgb(255, 50, 50, 56);
        private static readonly Color CAccent = Color.FromArgb(255, 72, 138, 255);
        private static readonly Color CAccentH = Color.FromArgb(255, 96, 160, 255);
        private static readonly Color CDanger = Color.FromArgb(255, 190, 48, 48);
        private static readonly Color CDangerH = Color.FromArgb(255, 220, 65, 65);
        private static readonly Color CText = Color.FromArgb(255, 210, 210, 215);
        private static readonly Color CTextDim = Color.FromArgb(255, 120, 120, 128);
        private static readonly Color CSuccess = Color.FromArgb(255, 55, 175, 95);
        private static readonly Color CGreen = Color.FromArgb(255, 55, 155, 75);
        private static readonly Color CTag2D = Color.FromArgb(255, 60, 175, 130);
        private static readonly Color CTag3D = Color.FromArgb(255, 75, 120, 210);

        private PointF _mouse;

        // ── Constructor ───────────────────────────────────────────────────────
        public ProjectLauncherPanel()
        {
            _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory, _newName);
            Refresh();
        }

        private void Refresh()
        {
            _projects = ProjectManager.GetRecentProjects();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Render
        // ═════════════════════════════════════════════════════════════════════
        public void Render(IEditorRenderer r, int winW, int winH)
        {
            _winW = winW;
            _winH = winH;

            var full = new RectangleF(0, 0, winW, winH);
            r.FillRect(full, CBg);

            DrawSidebar(r, winW, winH);
            DrawRightPanel(r, winW, winH);
        }

        // ── Sidebar ───────────────────────────────────────────────────────────
        private void DrawSidebar(IEditorRenderer r, int w, int h)
        {
            var sidebar = new RectangleF(0, 0, SideW, h);
            r.FillRect(sidebar, CSide);
            r.DrawLine(new PointF(SideW, 0), new PointF(SideW, h), CBorder);

            float y = PAD;

            // ── Logo / title ──────────────────────────────────────────────────
            r.FillRect(new RectangleF(PAD, y, SideW - PAD * 2, 48f),
                Color.FromArgb(255, 30, 30, 34));
            r.DrawRect(new RectangleF(PAD, y, SideW - PAD * 2, 48f), CBorder);

            DrawColorBar(r, new RectangleF(PAD, y, 4f, 48f), CAccent);
            r.DrawText("ELINTRIA ENGINE", new PointF(PAD + 12f, y + 8f), CAccent, 13f);
            r.DrawText("Project Manager", new PointF(PAD + 12f, y + 28f), CTextDim, 10f);
            y += 60f;

            // ── Action buttons ────────────────────────────────────────────────
            DrawSideBtn(r, "＋  New Project", PAD, y, SideW - PAD * 2, 34f,
                _mode == RightMode.NewProject ? CAccent : Color.FromArgb(255, 45, 45, 50),
                _mode == RightMode.NewProject ? Color.White : CText,
                () => { _mode = RightMode.NewProject; _selected = null; _confirmDelete = false; _newError = null; _newSuccess = null; });
            y += 38f;

            DrawSideBtn(r, "📂  Open from disk", PAD, y, SideW - PAD * 2, 34f,
                Color.FromArgb(255, 40, 40, 45), CText,
                OpenFromDisk);
            y += 42f;

            r.DrawLine(new PointF(PAD, y), new PointF(SideW - PAD, y), CBorder);
            y += 10f;

            r.DrawText("RECENT PROJECTS", new PointF(PAD + 4f, y), CTextDim, 9f);
            y += 18f;

            // ── Project list ──────────────────────────────────────────────────
            r.PushClip(new RectangleF(0, y, SideW, h - y));

            if (_projects.Count == 0)
            {
                r.DrawText("No projects yet.", new PointF(PAD + 4f, y + 12f), CTextDim, 10f);
                r.DrawText("Click '+ New Project' to get started.",
                    new PointF(PAD + 4f, y + 30f), CTextDim, 9f);
            }

            foreach (var proj in _projects)
            {
                bool sel = proj == _selected;
                bool hov = !sel && new RectangleF(4, y, SideW - 8f, RowH - 4f).Contains(_mouse);

                var card = new RectangleF(4f, y, SideW - 8f, RowH - 4f);
                r.FillRect(card, sel ? CCardSel : hov ? Color.FromArgb(255, 36, 36, 42) : CCard);
                r.DrawRect(card, sel ? CAccent : CBorder);

                // Type badge
                Color typeCol = proj.Type == ProjectType.TwoD ? CTag2D : CTag3D;
                string typeStr = proj.Type == ProjectType.TwoD ? "2D" : "3D";
                DrawPill(r, typeStr, typeCol, card.X + 8f, card.Y + 8f);

                r.DrawText(proj.Name,
                    new PointF(card.X + 36f, card.Y + 6f), CText, 11f);
                r.DrawText(TruncatePath(proj.RootPath, 30),
                    new PointF(card.X + 8f, card.Y + 28f), CTextDim, 9f);
                r.DrawText($"Opened {FormatDate(proj.LastOpenedAt)}",
                    new PointF(card.X + 8f, card.Y + 44f), CTextDim, 8f);

                string stats = $"Scenes: {proj.SceneCount}  Scripts: {proj.ScriptCount}";
                r.DrawText(stats, new PointF(card.Right - stats.Length * 6.2f - 4f, card.Y + 44f),
                    CTextDim, 8f);

                y += RowH;
            }

            r.PopClip();
        }

        // ── Right panel ───────────────────────────────────────────────────────
        private void DrawRightPanel(IEditorRenderer r, int w, int h)
        {
            var panel = new RectangleF(SideW, 0, w - SideW, h);
            r.FillRect(panel, CBg);
            r.PushClip(panel);

            switch (_mode)
            {
                case RightMode.NewProject:
                    DrawNewProjectForm(r, panel);
                    break;
                case RightMode.ProjectInfo:
                    if (_selected != null) DrawProjectInfo(r, panel, _selected);
                    break;
            }

            r.PopClip();
        }

        // ── New Project form ──────────────────────────────────────────────────
        private void DrawNewProjectForm(IEditorRenderer r, RectangleF panel)
        {
            float cx = panel.X + panel.Width / 2f;
            float lx = panel.X + PAD * 2;
            float fw = Math.Min(560f, panel.Width - PAD * 4);
            float x0 = cx - fw / 2f;
            float y = panel.Y + PAD * 2;

            // Header
            r.DrawText("Create New Project", new PointF(x0, y), CText, 18f);
            y += 30f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 18f;

            // ── Project Name ──────────────────────────────────────────────────
            DrawFormLabel(r, x0, y, "Project Name");
            y += 18f;
            DrawFormField(r, x0, fw, "newname", _newName, y, 26f,
                v => { _newName = v; _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory, v); });
            y += 34f;

            // ── Save Location ─────────────────────────────────────────────────
            DrawFormLabel(r, x0, y, "Save Location");
            y += 18f;
            DrawFormField(r, x0, fw - 100f, "newpath", _newPath, y, 26f,
                v => _newPath = v);
            // Browse button placeholder
            var browseBtn = new RectangleF(x0 + fw - 96f, y, 92f, 26f);
            bool bhov = browseBtn.Contains(_mouse);
            r.FillRect(browseBtn, bhov ? Color.FromArgb(255, 55, 55, 62) : Color.FromArgb(255, 42, 42, 48));
            r.DrawRect(browseBtn, CBorder);
            r.DrawText("📂 Browse", new PointF(browseBtn.X + 8f, browseBtn.Y + 6f), CText, 10f);
            y += 34f;

            // ── Description ───────────────────────────────────────────────────
            DrawFormLabel(r, x0, y, "Description  (optional)");
            y += 18f;
            DrawFormField(r, x0, fw, "newdesc", _newDesc, y, 26f, v => _newDesc = v);
            y += 38f;

            // ── Project Type ──────────────────────────────────────────────────
            DrawFormLabel(r, x0, y, "Project Type");
            y += 18f;

            float typeW = (fw - 10f) / 2f;
            DrawTypeCard(r, x0, y, typeW, 84f, "2D",
                "Orthographic camera\nFlat physics\nSprite renderer",
                _newType == ProjectType.TwoD, CTag2D,
                () => _newType = ProjectType.TwoD);
            DrawTypeCard(r, x0 + typeW + 10f, y, typeW, 84f, "3D",
                "Perspective camera\n3D physics\nMesh renderer",
                _newType == ProjectType.ThreeD, CTag3D,
                () => _newType = ProjectType.ThreeD);
            y += 98f;

            // ── Error / success messages ──────────────────────────────────────
            if (_newError != null)
            {
                r.FillRect(new RectangleF(x0, y, fw, 28f), Color.FromArgb(60, 200, 50, 50));
                r.DrawRect(new RectangleF(x0, y, fw, 28f), CDanger);
                r.DrawText("⚠  " + _newError, new PointF(x0 + 10f, y + 7f), CDanger, 10f);
                y += 36f;
            }
            if (_newSuccess != null)
            {
                r.FillRect(new RectangleF(x0, y, fw, 28f), Color.FromArgb(60, 50, 180, 80));
                r.DrawRect(new RectangleF(x0, y, fw, 28f), CSuccess);
                r.DrawText("✓  " + _newSuccess, new PointF(x0 + 10f, y + 7f), CSuccess, 10f);
                y += 36f;
            }

            // ── Create button ─────────────────────────────────────────────────
            var createBtn = new RectangleF(x0 + fw - 160f, y, 160f, 36f);
            bool chov = createBtn.Contains(_mouse);
            r.FillRect(createBtn, chov ? CAccentH : CAccent);
            r.DrawRect(createBtn, Color.FromArgb(255, 40, 90, 180));
            r.DrawText("Create Project →", new PointF(createBtn.X + 14f, createBtn.Y + 9f),
                Color.White, 12f);
        }

        // ── Project Info card ─────────────────────────────────────────────────
        private void DrawProjectInfo(IEditorRenderer r, RectangleF panel, ProjectManifest proj)
        {
            float cx = panel.X + panel.Width / 2f;
            float fw = Math.Min(560f, panel.Width - PAD * 4);
            float x0 = cx - fw / 2f;
            float y = panel.Y + PAD * 2;

            // Title
            r.DrawText(proj.Name, new PointF(x0, y), CText, 20f);
            Color typeCol = proj.Type == ProjectType.TwoD ? CTag2D : CTag3D;
            DrawPill(r, proj.Type == ProjectType.TwoD ? "2D" : "3D", typeCol, x0 + fw - 36f, y + 4f);
            y += 34f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 14f;

            // Info grid
            DrawInfoRow(r, x0, fw, y, "Location", proj.RootPath); y += 24f;
            DrawInfoRow(r, x0, fw, y, "Created", FormatDateFull(proj.CreatedAt)); y += 24f;
            DrawInfoRow(r, x0, fw, y, "Last Opened", FormatDateFull(proj.LastOpenedAt)); y += 24f;
            DrawInfoRow(r, x0, fw, y, "Engine", proj.EngineVersion); y += 24f;
            DrawInfoRow(r, x0, fw, y, "Scenes", proj.SceneCount.ToString()); y += 24f;
            DrawInfoRow(r, x0, fw, y, "Scripts", proj.ScriptCount.ToString()); y += 24f;

            if (!string.IsNullOrEmpty(proj.Description))
            {
                DrawInfoRow(r, x0, fw, y, "Description", proj.Description);
                y += 24f;
            }

            y += 10f;
            r.DrawLine(new PointF(x0, y), new PointF(x0 + fw, y), CBorder);
            y += 18f;

            // ── Open button ───────────────────────────────────────────────────
            var openBtn = new RectangleF(x0, y, fw - 210f, 40f);
            bool ohov = openBtn.Contains(_mouse);
            r.FillRect(openBtn, ohov ? CAccentH : CAccent);
            r.DrawRect(openBtn, Color.FromArgb(255, 40, 90, 180));
            r.DrawText("▶  Open Project", new PointF(openBtn.X + 16f, openBtn.Y + 11f),
                Color.White, 13f);

            // ── Remove from list ──────────────────────────────────────────────
            var removeBtn = new RectangleF(x0 + fw - 200f, y, 92f, 40f);
            bool rhov = removeBtn.Contains(_mouse);
            r.FillRect(removeBtn, rhov ? Color.FromArgb(255, 55, 55, 62) : Color.FromArgb(255, 42, 42, 48));
            r.DrawRect(removeBtn, CBorder);
            r.DrawText("Remove", new PointF(removeBtn.X + 14f, removeBtn.Y + 11f), CText, 11f);
            r.DrawText("(keep files)", new PointF(removeBtn.X + 6f, removeBtn.Y + 24f), CTextDim, 8f);

            // ── Delete button ─────────────────────────────────────────────────
            var deleteBtn = new RectangleF(x0 + fw - 100f, y, 100f, 40f);
            bool dhov = deleteBtn.Contains(_mouse);
            bool conf = _confirmDelete;

            r.FillRect(deleteBtn, dhov || conf ? CDangerH : CDanger);
            r.DrawRect(deleteBtn, Color.FromArgb(255, 140, 35, 35));
            if (conf)
            {
                r.DrawText("⚠ Confirm?", new PointF(deleteBtn.X + 8f, deleteBtn.Y + 11f),
                    Color.White, 10f);
            }
            else
            {
                r.DrawText("🗑 Delete", new PointF(deleteBtn.X + 12f, deleteBtn.Y + 11f),
                    Color.White, 11f);
                r.DrawText("(all files)", new PointF(deleteBtn.X + 10f, deleteBtn.Y + 24f),
                    Color.FromArgb(255, 255, 180, 180), 8f);
            }
            y += 50f;

            // Confirm note
            if (_confirmDelete)
            {
                r.DrawText("⚠  This will permanently delete all project files. Click Delete again to confirm.",
                    new PointF(x0, y), CDanger, 9f);
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        private void DrawSideBtn(IEditorRenderer r, string label,
            float x, float y, float w, float h, Color bg, Color fg, Action onClick)
        {
            var btn = new RectangleF(x, y, w, h);
            bool hov = btn.Contains(_mouse);
            Color c = hov ? LightenColor(bg, 1.12f) : bg;
            r.FillRect(btn, c);
            r.DrawRect(btn, CBorder);
            r.DrawText(label, new PointF(btn.X + 10f, btn.Y + (h - 12f) / 2f), fg, 11f);
        }

        private void DrawFormLabel(IEditorRenderer r, float x, float y, string label)
            => r.DrawText(label, new PointF(x, y), CTextDim, 10f);

        private void DrawFormField(IEditorRenderer r, float x, float w, string id,
            string value, float y, float h, Action<string> setter)
        {
            bool ed = _editId == id;
            var fr = new RectangleF(x, y, w, h);
            bool hov = fr.Contains(_mouse);
            r.FillRect(fr, ed ? Color.FromArgb(255, 25, 42, 70)
                       : hov ? Color.FromArgb(255, 38, 38, 46)
                             : Color.FromArgb(255, 30, 30, 36));
            r.DrawRect(fr, ed ? CAccent : hov ? CBorder : Color.FromArgb(255, 44, 44, 52));
            r.DrawText(ed ? _editBuf + "|" : (string.IsNullOrEmpty(value) ? " " : value),
                new PointF(fr.X + 8f, fr.Y + (h - 11f) / 2f), ed ? CText : value.Length > 0 ? CText : CTextDim, 11f);
        }

        private void DrawTypeCard(IEditorRenderer r, float x, float y, float w, float h,
            string title, string bullets, bool selected, Color accentColor, Action onSelect)
        {
            var card = new RectangleF(x, y, w, h);
            bool hov = !selected && card.Contains(_mouse);
            r.FillRect(card, selected ? Color.FromArgb(30, accentColor.R, accentColor.G, accentColor.B)
                           : hov ? Color.FromArgb(255, 36, 36, 42)
                                      : Color.FromArgb(255, 32, 32, 38));
            r.DrawRect(card, selected ? accentColor : CBorder);
            DrawColorBar(r, new RectangleF(x, y, 4f, h), selected ? accentColor
                         : Color.FromArgb(80, accentColor.R, accentColor.G, accentColor.B));

            r.DrawText(title, new PointF(x + 14f, y + 10f),
                selected ? accentColor : CText, 16f);

            float by = y + 34f;
            foreach (var line in bullets.Split('\n'))
            {
                r.DrawText("• " + line, new PointF(x + 14f, by), CTextDim, 9f);
                by += 14f;
            }
        }

        private static void DrawInfoRow(IEditorRenderer r, float x, float fw, float y,
            string label, string value)
        {
            r.DrawText(label + ":", new PointF(x, y + 2f), CTextDim, 10f);
            r.DrawText(value, new PointF(x + 120f, y + 2f), CText, 10f);
        }

        private static void DrawPill(IEditorRenderer r, string text, Color col,
            float x, float y)
        {
            var pill = new RectangleF(x, y, text.Length * 7f + 10f, 16f);
            r.FillRect(pill, Color.FromArgb(40, col.R, col.G, col.B));
            r.DrawRect(pill, col);
            r.DrawText(text, new PointF(pill.X + 5f, pill.Y + 2f), col, 9f);
        }

        private static void DrawColorBar(IEditorRenderer r, RectangleF rect, Color col)
            => r.FillRect(rect, col);

        // ═════════════════════════════════════════════════════════════════════
        //  Input
        // ═════════════════════════════════════════════════════════════════════
        public void OnMouseMove(PointF pos) => _mouse = pos;

        public void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (e.Button != MouseButton.Left) return;

            // Commit open edit
            if (_editId != null) { CommitEdit(); }

            // ── Sidebar ───────────────────────────────────────────────────────
            // New project button
            float y = PAD + 60f;
            var newBtn = new RectangleF(PAD, y, SideW - PAD * 2, 34f);
            if (newBtn.Contains(pos))
            {
                _mode = RightMode.NewProject;
                _selected = null;
                _confirmDelete = false;
                _newError = _newSuccess = null;
                return;
            }
            y += 38f;

            // Open from disk button
            var openDiskBtn = new RectangleF(PAD, y, SideW - PAD * 2, 34f);
            if (openDiskBtn.Contains(pos)) { OpenFromDisk(); return; }
            y += 42f + 10f + 18f;

            // Project list
            foreach (var proj in _projects)
            {
                var card = new RectangleF(4f, y, SideW - 8f, RowH - 4f);
                if (card.Contains(pos))
                {
                    _selected = proj;
                    _mode = RightMode.ProjectInfo;
                    _confirmDelete = false;
                    return;
                }
                y += RowH;
            }

            // ── Right panel ───────────────────────────────────────────────────
            float cx = SideW + (_winW - SideW) / 2f;
            float fw = Math.Min(560f, _winW - SideW - PAD * 4);
            float x0 = cx - fw / 2f;

            if (_mode == RightMode.NewProject)
            {
                HandleNewProjectClicks(pos, x0, fw);
            }
            else if (_mode == RightMode.ProjectInfo && _selected != null)
            {
                HandleInfoClicks(pos, x0, fw, _selected);
            }
        }

        private void HandleNewProjectClicks(PointF pos, float x0, float fw)
        {
            float y = PAD * 2 + 30f + 2f + 18f;

            // Name field
            var nameField = new RectangleF(x0, y, fw, 26f);
            if (nameField.Contains(pos))
            { StartEdit("newname", _newName, v => { _newName = v; _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory, v); }); return; }
            y += 34f;

            // Path field
            var pathField = new RectangleF(x0, y, fw - 100f, 26f);
            if (pathField.Contains(pos))
            { StartEdit("newpath", _newPath, v => _newPath = v); return; }

            // Browse button
            var browseBtn = new RectangleF(x0 + fw - 96f, y, 92f, 26f);
            if (browseBtn.Contains(pos)) { BrowseForFolder(); return; }
            y += 34f;

            // Desc field
            var descField = new RectangleF(x0, y, fw, 26f);
            if (descField.Contains(pos))
            { StartEdit("newdesc", _newDesc, v => _newDesc = v); return; }
            y += 38f;

            // Type cards
            float typeW = (fw - 10f) / 2f;
            if (new RectangleF(x0, y, typeW, 84f).Contains(pos))
            { _newType = ProjectType.TwoD; return; }
            if (new RectangleF(x0 + typeW + 10f, y, typeW, 84f).Contains(pos))
            { _newType = ProjectType.ThreeD; return; }
            y += 98f;

            // Skip error/success rows
            if (_newError != null) y += 36f;
            if (_newSuccess != null) y += 36f;

            // Create button
            var createBtn = new RectangleF(x0 + fw - 160f, y, 160f, 36f);
            if (createBtn.Contains(pos)) { TryCreateProject(); }
        }

        private void HandleInfoClicks(PointF pos, float x0, float fw, ProjectManifest proj)
        {
            // Approximate button Y (title + divider + info rows)
            float y = PAD * 2 + 34f + 2f + 14f + 24f * 6;
            if (!string.IsNullOrEmpty(proj.Description)) y += 24f;
            y += 10f + 2f + 18f;

            var openBtn = new RectangleF(x0, y, fw - 210f, 40f);
            var removeBtn = new RectangleF(x0 + fw - 200f, y, 92f, 40f);
            var deleteBtn = new RectangleF(x0 + fw - 100f, y, 100f, 40f);

            if (openBtn.Contains(pos))
            {
                OpenProject(proj);
                return;
            }
            if (removeBtn.Contains(pos))
            {
                ProjectManager.RemoveFromRegistry(proj);
                _selected = null;
                _mode = RightMode.NewProject;
                Refresh();
                return;
            }
            if (deleteBtn.Contains(pos))
            {
                if (_confirmDelete)
                {
                    ProjectManager.DeleteProject(proj, deleteFiles: true);
                    _selected = null;
                    _mode = RightMode.NewProject;
                    _confirmDelete = false;
                    Refresh();
                }
                else
                {
                    _confirmDelete = true;
                }
                return;
            }

            // Clicking anywhere else cancels delete confirmation
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
        private void TryCreateProject()
        {
            _newError = null;
            _newSuccess = null;

            if (string.IsNullOrWhiteSpace(_newName))
            { _newError = "Project name cannot be empty."; return; }

            if (string.IsNullOrWhiteSpace(_newPath))
            { _newError = "Save location cannot be empty."; return; }

            // Compose final path: if path ends with the project name already, use as-is
            string finalPath = _newPath.TrimEnd(Path.DirectorySeparatorChar,
                                                Path.AltDirectorySeparatorChar);
            if (!Path.GetFileName(finalPath).Equals(_newName,
                    StringComparison.OrdinalIgnoreCase))
                finalPath = Path.Combine(finalPath, _newName);

            if (Directory.Exists(finalPath) &&
                Directory.GetFileSystemEntries(finalPath).Length > 0)
            { _newError = "Directory already exists and is not empty."; return; }

            var manifest = ProjectManager.CreateProject(_newName, finalPath,
                _newType, _newDesc);

            if (manifest == null)
            { _newError = "Failed to create project. Check the path and try again."; return; }

            _newSuccess = $"Project created at {finalPath}";
            Refresh();

            // Auto-select in the list
            _selected = manifest;
            _mode = RightMode.ProjectInfo;
            _confirmDelete = false;
        }

        private void OpenProject(ProjectManifest proj)
        {
            var opened = ProjectManager.OpenProject(proj.ManifestPath);
            if (opened != null)
                ProjectOpened?.Invoke(opened.RootPath);
        }

        private void OpenFromDisk()
        {
            // Since we have no native file dialog, we look for project.elintria
            // files in the default projects directory and auto-import them.
            // In a real integration you would invoke a platform file dialog here.
            string dir = ProjectManager.DefaultProjectsDirectory;
            if (!Directory.Exists(dir)) return;

            foreach (var mf in Directory.GetFiles(dir, "project.elintria",
                SearchOption.AllDirectories))
            {
                ProjectManager.ImportProject(mf);
            }
            Refresh();
        }

        private void BrowseForFolder()
        {
            // Platform file dialog — auto-sets to default directory for now.
            // Replace with a native dialog call when integrating with NativeFileDialog etc.
            _newPath = Path.Combine(ProjectManager.DefaultProjectsDirectory,
                string.IsNullOrWhiteSpace(_newName) ? "NewProject" : _newName);
        }

        // ── Edit helpers ──────────────────────────────────────────────────────
        private void StartEdit(string id, string initial, Action<string> commit)
        {
            _editId = id;
            _editBuf = initial;
            _editCommit = commit;
        }

        private void CommitEdit()
        {
            _editCommit?.Invoke(_editBuf);
            _editId = null;
        }

        // ── Formatting helpers ────────────────────────────────────────────────
        private static string FormatDate(DateTime dt)
        {
            var diff = DateTime.UtcNow - dt;
            if (diff.TotalMinutes < 2) return "just now";
            if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return dt.ToLocalTime().ToString("MMM d, yyyy");
        }

        private static string FormatDateFull(DateTime dt)
            => dt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");

        private static string TruncatePath(string path, int maxChars)
        {
            if (path.Length <= maxChars) return path;
            return "…" + path[^(maxChars - 1)..];
        }

        private static Color LightenColor(Color c, float f)
            => Color.FromArgb(c.A,
                Math.Min(255, (int)(c.R * f)),
                Math.Min(255, (int)(c.G * f)),
                Math.Min(255, (int)(c.B * f)));
    }
}