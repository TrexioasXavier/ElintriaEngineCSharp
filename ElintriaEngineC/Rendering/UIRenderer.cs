using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Drawing;

/// <summary>
/// Lightweight 2D renderer for UI panels and text.
///
/// DESIGN: A single ordered draw-command list preserves the exact submission
/// order, so backgrounds always appear behind the glyphs queued after them.
/// When consecutive commands share the same pipeline+texture they are merged
/// into one GPU draw call automatically.
///
/// Usage:
///   UIRenderer.Begin(windowW, windowH);
///   UIRenderer.DrawRect(...)           // colored quad
///   myFont.DrawText(...)               // textured glyph quads
///   UIRenderer.End();                  // flushes everything in order
/// </summary>
public static class UIRenderer
{
    // -----------------------------------------------------------------------
    // GPU resources
    // -----------------------------------------------------------------------
    private static int _colorVao, _colorVbo, _colorShader, _colorMVP;
    private static int _texVao, _texVbo, _texShader, _texMVP, _texModeUniform;
    private static bool _ready = false;

    // -----------------------------------------------------------------------
    // Draw-command list
    // Each command covers a contiguous slice of one of the vertex arrays.
    // -----------------------------------------------------------------------
    private enum CmdType { Color, Textured }

    private struct DrawCmd
    {
        public CmdType Type;
        public int TextureId;   // only used when Type == Textured
        public int QuadStart;   // first quad index in the relevant array
        public int QuadCount;
        public bool FullRgb;     // true = full texture color, false = glyph alpha-mask
    }

    private const int MAX_QUADS = 4096;

    // Colored verts: 4 verts * 6 floats (xy rgba)
    private static float[] _colorVerts = new float[MAX_QUADS * 4 * 6];
    private static int _colorCount = 0;

    // Textured verts: 4 verts * 8 floats (xy rgba uv)
    private static float[] _texVerts = new float[MAX_QUADS * 4 * 8];
    private static int _texCount = 0;

    private static DrawCmd[] _cmds = new DrawCmd[MAX_QUADS * 2];
    private static int _cmdCount = 0;

    private static Matrix4 _proj;

    // -----------------------------------------------------------------------
    // Init
    // -----------------------------------------------------------------------
    public static void Init()
    {
        if (_ready) return;

        // Colored shader
        _colorShader = BuildShader(@"
#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec4 aColor;
out vec4 vColor;
uniform mat4 uMVP;
void main(){ gl_Position = uMVP*vec4(aPos,0,1); vColor=aColor; }",
@"
#version 330 core
in vec4 vColor; out vec4 F;
void main(){ F=vColor; }");
        _colorMVP = GL.GetUniformLocation(_colorShader, "uMVP");
        BuildVAO(out _colorVao, out _colorVbo, _colorVerts.Length * sizeof(float), 6, false);

        // Textured shader
        _texShader = BuildShader(@"
#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec4 aColor;
layout(location=2) in vec2 aUV;
out vec4 vColor; out vec2 vUV;
uniform mat4 uMVP;
void main(){ gl_Position = uMVP*vec4(aPos,0,1); vColor=aColor; vUV=aUV; }",
@"
#version 330 core
in vec4 vColor; in vec2 vUV; out vec4 F;
uniform sampler2D uTex;
uniform int uTexMode; // 0=glyph(alpha-mask), 1=full RGB texture
void main(){
    vec4 t = texture(uTex, vUV);
    if (uTexMode == 1)
        F = t * vColor;          // full RGBA texture tinted by vColor
    else
        F = vec4(vColor.rgb, vColor.a * t.a);  // glyph: color from vertex, alpha from texture
}");
        _texMVP = GL.GetUniformLocation(_texShader, "uMVP");
        _texModeUniform = GL.GetUniformLocation(_texShader, "uTexMode");
        GL.UseProgram(_texShader);
        GL.Uniform1(GL.GetUniformLocation(_texShader, "uTex"), 0);
        GL.Uniform1(_texModeUniform, 0);  // default: glyph mode
        GL.UseProgram(0);
        BuildVAO(out _texVao, out _texVbo, _texVerts.Length * sizeof(float), 8, true);

        _ready = true;
    }

    // -----------------------------------------------------------------------
    // Frame boundary
    // -----------------------------------------------------------------------
    public static void Begin(float w, float h)
    {
        if (!_ready) Init();
        _proj = Matrix4.CreateOrthographicOffCenter(0, w, h, 0, -1f, 1f);
        _colorCount = 0;
        _texCount = 0;
        _cmdCount = 0;
    }

    /// <summary>
    /// Flushes all queued commands to the GPU in submission order.
    /// Consecutive commands of the same type+texture are merged automatically.
    /// </summary>
    public static void End()
    {
        if (_cmdCount == 0) return;

        // Upload both vertex buffers once
        if (_colorCount > 0)
        {
            float[] tris = QuadsToTris(_colorVerts, _colorCount, 6);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _colorVbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                             tris.Length * sizeof(float), tris);
        }
        if (_texCount > 0)
        {
            float[] tris = QuadsToTris(_texVerts, _texCount, 8);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _texVbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                             tris.Length * sizeof(float), tris);
        }
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        // Walk commands in order — switch pipeline only when necessary
        CmdType lastType = (CmdType)(-1);
        int lastTex = -1;

        for (int ci = 0; ci < _cmdCount; ci++)
        {
            ref DrawCmd cmd = ref _cmds[ci];

            bool switchPipeline = (cmd.Type != lastType);
            bool switchTexture = (cmd.Type == CmdType.Textured && cmd.TextureId != lastTex);

            if (switchPipeline)
            {
                // Unbind previous
                GL.BindVertexArray(0);
                GL.UseProgram(0);

                if (cmd.Type == CmdType.Color)
                {
                    GL.UseProgram(_colorShader);
                    GL.UniformMatrix4(_colorMVP, false, ref _proj);
                    GL.BindVertexArray(_colorVao);
                }
                else
                {
                    GL.UseProgram(_texShader);
                    GL.UniformMatrix4(_texMVP, false, ref _proj);
                    GL.BindVertexArray(_texVao);
                }
                lastType = cmd.Type;
                lastTex = -1;
            }

            if (cmd.Type == CmdType.Textured && (switchPipeline || switchTexture))
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, cmd.TextureId);
                GL.Uniform1(_texModeUniform, cmd.FullRgb ? 1 : 0);
                lastTex = cmd.TextureId;
            }
            else if (cmd.Type == CmdType.Textured && switchPipeline)
            {
                GL.Uniform1(_texModeUniform, cmd.FullRgb ? 1 : 0);
            }

