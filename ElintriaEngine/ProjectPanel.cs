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
        private const float SliderH = 24f;
        private const float MinScale = 0.5f;
        private const float MaxScale = 3.0f;
        private float _scale = 1.0f;

        private float ListRowH => MathF.Max(16f, 22f * _scale);
        private float TileW => MathF.Max(48f, 68f * _scale);
        private float TileH => TileW + 20f;
        private float TileGap => MathF.Max(4f, 6f * _scale);
        private float IconFont => Math.Clamp(8f + (_scale - 1f) * 8f, 7f, 16f);
        private float TextFont => Math.Clamp(8f + (_scale - 1f) * 4f, 7f, 13f);

        private bool _sliderDrag;
        private float _sliderTrackX;
        private float _sliderTrackW;

        private bool _tileView = true;

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

            string scaleLabel = $"{(int)(_scale * 100)}%";
            r.DrawText("⊟", new PointF(sliderBar.X + 5f, sliderBar.Y + 5f), ColTextDim, 10f);
            r.DrawText("⊞", new PointF(sliderBar.Right - 18f, sliderBar.Y + 5f), ColTextDim, 10f);
            r.DrawText(scaleLabel, new PointF(sliderBar.Right - 50f, sliderBar.Y + 6f), ColTextDim, 8f);

            float padL = sliderBar.X + 18f;
            float padR = sliderBar.Right - 55f;
            _sliderTrackX = padL;
            _sliderTrackW = padR - padL;
            var track = new RectangleF(padL, sliderBar.Y + 10f, _sliderTrackW, 4f);
            r.FillRect(track, Color.FromArgb(255, 55, 55, 60));
            r.DrawRect(track, Color.FromArgb(255, 70, 70, 78));

            float frac = (_scale - MinScale) / (MaxScale - MinScale);
            float filled = _sliderTrackW * frac;
            if (filled > 0)
                r.FillRect(new RectangleF(padL, sliderBar.Y + 10f, filled, 4f),
                    Color.FromArgb(255, 70, 130, 255));

            float thumbX = padL + filled - 5f;
            var thumb = new RectangleF(thumbX, sliderBar.Y + 6f, 10f, 12f);
            r.FillRect(thumb, _sliderDrag
                ? Color.FromArgb(255, 100, 170, 255)
                : Color.FromArgb(255, 180, 195, 225));
            r.DrawRect(thumb, Color.FromArgb(255, 60, 100, 200));

            // ── Content area ───────────────────────────────────────────────────
            var cr = ContentArea;
            r.PushClip(cr);

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
            DrawScrollBarManual(r, cr);
        }

        private void DrawScrollBarManual(IEditorRenderer r, RectangleF cr)
        {
            if (ContentHeight <= cr.Height) return;
            var track = new RectangleF(cr.Right, cr.Y, 8f, cr.Height);
            r.FillRect(track, Color.FromArgb(255, 28, 28, 28));
            float ratio = cr.Height / ContentHeight;
            float thumbH = Math.Max(16f, cr.Height * ratio);
            float maxOff = ContentHeight - cr.Height;
            float tf = maxOff > 0 ? ScrollOffset / maxOff : 0f;
            float thumbY = cr.Y + tf * (cr.Height - thumbH);
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

                    // Small inline icon (square, rowH-4 tall)
                    float iconSize = MathF.Max(12f, rowH - 4f);
                    var iconRect = new RectangleF(cr.X + 3f, y + (rowH - iconSize) / 2f,
                                                    iconSize, iconSize);
                    DrawAssetIcon(r, iconRect, item.Type);

                    // File name
                    string nm = item == _renaming ? _renameBuffer + "|" : item.Name;
                    r.DrawText(nm, new PointF(cr.X + iconSize + 8f, y + (rowH - 10f) / 2f),
                        item.IsDirectory ? Color.FromArgb(255, 180, 200, 255) : ColText, TextFont);

                    // Extension badge (right-aligned)
                    if (!item.IsDirectory)
                    {
                        string ext = Path.GetExtension(item.Name).ToUpper();
                        r.DrawText(ext,
                            new PointF(cr.Right - ext.Length * 5f - 6f, y + (rowH - 10f) / 2f),
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
                int col = i % cols, row = i / cols;
                float tx = cr.X + gap + col * (tw + gap);
                float ty = cr.Y + gap + row * rowH - ScrollOffset;

                var item = _items[i];
                var outer = new RectangleF(tx - 2f, ty - 2f, tw + 4f, th + 4f);
                item.CachedBounds = outer;

                if (ty + th < cr.Y || ty > cr.Bottom) continue;

                bool sel = _selected == item, hov = _hovered == item;
                if (sel) r.FillRect(outer, ColSelected);
                else if (hov) r.FillRect(outer, ColHover);

                // Tile body
                float labelH = MathF.Max(14f, TextFont + 6f);
                var body = new RectangleF(tx, ty, tw, th - labelH);
                r.FillRect(body, TileBg(item.Type));
                r.DrawRect(body, Color.FromArgb(50, 255, 255, 255));

                // Draw the unique vector icon centred in the body
                float padding = body.Width * 0.15f;
                var iconRect = new RectangleF(
                    body.X + padding,
                    body.Y + padding,
                    body.Width - padding * 2f,
                    body.Height - padding * 2f);
                DrawAssetIcon(r, iconRect, item.Type);

                // Filename below tile
                string nm = item == _renaming ? _renameBuffer + "|" : TruncName(item.Name, _scale);
                r.DrawText(nm, new PointF(tx + 2f, ty + th - labelH + 2f), ColText, TextFont);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DrawAssetIcon
        //
        //  Draws a unique, recognisable icon for each AssetType inside `rect`.
        //  All drawing uses only FillRect / DrawRect / DrawLine so it works with
        //  any IEditorRenderer implementation.
        //
        //  Icon design summary:
        //    Folder   — classic tab + open box shape
        //    Script   — white paper with folded corner + green code lines
        //    Texture  — checker board with mountain/sun landscape
        //    Model    — isometric cube wireframe
        //    Material — glossy sphere: dark circle with bright specular spot
        //    Shader   — diamond / rhombus with gradient fill
        //    Scene    — globe grid: circle with latitude/longitude lines
        //    Prefab   — cyan hexagon outline
        //    Audio    — speaker cone + waveform lines
        //    Text     — lined paper with margin
        //    Unknown  — grey question mark block
        // ══════════════════════════════════════════════════════════════════════
        private static void DrawAssetIcon(IEditorRenderer r, RectangleF rect, AssetType type)
        {
            float x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
            // Helpers — fractional coordinates inside rect
            float X(float f) => x + w * f;
            float Y(float f) => y + h * f;
            float W(float f) => w * f;
            float H(float f) => h * f;
            RectangleF R(float fx, float fy, float fw, float fh)
                => new(X(fx), Y(fy), W(fw), H(fh));

            switch (type)
            {
                // ── Folder ────────────────────────────────────────────────────
                // Classic folder: tab on top-left, open rectangular body
                case AssetType.Folder:
                    {
                        var tabCol = Color.FromArgb(255, 200, 170, 80);
                        var bodyCol = Color.FromArgb(255, 220, 190, 100);
                        var shadow = Color.FromArgb(255, 160, 135, 60);
                        // body
                        r.FillRect(R(0f, 0.30f, 1f, 0.70f), bodyCol);
                        r.DrawRect(R(0f, 0.30f, 1f, 0.70f), shadow);
                        // tab (top-left)
                        r.FillRect(R(0f, 0.18f, 0.44f, 0.15f), tabCol);
                        r.DrawRect(R(0f, 0.18f, 0.44f, 0.15f), shadow);
                        // inner shadow line at top of body
                        r.DrawLine(new PointF(X(0.01f), Y(0.32f)), new PointF(X(0.99f), Y(0.32f)),
                            Color.FromArgb(80, 0, 0, 0));
                        break;
                    }

                // ── Script ────────────────────────────────────────────────────
                // White/light page with dog-eared top-right corner + green code lines
                case AssetType.Script:
                    {
                        var pageCol = Color.FromArgb(255, 235, 240, 248);
                        var foldCol = Color.FromArgb(255, 180, 195, 215);
                        var lineCol = Color.FromArgb(255, 60, 185, 90);
                        var lineCol2 = Color.FromArgb(255, 40, 140, 60);
                        float fold = 0.28f;   // fold size

                        // Page body (leave top-right corner for fold)
                        r.FillRect(R(0f, 0f, 1f - fold, 1f), pageCol);
                        r.FillRect(R(1f - fold, fold, fold, 1f - fold), pageCol);
                        r.DrawRect(R(0f, 0f, 1f, 1f), foldCol);

                        // Folded corner triangle: three lines forming the crease
                        r.FillRect(R(1f - fold, 0f, fold, fold), foldCol);
                        r.DrawLine(new PointF(X(1f - fold), Y(0f)),
                                   new PointF(X(1f - fold), Y(fold)), foldCol);
                        r.DrawLine(new PointF(X(1f - fold), Y(fold)),
                                   new PointF(X(1f), Y(fold)), foldCol);
                        // Diagonal crease line
                        r.DrawLine(new PointF(X(1f - fold), Y(0f)),
                                   new PointF(X(1f), Y(fold)),
                                   Color.FromArgb(255, 140, 160, 185));

                        // Code lines (green, varying widths to look like code)
                        float lx = 0.12f, lh = 0.07f, gap = 0.13f;
                        r.FillRect(R(lx, 0.22f, 0.55f, lh), lineCol);
                        r.FillRect(R(lx, 0.22f + gap, 0.72f, lh), lineCol2);
                        r.FillRect(R(lx + 0.1f, 0.22f + gap * 2f, 0.45f, lh), lineCol);
                        r.FillRect(R(lx, 0.22f + gap * 3f, 0.62f, lh), lineCol2);
                        r.FillRect(R(lx, 0.22f + gap * 4f, 0.38f, lh), lineCol);
                        break;
                    }

                // ── Texture ───────────────────────────────────────────────────
                // Checkerboard background (grey squares) + mountain silhouette + sun
                case AssetType.Texture:
                    {
                        // Checkerboard: 4×4 cells
                        var c1 = Color.FromArgb(255, 90, 90, 95);
                        var c2 = Color.FromArgb(255, 60, 60, 65);
                        int cells = 4;
                        for (int cx = 0; cx < cells; cx++)
                            for (int cy = 0; cy < cells; cy++)
                            {
                                bool dark = (cx + cy) % 2 == 0;
                                r.FillRect(new RectangleF(
                                    x + w * cx / cells, y + h * cy / cells,
                                    w / cells + 1f, h / cells + 1f),
                                    dark ? c1 : c2);
                            }

                        // Sky gradient strip (top third)
                        r.FillRect(R(0f, 0f, 1f, 0.40f), Color.FromArgb(200, 70, 130, 210));

                        // Sun (small bright square top-right)
                        r.FillRect(R(0.62f, 0.06f, 0.22f, 0.22f), Color.FromArgb(255, 255, 225, 60));
                        r.DrawRect(R(0.62f, 0.06f, 0.22f, 0.22f), Color.FromArgb(200, 230, 190, 0));

                        // Mountain silhouette (two triangles made of stacked rects)
                        var mtnCol = Color.FromArgb(255, 55, 110, 65);
                        var mtnDark = Color.FromArgb(255, 35, 80, 45);
                        // Left mountain
                        for (int mi = 0; mi < 8; mi++)
                        {
                            float mf = mi / 8f;
                            float mw = (0.5f - mf * 0.5f);
                            r.FillRect(new RectangleF(
                                x + w * (0.0f + mf * 0.25f), y + h * (0.38f + mf * 0.30f),
                                w * mw, h * 0.08f), mi < 4 ? mtnCol : mtnDark);
                        }
                        // Right mountain (smaller)
                        for (int mi = 0; mi < 6; mi++)
                        {
                            float mf = mi / 6f;
                            float mw = (0.38f - mf * 0.38f);
                            r.FillRect(new RectangleF(
                                x + w * (0.55f + mf * 0.20f), y + h * (0.50f + mf * 0.25f),
                                w * mw, h * 0.08f), mtnCol);
                        }

                        // Ground strip at bottom
                        r.FillRect(R(0f, 0.82f, 1f, 0.18f), Color.FromArgb(200, 40, 90, 45));
                        r.DrawRect(R(0f, 0f, 1f, 1f), Color.FromArgb(100, 255, 255, 255));
                        break;
                    }

                // ── Model (3D) ────────────────────────────────────────────────
                // Isometric cube: three visible faces with different shades
                case AssetType.Model:
                    {
                        var top = Color.FromArgb(255, 180, 150, 255);
                        var left = Color.FromArgb(255, 100, 75, 180);
                        var right = Color.FromArgb(255, 130, 100, 220);
                        var edge = Color.FromArgb(255, 60, 45, 130);

                        // Top face (parallelogram via stacked rects)
                        for (int row = 0; row < 6; row++)
                        {
                            float rf = row / 6f;
                            float off = rf * 0.25f;
                            r.FillRect(new RectangleF(
                                x + w * (0.25f + off), y + h * (0.08f + rf * 0.14f),
                                w * 0.50f, h * 0.06f), top);
                        }
                        // Left face
                        for (int col = 0; col < 6; col++)
                        {
                            float cf = col / 6f;
                            float off = cf * 0.14f;
                            r.FillRect(new RectangleF(
                                x + w * (0.08f + cf * 0.17f), y + h * (0.38f + off),
                                w * 0.17f, h * 0.50f), left);
                        }
                        // Right face
                        r.FillRect(R(0.50f, 0.38f, 0.42f, 0.50f), right);
                        // Edge outlines
                        r.DrawRect(R(0.50f, 0.38f, 0.42f, 0.50f), edge);
                        // Top outline lines
                        r.DrawLine(new PointF(X(0.25f), Y(0.22f)), new PointF(X(0.50f), Y(0.08f)), edge);
                        r.DrawLine(new PointF(X(0.50f), Y(0.08f)), new PointF(X(0.92f), Y(0.22f)), edge);
                        r.DrawLine(new PointF(X(0.25f), Y(0.22f)), new PointF(X(0.50f), Y(0.38f)), edge);
                        r.DrawLine(new PointF(X(0.08f), Y(0.38f)), new PointF(X(0.08f), Y(0.88f)), edge);
                        break;
                    }

                // ── Material ──────────────────────────────────────────────────
                // Sphere: concentric oval rings + bright specular highlight
                case AssetType.Material:
                    {
                        var colors = new[]
                        {
                        Color.FromArgb(255, 30,  60, 180),   // deep blue core
                        Color.FromArgb(255, 40,  80, 210),
                        Color.FromArgb(255, 55, 105, 230),
                        Color.FromArgb(255, 70, 130, 245),
                        Color.FromArgb(255, 90, 155, 255),
                        Color.FromArgb(255, 110,175, 255),
                        Color.FromArgb(255, 135,195, 255),   // bright edge
                    };

                        // Draw sphere as concentric filled ellipses (stacked rects approximation)
                        int rings = colors.Length;
                        for (int ri = 0; ri < rings; ri++)
                        {
                            float t2 = (float)ri / rings;
                            float rad = 0.5f - t2 * 0.5f;
                            float cx2 = 0.5f - rad;
                            float cy2 = 0.5f - rad * (h / w);  // aspect correct
                            r.FillRect(new RectangleF(
                                x + w * cx2, y + h * cy2,
                                w * rad * 2f, h * rad * 2f * (h / w)),
                                colors[rings - 1 - ri]);
                        }

                        // Specular highlight: small bright white square top-left of sphere
                        r.FillRect(R(0.22f, 0.14f, 0.22f, 0.16f), Color.FromArgb(200, 255, 255, 255));
                        r.FillRect(R(0.25f, 0.17f, 0.14f, 0.10f), Color.FromArgb(240, 255, 255, 255));

                        // Rim line
                        r.DrawRect(new RectangleF(x + w * 0.02f, y + h * 0.02f,
                            w * 0.96f, h * 0.96f),
                            Color.FromArgb(60, 255, 255, 255));
                        break;
                    }

                // ── Shader ────────────────────────────────────────────────────
                // Diamond / rhombus shape with cyan gradient fill + grid lines
                case AssetType.Shader:
                    {
                        var col1 = Color.FromArgb(255, 20, 190, 220);
                        var col2 = Color.FromArgb(255, 10, 130, 160);
                        var edge = Color.FromArgb(255, 0, 220, 255);

                        // Diamond via stacked horizontal bars
                        int bars = 12;
                        for (int bi = 0; bi < bars; bi++)
                        {
                            float bf = bi / (float)bars;
                            float bfw = bi <= bars / 2
                                ? bf * 2f
                                : (1f - bf) * 2f;
                            float bx2 = 0.5f - bfw * 0.5f;
                            var bar = new RectangleF(
                                x + w * bx2,
                                y + h * (bf + 0.5f / bars),
                                w * bfw, h / bars);
                            r.FillRect(bar, bi <= bars / 2 ? col1 : col2);
                        }

                        // Grid lines across diamond
                        for (int gl = 1; gl < 4; gl++)
                        {
                            float gf = gl / 4f;
                            float gw = (gf < 0.5f ? gf : 1f - gf) * 2f * 0.9f;
                            r.DrawLine(
                                new PointF(x + w * (0.5f - gw / 2f), y + h * gf),
                                new PointF(x + w * (0.5f + gw / 2f), y + h * gf),
                                Color.FromArgb(80, 0, 220, 255));
                        }

                        // Outline — four edges of the diamond
                        r.DrawLine(new PointF(X(0.5f), Y(0.02f)), new PointF(X(0.98f), Y(0.5f)), edge);
                        r.DrawLine(new PointF(X(0.98f), Y(0.5f)), new PointF(X(0.5f), Y(0.98f)), edge);
                        r.DrawLine(new PointF(X(0.5f), Y(0.98f)), new PointF(X(0.02f), Y(0.5f)), edge);
                        r.DrawLine(new PointF(X(0.02f), Y(0.5f)), new PointF(X(0.5f), Y(0.02f)), edge);
                        break;
                    }

                // ── Scene ─────────────────────────────────────────────────────
                // Globe: circle approximated with stacked rects + lat/lon grid lines
                case AssetType.Scene:
                    {
                        var globeBg = Color.FromArgb(255, 20, 60, 140);
                        var land = Color.FromArgb(255, 50, 130, 60);
                        var gridCol = Color.FromArgb(120, 100, 180, 255);
                        var edgeCol = Color.FromArgb(255, 60, 120, 220);

                        // Globe body (circle via stacked rects)
                        int gr = 14;
                        for (int ri = 0; ri < gr; ri++)
                        {
                            float rf = ri / (float)gr;
                            float rh2 = rf < 0.5f ? rf * 2f : (1f - rf) * 2f;
                            float rw2 = MathF.Sqrt(MathF.Max(0, rh2 * (2f - rh2)));
                            float ry = rf;
                            r.FillRect(new RectangleF(
                                x + w * (0.5f - rw2 * 0.48f),
                                y + h * (ry + 0.5f / gr),
                                w * rw2 * 0.96f, h / gr), globeBg);
                        }

                        // Land patches (small green rects scattered on globe)
                        r.FillRect(R(0.20f, 0.30f, 0.22f, 0.18f), land);
                        r.FillRect(R(0.52f, 0.24f, 0.28f, 0.22f), land);
                        r.FillRect(R(0.30f, 0.55f, 0.18f, 0.15f), land);
                        r.FillRect(R(0.55f, 0.58f, 0.20f, 0.12f), land);

                        // Latitude lines (3 horizontal)
                        foreach (float lat in new[] { 0.28f, 0.50f, 0.72f })
                        {
                            float lf = lat < 0.5f ? lat * 2f : (1f - lat) * 2f;
                            float lw2 = MathF.Sqrt(MathF.Max(0, lf * (2f - lf))) * 0.48f;
                            r.DrawLine(new PointF(x + w * (0.5f - lw2), y + h * lat),
                                       new PointF(x + w * (0.5f + lw2), y + h * lat),
                                       gridCol);
                        }
                        // Longitude lines (2 vertical, curved approximated)
                        r.DrawLine(new PointF(X(0.50f), Y(0.03f)), new PointF(X(0.50f), Y(0.97f)), gridCol);
                        r.DrawLine(new PointF(X(0.26f), Y(0.10f)), new PointF(X(0.26f), Y(0.90f)), gridCol);
                        r.DrawLine(new PointF(X(0.74f), Y(0.10f)), new PointF(X(0.74f), Y(0.90f)), gridCol);

                        // Outline
                        for (int ri = 0; ri < gr; ri++)
                        {
                            float rf = ri / (float)gr;
                            float rh2 = rf < 0.5f ? rf * 2f : (1f - rf) * 2f;
                            float rw2 = MathF.Sqrt(MathF.Max(0, rh2 * (2f - rh2)));
                            float ry = rf;
                            r.DrawRect(new RectangleF(
                                x + w * (0.5f - rw2 * 0.48f),
                                y + h * (ry + 0.5f / gr),
                                w * rw2 * 0.96f, h / gr), edgeCol);
                        }
                        break;
                    }

                // ── Prefab ────────────────────────────────────────────────────
                // Cyan hexagon outline with a small inner circle
                case AssetType.Prefab:
                    {
                        var hexCol = Color.FromArgb(255, 80, 200, 255);
                        var fillCol = Color.FromArgb(255, 20, 80, 140);
                        var dotCol = Color.FromArgb(255, 140, 230, 255);

                        // Hexagon: 6 rows of varying width
                        float[] widths = { 0.50f, 0.80f, 1.00f, 1.00f, 0.80f, 0.50f };
                        float[] tops = { 0.00f, 0.17f, 0.33f, 0.50f, 0.67f, 0.83f };
                        for (int hi = 0; hi < 6; hi++)
                        {
                            float hw = widths[hi];
                            r.FillRect(new RectangleF(
                                x + w * (0.5f - hw / 2f * 0.9f),
                                y + h * (tops[hi] + 0.02f),
                                w * hw * 0.9f, h * 0.18f), fillCol);
                        }
                        // Outline: edges of hexagon
                        PointF[] hex = new PointF[6];
                        for (int hi = 0; hi < 6; hi++)
                        {
                            float ang = MathF.PI / 180f * (60f * hi - 30f);
                            hex[hi] = new PointF(
                                x + w * 0.5f + w * 0.44f * MathF.Cos(ang),
                                y + h * 0.5f + h * 0.44f * MathF.Sin(ang));
                        }
                        for (int hi = 0; hi < 6; hi++)
                            r.DrawLine(hex[hi], hex[(hi + 1) % 6], hexCol);

                        // Inner dot
                        r.FillRect(R(0.38f, 0.38f, 0.24f, 0.24f), dotCol);
                        r.DrawRect(R(0.38f, 0.38f, 0.24f, 0.24f), hexCol);
                        break;
                    }

                // ── Audio ─────────────────────────────────────────────────────
                // Speaker cone (trapezoid) + sound wave arcs (curved via line segments)
                case AssetType.Audio:
                    {
                        var speakerCol = Color.FromArgb(255, 210, 100, 160);
                        var waveCol = Color.FromArgb(255, 240, 140, 190);
                        var coneCol = Color.FromArgb(255, 160, 60, 120);

                        // Speaker body (rectangle left side)
                        r.FillRect(R(0.08f, 0.35f, 0.25f, 0.30f), speakerCol);
                        r.DrawRect(R(0.08f, 0.35f, 0.25f, 0.30f), coneCol);

                        // Speaker cone (trapezoid: wider on right, made of 6 rows)
                        for (int ci = 0; ci < 7; ci++)
                        {
                            float cf = ci / 7f;
                            float cw = 0.08f + cf * 0.22f;
                            float cy2 = 0.20f + cf * 0.35f;
                            r.FillRect(new RectangleF(
                                x + w * (0.30f - cw / 2f + 0.15f + cf * 0.10f),
                                y + h * cy2,
                                w * cw * 0.9f, h * 0.09f), speakerCol);
                        }

                        // Sound waves (3 arcs drawn as short line segments)
                        float[] waveR = { 0.18f, 0.26f, 0.34f };
                        foreach (float wr2 in waveR)
                        {
                            for (int wi = -2; wi <= 2; wi++)
                            {
                                float a1 = wi * 0.35f - 0.18f;
                                float a2 = (wi + 1) * 0.35f - 0.18f;
                                r.DrawLine(
                                    new PointF(x + w * (0.62f + wr2 * MathF.Sin(a1)),
                                               y + h * (0.50f - wr2 * MathF.Cos(a1) * (w / h))),
                                    new PointF(x + w * (0.62f + wr2 * MathF.Sin(a2)),
                                               y + h * (0.50f - wr2 * MathF.Cos(a2) * (w / h))),
                                    waveCol);
                            }
                        }
                        break;
                    }

                // ── Text ──────────────────────────────────────────────────────
                // Lined paper: white page, red margin line, grey text lines
                case AssetType.Text:
                    {
                        var pageCol = Color.FromArgb(255, 240, 240, 235);
                        var marginCol = Color.FromArgb(255, 220, 80, 80);
                        var lineCol = Color.FromArgb(180, 140, 140, 160);
                        var fold = Color.FromArgb(255, 200, 200, 195);

                        r.FillRect(R(0f, 0f, 1f, 1f), pageCol);
                        r.DrawRect(R(0f, 0f, 1f, 1f), fold);

                        // Red margin line
                        r.DrawLine(new PointF(X(0.22f), Y(0.05f)),
                                   new PointF(X(0.22f), Y(0.95f)), marginCol);

                        // Text lines (6 grey lines, varying widths)
                        float[] lw = { 0.65f, 0.72f, 0.58f, 0.70f, 0.50f, 0.68f };
                        for (int li = 0; li < lw.Length; li++)
                        {
                            float ly = 0.16f + li * 0.135f;
                            r.FillRect(new RectangleF(
                                X(0.26f), Y(ly), W(lw[li]), H(0.07f)), lineCol);
                        }
                        break;
                    }

                // ── Unknown ───────────────────────────────────────────────────
                default:
                    {
                        r.FillRect(rect, Color.FromArgb(255, 58, 58, 62));
                        r.DrawRect(rect, Color.FromArgb(255, 90, 90, 95));
                        // Question mark: vertical bar + dot
                        r.FillRect(R(0.38f, 0.18f, 0.24f, 0.38f), Color.FromArgb(255, 160, 160, 165));
                        r.FillRect(R(0.38f, 0.64f, 0.24f, 0.18f), Color.FromArgb(255, 160, 160, 165));
                        break;
                    }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
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

        private static Color TileBg(AssetType t) => t switch
        {
            AssetType.Folder => Color.FromArgb(255, 50, 85, 130),
            AssetType.Script => Color.FromArgb(255, 30, 80, 40),
            AssetType.Texture => Color.FromArgb(255, 50, 50, 58),
            AssetType.Model => Color.FromArgb(255, 45, 35, 95),
            AssetType.Material => Color.FromArgb(255, 15, 35, 90),
            AssetType.Shader => Color.FromArgb(255, 15, 75, 90),
            AssetType.Scene => Color.FromArgb(255, 15, 35, 80),
            AssetType.Prefab => Color.FromArgb(255, 20, 60, 90),
            AssetType.Audio => Color.FromArgb(255, 80, 30, 70),
            AssetType.Text => Color.FromArgb(255, 60, 60, 50),
            _ => Color.FromArgb(255, 45, 45, 48),
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

            if (SliderBarRect.Contains(pos) && e.Button == MouseButton.Left)
            {
                if (pos.X >= _sliderTrackX && pos.X <= _sliderTrackX + _sliderTrackW)
                { _sliderDrag = true; ApplySliderDrag(pos.X); }
                return;
            }

            var tBtn = new RectangleF(Bounds.Right - 22f, Bounds.Y + HeaderH + 3f, 18f, 16f);
            if (tBtn.Contains(pos)) { _tileView = !_tileView; ScrollOffset = 0f; return; }

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

            if (_sliderDrag) { ApplySliderDrag(pos.X); return; }

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

        private FileItem? HitTest(PointF pos)
        {
            foreach (var i in _items)
                if (i.CachedBounds.Contains(pos)) return i;
            return null;
        }

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

        private void ShowContextMenu(PointF pos, FileItem? target)
        {
            var items = new List<ContextMenuItem>
            {
                new("Create", null) { IsDisabled = true },
                new("  Folder",     () => CreateAsset("New Folder",   AssetKind.Folder)),
                new("  C# Script",  () => CreateAsset("NewScript",    AssetKind.Script)),
                new("  Scene",      () => CreateAsset("New Scene",    AssetKind.Scene)),
                new("  Material",   () => CreateAsset("New Material", AssetKind.Material)),
                new("  Shader",     () => CreateAsset("New Shader",   AssetKind.Shader)),
                new("  Plain Text", () => CreateAsset("notes",        AssetKind.Text)),
                new("  Prefab",     () => CreateAsset("New Prefab",   AssetKind.Prefab)),
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