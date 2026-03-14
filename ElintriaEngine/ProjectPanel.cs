using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;

namespace ElintriaEngine.UI.Panels
{
    public enum AssetType
    {
        Folder, Script, Texture, Model, Material,
        Shader, Scene, Prefab, Audio, Text, Unknown
    }

    public class FileItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public AssetType Type { get; }
        public bool IsDirectory { get; }
        public RectangleF CachedBounds { get; set; }
        public FileItem(string name, string path, AssetType type, bool isDir)
        { Name = name; FullPath = path; Type = type; IsDirectory = isDir; }
    }

    public class ProjectPanel : Panel
    {
        private string _rootPath = "";
        private string _curPath = "";
        private List<FileItem> _items = new();
        private FileItem? _selected;
        private FileItem? _hovered;
        private FileItem? _renaming;
        private string _renameBuffer = "";
        private ContextMenu? _ctxMenu;
        private bool _showCtx;
        private List<string> _breadcrumbs = new();

        public string CurrentPath => _curPath;
        public (int W, int H) ScreenSize { get; set; } = (1920, 1080);
        public override ContextMenu? GetActiveContextMenu() => _showCtx ? _ctxMenu : null;

        public FileItem? ActiveDrag { get; private set; }
        private FileItem? _dragItem;
        private PointF _dragStart;

        private FileItem? _lastClick;
        private double _lastClickTime;

        public event Action<FileItem>? AssetSelected;
        public event Action<FileItem>? AssetDoubleClicked;
        public event Action<FileItem>? DragStarted;

        // ── Layout constants ──────────────────────────────────────────────────
        private const float BreadH = 22f;
        private const float SliderH = 24f;   // bottom toolbar with size slider
        private const float MinScale = 0.5f;
        private const float MaxScale = 3.0f;
        private float _scale = 1.0f;          // user-controlled size scale

        // Scaled dimensions computed each frame
        private float ListRowH => MathF.Max(16f, 22f * _scale);
        private float TileW => MathF.Max(48f, 68f * _scale);
        private float TileH => TileW + 20f;
        private float TileGap => MathF.Max(4f, 6f * _scale);
        private float IconFont => Math.Clamp(8f + (_scale - 1f) * 8f, 7f, 16f);
        private float TextFont => Math.Clamp(8f + (_scale - 1f) * 4f, 7f, 13f);

        // Slider drag state
        private bool _sliderDrag;
        private float _sliderTrackX;
        private float _sliderTrackW;

        // Tile vs list
        private bool _tileView = true;

        // ── Prefab drop highlight ─────────────────────────────────────────────
        public bool PrefabDropHighlight { get; set; }

        public ProjectPanel(RectangleF bounds) : base("Project", bounds)
        { MinWidth = 180f; MinHeight = 100f; }

        public void SetRootPath(string path)
        {
            _rootPath = path;
            _curPath = path;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Refresh();
        }

        public void Refresh()
        {
            _items.Clear();
            _breadcrumbs.Clear();

            if (!Directory.Exists(_curPath)) return;

            string rel = Path.GetRelativePath(_rootPath, _curPath);
            _breadcrumbs.Add("Assets");
            if (rel != ".")
                foreach (var part in rel.Split(Path.DirectorySeparatorChar))
                    _breadcrumbs.Add(part);

            var dir = new DirectoryInfo(_curPath);
            foreach (var d in dir.GetDirectories().Where(d => !d.Name.StartsWith('.')).OrderBy(d => d.Name))
                _items.Add(new FileItem(d.Name, d.FullName, AssetType.Folder, true));
            foreach (var f in dir.GetFiles().Where(f => !f.Name.StartsWith('.')).OrderBy(f => f.Name))
                _items.Add(new FileItem(f.Name, f.FullName, Classify(f.Extension), false));
        }

        private static AssetType Classify(string ext) => ext.ToLowerInvariant() switch
        {
            ".cs" => AssetType.Script,
            ".png" or ".jpg" or ".jpeg"
            or ".bmp" or ".tga" or ".hdr" => AssetType.Texture,
            ".fbx" or ".obj" or ".dae"
            or ".gltf" or ".glb" => AssetType.Model,
            ".mat" => AssetType.Material,
            ".shader" or ".glsl" or ".vert"
            or ".frag" or ".geom" or ".comp" => AssetType.Shader,
            ".scene" => AssetType.Scene,
            ".prefab" => AssetType.Prefab,
            ".mp3" or ".wav" or ".ogg" or ".flac" => AssetType.Audio,
            ".txt" or ".md" or ".json" => AssetType.Text,
            _ => AssetType.Unknown,
        };

        // ══════════════════════════════════════════════════════════════════════
        //  Render
        // ══════════════════════════════════════════════════════════════════════
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            DrawHeader(r);

            // ── Breadcrumb bar ─────────────────────────────────────────────────
            var breadRect = new RectangleF(Bounds.X, Bounds.Y + HeaderH, Bounds.Width, BreadH);
            r.FillRect(breadRect, Color.FromArgb(255, 28, 28, 28));
            r.DrawLine(new PointF(Bounds.X, breadRect.Bottom),
                       new PointF(Bounds.Right, breadRect.Bottom),
                       Color.FromArgb(255, 50, 50, 50));

            float bx = Bounds.X + 6f;
            for (int i = 0; i < _breadcrumbs.Count; i++)
            {
                bool last = i == _breadcrumbs.Count - 1;
                r.DrawText(_breadcrumbs[i], new PointF(bx, breadRect.Y + 5f),
                    last ? ColText : Color.FromArgb(255, 100, 158, 255), 10f);
                bx += _breadcrumbs[i].Length * 6.0f + 4f;
                if (!last) { r.DrawText(">", new PointF(bx, breadRect.Y + 5f), ColTextDim, 10f); bx += 12f; }
            }

            // View-mode toggle (top-right of breadcrumb)
            var tBtn = new RectangleF(Bounds.Right - 22f, breadRect.Y + 3f, 18f, 16f);
            r.FillRect(tBtn, Color.FromArgb(255, 52, 52, 52));
            r.DrawRect(tBtn, ColBorder);
            r.DrawText(_tileView ? "L" : "T", new PointF(tBtn.X + 4f, tBtn.Y + 2f), ColText, 9f);

            // ── Bottom slider toolbar ──────────────────────────────────────────
            var sliderBar = new RectangleF(Bounds.X, Bounds.Bottom - SliderH, Bounds.Width, SliderH);
            r.FillRect(sliderBar, Color.FromArgb(255, 24, 24, 26));
            r.DrawLine(new PointF(sliderBar.X, sliderBar.Y),
                       new PointF(sliderBar.Right, sliderBar.Y),
                       Color.FromArgb(255, 50, 50, 55));

            // Scale label
            string scaleLabel = $"{(int)(_scale * 100)}%";
            r.DrawText("⊟", new PointF(sliderBar.X + 5f, sliderBar.Y + 5f), ColTextDim, 10f);
            r.DrawText("⊞", new PointF(sliderBar.Right - 18f, sliderBar.Y + 5f), ColTextDim, 10f);
            r.DrawText(scaleLabel,
                new PointF(sliderBar.Right - 50f, sliderBar.Y + 6f),
                ColTextDim, 8f);

            // Track
            float padL = sliderBar.X + 18f;
            float padR = sliderBar.Right - 55f;
            _sliderTrackX = padL;
            _sliderTrackW = padR - padL;
            var track = new RectangleF(padL, sliderBar.Y + 10f, _sliderTrackW, 4f);
            r.FillRect(track, Color.FromArgb(255, 55, 55, 60));
            r.DrawRect(track, Color.FromArgb(255, 70, 70, 78));

            // Filled portion
            float frac = (_scale - MinScale) / (MaxScale - MinScale);
            float filled = _sliderTrackW * frac;
            if (filled > 0)
                r.FillRect(new RectangleF(padL, sliderBar.Y + 10f, filled, 4f),
                    Color.FromArgb(255, 70, 130, 255));

            // Thumb
            float thumbX = padL + filled - 5f;
            var thumb = new RectangleF(thumbX, sliderBar.Y + 6f, 10f, 12f);
            r.FillRect(thumb, _sliderDrag
                ? Color.FromArgb(255, 100, 170, 255)
                : Color.FromArgb(255, 180, 195, 225));
            r.DrawRect(thumb, Color.FromArgb(255, 60, 100, 200));

            // ── Content area ───────────────────────────────────────────────────
            var cr = ContentArea;

            r.PushClip(cr);

            // Prefab drop highlight
            if (PrefabDropHighlight)
            {
                r.FillRect(cr, Color.FromArgb(40, 80, 200, 80));
                r.DrawRect(cr, Color.FromArgb(200, 80, 220, 100), 2f);
                r.DrawText("Drop to create Prefab",
                    new PointF(cr.X + cr.Width / 2f - 60f, cr.Y + cr.Height / 2f - 8f),
                    Color.FromArgb(220, 120, 255, 130), 11f);
            }
            else
            {
                r.FillRect(cr, ColBg);
                if (_items.Count == 0)
                    r.DrawText("(empty folder)", new PointF(cr.X + 10f, cr.Y + 10f), ColTextDim, 11f);
                else if (_tileView)
                    RenderTiles(r, cr);
                else
                    RenderList(r, cr);
            }

            r.PopClip();

            // Scrollbar (outside clip)
            DrawScrollBarManual(r, cr);
            // context menu rendered above dock overlay by EditorLayout
        }

        private void DrawScrollBarManual(IEditorRenderer r, RectangleF cr)
        {
            if (ContentHeight <= cr.Height) return;
            var track = new RectangleF(cr.Right, cr.Y, 8f, cr.Height);
            r.FillRect(track, Color.FromArgb(255, 28, 28, 28));
            float ratio = cr.Height / ContentHeight;
            float thumbH = Math.Max(16f, cr.Height * ratio);
            float maxOff = ContentHeight - cr.Height;
            float frac = maxOff > 0 ? ScrollOffset / maxOff : 0f;
            float thumbY = cr.Y + frac * (cr.Height - thumbH);
            r.FillRect(new RectangleF(track.X + 1f, thumbY, 6f, thumbH),
                Color.FromArgb(255, 80, 80, 80));
        }

        // ── List view ─────────────────────────────────────────────────────────
        private void RenderList(IEditorRenderer r, RectangleF cr)
        {
            float rowH = ListRowH;
            ContentHeight = _items.Count * rowH;
            float y = cr.Y - ScrollOffset;

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var row = new RectangleF(cr.X, y, cr.Width, rowH);
                item.CachedBounds = row;

                if (y + rowH >= cr.Y && y <= cr.Bottom)
                {
                    if ((i & 1) == 1) r.FillRect(row, Color.FromArgb(12, 255, 255, 255));
                    if (_selected == item) r.FillRect(row, ColSelected);
                    else if (_hovered == item) r.FillRect(row, ColHover);

                    // Icon badge
                    string icon = TypeLabel(item);
                    Color iCol = IconColor(item.Type);
                    float badgeW = icon.Length * 5.5f + 4f;
                    var badge = new RectangleF(cr.X + 3f, y + (rowH - 14f) / 2f, badgeW, 14f);
                    r.FillRect(badge, Color.FromArgb(180, TileBg(item.Type)));
                    r.DrawText(icon, new PointF(badge.X + 2f, badge.Y + 2f), Color.White, MathF.Max(7f, TextFont - 1f));

                    // File name
                    string nm = item == _renaming ? _renameBuffer + "|" : item.Name;
                    r.DrawText(nm, new PointF(cr.X + badgeW + 8f, y + (rowH - 10f) / 2f),
                        item.IsDirectory ? Color.FromArgb(255, 180, 200, 255) : ColText, TextFont);

                    // Extension (right-aligned)
                    if (!item.IsDirectory)
                    {
                        string ext = Path.GetExtension(item.Name).ToUpper();
                        r.DrawText(ext, new PointF(cr.Right - ext.Length * 5f - 6f, y + (rowH - 10f) / 2f),
                            ColTextDim, MathF.Max(7f, TextFont - 2f));
                    }
                }
                y += rowH;
            }
        }

        // ── Tile view ─────────────────────────────────────────────────────────
        private void RenderTiles(IEditorRenderer r, RectangleF cr)
        {
            float tw = TileW, th = TileH, gap = TileGap;
            int cols = Math.Max(1, (int)((cr.Width - gap) / (tw + gap)));
            float rowH = th + gap;

            ContentHeight = (float)Math.Ceiling(_items.Count / (float)cols) * rowH + gap;

            for (int i = 0; i < _items.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                float tx = cr.X + gap + col * (tw + gap);
                float ty = cr.Y + gap + row * rowH - ScrollOffset;

                var item = _items[i];
                var outer = new RectangleF(tx - 2f, ty - 2f, tw + 4f, th + 4f);
                item.CachedBounds = outer;

                if (ty + th < cr.Y || ty > cr.Bottom) continue;

                bool sel = _selected == item, hov = _hovered == item;
                if (sel) r.FillRect(outer, ColSelected);
                else if (hov) r.FillRect(outer, ColHover);

                // Tile body — leave room at bottom for filename
                float labelH = MathF.Max(14f, TextFont + 6f);
                var body = new RectangleF(tx, ty, tw, th - labelH);
                r.FillRect(body, TileBg(item.Type));
                r.DrawRect(body, Color.FromArgb(50, 255, 255, 255));

                // Icon centred in body
                string icon = TypeIcon(item);
                float iconSz = IconFont;
                float iconX = tx + (tw - iconSz * 0.65f) / 2f;
                float iconY = ty + (body.Height - iconSz) / 2f;
                r.DrawText(icon, new PointF(iconX, iconY), Color.White, iconSz);

                // Type badge (top-left corner)
                string badge = TypeLabel(item);
                var badgeR = new RectangleF(tx + 2f, ty + 2f, badge.Length * 5f + 4f, 12f);
                r.FillRect(badgeR, Color.FromArgb(150, 20, 20, 20));
                r.DrawText(badge, new PointF(badgeR.X + 2f, badgeR.Y + 1f), IconColor(item.Type), 7f);

                // Filename below tile
                string nm = item == _renaming ? _renameBuffer + "|" : TruncName(item.Name, _scale);
                r.DrawText(nm, new PointF(tx + 2f, ty + th - labelH + 2f), ColText, TextFont);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        // Large unicode icon for tile centre
        private static string TypeIcon(FileItem i) => i.Type switch
        {
            AssetType.Folder => "📁",
            AssetType.Script => "📄",
            AssetType.Texture => "🖼",
            AssetType.Model => "📦",
            AssetType.Material => "🎨",
            AssetType.Shader => "✦",
            AssetType.Scene => "🌐",
            AssetType.Prefab => "⬡",
            AssetType.Audio => "♪",
            AssetType.Text => "📝",
            _ => "?",
        };

        // Short ASCII badge
        private static string TypeLabel(FileItem i) => i.Type switch
        {
            AssetType.Folder => "DIR",
            AssetType.Script => "C#",
            AssetType.Texture => "IMG",
            AssetType.Model => "3D",
            AssetType.Material => "MAT",
            AssetType.Shader => "SHD",
            AssetType.Scene => "SCN",
            AssetType.Prefab => "PFB",
            AssetType.Audio => "SND",
            AssetType.Text => "TXT",
            _ => "???",
        };

        private static Color IconColor(AssetType t) => t switch
        {
            AssetType.Folder => Color.FromArgb(255, 160, 190, 255),
            AssetType.Script => Color.FromArgb(255, 100, 210, 100),
            AssetType.Texture => Color.FromArgb(255, 210, 130, 80),
            AssetType.Model => Color.FromArgb(255, 160, 120, 220),
            AssetType.Material => Color.FromArgb(255, 220, 190, 60),
            AssetType.Shader => Color.FromArgb(255, 80, 200, 220),
            AssetType.Scene => Color.FromArgb(255, 80, 190, 80),
            AssetType.Prefab => Color.FromArgb(255, 120, 200, 255),
            AssetType.Audio => Color.FromArgb(255, 210, 100, 160),
            _ => Color.FromArgb(255, 160, 160, 160),
        };

        private static Color TileBg(AssetType t) => t switch
        {
            AssetType.Folder => Color.FromArgb(255, 50, 85, 130),
            AssetType.Script => Color.FromArgb(255, 40, 120, 50),
            AssetType.Texture => Color.FromArgb(255, 120, 55, 50),
            AssetType.Model => Color.FromArgb(255, 65, 50, 130),
            AssetType.Material => Color.FromArgb(255, 130, 100, 35),
            AssetType.Shader => Color.FromArgb(255, 35, 115, 130),
            AssetType.Scene => Color.FromArgb(255, 35, 85, 35),
            AssetType.Prefab => Color.FromArgb(255, 30, 90, 140),
            AssetType.Audio => Color.FromArgb(255, 120, 45, 110),
            _ => Color.FromArgb(255, 58, 58, 58),
        };

        private string TruncName(string s, float scale)
        {
            int max = Math.Max(6, (int)(TileW / (TextFont * 0.62f)));
            return s.Length > max ? s[..(max - 1)] + "~" : s;
        }

        private RectangleF ContentArea => new(
            Bounds.X,
            Bounds.Y + HeaderH + BreadH,
            Bounds.Width - 8f,
            Bounds.Height - HeaderH - BreadH - SliderH);

        private RectangleF SliderBarRect => new(
            Bounds.X, Bounds.Bottom - SliderH, Bounds.Width, SliderH);

        // ══════════════════════════════════════════════════════════════════════
        //  Input
        // ══════════════════════════════════════════════════════════════════════
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

            // ── Slider bar ────────────────────────────────────────────────────
            if (SliderBarRect.Contains(pos) && e.Button == MouseButton.Left)
            {
                if (pos.X >= _sliderTrackX && pos.X <= _sliderTrackX + _sliderTrackW)
                {
                    _sliderDrag = true;
                    ApplySliderDrag(pos.X);
                }
                return;
            }

            // ── View toggle ───────────────────────────────────────────────────
            var tBtn = new RectangleF(Bounds.Right - 22f, Bounds.Y + HeaderH + 3f, 18f, 16f);
            if (tBtn.Contains(pos)) { _tileView = !_tileView; ScrollOffset = 0f; return; }

            // ── Breadcrumb ────────────────────────────────────────────────────
            if (HandleBreadcrumb(pos)) return;

            var ca = ContentArea;
            if (!ca.Contains(pos)) { base.OnMouseDown(e, pos); return; }

            var hit = HitTest(pos);

            if (e.Button == MouseButton.Right)
            {
                _selected = hit;
                if (hit != null) AssetSelected?.Invoke(hit);
                ShowContextMenu(pos, hit);
                return;
            }

            if (hit == null) { _selected = null; return; }

            double now = Environment.TickCount64 / 1000.0;
            if (_lastClick == hit && now - _lastClickTime < 0.4)
            { HandleDoubleClick(hit); _lastClick = null; return; }

            _lastClick = hit;
            _lastClickTime = now;
            _selected = hit;
            AssetSelected?.Invoke(hit);
            _dragItem = hit;
            _dragStart = pos;
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _sliderDrag = false;
            ActiveDrag = null; _dragItem = null;
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseMove(PointF pos)
        {
            base.OnMouseMove(pos);
            _ctxMenu?.OnMouseMove(pos);

            if (_sliderDrag)
            {
                ApplySliderDrag(pos.X);
                return;
            }

            _hovered = ContentArea.Contains(pos) ? HitTest(pos) : null;

            if (_dragItem != null && ActiveDrag == null)
            {
                float d = MathF.Sqrt(MathF.Pow(pos.X - _dragStart.X, 2) +
                                     MathF.Pow(pos.Y - _dragStart.Y, 2));
                if (d > 5f) { ActiveDrag = _dragItem; DragStarted?.Invoke(_dragItem); }
            }
        }

        public override void OnMouseScroll(float delta)
        {
            if (SliderBarRect.Contains(new PointF(_dragStart.X, Bounds.Bottom - 1f))) return;
            float max = Math.Max(0, ContentHeight - ContentArea.Height);
            ScrollOffset = Math.Clamp(ScrollOffset - delta * 28f, 0f, max);
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
        }

        public override void OnTextInput(TextInputEventArgs e)
        { if (_renaming != null) _renameBuffer += e.AsString; }

        private void ApplySliderDrag(float mouseX)
        {
            float frac = Math.Clamp((mouseX - _sliderTrackX) / _sliderTrackW, 0f, 1f);
            _scale = MinScale + frac * (MaxScale - MinScale);
        }

        // ── Hit test ──────────────────────────────────────────────────────────
        private FileItem? HitTest(PointF pos)
        {
            foreach (var i in _items)
                if (i.CachedBounds.Contains(pos)) return i;
            return null;
        }

        // ── Breadcrumb ────────────────────────────────────────────────────────
        private bool HandleBreadcrumb(PointF pos)
        {
            float bx = Bounds.X + 6f;
            float by = Bounds.Y + HeaderH + 5f;
            for (int i = 0; i < _breadcrumbs.Count; i++)
            {
                float bw = _breadcrumbs[i].Length * 6.0f + 14f;
                if (new RectangleF(bx, by, bw, 14f).Contains(pos))
                { NavToBreadcrumb(i); return true; }
                bx += bw + 12f;
            }
            return false;
        }

        private void NavToBreadcrumb(int index)
        {
            string p = _rootPath;
            for (int i = 1; i <= index; i++) p = Path.Combine(p, _breadcrumbs[i]);
            _curPath = p; ScrollOffset = 0f; Refresh();
        }

        // ── Double click ──────────────────────────────────────────────────────
        private void HandleDoubleClick(FileItem item)
        {
            AssetDoubleClicked?.Invoke(item);
            if (item.IsDirectory) { _curPath = item.FullPath; ScrollOffset = 0f; Refresh(); return; }
            if (item.Type == AssetType.Script) OpenScript(item.FullPath);
        }

        private void OpenScript(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            while (dir != null)
            {
                var slns = Directory.GetFiles(dir, "*.sln");
                if (slns.Length > 0)
                { Process.Start(new ProcessStartInfo(slns[0]) { UseShellExecute = true }); return; }
                if (dir == _rootPath) break;
                dir = Path.GetDirectoryName(dir);
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        // ── Rename ────────────────────────────────────────────────────────────
        private void StartRename(FileItem item)
        { _renaming = item; _renameBuffer = Path.GetFileNameWithoutExtension(item.Name); }

        private void CommitRename()
        {
            if (_renaming == null || _renameBuffer.Trim().Length == 0) { _renaming = null; return; }
            string ext = Path.GetExtension(_renaming.Name);
            string dest = Path.Combine(Path.GetDirectoryName(_renaming.FullPath)!, _renameBuffer.Trim() + ext);
            try
            {
                if (_renaming.IsDirectory) Directory.Move(_renaming.FullPath, dest);
                else File.Move(_renaming.FullPath, dest);
                Refresh();
            }
            catch { }
            _renaming = null;
        }

        // ── Delete ────────────────────────────────────────────────────────────
        private void DeleteSelected()
        {
            if (_selected == null) return;
            try
            {
                if (_selected.IsDirectory) Directory.Delete(_selected.FullPath, true);
                else File.Delete(_selected.FullPath);
                _selected = null; Refresh();
            }
            catch { }
        }

        // ── Context menu ──────────────────────────────────────────────────────
        private void ShowContextMenu(PointF pos, FileItem? target)
        {
            var items = new List<ContextMenuItem>
            {
                new("Create", null) { IsDisabled = true },
                new("  Folder",      () => CreateAsset("New Folder",    AssetKind.Folder)),
                new("  C# Script",   () => CreateAsset("NewScript",     AssetKind.Script)),
                new("  Scene",       () => CreateAsset("New Scene",     AssetKind.Scene)),
                new("  Material",    () => CreateAsset("New Material",  AssetKind.Material)),
                new("  Shader",      () => CreateAsset("New Shader",    AssetKind.Shader)),
                new("  Plain Text",  () => CreateAsset("notes",         AssetKind.Text)),
                new("  Prefab",      () => CreateAsset("New Prefab",    AssetKind.Prefab)),
            };
            if (target != null)
            {
                items.Add(ContextMenuItem.Separator);
                items.Add(new("Rename (F2)", () => StartRename(target)));
                items.Add(new("Delete", () => { _selected = target; DeleteSelected(); }));
                items.Add(new("Show in Explorer", () => RevealInExplorer(target.FullPath)));
            }
            else
            {
                items.Add(ContextMenuItem.Separator);
                items.Add(new("Show in Explorer", () => RevealInExplorer(_curPath)));
                items.Add(new("Refresh", Refresh));
            }
            _ctxMenu = new ContextMenu(pos, items);
            _ctxMenu.Reposition(ScreenSize.W, ScreenSize.H);
            _showCtx = true;
        }

        private enum AssetKind { Folder, Script, Scene, Material, Shader, Text, Prefab }

        private void CreateAsset(string name, AssetKind kind)
        {
            _showCtx = false;
            string ext = kind switch
            {
                AssetKind.Script => "cs",
                AssetKind.Scene => "scene",
                AssetKind.Material => "mat",
                AssetKind.Shader => "shader",
                AssetKind.Text => "txt",
                AssetKind.Prefab => "prefab",
                _ => ""
            };
            string path = UniquePath(_curPath, name, ext.Length > 0 ? "." + ext : "");

            switch (kind)
            {
                case AssetKind.Folder: Directory.CreateDirectory(path); break;
                case AssetKind.Script:
                    string cn = Path.GetFileNameWithoutExtension(path);
                    File.WriteAllText(path, ScriptTemplates.CSharpScript(cn));
                    ScriptProjectGenerator.EnsureProjectForScript(path, _rootPath);
                    break;
                case AssetKind.Scene: File.WriteAllText(path, ScriptTemplates.Scene(name)); break;
                case AssetKind.Material: File.WriteAllText(path, ScriptTemplates.Material()); break;
                case AssetKind.Shader: File.WriteAllText(path, ScriptTemplates.Shader(name)); break;
                case AssetKind.Text: File.WriteAllText(path, ""); break;
                case AssetKind.Prefab: File.WriteAllText(path, ScriptTemplates.Prefab()); break;
            }
            Refresh();
        }

        private static string UniquePath(string dir, string name, string ext)
        {
            string p = Path.Combine(dir, name + ext); int n = 1;
            while (File.Exists(p) || Directory.Exists(p))
                p = Path.Combine(dir, $"{name} ({n++}){ext}");
            return p;
        }

        private static void RevealInExplorer(string path)
        {
            if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
        }
    }
}