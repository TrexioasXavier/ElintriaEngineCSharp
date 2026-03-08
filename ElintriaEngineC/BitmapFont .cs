using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

/// <summary>
/// Bakes a System.Drawing font into an OpenGL texture atlas at startup.
/// Supports ASCII 32-126. Each glyph is stored in a fixed cell.
///
/// Usage:
///   var font = new BitmapFont("Arial", 14f);
///   // inside UIRenderer.Begin/End:
///   font.DrawText("Hello!", x, y, Color.White);
///   float w = font.MeasureText("Hello!");
/// </summary>
public class BitmapFont : IDisposable
{
    // Atlas layout
    private const int FIRST_CHAR = 32;
    private const int LAST_CHAR = 126;
    private const int CHAR_COUNT = LAST_CHAR - FIRST_CHAR + 1;
    private const int COLS = 16;
    private const int ROWS = (CHAR_COUNT + COLS - 1) / COLS;

    public int CellW { get; private set; }
    public int CellH { get; private set; }
    public float LineH => CellH;

    private int _texture;
    private int _atlasW, _atlasH;

    // Per-glyph advance widths (for proportional spacing)
    private float[] _advances = new float[CHAR_COUNT];

    public BitmapFont(string fontName = "Consolas", float size = 13f,
                      FontStyle style = FontStyle.Regular)
    {
        using var measureBmp = new Bitmap(1, 1);
        using var measureGfx = Graphics.FromImage(measureBmp);
        using var font = new Font(fontName, size, style, GraphicsUnit.Pixel);

        // Measure tallest glyph for cell height
        SizeF cellSz = measureGfx.MeasureString("Wg|", font);
        CellH = (int)Math.Ceiling(cellSz.Height) + 2;

        // Measure each glyph width
        int maxW = 0;
        for (int i = 0; i < CHAR_COUNT; i++)
        {
            char c = (char)(FIRST_CHAR + i);
            SizeF sz = measureGfx.MeasureString(c.ToString(), font,
                           PointF.Empty, StringFormat.GenericTypographic);
            _advances[i] = sz.Width == 0 ? size * 0.4f : sz.Width;
            int wi = (int)Math.Ceiling(_advances[i]) + 2;
            if (wi > maxW) maxW = wi;
        }
        CellW = maxW;

        _atlasW = CellW * COLS;
        _atlasH = CellH * ROWS;

        // Render glyphs into atlas bitmap
        using var atlasBmp = new Bitmap(_atlasW, _atlasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var gfx = Graphics.FromImage(atlasBmp);
        gfx.Clear(Color.Transparent);
        gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        gfx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var brush = new SolidBrush(Color.White);
        var fmt = StringFormat.GenericTypographic;
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

        for (int i = 0; i < CHAR_COUNT; i++)
        {
            int col = i % COLS;
            int row = i / COLS;
            float px = col * CellW + 1;
            float py = row * CellH + 1;
            gfx.DrawString(((char)(FIRST_CHAR + i)).ToString(), font, brush, px, py, fmt);
        }

        // Upload to OpenGL
        _texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        BitmapData data = atlasBmp.LockBits(
            new Rectangle(0, 0, _atlasW, _atlasH),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                      _atlasW, _atlasH, 0,
                      OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
                      PixelType.UnsignedByte, data.Scan0);
        atlasBmp.UnlockBits(data);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>Returns pixel width of a string.</summary>
    public float MeasureText(string text)
    {
        float w = 0;
        foreach (char c in text) w += MeasureGlyphAdvance(c);
        return w;
    }

    /// <summary>
    /// Returns pixel width of the first <paramref name="charCount"/> characters.
    /// Used by InputField to map a click X position to a cursor index.
    /// </summary>
    public float MeasureText(string text, int charCount)
    {
        float w = 0;
        int n = Math.Min(charCount, text.Length);
        for (int i = 0; i < n; i++) w += MeasureGlyphAdvance(text[i]);
        return w;
    }

    /// <summary>Queue text for drawing via UIRenderer. Must be between Begin/End.</summary>
    public void DrawText(string text, float x, float y, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        float cx = x;
        foreach (char c in text)
        {
            DrawGlyph(c, cx, y, color);
            cx += MeasureGlyphAdvance(c);
        }
    }

    /// <summary>Draw a substring (e.g. selected region).</summary>
    public void DrawText(string text, int start, int length, float x, float y, Color color)
    {
        if (string.IsNullOrEmpty(text) || length <= 0) return;
        float cx = x;
        // Advance past characters before start
        for (int i = 0; i < start && i < text.Length; i++) cx += MeasureGlyphAdvance(text[i]);
        for (int i = start; i < start + length && i < text.Length; i++)
        {
            DrawGlyph(text[i], cx, y, color);
            cx += MeasureGlyphAdvance(text[i]);
        }
    }

    public int TextureId => _texture;
    public int AtlasW => _atlasW;
    public int AtlasH => _atlasH;

    // Returns the UV rect (u0,v0,u1,v1) for a character
    public (float u0, float v0, float u1, float v1) GetUV(char c)
    {
        int idx = c - FIRST_CHAR;
        if (idx < 0 || idx >= CHAR_COUNT) idx = '?' - FIRST_CHAR;
        int col = idx % COLS;
        int row = idx / COLS;
        float u0 = (col * CellW + 1f) / _atlasW;
        float v0 = (row * CellH + 1f) / _atlasH;
        float u1 = (col * CellW + CellW - 1f) / _atlasW;
        float v1 = (row * CellH + CellH - 1f) / _atlasH;
        return (u0, v0, u1, v1);
    }

    // ------------------------------------------------------------------
    // Private
    // ------------------------------------------------------------------

    public float MeasureGlyphAdvance(char c)
    {
        int idx = c - FIRST_CHAR;
        if (idx < 0 || idx >= CHAR_COUNT) return CellW;
        return _advances[idx];
    }

    private void DrawGlyph(char c, float x, float y, Color color)
    {
        var (u0, v0, u1, v1) = GetUV(c);
        UIRenderer.DrawTexturedRect(x, y, MeasureGlyphAdvance(c), CellH, u0, v0, u1, v1, color, _texture);
    }

    public void Dispose()
    {
        GL.DeleteTexture(_texture);
    }
}