 using ElintriaEngineC.WindowCreation;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using System.Collections.Generic;
using System.Drawing;

// =============================================================================
//  Panel  —  base of every 2-D UI widget
// =============================================================================
//
//  COORDINATE SYSTEM
//  -----------------
//  Position is RELATIVE to parent (absolute if parent == null).
//  GetAbsolutePosition() walks the chain → screen-space top-left.
//  All Draw / hit-test code works in absolute screen coords.
//
//  INPUT ROUTING
//  -------------
//  Panel.DispatchMousePos is set by Editor at the very top of every
//  OnMouseMove, OnMouseDown, OnMouseUp — before any other code runs.
//  Every panel reads this one value.  Nobody touches MouseState directly.
//
//  HandleMouseDown  – children back-to-front, first consumer wins, returns bool.
//  HandleMouseUp    – broadcast to ALL children (drag-release safety), returns bool.
//  HandleMouseMove  – broadcast to all children, no return value.
//  HandleKeyDown    – routed only to the focused panel, children first.
//  HandleTextInput  – same as HandleKeyDown.
//
//  FOCUS
//  -----
//  Panel.SetFocus(p) / Panel.ClearFocus() manage a single global focused panel.
//  InputField calls SetFocus on click; keyboard/text events check IsFocused.
//
// =============================================================================

public class Panel
{
    // ── Layout ────────────────────────────────────────────────────────────
    public Vector2 Position { get; set; } = Vector2.Zero;
    public Vector2 Size { get; set; } = new Vector2(-300, -300);

    // ── Appearance ────────────────────────────────────────────────────────
    public Color BackgroundColor { get; set; } = Color.Transparent;

    // When true, children that fall entirely outside this panel's rect are
    // skipped during Draw (soft clip — no GL scissor needed).
    public bool ClipChildren { get; set; } = false;

    // ── Hierarchy ─────────────────────────────────────────────────────────
    public List<Panel> Children { get; } = new List<Panel>();
    public Panel Parent { get; private set; }

    public void AddChild(Panel child)
    {
        child.Parent?.RemoveChild(child);
        Children.Add(child);
        child.Parent = this;
    }

    public void RemoveChild(Panel child)
    {
        Children.Remove(child);
        if (child.Parent == this) child.Parent = null;
    }

    public void ClearChildren()
    {
        // iterate backwards so indices stay valid
        for (int i = Children.Count - 1; i >= 0; i--)
            RemoveChild(Children[i]);
    }

    // ── State ─────────────────────────────────────────────────────────────
    public bool Visible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool IsHovered { get; protected set; }
    public bool IsPressed { get; protected set; }

    // ── Mouse position ────────────────────────────────────────────────────
    // Set by Editor before every dispatch.  One write, everyone reads.
    public static Vector2 DispatchMousePos { get; set; }
    public Vector2 GetMousePosition() => DispatchMousePos;

    // ── Focus ─────────────────────────────────────────────────────────────
    private static Panel _focused;
    public bool IsFocused => _focused == this;

    public static void SetFocus(Panel p)
    {
        if (_focused == p) return;
        _focused?.OnLostFocus();
        _focused = p;
        _focused?.OnGotFocus();
    }

    public static void ClearFocus()
    {
        if (_focused == null) return;
        _focused.OnLostFocus();
        _focused = null;
    }

    protected virtual void OnGotFocus() { }
    protected virtual void OnLostFocus() { }

    // ── Coordinates ───────────────────────────────────────────────────────
    public virtual Vector2 GetAbsolutePosition()
        => Parent == null ? Position : Parent.GetAbsolutePosition() + Position;

    public virtual bool IsPointInside(Vector2 pt)
    {
        Vector2 a = GetAbsolutePosition();
        return pt.X >= a.X && pt.X < a.X + Size.X
            && pt.Y >= a.Y && pt.Y < a.Y + Size.Y;
    }

    // ── Draw ──────────────────────────────────────────────────────────────
    public virtual void Draw()
    {
        if (!Visible) return;

        Vector2 abs = GetAbsolutePosition();

        if (BackgroundColor.A > 0)
            UIRenderer.DrawRect(abs.X, abs.Y, Size.X, Size.Y, BackgroundColor);

        DrawChildren(abs);
    }

    // Shared helper so subclasses can call it after their own drawing.
    protected void DrawChildren(Vector2 abs)
    {
        foreach (var child in Children)
        {
            if (!child.Visible) continue;

            if (ClipChildren)
            {
                Vector2 ca = child.GetAbsolutePosition();
                if (ca.X + child.Size.X <= abs.X || ca.X >= abs.X + Size.X) continue;
                if (ca.Y + child.Size.Y <= abs.Y || ca.Y >= abs.Y + Size.Y) continue;
            }

            child.Draw();
        }
    }

    // ── Update ────────────────────────────────────────────────────────────
    public virtual void Update(float dt)
    {
        if (!Visible) return;

        IsHovered = Enabled && IsPointInside(GetMousePosition());
        if (!IsHovered) IsPressed = false;

        for (int i = 0; i < Children.Count; i++)
            Children[i].Update(dt);
    }

    // ── Input — MouseDown ────────────────────────────────────────────────
    // Children tested back-to-front (last drawn = topmost).
    // Returns true if any panel consumed the event.
    public virtual bool HandleMouseDown(MouseButtonEventArgs e)
    {
        if (!Enabled || !Visible) return false;
        if (!IsPointInside(GetMousePosition())) return false;

        for (int i = Children.Count - 1; i >= 0; i--)
            if (Children[i].HandleMouseDown(e)) return true;

        return OnMouseDown(e);
    }

    // ── Input — MouseUp ──────────────────────────────────────────────────
    // Broadcast: every child gets it so drag-releases are never lost.
    public virtual bool HandleMouseUp(MouseButtonEventArgs e)
    {
        if (!Visible) return false;

        bool consumed = false;
        for (int i = Children.Count - 1; i >= 0; i--)
            consumed |= Children[i].HandleMouseUp(e);

        consumed |= OnMouseUp(e);
        IsPressed = false;
        return consumed;
    }

    // ── Input — MouseMove ────────────────────────────────────────────────
    // Broadcast: keeps IsHovered fresh without polling.
    public virtual void HandleMouseMove(MouseMoveEventArgs e)
    {
        if (!Visible) return;
        for (int i = 0; i < Children.Count; i++)
            Children[i].HandleMouseMove(e);
        OnMouseMove(e);
    }

    // ── Input — Keyboard / Text ───────────────────────────────────────────
    public virtual bool HandleKeyDown(KeyboardKeyEventArgs e)
    {
        if (!Enabled || !Visible) return false;
        for (int i = Children.Count - 1; i >= 0; i--)
            if (Children[i].HandleKeyDown(e)) return true;
        return OnKeyDown(e);
    }

    public virtual bool HandleTextInput(TextInputEventArgs e)
    {
        if (!Enabled || !Visible) return false;
        for (int i = Children.Count - 1; i >= 0; i--)
            if (Children[i].HandleTextInput(e)) return true;
        return OnTextInput(e);
    }

    // ── Virtual hooks ────────────────────────────────────────────────────
    protected virtual bool OnMouseDown(MouseButtonEventArgs e) => false;
    protected virtual bool OnMouseUp(MouseButtonEventArgs e) => false;
    protected virtual void OnMouseMove(MouseMoveEventArgs e) { }
    protected virtual bool OnKeyDown(KeyboardKeyEventArgs e) => false;
    protected virtual bool OnTextInput(TextInputEventArgs e) => false;
} 






































