using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using OpenTK.Mathematics;
using ElintriaEngine.UI.Panels;

namespace ElintriaEngine.Rendering
{
    /// <summary>
    /// Concrete implementation of <see cref="IEditorRenderer"/>.
    /// Backed by <see cref="BatchRenderer"/> (OpenGL 3.3) and <see cref="FontAtlas"/>.
    /// All coordinates are window-space pixels, origin = top-left.
    /// </summary>
    public class EditorRenderer : IEditorRenderer, IDisposable
    {
        private readonly BatchRenderer _batch;
        private readonly FontAtlas _font;
        private readonly TextureCache _textures;

        public EditorRenderer(string? fontPath = null)
        {
            _batch = new BatchRenderer();
            _font = FontAtlas.Load(fontPath);
            _textures = new TextureCache();
        }

        // ── Frame lifecycle ────────────────────────────────────────────────────
        public void BeginFrame(int width, int height) => _batch.Begin(width, height);
        public void EndFrame() => _batch.End();

        // ── IEditorRenderer ───────────────────────────────────────────────────
        public void FillRect(RectangleF rect, Color color)
            => _batch.FillRect(rect, color);

        public void DrawRect(RectangleF rect, Color color, float thickness = 1f)
            => _batch.DrawRect(rect, color, thickness);

        public void DrawLine(PointF from, PointF to, Color color, float thickness = 1f)
            => _batch.DrawLine(from, to, color, thickness);

        public void DrawImage(string texturePath, RectangleF dest, Color tint)
        {
            int id = _textures.Get(texturePath);
            if (id > 0) _batch.DrawTexture(id, dest, tint);
            else _batch.FillRect(dest, Color.FromArgb(80, 128, 128, 128));
        }

        public void DrawText(string text, PointF position, Color color, float size = 12f)
        {
            if (string.IsNullOrEmpty(text)) return;

            float x = position.X, y = position.Y;

            foreach (char c in text)
            {
                if (c == '\n') { y += _font.LineHeight(size); x = position.X; continue; }
                if (c < 32) continue;

                var g = _font.GetGlyph(c, size);
                if (g.Width > 0 && g.Height > 0)
                {
                    var dest = new RectangleF(
                        x + g.XOff,
                        y + g.YOff + size,   // baseline-relative
                        g.Width,
                        g.Height);
                    _batch.DrawGlyph(_font.TextureId, dest, g.UV, color);
                }
                x += g.Advance;
            }
        }

        public void PushClip(RectangleF rect) => _batch.PushScissor(rect);
        public void PopClip() => _batch.PopScissor();

        public Vector2 MeasureText(string text, float size)
        {
            if (string.IsNullOrEmpty(text)) return Vector2.Zero;
            float maxW = 0f, lineW = 0f, lineH = _font.LineHeight(size);
            int lines = 1;
            foreach (char c in text)
            {
                if (c == '\n') { if (lineW > maxW) maxW = lineW; lineW = 0; lines++; continue; }
                var g = _font.GetGlyph(c, size);
                lineW += g.Advance;
            }
            if (lineW > maxW) maxW = lineW;
            return new Vector2(maxW, lines * lineH);
        }

        public void Dispose() { _batch.Dispose(); _font.Dispose(); _textures.Dispose(); }
    }

    // ── Simple texture cache ───────────────────────────────────────────────────
    internal class TextureCache : IDisposable
    {
        private readonly Dictionary<string, int> _cache = new();

        public int Get(string path)
        {
            if (_cache.TryGetValue(path, out int id)) return id;

            if (!File.Exists(path)) { _cache[path] = 0; return 0; }

            try
            {
                using var bmp = new System.Drawing.Bitmap(path);
                id = UploadBitmap(bmp);
            }
            catch { id = 0; }

            _cache[path] = id;
            return id;
        }

        private static int UploadBitmap(System.Drawing.Bitmap bmp)
        {
            var data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            int tex = OpenTK.Graphics.OpenGL4.GL.GenTexture();
            OpenTK.Graphics.OpenGL4.GL.BindTexture(
                OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D, tex);
            OpenTK.Graphics.OpenGL4.GL.TexImage2D(
                OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D, 0,
                OpenTK.Graphics.OpenGL4.PixelInternalFormat.Rgba,
                bmp.Width, bmp.Height, 0,
                OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
                OpenTK.Graphics.OpenGL4.PixelType.UnsignedByte,
                data.Scan0);
            OpenTK.Graphics.OpenGL4.GL.TexParameter(
                OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,
                OpenTK.Graphics.OpenGL4.TextureParameterName.TextureMinFilter,
                (int)OpenTK.Graphics.OpenGL4.TextureMinFilter.Linear);
            OpenTK.Graphics.OpenGL4.GL.TexParameter(
                OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,
                OpenTK.Graphics.OpenGL4.TextureParameterName.TextureMagFilter,
                (int)OpenTK.Graphics.OpenGL4.TextureMagFilter.Linear);

            bmp.UnlockBits(data);
            OpenTK.Graphics.OpenGL4.GL.BindTexture(
                OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D, 0);
            return tex;
        }

        public void Dispose()
        {
            foreach (var kv in _cache)
                if (kv.Value > 0)
                    OpenTK.Graphics.OpenGL4.GL.DeleteTexture(kv.Value);
            _cache.Clear();
        }
    }
}