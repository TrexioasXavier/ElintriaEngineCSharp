using Elintria.Engine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Drawing;

/// <summary>
/// A 2-D UI text label that lives inside the Panel hierarchy.
///
/// Features:
///   - Renders via BitmapFont / UIRenderer (no legacy GL)
///   - AutoSize: shrinks/grows the panel to fit the text
///   - TextAlign: Left / Center / Right  horizontally inside the panel
///   - Optional drop-shadow
///   - Wraps long text when WordWrap = true
///
/// Usage:
///   var lbl = new Text { Content = "Hello", Font = myFont, TextColor = Color.White };
///   panel.AddChild(lbl);
/// </summary>
public class Text : Panel
{
    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------
    public string Content { get; set; } = "Text";
    public BitmapFont Font { get; set; }
    public Color TextColor { get; set; } = Color.White;

    /// <summary>When true, Size.X/Y is overwritten to fit the text each frame.</summary>
    public bool AutoSize { get; set; } = false;

    public enum HAlign { Left, Center, Right }
    public enum VAlign { Top, Middle, Bottom }
    public HAlign HorizontalAlign { get; set; } = HAlign.Left;
    public VAlign VerticalAlign { get; set; } = VAlign.Middle;

    /// <summary>Padding inside the panel before text starts.</summary>
    public float PadX { get; set; } = 4f;
    public float PadY { get; set; } = 3f;

    /// <summary>Soft word-wrap. Only active when AutoSize = false.</summary>
    public bool WordWrap { get; set; } = false;

    /// <summary>Draws a 1-pixel drop shadow at (+1,+1).</summary>
    public bool DropShadow { get; set; } = false;
    public Color DropShadowColor { get; set; } = Color.FromArgb(160, 0, 0, 0);

