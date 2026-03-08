using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace ElintriaEngine.Rendering
{
    // ── Per-vertex layout ──────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct UIVertex
    {
        public float X, Y;      // screen position
        public float U, V;      // texture coordinate
        public float R, G, B, A; // colour

        public static readonly int Stride = Marshal.SizeOf<UIVertex>();
    }

    // ── A single draw-call batch ───────────────────────────────────────────────
    internal class DrawBatch
    {
        public List<UIVertex> Vertices = new(1024);
        public List<uint> Indices = new(2048);
        public int TextureId = 0;
        public int Mode = 0;    // UIShader uMode
    }

    /// <summary>
    /// OpenGL 3.3-core 2D batch renderer.
    /// Accumulates quads and lines, then flushes them to the GPU in one or few draw calls.
    /// All coordinates are in window-space pixels (origin = top-left).
    /// </summary>
    public class BatchRenderer : IDisposable
    {
        // ── GL objects ─────────────────────────────────────────────────────────
        private int _vao, _vbo, _ebo;
        private int _shader;
        private int _locProjection, _locTexture, _locMode;

        // ── Batching state ─────────────────────────────────────────────────────
        private readonly List<DrawBatch> _batches = new();
        private DrawBatch _current = new();

        // ── Scissor stack ──────────────────────────────────────────────────────
        private readonly Stack<Rectangle> _scissorStack = new();
        private int _viewportH;

        // ── 1×1 white texture (for flat-colour draws) ──────────────────────────
        private int _whiteTexture;

        // ── Constructor ────────────────────────────────────────────────────────
        public BatchRenderer()
        {
            BuildShader();
            BuildBuffers();
            BuildWhiteTexture();
        }

        // ── Frame lifecycle ────────────────────────────────────────────────────
        public void Begin(int viewportWidth, int viewportHeight)
        {
            _viewportH = viewportHeight;
            _batches.Clear();
            _current = new DrawBatch { TextureId = _whiteTexture, Mode = 0 };
            _scissorStack.Clear();

            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.ScissorTest);
        }

        public void End()
        {
            FlushCurrent();
            Flush();
            GL.Disable(EnableCap.ScissorTest);
        }

        // ── Scissor / clip ─────────────────────────────────────────────────────
        public void PushScissor(RectangleF rect)
        {
            FlushCurrent(); // must flush before changing GL state

            int y = _viewportH - (int)(rect.Y + rect.Height);
            var gl = new Rectangle((int)rect.X, y, (int)rect.Width, (int)rect.Height);
            _scissorStack.Push(gl);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(gl.X, gl.Y, gl.Width, gl.Height);
        }

        public void PopScissor()
        {
            FlushCurrent();
            if (_scissorStack.Count > 0) _scissorStack.Pop();

            if (_scissorStack.Count == 0)
                GL.Disable(EnableCap.ScissorTest);
            else
            {
                var prev = _scissorStack.Peek();
                GL.Scissor(prev.X, prev.Y, prev.Width, prev.Height);
            }
        }

        // ── Draw calls ─────────────────────────────────────────────────────────
        public void FillRect(RectangleF rect, Color color)
        {
            EnsureBatch(_whiteTexture, 0);
            uint b = (uint)_current.Vertices.Count;

            float r = color.R / 255f, g = color.G / 255f,
                  bl = color.B / 255f, a = color.A / 255f;

            _current.Vertices.Add(new UIVertex { X = rect.Left, Y = rect.Top, U = 0, V = 0, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = rect.Right, Y = rect.Top, U = 1, V = 0, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = rect.Right, Y = rect.Bottom, U = 1, V = 1, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = rect.Left, Y = rect.Bottom, U = 0, V = 1, R = r, G = g, B = bl, A = a });

            _current.Indices.AddRange(new uint[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        public void DrawRect(RectangleF rect, Color color, float thickness = 1f)
        {
            // Four lines
            float t = thickness;
            FillRect(new RectangleF(rect.Left, rect.Top, rect.Width, t), color);
            FillRect(new RectangleF(rect.Left, rect.Bottom - t, rect.Width, t), color);
            FillRect(new RectangleF(rect.Left, rect.Top, t, rect.Height), color);
            FillRect(new RectangleF(rect.Right - t, rect.Top, t, rect.Height), color);
        }

        public void DrawLine(PointF from, PointF to, Color color, float thickness = 1f)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;
            float nx = -dy / len * thickness * 0.5f, ny = dx / len * thickness * 0.5f;

            EnsureBatch(_whiteTexture, 0);
            uint b = (uint)_current.Vertices.Count;
            float r = color.R / 255f, g = color.G / 255f, bl = color.B / 255f, a = color.A / 255f;

            _current.Vertices.Add(new UIVertex { X = from.X + nx, Y = from.Y + ny, U = 0, V = 0, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = to.X + nx, Y = to.Y + ny, U = 1, V = 0, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = to.X - nx, Y = to.Y - ny, U = 1, V = 1, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = from.X - nx, Y = from.Y - ny, U = 0, V = 1, R = r, G = g, B = bl, A = a });
            _current.Indices.AddRange(new uint[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        public void DrawTexture(int textureId, RectangleF dest, Color tint)
        {
            EnsureBatch(textureId, 2);
            uint b = (uint)_current.Vertices.Count;
            float r = tint.R / 255f, g = tint.G / 255f, bl = tint.B / 255f, a = tint.A / 255f;
            _current.Vertices.Add(new UIVertex { X = dest.Left, Y = dest.Top, U = 0, V = 0, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = dest.Right, Y = dest.Top, U = 1, V = 0, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = dest.Right, Y = dest.Bottom, U = 1, V = 1, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = dest.Left, Y = dest.Bottom, U = 0, V = 1, R = r, G = g, B = bl, A = a });
            _current.Indices.AddRange(new uint[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        // ── Glyph quad (used by FontAtlas) ─────────────────────────────────────
        public void DrawGlyph(int atlasTexture, RectangleF dest,
            RectangleF uv, Color color)
        {
            EnsureBatch(atlasTexture, 1);
            uint b = (uint)_current.Vertices.Count;
            float r = color.R / 255f, g = color.G / 255f, bl = color.B / 255f, a = color.A / 255f;
            _current.Vertices.Add(new UIVertex { X = dest.Left, Y = dest.Top, U = uv.Left, V = uv.Top, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = dest.Right, Y = dest.Top, U = uv.Right, V = uv.Top, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = dest.Right, Y = dest.Bottom, U = uv.Right, V = uv.Bottom, R = r, G = g, B = bl, A = a });
            _current.Vertices.Add(new UIVertex { X = dest.Left, Y = dest.Bottom, U = uv.Left, V = uv.Bottom, R = r, G = g, B = bl, A = a });
            _current.Indices.AddRange(new uint[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        // ── Batch management ───────────────────────────────────────────────────
        private void EnsureBatch(int texId, int mode)
        {
            if (_current.TextureId != texId || _current.Mode != mode)
            {
                if (_current.Vertices.Count > 0) _batches.Add(_current);
                _current = new DrawBatch { TextureId = texId, Mode = mode };
            }
        }

        private void FlushCurrent()
        {
            if (_current.Vertices.Count > 0)
            {
                _batches.Add(_current);
                _current = new DrawBatch
                { TextureId = _batches[^1].TextureId, Mode = _batches[^1].Mode };
            }
        }

        // ── GPU flush ──────────────────────────────────────────────────────────
        private void Flush()
        {
            if (_batches.Count == 0) return;

            // Recompute projection each flush (handles resize)
            GL.UseProgram(_shader);

            int[] vp = new int[4];
            GL.GetInteger(GetPName.Viewport, vp);
            int vpW = vp[2], vpH = vp[3];

            var proj = Matrix4.CreateOrthographicOffCenter(0, vpW, vpH, 0, -1, 1);
            GL.UniformMatrix4(_locProjection, false, ref proj);
            GL.Uniform1(_locTexture, 0);

            GL.BindVertexArray(_vao);

            foreach (var batch in _batches)
            {
                if (batch.Vertices.Count == 0) continue;

                var verts = batch.Vertices.ToArray();
                var idxs = batch.Indices.ToArray();

                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                GL.BufferData(BufferTarget.ArrayBuffer,
                    verts.Length * UIVertex.Stride, verts,
                    BufferUsageHint.DynamicDraw);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
                GL.BufferData(BufferTarget.ElementArrayBuffer,
                    idxs.Length * sizeof(uint), idxs,
                    BufferUsageHint.DynamicDraw);

                GL.Uniform1(_locMode, batch.Mode);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, batch.TextureId);

                GL.DrawElements(PrimitiveType.Triangles, idxs.Length,
                    DrawElementsType.UnsignedInt, 0);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
            _batches.Clear();
        }

        // ── GL object builders ────────────────────────────────────────────────
        private void BuildShader()
        {
            int vert = CompileShader(ShaderType.VertexShader, UIShaders.VertexSource);
            int frag = CompileShader(ShaderType.FragmentShader, UIShaders.FragmentSource);

            _shader = GL.CreateProgram();
            GL.AttachShader(_shader, vert);
            GL.AttachShader(_shader, frag);
            GL.LinkProgram(_shader);
            GL.GetProgram(_shader, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("UI shader link error: " + GL.GetProgramInfoLog(_shader));

            GL.DeleteShader(vert);
            GL.DeleteShader(frag);

            _locProjection = GL.GetUniformLocation(_shader, "uProjection");
            _locTexture = GL.GetUniformLocation(_shader, "uTexture");
            _locMode = GL.GetUniformLocation(_shader, "uMode");
        }

        private static int CompileShader(ShaderType type, string src)
        {
            int id = GL.CreateShader(type);
            GL.ShaderSource(id, src);
            GL.CompileShader(id);
            GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new Exception($"Shader compile error ({type}): " + GL.GetShaderInfoLog(id));
            return id;
        }

        private void BuildBuffers()
        {
            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);

            _vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

            _ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);

            int s = UIVertex.Stride;
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, s, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, s, 8);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, s, 16);

            GL.BindVertexArray(0);
        }

        private void BuildWhiteTexture()
        {
            _whiteTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _whiteTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte,
                new byte[] { 255, 255, 255, 255 });
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // ── Dispose ────────────────────────────────────────────────────────────
        public void Dispose()
        {
            GL.DeleteVertexArray(_vao);
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_ebo);
            GL.DeleteProgram(_shader);
            GL.DeleteTexture(_whiteTexture);
        }
    }
}