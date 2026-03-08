using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Drawing;

// =============================================================================
//  Button
// =============================================================================
//  • IsPressed set on MouseDown; click fires on MouseUp inside bounds.
//  • IsHovered maintained by Panel.Update every frame.
//  • Label drawn with BitmapFont — no child Text panel needed.
//  • Optional Icon string drawn left of label (e.g. "▶" "●" "×").
//  • TextAlign: Left / Center / Right.
// =============================================================================

public class Button : Panel
{
    // ── Appearance ────────────────────────────────────────────────────────
    public string Label { get; set; } = "Button";
    public string Icon { get; set; }
    public BitmapFont Font { get; set; }

    public Color NormalColor { get; set; } = Color.FromArgb(255, 60, 60, 68);
    public Color HoverColor { get; set; } = Color.FromArgb(255, 75, 75, 90);
    public Color PressedColor { get; set; } = Color.FromArgb(255, 44, 93, 180);
    public Color DisabledColor { get; set; } = Color.FromArgb(120, 80, 80, 88);
    public Color LabelColor { get; set; } = Color.FromArgb(255, 215, 215, 220);
    public Color LabelDisabled { get; set; } = Color.FromArgb(100, 140, 140, 145);

    public enum Align { Left, Center, Right }
    public Align TextAlign { get; set; } = Align.Center;

    // Draw a 1-px border around the button
    public bool ShowBorder { get; set; } = false;
    public Color BorderColor { get; set; } = Color.FromArgb(80, 100, 100, 120);

    // ── Events ────────────────────────────────────────────────────────────
    public event System.Action OnClick;

    // ── Draw ──────────────────────────────────────────────────────────────
    public override void Draw()
    {
        if (!Visible) return;

        Vector2 abs = GetAbsolutePosition();

        // Background
        Color bg = !Enabled ? DisabledColor
                 : IsPressed ? PressedColor
                 : IsHovered ? HoverColor
                 : BackgroundColor.A > 0 ? BackgroundColor
                 : NormalColor;

        UIRenderer.DrawRect(abs.X, abs.Y, Size.X, Size.Y, bg);

        if (ShowBorder)
            UIRenderer.DrawRectOutline(abs.X, abs.Y, Size.X, Size.Y, BorderColor);

        // Label
        if (Font != null && !string.IsNullOrEmpty(Label))
        {
            Color tc = Enabled ? LabelColor : LabelDisabled;
            float ty = abs.Y + (Size.Y - Font.LineH) * 0.5f;
            float lx = abs.X + 6f;

            // Optional icon on the left
            if (!string.IsNullOrEmpty(Icon))
            {
                Font.DrawText(Icon, lx, ty, tc);
                lx += Font.MeasureText(Icon) + 4f;
            }

            float tw = Font.MeasureText(Label);
            float tx = TextAlign switch
            {
                Align.Center => abs.X + (Size.X - tw) * 0.5f,
                Align.Right => abs.X + Size.X - tw - 6f,
                _ => lx,
            };

            Font.DrawText(Label, tx, ty, tc);
        }

        DrawChildren(abs);
    }

    // ── Input ─────────────────────────────────────────────────────────────
    protected override bool OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.Button != MouseButton.Left || !Enabled) return false;
        IsPressed = true;
        Panel.SetFocus(this);
        return true;
    }

    protected override bool OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.Button != MouseButton.Left) return false;
        bool wasPressed = IsPressed;
        IsPressed = false;
        if (wasPressed && Enabled && IsPointInside(GetMousePosition()))
            OnClick?.Invoke();
        return wasPressed;
    }
}