    // ------------------------------------------------------------------
    // Draw
    // ------------------------------------------------------------------
    public override void Draw()
    {
        if (!Visible || Font == null) return;

        base.Draw(); // background + children

        Vector2 abs = GetAbsolutePosition();

        if (AutoSize)
        {
            SizeF s = MeasureContent();
            Size = new Vector2(s.Width + PadX * 2f, s.Height + PadY * 2f);
        }

        if (string.IsNullOrEmpty(Content)) return;

        string[] lines = WordWrap ? BuildWrappedLines() : new[] { Content };

        float lineH = Font.LineH;
        float totalH = lines.Length * lineH;

        // Vertical start
        float startY = VerticalAlign switch
        {
            VAlign.Middle => abs.Y + PadY + (Size.Y - PadY * 2f - totalH) * 0.5f,
            VAlign.Bottom => abs.Y + Size.Y - PadY - totalH,
            _ => abs.Y + PadY
        };

        foreach (string line in lines)
        {
            float lineW = Font.MeasureText(line);

            float startX = HorizontalAlign switch
            {
                HAlign.Center => abs.X + PadX + (Size.X - PadX * 2f - lineW) * 0.5f,
                HAlign.Right => abs.X + Size.X - PadX - lineW,
                _ => abs.X + PadX
            };

            if (DropShadow)
                Font.DrawText(line, startX + 1f, startY + 1f, DropShadowColor);

            Font.DrawText(line, startX, startY, TextColor);

            startY += lineH;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private SizeF MeasureContent()
    {
        if (Font == null) return new SizeF(0, 0);
        float w = Font.MeasureText(Content);
        float h = Font.LineH;
        return new SizeF(w, h);
    }

    private string[] BuildWrappedLines()
    {
        if (Font == null) return new[] { Content };
        float maxW = Size.X - PadX * 2f;
        if (maxW <= 0) return new[] { Content };

        var lines = new System.Collections.Generic.List<string>();
        string[] words = Content.Split(' ');
        string current = "";

        foreach (string word in words)
        {
            string test = current.Length == 0 ? word : current + " " + word;
            if (Font.MeasureText(test) > maxW && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else current = test;
        }
        if (current.Length > 0) lines.Add(current);
        return lines.ToArray();
    }
}

// =============================================================================
// WorldText  — billboard text that lives in 3-D world space
// =============================================================================

/// <summary>
/// A text label that exists at a 3-D world position and always faces the camera
/// (spherical billboard).
///
/// Rendering is done via UIRenderer / BitmapFont using a temporary ortho
/// projection computed from the billboard's projected screen rect.
///
/// Call WorldText.DrawAll(camera, view, proj, screenW, screenH) once per frame
/// AFTER the normal UIRenderer.Begin/End block (it manages its own Begin/End).
///
/// Usage:
///   var wt = new WorldText { Content = "Enemy", Font = myFont };
///   wt.WorldPosition = new Vector3(1, 2, 0);
///   WorldText.Register(wt);           // add to global list
///   WorldText.Unregister(wt);         // remove
/// </summary>
public class WorldText
{
    // ------------------------------------------------------------------
    // Global registry
    // ------------------------------------------------------------------
    private static readonly System.Collections.Generic.List<WorldText> _all
        = new System.Collections.Generic.List<WorldText>();

    public static void Register(WorldText wt) { if (!_all.Contains(wt)) _all.Add(wt); }
    public static void Unregister(WorldText wt) { _all.Remove(wt); }

    /// <summary>
    /// Draw every registered WorldText.
    /// Call this INSIDE the blend-enabled section, AFTER 3-D mesh rendering.
    /// UIRenderer must NOT be in a Begin/End block when this is called.
    /// </summary>
    public static void DrawAll(Camera camera,
                               Matrix4 view, Matrix4 proj,
                               float screenW, float screenH)
    {
        UIRenderer.Begin(screenW, screenH);
        foreach (var wt in _all)
            wt.DrawWorld(camera, view, proj, screenW, screenH);
        UIRenderer.End();
    }

    // ------------------------------------------------------------------
    // Instance properties
    // ------------------------------------------------------------------
    public string Content { get; set; } = "Label";
    public BitmapFont Font { get; set; }
    public Color TextColor { get; set; } = Color.White;
    public bool Visible { get; set; } = true;

    /// <summary>World-space anchor point.</summary>
    public Vector3 WorldPosition { get; set; } = Vector3.Zero;

    /// <summary>
    /// Uniform scale in world units. 1.0 = text is roughly 1 world-unit tall.
    /// </summary>
    public float Scale { get; set; } = 0.05f;

    /// <summary>Vertical offset above WorldPosition in world units.</summary>
    public float YOffset { get; set; } = 0f;

    /// <summary>Background quad color. Transparent = no background.</summary>
    public Color BackgroundColor { get; set; } = Color.Transparent;
    public float PadX { get; set; } = 4f;
    public float PadY { get; set; } = 2f;

    public bool DropShadow { get; set; } = false;
    public Color DropShadowColor { get; set; } = Color.FromArgb(160, 0, 0, 0);

    // ------------------------------------------------------------------
    // Per-instance draw (called by DrawAll)
    // ------------------------------------------------------------------
    private void DrawWorld(Camera camera,
                           Matrix4 view, Matrix4 proj,
                           float screenW, float screenH)
    {
        if (!Visible || Font == null || string.IsNullOrEmpty(Content)) return;

        // ---- Project world anchor to screen coords ----------------------
        Vector3 anchor = WorldPosition + Vector3.UnitY * YOffset;
        Vector4 clip = new Vector4(anchor, 1.0f) * view * proj;

        // Behind camera?
        if (clip.W <= 0f) return;

        Vector3 ndc = clip.Xyz / clip.W;
        if (ndc.X < -1.2f || ndc.X > 1.2f || ndc.Y < -1.2f || ndc.Y > 1.2f) return;

        float sx = (ndc.X * 0.5f + 0.5f) * screenW;
        float sy = (1f - (ndc.Y * 0.5f + 0.5f)) * screenH; // flip Y

        // ---- Compute pixel size from world Scale -------------------------
        // Project a point 'Scale' units to the right in camera-right space
        Vector3 right = new Vector3(view.Row0.X, view.Row0.Y, view.Row0.Z);
        Vector3 rWorld = anchor + right * Scale;
        Vector4 rClip = new Vector4(rWorld, 1f) * view * proj;
        Vector3 rNdc = rClip.Xyz / rClip.W;
        float pixelW = Math.Abs((rNdc.X - ndc.X) * 0.5f * screenW);
        // pixelW is how many pixels correspond to one 'Scale' unit
        // Use it as the text pixel scale factor
        float textW = Font.MeasureText(Content);
        float textH = Font.LineH;
        float factor = pixelW / Math.Max(textW, 1f);  // shrink/grow proportionally

        // Apply factor to give a consistent apparent size regardless of distance
        float drawW = textW * factor + PadX * 2f;
        float drawH = textH * factor + PadY * 2f;

        float left = sx - drawW * 0.5f;
        float top = sy - drawH * 0.5f;

        // ---- Background -------------------------------------------------
        if (BackgroundColor != Color.Transparent)
            UIRenderer.DrawRect(left, top, drawW, drawH, BackgroundColor);

        // ---- Text -------------------------------------------------------
        // We scale by drawing at a temporary ortho that maps font pixels → drawH
        // The simplest approach: recompute a per-glyph scaled draw call.
        float glyphScaleX = (drawW - PadX * 2f) / Math.Max(textW, 1f);
        float glyphScaleY = (drawH - PadY * 2f) / Math.Max(textH, 1f);
        float gx = left + PadX;
        float gy = top + PadY;

        DrawScaledText(Content, gx, gy, glyphScaleX, glyphScaleY, TextColor, DropShadow);
    }

    private void DrawScaledText(string text, float x, float y,
                                float sx, float sy, Color color, bool shadow)
    {
        // Walk glyphs manually so we can apply per-axis scale
        float cx = x;
        foreach (char c in text)
        {
            var (u0, v0, u1, v1) = Font.GetUV(c);
            float gw = Font.CellW * sx;
            float gh = Font.LineH * sy;

            if (shadow)
                UIRenderer.DrawTexturedRect(cx + 1, y + 1, gw, gh, u0, v0, u1, v1, DropShadowColor, Font.TextureId);

            UIRenderer.DrawTexturedRect(cx, y, gw, gh, u0, v0, u1, v1, color, Font.TextureId);
            cx += Font.MeasureGlyphAdvance(c) * sx;
        }
    }
}