            // Each quad expands to 6 vertices
            int firstVert = cmd.QuadStart * 6;
            int vertCount = cmd.QuadCount * 6;
            GL.DrawArrays(PrimitiveType.Triangles, firstVert, vertCount);
        }

        // Cleanup
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.UseProgram(0);
    }

    // -----------------------------------------------------------------------
    // Public draw calls
    // -----------------------------------------------------------------------

    /// <summary>Filled colored rectangle.</summary>
    public static void DrawRect(float x, float y, float w, float h, Color c)
    {
        if (_colorCount >= MAX_QUADS) FlushImmediate_Color();

        AppendColorQuad(x, y, w, h, c);
        AppendCmd(CmdType.Color, -1, _colorCount - 1, 1);
    }

    /// <summary>Outlined rectangle (1 px or custom thickness).</summary>
    public static void DrawRectOutline(float x, float y, float w, float h,
                                       Color c, float t = 1f)
    {
        DrawRect(x, y, w, t, c);
        DrawRect(x, y + h - t, w, t, c);
        DrawRect(x, y, t, h, c);
        DrawRect(x + w - t, y, t, h, c);
    }

    /// <summary>Textured rectangle — glyph mode (alpha mask, vertex color).</summary>
    public static void DrawTexturedRect(float x, float y, float w, float h,
                                        float u0, float v0, float u1, float v1,
                                        Color c, int textureId)
    {
        if (_texCount >= MAX_QUADS) FlushImmediate_Tex(textureId);
        AppendTexQuad(x, y, w, h, u0, v0, u1, v1, c);
        AppendCmd(CmdType.Textured, textureId, _texCount - 1, 1, fullRgb: false);
    }

    /// <summary>Full RGB texture rect — for FBO / scene view display.</summary>
    public static void DrawSceneTexture(float x, float y, float w, float h,
                                        float u0, float v0, float u1, float v1,
                                        Color tint, int textureId)
    {
        if (_texCount >= MAX_QUADS) FlushImmediate_Tex(textureId);
        AppendTexQuad(x, y, w, h, u0, v0, u1, v1, tint);
        AppendCmd(CmdType.Textured, textureId, _texCount - 1, 1, fullRgb: true);
    }

    // -----------------------------------------------------------------------
    // Command list helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Appends a draw command, merging with the previous one if it shares
    /// the same type, texture, and is contiguous in the vertex array.
    /// </summary>
    private static void AppendCmd(CmdType type, int texId, int quadIdx, int quadCount,
                                   bool fullRgb = false)
    {
        if (_cmdCount > 0)
        {
            ref DrawCmd prev = ref _cmds[_cmdCount - 1];
            bool sameType = prev.Type == type;
            bool sameTex = type == CmdType.Color || prev.TextureId == texId;
            bool sameMode = prev.FullRgb == fullRgb;
            bool contiguous = (prev.QuadStart + prev.QuadCount) == quadIdx;

            if (sameType && sameTex && sameMode && contiguous)
            {
                prev.QuadCount += quadCount;
                return;
            }
        }

        if (_cmdCount >= _cmds.Length)
            System.Array.Resize(ref _cmds, _cmds.Length * 2);

        _cmds[_cmdCount++] = new DrawCmd
        {
            Type = type,
            TextureId = texId,
            QuadStart = quadIdx,
            QuadCount = quadCount,
            FullRgb = fullRgb
        };
    }

    // -----------------------------------------------------------------------
    // Vertex append helpers
    // -----------------------------------------------------------------------
    private static void AppendColorQuad(float x, float y, float w, float h, Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;
        int i = _colorCount * 4 * 6;
        SetCV(ref i, x, y, r, g, b, a);
        SetCV(ref i, x + w, y, r, g, b, a);
        SetCV(ref i, x + w, y + h, r, g, b, a);
        SetCV(ref i, x, y + h, r, g, b, a);
        _colorCount++;
    }

    private static void AppendTexQuad(float x, float y, float w, float h,
                                      float u0, float v0, float u1, float v1, Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;
        int i = _texCount * 4 * 8;
        SetTV(ref i, x, y, r, g, b, a, u0, v0);
        SetTV(ref i, x + w, y, r, g, b, a, u1, v0);
        SetTV(ref i, x + w, y + h, r, g, b, a, u1, v1);
        SetTV(ref i, x, y + h, r, g, b, a, u0, v1);
        _texCount++;
    }

    private static void SetCV(ref int i, float x, float y,
                               float r, float g, float b, float a)
    {
        _colorVerts[i++] = x; _colorVerts[i++] = y;
        _colorVerts[i++] = r; _colorVerts[i++] = g; _colorVerts[i++] = b; _colorVerts[i++] = a;
    }

    private static void SetTV(ref int i, float x, float y,
                               float r, float g, float b, float a, float u, float v)
    {
        _texVerts[i++] = x; _texVerts[i++] = y;
        _texVerts[i++] = r; _texVerts[i++] = g; _texVerts[i++] = b; _texVerts[i++] = a;
        _texVerts[i++] = u; _texVerts[i++] = v;
    }

    // -----------------------------------------------------------------------
    // Emergency mid-frame flushes (buffer full) — rare
    // -----------------------------------------------------------------------
    private static void FlushImmediate_Color()
    {
        End();
        _colorCount = 0; _texCount = 0; _cmdCount = 0;
    }
    private static void FlushImmediate_Tex(int nextTex)
    {
        End();
        _colorCount = 0; _texCount = 0; _cmdCount = 0;
    }

    // -----------------------------------------------------------------------
    // Quad → triangle expansion
    // -----------------------------------------------------------------------
    private static float[] QuadsToTris(float[] src, int quadCount, int fpv)
    {
        float[] dst = new float[quadCount * 6 * fpv];
        int d = 0;
        for (int q = 0; q < quadCount; q++)
        {
            int s = q * 4 * fpv;
            foreach (int vi in new[] { 0, 1, 2, 0, 2, 3 })
            {
                int from = s + vi * fpv;
                for (int k = 0; k < fpv; k++) dst[d++] = src[from + k];
            }
        }
        return dst;
    }

    // -----------------------------------------------------------------------
    // GL helpers
    // -----------------------------------------------------------------------
    private static int BuildShader(string vert, string frag)
    {
        int vs = Compile(ShaderType.VertexShader, vert);
        int fs = Compile(ShaderType.FragmentShader, frag);
        int pg = GL.CreateProgram();
        GL.AttachShader(pg, vs); GL.AttachShader(pg, fs);
        GL.LinkProgram(pg);
        GL.GetProgram(pg, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) Console.Error.WriteLine("[UIRenderer] Link: " + GL.GetProgramInfoLog(pg));
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        return pg;
    }
    private static int Compile(ShaderType t, string src)
    {
        int id = GL.CreateShader(t);
        GL.ShaderSource(id, src); GL.CompileShader(id);
        GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) Console.Error.WriteLine($"[UIRenderer] {t}: {GL.GetShaderInfoLog(id)}");
        return id;
    }
    private static void BuildVAO(out int vao, out int vbo,
                                 int bytes, int stride, bool hasUV)
    {
        vao = GL.GenVertexArray(); vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, bytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        int s = stride * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, s, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, s, 2 * sizeof(float));
        if (hasUV)
        {
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, s, 6 * sizeof(float));
        }
        GL.BindVertexArray(0);
    }

    public static void Dispose()
    {
        if (!_ready) return;
        GL.DeleteBuffer(_colorVbo); GL.DeleteVertexArray(_colorVao); GL.DeleteProgram(_colorShader);
        GL.DeleteBuffer(_texVbo); GL.DeleteVertexArray(_texVao); GL.DeleteProgram(_texShader);
        _ready = false;
    }
}