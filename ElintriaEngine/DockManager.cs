using ElintriaEngine.UI.Panels;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace ElintriaEngine.UI
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DockNode — binary layout tree
    // ══════════════════════════════════════════════════════════════════════════
    public abstract class DockNode
    {
        public RectangleF ComputedBounds { get; protected set; }

        public abstract void Layout(RectangleF rect);
        public abstract IEnumerable<Panel> Panels();
        public abstract bool TryRemove(Panel p, out DockNode? replacement);
        public abstract bool Contains(Panel p);
        public abstract bool TryInsertBeside(Panel anchor, Panel newPanel,
                                              DockZone zone, out DockNode result);
    }

    public class LeafNode : DockNode
    {
        public Panel Panel { get; private set; }
        public LeafNode(Panel p) => Panel = p;

        public override void Layout(RectangleF rect)
        {
            ComputedBounds = rect;
            Panel.Bounds = rect;
        }
        public override IEnumerable<Panel> Panels() { yield return Panel; }
        public override bool Contains(Panel p) => Panel == p;

        public override bool TryRemove(Panel p, out DockNode? replacement)
        {
            replacement = null;
            return Panel == p;
        }

        public override bool TryInsertBeside(Panel anchor, Panel newPanel,
                                              DockZone zone, out DockNode result)
        {
            if (Panel != anchor) { result = this; return false; }

            var newLeaf = new LeafNode(newPanel);
            bool horizSplit = zone == DockZone.Left || zone == DockZone.Right;
            bool firstIsNew = zone == DockZone.Left || zone == DockZone.Top;

            result = new SplitNode(
                isHorizontal: horizSplit,
                ratio: 0.5f,
                first: firstIsNew ? (DockNode)newLeaf : this,
                second: firstIsNew ? (DockNode)this : newLeaf);
            return true;
        }
    }

    public class SplitNode : DockNode
    {
        public bool IsHorizontal { get; set; }
        public float Ratio { get; set; }
        public DockNode First { get; private set; }
        public DockNode Second { get; private set; }

        private bool _resizing;
        private float _resizeStart;

        public SplitNode(bool isHorizontal, float ratio, DockNode first, DockNode second)
        {
            IsHorizontal = isHorizontal;
            Ratio = ratio;
            First = first;
            Second = second;
        }

        public override void Layout(RectangleF rect)
        {
            ComputedBounds = rect;
            if (IsHorizontal)
            {
                float w1 = rect.Width * Ratio;
                First.Layout(new RectangleF(rect.X, rect.Y, w1, rect.Height));
                Second.Layout(new RectangleF(rect.X + w1, rect.Y, rect.Width - w1, rect.Height));
            }
            else
            {
                float h1 = rect.Height * Ratio;
                First.Layout(new RectangleF(rect.X, rect.Y, rect.Width, h1));
                Second.Layout(new RectangleF(rect.X, rect.Y + h1, rect.Width, rect.Height - h1));
            }
        }

        public override IEnumerable<Panel> Panels()
        {
            foreach (var p in First.Panels()) yield return p;
            foreach (var p in Second.Panels()) yield return p;
        }

        public override bool Contains(Panel p) => First.Contains(p) || Second.Contains(p);

        public override bool TryRemove(Panel p, out DockNode? replacement)
        {
            if (First.TryRemove(p, out var rep))
            {
                replacement = rep == null ? Second : new SplitNode(IsHorizontal, 0.5f, rep, Second);
                return true;
            }
            if (Second.TryRemove(p, out rep))
            {
                replacement = rep == null ? First : new SplitNode(IsHorizontal, 0.5f, First, rep);
                return true;
            }
            replacement = null;
            return false;
        }

        public override bool TryInsertBeside(Panel anchor, Panel newPanel,
                                              DockZone zone, out DockNode result)
        {
            if (First.TryInsertBeside(anchor, newPanel, zone, out var newFirst))
            { result = new SplitNode(IsHorizontal, Ratio, newFirst, Second); return true; }
            if (Second.TryInsertBeside(anchor, newPanel, zone, out var newSecond))
            { result = new SplitNode(IsHorizontal, Ratio, First, newSecond); return true; }
            result = this;
            return false;
        }

        public RectangleF DividerRect()
        {
            if (IsHorizontal)
            {
                float x = First.ComputedBounds.Right - 2f;
                return new RectangleF(x, ComputedBounds.Y, 5f, ComputedBounds.Height);
            }
            else
            {
                float y = First.ComputedBounds.Bottom - 2f;
                return new RectangleF(ComputedBounds.X, y, ComputedBounds.Width, 5f);
            }
        }

        public void DragDivider(float delta)
        {
            if (IsHorizontal)
                Ratio = Math.Clamp(Ratio + delta / ComputedBounds.Width, 0.1f, 0.9f);
            else
                Ratio = Math.Clamp(Ratio + delta / ComputedBounds.Height, 0.1f, 0.9f);
        }
    }

    // ── Drop zone types ───────────────────────────────────────────────────────
    public enum DockZone { Top, Bottom, Left, Right, Center }

    public struct DropTarget
    {
        public Panel AnchorPanel;
        public DockZone Zone;
        public RectangleF IconRect;
        public RectangleF PreviewRect;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  DockManager
    // ══════════════════════════════════════════════════════════════════════════
    public class DockManager
    {
        private DockNode _root;
        private RectangleF _area;

        // ── Divider drag ──────────────────────────────────────────────────────
        private SplitNode? _divDrag;
        private PointF _divStart;
        private float _divRatioStart;
        private const float DivHit = 5f;

        // ── Panel drag ────────────────────────────────────────────────────────
        private Panel? _dragging;
        private PointF _dragOffset;
        private PointF _floatPos;
        private List<DropTarget> _dropTargets = new();
        private DropTarget? _hovered;
        private bool _dragStarted;
        private PointF _dragDownPos;
        private const float DragThresh = 12f;

        private Panel? _floatingPanel;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color CGhostFill = Color.FromArgb(55, 60, 130, 255);
        private static readonly Color CGhostBdr = Color.FromArgb(160, 90, 160, 255);
        private static readonly Color CDivider = Color.FromArgb(255, 50, 52, 62);
        private static readonly Color CDivHov = Color.FromArgb(255, 80, 150, 255);

        public DockManager(DockNode root, RectangleF area)
        {
            _root = root;
            _area = area;
            Relayout();
        }

        public void SetArea(RectangleF area) { _area = area; Relayout(); }
        private void Relayout() => _root.Layout(_area);

        // ══════════════════════════════════════════════════════════════════════
        //  Draw overlay
        // ══════════════════════════════════════════════════════════════════════
        public void DrawOverlay(IEditorRenderer r, PointF mouse)
        {
            DrawDividers(r, _root, mouse);

            if (_dragging == null || !_dragStarted) return;

            // Dim entire dock area
            r.FillRect(_area, Color.FromArgb(55, 8, 8, 18));

            // Floating ghost panel
            float gw = Math.Min(_dragging.Bounds.Width, 300f);
            float gh = 80f;
            var ghost = new RectangleF(_floatPos.X, _floatPos.Y, gw, gh);
            r.FillRect(ghost, Color.FromArgb(100, 35, 55, 110));
            r.DrawRect(ghost, Color.FromArgb(210, 80, 145, 255), 2f);
            r.FillRect(new RectangleF(ghost.X, ghost.Y, ghost.Width, 22f),
                Color.FromArgb(160, 50, 90, 180));
            r.DrawText(_dragging.Title,
                new PointF(ghost.X + 8f, ghost.Y + 5f),
                Color.FromArgb(255, 215, 230, 255), 11f);
            r.DrawText("Drag to a zone arrow to dock",
                new PointF(ghost.X + 8f, ghost.Y + 30f),
                Color.FromArgb(160, 170, 185, 210), 9f);

            // Ghost preview for hovered zone
            if (_hovered.HasValue)
            {
                var dt = _hovered.Value;
                r.FillRect(dt.PreviewRect, CGhostFill);
                r.DrawRect(dt.PreviewRect, CGhostBdr, 2f);
                r.DrawText(dt.Zone + " →  " + _dragging.Title,
                    new PointF(dt.PreviewRect.X + 8f, dt.PreviewRect.Y + 6f),
                    Color.FromArgb(200, 190, 215, 255), 9f);
            }

            // Compass clusters per anchor panel
            var seen = new HashSet<Panel>();
            foreach (var dt in _dropTargets)
                if (seen.Add(dt.AnchorPanel))
                    DrawCompass(r, dt.AnchorPanel, mouse);
        }

        // ── Compass: 5-icon crosshair centered on panel ───────────────────────
        private void DrawCompass(IEditorRenderer r, Panel anchor, PointF mouse)
        {
            var b = anchor.Bounds;
            float ic = 32f;
            float gp = ic + 5f;
            float cx = b.X + b.Width / 2f;
            float cy = b.Y + b.Height / 2f;

            // Don't draw if panel is too small to fit the compass
            if (b.Width < ic * 3.5f || b.Height < ic * 3.5f)
            {
                // Fallback: single centre icon only
                DrawZoneBtn(r, MkRect(cx, cy, ic), DockZone.Center, mouse);
                return;
            }

            var ctr = MkRect(cx, cy, ic);
            var top = MkRect(cx, cy - gp, ic);
            var bot = MkRect(cx, cy + gp, ic);
            var lft = MkRect(cx - gp, cy, ic);
            var rgt = MkRect(cx + gp, cy, ic);

            // Connecting lines
            var lineCol = Color.FromArgb(70, 100, 155, 255);
            r.DrawLine(Ctr(ctr), Ctr(top), lineCol, 1f);
            r.DrawLine(Ctr(ctr), Ctr(bot), lineCol, 1f);
            r.DrawLine(Ctr(ctr), Ctr(lft), lineCol, 1f);
            r.DrawLine(Ctr(ctr), Ctr(rgt), lineCol, 1f);

            DrawZoneBtn(r, top, DockZone.Top, mouse);
            DrawZoneBtn(r, bot, DockZone.Bottom, mouse);
            DrawZoneBtn(r, lft, DockZone.Left, mouse);
            DrawZoneBtn(r, rgt, DockZone.Right, mouse);
            DrawZoneBtn(r, ctr, DockZone.Center, mouse);
        }

        private static RectangleF MkRect(float cx, float cy, float sz) =>
            new RectangleF(cx - sz / 2f, cy - sz / 2f, sz, sz);

        private static PointF Ctr(RectangleF r) =>
            new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);

        private void DrawZoneBtn(IEditorRenderer r, RectangleF rect, DockZone zone, PointF mouse)
        {
            bool hov = rect.Contains(mouse);

            var fill = hov
                ? Color.FromArgb(235, 70, 145, 255)
                : Color.FromArgb(195, 32, 55, 120);
            var bdr = hov
                ? Color.FromArgb(255, 130, 200, 255)
                : Color.FromArgb(210, 70, 115, 210);

            r.FillRect(rect, fill);
            r.DrawRect(rect, bdr, hov ? 2f : 1f);

            string arrow = zone switch
            {
                DockZone.Top => "▲",
                DockZone.Bottom => "▼",
                DockZone.Left => "◄",
                DockZone.Right => "►",
                _ => "⊞",
            };
            r.DrawText(arrow,
                new PointF(rect.X + (rect.Width - 10f) / 2f, rect.Y + (rect.Height - 11f) / 2f),
                hov ? Color.White : Color.FromArgb(220, 200, 220, 255), 10f);
        }

        // ── Dividers ──────────────────────────────────────────────────────────
        private void DrawDividers(IEditorRenderer r, DockNode node, PointF mouse)
        {
            if (node is not SplitNode split) return;
            var div = split.DividerRect();
            bool hov = div.Contains(mouse) || _divDrag == split;
            r.FillRect(div, hov ? CDivHov : CDivider);
            DrawDividers(r, split.First, mouse);
            DrawDividers(r, split.Second, mouse);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Mouse input
        // ══════════════════════════════════════════════════════════════════════
        public bool OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (e.Button != MouseButton.Left) return false;

            if (HitDivider(_root, pos, out var hit))
            {
                _divDrag = hit;
                _divStart = pos;
                _divRatioStart = hit!.Ratio;
                return true;
            }

            foreach (var p in _root.Panels())
            {
                var hdr = new RectangleF(p.Bounds.X, p.Bounds.Y, p.Bounds.Width, 22f);
                if (hdr.Contains(pos))
                {
                    _dragging = p;
                    _dragDownPos = pos;
                    _dragOffset = new PointF(pos.X - p.Bounds.X, pos.Y - p.Bounds.Y);
                    _dragStarted = false;
                    return false;
                }
            }
            return false;
        }

        public void OnMouseMove(PointF pos)
        {
            // Divider drag
            if (_divDrag != null)
            {
                float delta = _divDrag.IsHorizontal
                    ? pos.X - _divStart.X
                    : pos.Y - _divStart.Y;
                _divDrag.Ratio = _divRatioStart + delta /
                    (_divDrag.IsHorizontal ? _divDrag.ComputedBounds.Width
                                           : _divDrag.ComputedBounds.Height);
                _divDrag.Ratio = Math.Clamp(_divDrag.Ratio, 0.08f, 0.92f);
                Relayout();
                return;
            }

            if (_dragging == null) return;

            if (!_dragStarted)
            {
                float dx = pos.X - _dragDownPos.X;
                float dy = pos.Y - _dragDownPos.Y;
                if (MathF.Sqrt(dx * dx + dy * dy) < DragThresh) return;

                _dragStarted = true;
                _floatingPanel = _dragging;
                if (_root.TryRemove(_dragging, out var rep))
                    _root = rep ?? new LeafNode(_dragging);
                Relayout();
                BuildDropTargets();
            }

            _floatPos = new PointF(pos.X - _dragOffset.X, pos.Y - _dragOffset.Y);

            // Hit-test compass icon rects
            _hovered = null;
            float ic = 32f;
            float gp = ic + 5f;
            foreach (var panel in _root.Panels())
            {
                var b = panel.Bounds;
                float cx = b.X + b.Width / 2f;
                float cy = b.Y + b.Height / 2f;

                bool big = b.Width >= ic * 3.5f && b.Height >= ic * 3.5f;
                var zones = big
                    ? new[] {
                        (DockZone.Center, MkRect(cx,      cy,      ic)),
                        (DockZone.Top,    MkRect(cx,      cy - gp, ic)),
                        (DockZone.Bottom, MkRect(cx,      cy + gp, ic)),
                        (DockZone.Left,   MkRect(cx - gp, cy,      ic)),
                        (DockZone.Right,  MkRect(cx + gp, cy,      ic)),
                      }
                    : new[] { (DockZone.Center, MkRect(cx, cy, ic)) };

                foreach (var (zone, rect) in zones)
                {
                    if (rect.Contains(pos))
                    {
                        _hovered = FindDropTarget(panel, zone);
                        goto doneHit;
                    }
                }
            }
        doneHit:;
        }

        private DropTarget? FindDropTarget(Panel anchor, DockZone zone)
        {
            foreach (var dt in _dropTargets)
                if (dt.AnchorPanel == anchor && dt.Zone == zone) return dt;
            return null;
        }

        public bool OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            if (_divDrag != null) { _divDrag = null; return true; }

            if (_dragging == null) return false;

            bool wasDragging = _dragStarted;
            var panel = _dragging;
            _dragging = null;
            _dragStarted = false;

            if (!wasDragging) return false;

            if (_hovered.HasValue)
            {
                var dt = _hovered.Value;
                _hovered = null;
                _dropTargets.Clear();

                if (_root.TryInsertBeside(dt.AnchorPanel, panel, dt.Zone, out var newRoot))
                    _root = newRoot;
                else
                    _root = new SplitNode(
                        dt.Zone == DockZone.Left || dt.Zone == DockZone.Right,
                        0.5f,
                        dt.Zone == DockZone.Left || dt.Zone == DockZone.Top
                            ? (DockNode)new LeafNode(panel)
                            : new LeafNode(dt.AnchorPanel),
                        dt.Zone == DockZone.Left || dt.Zone == DockZone.Top
                            ? (DockNode)new LeafNode(dt.AnchorPanel)
                            : new LeafNode(panel));
            }
            else
            {
                // Re-insert on the right edge if dropped outside any zone
                var anyLeaf = FirstLeaf(_root);
                if (anyLeaf != null)
                {
                    if (_root.TryInsertBeside(anyLeaf, panel, DockZone.Right, out var newRoot))
                        _root = newRoot;
                    else
                        _root = new SplitNode(true, 0.75f, _root, new LeafNode(panel));
                }
                else
                    _root = new LeafNode(panel);
            }

            _dropTargets.Clear();
            _floatingPanel = null;
            Relayout();
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Drop targets
        // ══════════════════════════════════════════════════════════════════════
        private void BuildDropTargets()
        {
            _dropTargets.Clear();
            foreach (var panel in _root.Panels())
            {
                if (panel == _dragging) continue;
                AddZones(panel);
            }
        }

        private void AddZones(Panel panel)
        {
            var b = panel.Bounds;
            float ic = 32f;
            float gp = ic + 5f;
            float cx = b.X + b.Width / 2f;
            float cy = b.Y + b.Height / 2f;

            bool big = b.Width >= ic * 3.5f && b.Height >= ic * 3.5f;

            AddZone(panel, DockZone.Center, MkRect(cx, cy, ic), b);
            if (big)
            {
                AddZone(panel, DockZone.Top, MkRect(cx, cy - gp, ic),
                    new RectangleF(b.X, b.Y, b.Width, b.Height * 0.5f));
                AddZone(panel, DockZone.Bottom, MkRect(cx, cy + gp, ic),
                    new RectangleF(b.X, b.Y + b.Height * 0.5f, b.Width, b.Height * 0.5f));
                AddZone(panel, DockZone.Left, MkRect(cx - gp, cy, ic),
                    new RectangleF(b.X, b.Y, b.Width * 0.5f, b.Height));
                AddZone(panel, DockZone.Right, MkRect(cx + gp, cy, ic),
                    new RectangleF(b.X + b.Width * 0.5f, b.Y, b.Width * 0.5f, b.Height));
            }
        }

        private void AddZone(Panel panel, DockZone zone, RectangleF icon, RectangleF preview)
        {
            _dropTargets.Add(new DropTarget
            {
                AnchorPanel = panel,
                Zone = zone,
                IconRect = icon,
                PreviewRect = preview,
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Utility
        // ══════════════════════════════════════════════════════════════════════
        private bool HitDivider(DockNode node, PointF pos, out SplitNode? hit)
        {
            if (node is SplitNode split)
            {
                if (split.DividerRect().Contains(pos)) { hit = split; return true; }
                if (HitDivider(split.First, pos, out hit)) return true;
                if (HitDivider(split.Second, pos, out hit)) return true;
            }
            hit = null;
            return false;
        }

        private static Panel? FirstLeaf(DockNode node) => node switch
        {
            LeafNode leaf => leaf.Panel,
            SplitNode spl => FirstLeaf(spl.First),
            _ => null,
        };

        public IEnumerable<Panel> AllPanels() => _root.Panels();
        public bool IsDragging => _dragging != null && _dragStarted;

        /// <summary>
        /// Insert a panel into the dock tree next to an anchor panel.
        /// zone: 0=left,1=right,2=top,3=bottom of anchor.
        /// </summary>
        public void AddPanel(Panel panel, Panel anchor, DockZone zone)
        {
            if (_root.TryInsertBeside(anchor, panel, zone, out var newRoot))
                _root = newRoot;
            else
                // Fallback: split root horizontally
                _root = new SplitNode(true, 0.5f, _root, new LeafNode(panel));
            panel.Locked = false;
            Relayout();
        }

        /// <summary>Remove a panel from the dock tree (e.g. when a floating panel is hidden).</summary>
        public bool RemovePanel(Panel panel)
        {
            if (!_root.Contains(panel)) return false;
            if (_root.TryRemove(panel, out var rep))
                _root = rep ?? new LeafNode(panel); // keep something valid
            Relayout();
            return true;
        }

        public bool ContainsPanel(Panel panel) => _root.Contains(panel);
    }
}