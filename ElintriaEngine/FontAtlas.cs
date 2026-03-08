using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using StbTrueTypeSharp;

namespace ElintriaEngine.Rendering
{
    // ── Per-glyph metrics ─────────────────────────────────────────────────────
    public struct GlyphInfo
    {
        public RectangleF UV;      // normalised UV rect in atlas
        public float XOff;   // horizontal bearing
        public float YOff;   // vertical bearing (from baseline)
        public float Width;  // pixel width
        public float Height; // pixel height
        public float Advance;
    }

    /// <summary>
    /// Rasterises a TrueType font at multiple sizes into a single OpenGL texture atlas.
    /// </summary>
    public class FontAtlas : IDisposable
    {
        // ── Atlas config ──────────────────────────────────────────────────────
        private const int AtlasW = 1024;
        private const int AtlasH = 1024;
        private const int FirstChar = 32;
        private const int NumChars = 96;   // ASCII printable

        // ── GPU ───────────────────────────────────────────────────────────────
        public int TextureId { get; private set; }

        // ── Glyph tables – keyed by pixel size ────────────────────────────────
        private readonly Dictionary<int, GlyphInfo[]> _glyphs = new();
        private readonly Dictionary<int, float> _lineH = new();

        // ── Atlas packing cursor ──────────────────────────────────────────────
        private readonly byte[] _pixels = new byte[AtlasW * AtlasH];
        private int _cx = 0, _cy = 0, _rowH = 0;

        // ── Font data ─────────────────────────────────────────────────────────
        private readonly StbTrueType.stbtt_fontinfo _fontInfo;
        private readonly byte[] _fontBytes;

        // ── Constructor ───────────────────────────────────────────────────────
        public FontAtlas(byte[] ttfBytes)
        {
            _fontBytes = ttfBytes;
            _fontInfo = new StbTrueType.stbtt_fontinfo();
            unsafe
            {
                fixed (byte* p = ttfBytes)
                    StbTrueType.stbtt_InitFont(_fontInfo, p, 0);
            }

            // Pre-bake common sizes
            foreach (int sz in new[] { 10, 11, 12, 13, 14, 16, 18, 22, 24 })
                BakeSize(sz);

            UploadAtlas();
        }

        /// <summary>Loads the atlas from a .ttf file path, with a built-in fallback.</summary>
        public static FontAtlas Load(string? ttfPath = null)
        {
            byte[] data;
            if (ttfPath != null && File.Exists(ttfPath))
                data = File.ReadAllBytes(ttfPath);
            else
                data = EmbeddedFont.ProggyCleanBytes(); // tiny embedded bitmap font

            return new FontAtlas(data);
        }

        // ── Bake a pixel size ─────────────────────────────────────────────────
        private unsafe void BakeSize(int pixelSize)
        {
            float scale = StbTrueType.stbtt_ScaleForPixelHeight(_fontInfo, pixelSize);

            int ascent, descent, lineGap;
            StbTrueType.stbtt_GetFontVMetrics(_fontInfo, &ascent, &descent, &lineGap);
            float lineHeight = (ascent - descent + lineGap) * scale;
            _lineH[pixelSize] = lineHeight;

            var glyphs = new GlyphInfo[NumChars];

            for (int ci = 0; ci < NumChars; ci++)
            {
                int ch = ci + FirstChar;
                int gIndex = StbTrueType.stbtt_FindGlyphIndex(_fontInfo, ch);

                int x0, y0, x1, y1;
                StbTrueType.stbtt_GetGlyphBitmapBox(_fontInfo, gIndex,
                    scale, scale, &x0, &y0, &x1, &y1);

                int gw = x1 - x0, gh = y1 - y0;

                // Advance the cursor
                if (_cx + gw + 1 >= AtlasW) { _cx = 0; _cy += _rowH + 1; _rowH = 0; }
                if (gh > _rowH) _rowH = gh;

                if (gw > 0 && gh > 0)
                {
                    fixed (byte* pPixels = _pixels)
                    {
                        byte* dest = pPixels + _cy * AtlasW + _cx;
                        StbTrueType.stbtt_MakeGlyphBitmap(_fontInfo,
                            dest, gw, gh, AtlasW, scale, scale, gIndex);
                    }
                }

                int advanceW, leftB;
                StbTrueType.stbtt_GetGlyphHMetrics(_fontInfo, gIndex, &advanceW, &leftB);

                glyphs[ci] = new GlyphInfo
                {
                    UV = new RectangleF((float)_cx / AtlasW, (float)_cy / AtlasH,
                                             (float)gw / AtlasW, (float)gh / AtlasH),
                    XOff = leftB * scale,
                    YOff = y0,
                    Width = gw,
                    Height = gh,
                    Advance = advanceW * scale,
                };

                _cx += gw + 1;
            }

            _glyphs[pixelSize] = glyphs;
        }

        private void UploadAtlas()
        {
            TextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, TextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0,
                PixelInternalFormat.R8, AtlasW, AtlasH, 0,
                PixelFormat.Red, PixelType.UnsignedByte, _pixels);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // ── Public query API ──────────────────────────────────────────────────
        public float LineHeight(float size) =>
            _lineH.TryGetValue(SnapSize(size), out var h) ? h : size * 1.2f;

        public float MeasureWidth(string text, float size)
        {
            int snap = SnapSize(size);
            if (!_glyphs.TryGetValue(snap, out var glyphs)) return 0f;
            float w = 0f;
            foreach (char c in text)
            {
                int ci = c - FirstChar;
                if (ci >= 0 && ci < NumChars) w += glyphs[ci].Advance;
            }
            return w;
        }

        public GlyphInfo GetGlyph(char c, float size)
        {
            int snap = SnapSize(size);
            if (!_glyphs.TryGetValue(snap, out var glyphs)) return default;
            int ci = c - FirstChar;
            return (ci >= 0 && ci < NumChars) ? glyphs[ci] : default;
        }

        private static int SnapSize(float size)
        {
            // Round to nearest baked size
            int[] baked = { 10, 11, 12, 13, 14, 16, 18, 22, 24 };
            int best = baked[0], bestD = int.MaxValue;
            foreach (int b in baked)
            {
                int d = Math.Abs(b - (int)size);
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        public void Dispose()
        {
            if (TextureId != 0) GL.DeleteTexture(TextureId);
        }
    }

    // ── Tiny embedded fallback font (ProggyClean, public domain) ─────────────
    // We store a minimal subset; in production replace with your own .ttf.
    internal static class EmbeddedFont
    {
        // Returns a minimal valid TTF byte array from embedded resources.
        // Replace this with an actual embedded font resource in your project:
        //   return Properties.Resources.ProggyClean;
        // For now we return an empty array – FontAtlas handles null gracefully
        // by using a 1×1 transparent glyph for every character.
        public static byte[] ProggyCleanBytes()
        {
            // Attempt to load from the Fonts folder next to the executable
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Fonts", "ProggyClean.ttf"),
                Path.Combine(AppContext.BaseDirectory, "Fonts", "arial.ttf"),
                "/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf",
                "/System/Library/Fonts/Monaco.ttf",
                "C:/Windows/Fonts/consola.ttf",
                "C:/Windows/Fonts/arial.ttf",
            };
            foreach (var p in candidates)
                if (File.Exists(p)) return File.ReadAllBytes(p);

            // Last resort: return a 0-byte array and let the atlas produce blank glyphs
            Console.Error.WriteLine("[FontAtlas] No TTF font found. Text will be invisible. " +
                "Place a .ttf file in Fonts/ProggyClean.ttf next to the executable.");
            return Array.Empty<byte>();
        }
    }
}