using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Elintria.Editor.UI
{
    // =========================================================================
    //  ContextMenuItem
    // =========================================================================
    public class ContextMenuItem
    {
        public string Label { get; set; }
        public Action Action { get; set; }
        public bool IsSep { get; set; }
        public bool Disabled { get; set; }
        public List<ContextMenuItem> Sub { get; set; }
        public bool HasSub => Sub != null && Sub.Count > 0;

        // ── Factory helpers ───────────────────────────────────────────────
        public static ContextMenuItem Sep()
            => new ContextMenuItem { IsSep = true };

        public static ContextMenuItem Item(string label, Action action, bool disabled = false)
            => new ContextMenuItem { Label = label, Action = action, Disabled = disabled };

        // Named "SubMenu" so call-sites are unambiguous.
        public static ContextMenuItem SubMenu(string label, List<ContextMenuItem> children)
            => new ContextMenuItem { Label = label, Sub = children };
    }

    // =========================================================================
    //  ContextMenuLevel  — one popup panel (root or sub-menu)
    // =========================================================================
    //
    //  Design rules
    //  ------------
    //  • This class knows nothing about the Panel hierarchy.
    //    Position is always in absolute screen space.
    //  • HotIndex tracks which row the mouse is over.
    //    It is updated every Update() call and ONLY changes when the mouse is
    //    actually inside THIS panel — so moving into a sub-menu never clears
    //    the hot item on the parent.
    //  • A sub-menu opens the moment HotIndex lands on a HasSub item.
    //    It stays open until the user hovers a DIFFERENT item on this panel.
    //  • Closing is the Manager's job; this class just calls OnActionFired
    //    when a terminal item is clicked.
    //
    // =========================================================================
    internal class ContextMenuLevel
    {
        // ── Colours ───────────────────────────────────────────────────────
        static readonly Color C_Bg = Color.FromArgb(255, 50, 50, 55);
        static readonly Color C_Border = Color.FromArgb(255, 22, 22, 25);
        static readonly Color C_Shadow = Color.FromArgb(85, 0, 0, 0);
        static readonly Color C_Hover = Color.FromArgb(255, 44, 93, 180);
        static readonly Color C_Text = Color.FromArgb(255, 210, 210, 215);
        static readonly Color C_Disabled = Color.FromArgb(110, 130, 130, 135);
        static readonly Color C_Sep = Color.FromArgb(90, 90, 90, 100);
        static readonly Color C_Arrow = Color.FromArgb(160, 170, 170, 180);

        const float ITEM_H = 22f;
        const float SEP_H = 7f;
        const float PAD_L = 22f;   // left indent — room for future icons
        const float PAD_R = 20f;   // right indent — room for ▶ arrow
        const float MIN_W = 180f;

        // ── State ─────────────────────────────────────────────────────────
        public Vector2 Pos { get; private set; }
        public Vector2 Size { get; private set; }

        private readonly List<ContextMenuItem> _items;
        private readonly BitmapFont _font;

        // Which item index is currently highlighted (-1 = none).
        private int _hotIdx = -1;

        // The open sub-menu level, if any.
        private ContextMenuLevel _sub;

        // Called when any terminal action anywhere in this tree fires.
        // The Manager wires this to ContextMenuManager.Close().
        public Action OnActionFired;

        // ── Constructor ───────────────────────────────────────────────────
        public ContextMenuLevel(List<ContextMenuItem> items, BitmapFont font, Vector2 requestedPos)
        {
            _items = items;
            _font = font;

            // Measure width from labels
            float w = MIN_W;
            foreach (var it in items)
            {
                if (it.IsSep || it.Label == null) continue;
                float tw = (_font?.MeasureText(it.Label) ?? 120f) + PAD_L + PAD_R + 4f;
                if (tw > w) w = tw;
            }

            // Measure height
            float h = 6f; // top + bottom padding
            foreach (var it in items) h += it.IsSep ? SEP_H : ITEM_H;

            Size = new Vector2(w, h);

            // Clamp so the menu stays fully on screen
            Vector2 screen = ContextMenuManager.ScreenSize;
            float x = MathF.Max(2f, MathF.Min(requestedPos.X, screen.X - w - 2f));
            float y = MathF.Max(2f, MathF.Min(requestedPos.Y, screen.Y - h - 2f));
            Pos = new Vector2(x, y);
        }

        // ── Update ────────────────────────────────────────────────────────
        // Called every frame with the current mouse position.
        public void Update(Vector2 mp)
        {
            // Recurse into the open sub-menu first so it can update itself.
            _sub?.Update(mp);

            // Only change HotIndex when the mouse is over THIS panel.
            if (!HitTest(mp)) return;

            int newHot = GetItemAt(mp);

            if (newHot == _hotIdx) return;   // no change — nothing to do

            // Mouse moved to a different item inside this panel.
            _hotIdx = newHot;

            // If there was an open sub-menu and we moved away from its owner, close it.
            if (_sub != null && _hotIdx != GetSubOwner())
                CloseSub();

            // Open a new sub-menu if we landed on a HasSub item.
            if (_hotIdx >= 0 && _items[_hotIdx].HasSub && _sub == null)
                OpenSub(_hotIdx);
        }

        // ── Draw ──────────────────────────────────────────────────────────
        public void Draw()
        {
            float ax = Pos.X, ay = Pos.Y, w = Size.X;

            // Shadow
            UIRenderer.DrawRect(ax + 4f, ay + 4f, w, Size.Y, C_Shadow);
            // Body
            UIRenderer.DrawRect(ax, ay, w, Size.Y, C_Bg);
            // Border
            UIRenderer.DrawRectOutline(ax, ay, w, Size.Y, C_Border);

            float y = ay + 4f;

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];

                if (it.IsSep)
                {
                    UIRenderer.DrawRect(ax + 4f, y + SEP_H * 0.5f - 0.5f,
                                        w - 8f, 1f, C_Sep);
                    y += SEP_H;
                    continue;
                }

                bool hot = (i == _hotIdx);

                // Highlight row
                if (hot)
                    UIRenderer.DrawRect(ax + 2f, y, w - 4f, ITEM_H, C_Hover);

                if (_font != null)
                {
                    float ty = y + (ITEM_H - _font.LineH) * 0.5f;
                    Color tc = it.Disabled ? C_Disabled : C_Text;

                    _font.DrawText(it.Label ?? "", ax + PAD_L, ty, tc);

                    if (it.HasSub)
                        _font.DrawText("›", ax + w - PAD_R + 2f, ty,
                            hot ? C_Text : C_Arrow);
                }

                y += ITEM_H;
            }

            // Draw sub-menu on top of this panel
            _sub?.Draw();
        }

        // ── HandleClick ───────────────────────────────────────────────────
        // Call only when the mouse button was released.
        // Returns true = event consumed (either by sub or this level).
        public bool HandleClick(Vector2 mp)
        {
            // Sub-menu gets first crack
            if (_sub != null && _sub.HandleClick(mp)) return true;

            // Not over this panel
            if (!HitTest(mp)) return false;

            // Over the panel but not on a clickable item
            int idx = GetItemAt(mp);
            if (idx < 0) return true;  // absorb — click landed inside panel body

            var it = _items[idx];
            if (it.IsSep || it.Disabled) return true;
            if (it.HasSub) return true;  // sub-menus open on hover

            // Execute the action, then tell the Manager to close everything.
            it.Action?.Invoke();
            OnActionFired?.Invoke();
            return true;
        }

        // ── IsOverTree ────────────────────────────────────────────────────
        // True if mp is inside this panel OR any open sub-panel.
        public bool IsOverTree(Vector2 mp)
            => HitTest(mp) || (_sub != null && _sub.IsOverTree(mp));

        // ── Private helpers ───────────────────────────────────────────────
        private bool HitTest(Vector2 mp)
            => mp.X >= Pos.X && mp.X < Pos.X + Size.X
            && mp.Y >= Pos.Y && mp.Y < Pos.Y + Size.Y;

        // Returns the item index under mp, or -1 (separator rows don't count).
        private int GetItemAt(Vector2 mp)
        {
            if (!HitTest(mp)) return -1;
            float y = Pos.Y + 4f;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].IsSep) { y += SEP_H; continue; }
                if (mp.Y >= y && mp.Y < y + ITEM_H) return i;
                y += ITEM_H;
            }
            return -1;
        }

        // Returns the index of the item that owns the currently open sub-menu.
        private int GetSubOwner()
        {
            if (_sub == null) return -1;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].HasSub) return i;   // only one sub open at a time
            return -1;
        }

        // Returns the Y position of the top edge of item[idx] in screen coords.
        private float ItemScreenY(int idx)
        {
            float y = Pos.Y + 4f;
            for (int i = 0; i < _items.Count; i++)
            {
                if (i == idx) return y;
                y += _items[i].IsSep ? SEP_H : ITEM_H;
            }
            return y;
        }

        private void OpenSub(int idx)
        {
            float sy = ItemScreenY(idx);
            float sx = Pos.X + Size.X - 2f;   // try right side

            // If going right would clip off screen, go left instead
            var screen = ContextMenuManager.ScreenSize;
            float subW = MIN_W;  // worst-case estimate; constructor will clamp anyway
            if (sx + subW > screen.X)
                sx = Pos.X - subW + 2f;

            _sub = new ContextMenuLevel(_items[idx].Sub, _font, new Vector2(sx, sy - 4f));
            _sub.OnActionFired = () => OnActionFired?.Invoke();
        }

        private void CloseSub()
        {
            _sub = null;
        }
    }

    // =========================================================================
    //  ContextMenuManager  — global singleton
    // =========================================================================
    //
    //  Usage from Editor
    //  -----------------
    //  Open / close:
    //      ContextMenuManager.Open(items, screenPos);
    //      ContextMenuManager.Close();
    //
    //  Every frame:
    //      ContextMenuManager.Update(dt);   // drives hover / sub-menus
    //      ContextMenuManager.Draw();       // called AFTER all panels, topmost layer
    //
    //  Input (call BEFORE panel dispatch):
    //      bool consumed = ContextMenuManager.HandleMouseDown(e);
    //      // if consumed == false the click fell outside; dispatch normally to panels
    //      ContextMenuManager.HandleKeyDown(e);   // Escape closes
    //
    // =========================================================================
    public static class ContextMenuManager
    {
        private static ContextMenuLevel _root;
        private static BitmapFont _font;
        private static Action _closeCallback;

        public static bool IsOpen => _root != null;
        public static Vector2 ScreenSize { get; set; } = new Vector2(1280, 800);

        public static void Init(BitmapFont font) => _font = font;

        // ── Open ──────────────────────────────────────────────────────────
        // closeCallback is optional; fired when the menu closes for any reason.
        public static void Open(List<ContextMenuItem> items, Vector2 screenPos,
                                Action closeCallback = null)
        {
            _root = new ContextMenuLevel(items, _font, screenPos);
            _root.OnActionFired = Close;
            _closeCallback = closeCallback;
        }

        // ── Close ─────────────────────────────────────────────────────────
        public static void Close()
        {
            if (_root == null) return;
            _root = null;
            var cb = _closeCallback;
            _closeCallback = null;
            cb?.Invoke();
        }

        // ── Per-frame update ──────────────────────────────────────────────
        public static void Update(float dt)
            => _root?.Update(Panel.DispatchMousePos);

        // ── Draw (call last — always on top) ──────────────────────────────
        public static void Draw()
            => _root?.Draw();

        // ── HandleMouseDown ───────────────────────────────────────────────
        // Call this BEFORE dispatching to panels.
        //
        // Returns true  → click was inside the menu tree; event consumed.
        // Returns false → click was outside; menu closed; caller dispatches normally.
        public static bool HandleMouseDown(MouseButtonEventArgs e)
        {
            if (_root == null) return false;

            Vector2 mp = Panel.DispatchMousePos;

            // Any button click outside the tree → close, don't consume.
            if (!_root.IsOverTree(mp))
            {
                Close();
                return false;
            }

            // Left-click inside → let the menu act on it.
            if (e.Button == MouseButton.Left)
            {
                _root.HandleClick(mp);
                return true;
            }

            // Right-click inside → absorb (don't open another menu on top).
            return true;
        }

        // ── HandleKeyDown ─────────────────────────────────────────────────
        public static bool HandleKeyDown(KeyboardKeyEventArgs e)
        {
            if (_root == null) return false;
            if (e.Key == Keys.Escape) { Close(); return true; }
            return false;
        }
    }
}