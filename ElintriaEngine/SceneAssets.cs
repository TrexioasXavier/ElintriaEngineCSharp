using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace ElintriaEngine.Rendering.Scene
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Mesh  –  holds GPU vertex/index buffers
    // ═══════════════════════════════════════════════════════════════════════════
    public class Mesh : IDisposable
    {
        public string Name { get; }
        public int VAO { get; private set; }
        public int IndexCount { get; private set; }

        private int _vbo, _ebo;

        // ── Vertex layout: pos(3) + normal(3) + uv(2) = 8 floats = 32 bytes ──
        public readonly int VertexStride = 8 * sizeof(float);

        private Mesh(string name) => Name = name;

        /// <summary>Upload raw interleaved float data [x,y,z, nx,ny,nz, u,v, ...]</summary>
        public static Mesh FromArrays(string name, float[] vertices, uint[] indices)
        {
            var m = new Mesh(name);

            m.VAO = GL.GenVertexArray();
            GL.BindVertexArray(m.VAO);

            m._vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, m._vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float),
                vertices, BufferUsageHint.StaticDraw);

            m._ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, m._ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint),
                indices, BufferUsageHint.StaticDraw);

            int s = m.VertexStride;
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, s, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, s, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, s, 24);

            GL.BindVertexArray(0);
            m.IndexCount = indices.Length;
            return m;
        }

        // ── Built-in primitive factories ──────────────────────────────────────
        public static Mesh Cube()
        {
            float[] v = {
                // pos              normal          uv
                -0.5f,-0.5f,-0.5f,  0, 0,-1,  0,0,
                 0.5f,-0.5f,-0.5f,  0, 0,-1,  1,0,
                 0.5f, 0.5f,-0.5f,  0, 0,-1,  1,1,
                -0.5f, 0.5f,-0.5f,  0, 0,-1,  0,1,

                -0.5f,-0.5f, 0.5f,  0, 0, 1,  0,0,
                 0.5f,-0.5f, 0.5f,  0, 0, 1,  1,0,
                 0.5f, 0.5f, 0.5f,  0, 0, 1,  1,1,
                -0.5f, 0.5f, 0.5f,  0, 0, 1,  0,1,

                -0.5f, 0.5f, 0.5f, -1, 0, 0,  0,0,
                -0.5f, 0.5f,-0.5f, -1, 0, 0,  1,0,
                -0.5f,-0.5f,-0.5f, -1, 0, 0,  1,1,
                -0.5f,-0.5f, 0.5f, -1, 0, 0,  0,1,

                 0.5f, 0.5f, 0.5f,  1, 0, 0,  0,0,
                 0.5f, 0.5f,-0.5f,  1, 0, 0,  1,0,
                 0.5f,-0.5f,-0.5f,  1, 0, 0,  1,1,
                 0.5f,-0.5f, 0.5f,  1, 0, 0,  0,1,

                -0.5f,-0.5f,-0.5f,  0,-1, 0,  0,0,
                 0.5f,-0.5f,-0.5f,  0,-1, 0,  1,0,
                 0.5f,-0.5f, 0.5f,  0,-1, 0,  1,1,
                -0.5f,-0.5f, 0.5f,  0,-1, 0,  0,1,

                -0.5f, 0.5f,-0.5f,  0, 1, 0,  0,0,
                 0.5f, 0.5f,-0.5f,  0, 1, 0,  1,0,
                 0.5f, 0.5f, 0.5f,  0, 1, 0,  1,1,
                -0.5f, 0.5f, 0.5f,  0, 1, 0,  0,1,
            };
            uint[] idx = new uint[36];
            for (int f = 0; f < 6; f++)
            {
                uint b = (uint)(f * 4);
                int o = f * 6;
                idx[o] = b; idx[o + 1] = b + 1; idx[o + 2] = b + 2;
                idx[o + 3] = b; idx[o + 4] = b + 2; idx[o + 5] = b + 3;
            }
            return FromArrays("Cube", v, idx);
        }

        public static Mesh Sphere(int rings = 24, int sectors = 24)
        {
            var verts = new List<float>();
            var idxs = new List<uint>();

            for (int r = 0; r <= rings; r++)
            {
                float phi = MathF.PI * r / rings;
                for (int s = 0; s <= sectors; s++)
                {
                    float theta = 2 * MathF.PI * s / sectors;
                    float x = MathF.Sin(phi) * MathF.Cos(theta);
                    float y = MathF.Cos(phi);
                    float z = MathF.Sin(phi) * MathF.Sin(theta);
                    verts.AddRange(new[] { x*0.5f, y*0.5f, z*0.5f, x, y, z,
                        (float)s/sectors, (float)r/rings });
                }
            }
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < sectors; s++)
                {
                    uint a = (uint)(r * (sectors + 1) + s), b = a + 1,
                         c = (uint)((r + 1) * (sectors + 1) + s), d = c + 1;
                    idxs.AddRange(new[] { a, c, b, b, c, d });
                }
            return FromArrays("Sphere", verts.ToArray(), idxs.ToArray());
        }

        public static Mesh Plane()
        {
            float[] v = {
                -5,-0, 5,  0,1,0,  0,0,
                 5,-0, 5,  0,1,0,  5,0,
                 5,-0,-5,  0,1,0,  5,5,
                -5,-0,-5,  0,1,0,  0,5,
            };
            uint[] idx = { 0, 1, 2, 0, 2, 3 };
            return FromArrays("Plane", v, idx);
        }

        public void Draw()
        {
            GL.BindVertexArray(VAO);
            GL.DrawElements(PrimitiveType.Triangles, IndexCount,
                DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }

        public void Dispose()
        {
            GL.DeleteVertexArray(VAO);
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_ebo);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Texture2D
    // ═══════════════════════════════════════════════════════════════════════════
    public class Texture2D : IDisposable
    {
        public int Handle { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Path { get; private set; } = "";

        // 1×1 white fallback
        public static Texture2D White { get; } = CreateSolid(255, 255, 255);
        public static Texture2D Gray { get; } = CreateSolid(128, 128, 128);
        public static Texture2D Black { get; } = CreateSolid(0, 0, 0);

        private static Texture2D CreateSolid(byte r, byte g, byte b)
        {
            var t = new Texture2D { Width = 1, Height = 1 };
            t.Handle = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, t.Handle);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, 1, 1,
                0, PixelFormat.Rgb, PixelType.UnsignedByte, new byte[] { r, g, b });
            SetParams();
            return t;
        }

        public static Texture2D Load(string path)
        {
            var t = new Texture2D { Path = path };
            if (!File.Exists(path)) return White;
            try
            {
                using var bmp = new System.Drawing.Bitmap(path);
                t.Width = bmp.Width;
                t.Height = bmp.Height;
                var data = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                t.Handle = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, t.Handle);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                    bmp.Width, bmp.Height, 0,
                    PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                bmp.UnlockBits(data);
                SetParams();
            }
            catch { return White; }
            return t;
        }

        private static void SetParams()
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void Bind(int unit = 0)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, Handle);
        }

        public void Dispose() { if (Handle > 0) GL.DeleteTexture(Handle); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SceneShader  –  compiles and caches a GLSL program
    // ═══════════════════════════════════════════════════════════════════════════
    public class SceneShader : IDisposable
    {
        public int Program { get; private set; }
        private readonly Dictionary<string, int> _locs = new();

        public static SceneShader Compile(string vertSrc, string fragSrc)
        {
            int vert = CompileStage(ShaderType.VertexShader, vertSrc);
            int frag = CompileStage(ShaderType.FragmentShader, fragSrc);
            int prog = GL.CreateProgram();
            GL.AttachShader(prog, vert); GL.AttachShader(prog, frag);
            GL.LinkProgram(prog);
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("Shader link: " + GL.GetProgramInfoLog(prog));
            GL.DeleteShader(vert); GL.DeleteShader(frag);
            return new SceneShader { Program = prog };
        }

        private static int CompileStage(ShaderType type, string src)
        {
             
            Console.WriteLine($"=== {type} SOURCE START ===");
            var lines1 = src.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines1.Length; i++)
            {
                string line = lines1[i].Replace("\t", "\\t").Replace(" ", "·"); // · for spaces
                Console.WriteLine($"{i + 1,3}| {line}");
                // Optional: hex dump suspicious lines
                if (i + 1 >= 60 && i + 1 <= 70) // around line 64
                {
                    Console.Write("HEX: ");
                    foreach (char c in lines1[i]) Console.Write($"{(int)c:X4} ");
                    Console.WriteLine();
                }
            }
            Console.WriteLine($"=== {type} SOURCE END ===\n");



            int id = GL.CreateShader(type);

            GL.ShaderSource(id, src);
            GL.CompileShader(id);

            GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                string log = GL.GetShaderInfoLog(id);
                string header = $"Failed to compile {type}:\n";
                // Optional: print first few lines of source with line numbers
                var lines = src.Split('\n');
                for (int i = 0; i < Math.Min(10, lines.Length); i++)
                    header += $"{i + 1,3}: {lines[i]}\n";
                header += "...\n";

                throw new Exception(header + "Compile log:\n" + log);
            }
            return id;
        }

        public void Use() => GL.UseProgram(Program);

        public void SetMat4(string name, ref Matrix4 m) => GL.UniformMatrix4(Loc(name), false, ref m);
        public void SetVec3(string name, Vector3 v) => GL.Uniform3(Loc(name), v);
        public void SetVec4(string name, Vector4 v) => GL.Uniform4(Loc(name), v);
        public void SetInt(string name, int v) => GL.Uniform1(Loc(name), v);
        public void SetFloat(string name, float v) => GL.Uniform1(Loc(name), v);

        private int Loc(string name)
        {
            if (!_locs.TryGetValue(name, out int l))
                _locs[name] = l = GL.GetUniformLocation(Program, name);
            return l;
        }

        public void Dispose() { if (Program > 0) GL.DeleteProgram(Program); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Material  –  wraps a shader + uniform values
    // ═══════════════════════════════════════════════════════════════════════════
    public class Material : IDisposable
    {
        public string Name { get; set; } = "Material";
        public SceneShader Shader { get; set; }
        public Texture2D AlbedoMap { get; set; } = Texture2D.White;
        public Vector4 Color { get; set; } = Vector4.One;
        public float Metallic { get; set; } = 0f;
        public float Roughness { get; set; } = 0.5f;
        public bool Wireframe { get; set; } = false;

        public Material(SceneShader shader) => Shader = shader;

        public void Bind()
        {
            if (Wireframe) GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            else GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

            Shader.Use();
            AlbedoMap.Bind(0);
            Shader.SetInt("uAlbedo", 0);
            Shader.SetVec4("uColor", Color);
            Shader.SetFloat("uMetallic", Metallic);
            Shader.SetFloat("uRoughness", Roughness);
        }

        public void Dispose() { Shader?.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Built-in GLSL source strings
    // ═══════════════════════════════════════════════════════════════════════════
    public static class BuiltinShaderSource
    {
        public const string StandardVert = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMat;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;

void main(){
    vec4 world = uModel * vec4(aPos,1.0);
    vWorldPos  = world.xyz;
    vNormal    = normalize(uNormalMat * aNormal);
    vUV        = aUV;
    gl_Position = uProjection * uView * world;
}";

        public const string StandardFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;

uniform sampler2D uAlbedo;
uniform vec4  uColor;
uniform float uMetallic;
uniform float uRoughness;
uniform vec3  uCamPos;

// -- Directional lights --------------------------------------
#define MAX_DIR_LIGHTS 4
uniform int  uDirCount;
uniform vec3 uDirDir  [MAX_DIR_LIGHTS];   // world-space direction (points away from light)
uniform vec3 uDirColor[MAX_DIR_LIGHTS];   // pre-multiplied by intensity

// -- Spot lights --------------------------------------
#define MAX_SPOT_LIGHTS 8
uniform int  uSpotCount;
uniform vec3  uSpotPos      [MAX_SPOT_LIGHTS];
uniform vec3  uSpotDir      [MAX_SPOT_LIGHTS];
uniform vec3  uSpotColor    [MAX_SPOT_LIGHTS];
uniform float uSpotRange    [MAX_SPOT_LIGHTS];
uniform float uSpotCosInner [MAX_SPOT_LIGHTS];  // cos(half inner angle)
uniform float uSpotCosOuter [MAX_SPOT_LIGHTS];  // cos(half outer angle)

// Ambient when no lights are present
uniform float uAmbient;

out vec4 FragColor;

void main(){
    vec4  albedo   = texture(uAlbedo, vUV) * uColor;
    vec3  N        = normalize(vNormal);
    vec3  V        = normalize(uCamPos - vWorldPos);
    float roughness = max(uRoughness, 0.01);
    float shininess = mix(8.0, 256.0, 1.0 - roughness);

    vec3 Lo = vec3(0.0);

    // -- Directional lights --------------------------------------
    for (int i = 0; i < uDirCount; i++) {
        vec3  L    = normalize(-uDirDir[i]);
        vec3  H    = normalize(L + V);
        float diff = max(dot(N, L), 0.0);
        float spec = pow(max(dot(N, H), 0.0), shininess);
        Lo += albedo.rgb * diff * uDirColor[i]
            + spec * uDirColor[i] * mix(0.04, 1.0, uMetallic);
    }

    // -- Spot lights --------------------------------------
    for(int i = 0; i < uSpotCount; i++)
	{
		vec3 toFrag = vWorldPos - uSpotPos[i];
		float dist = length(toFrag);
		if(dist > uSpotRange[i]) continue;
		vec3 L = normalize(-toFrag);
		float cosA = dot(normalize(toFrag), normalize(uSpotDir[i]));
		float cone = smoothstep(uSpotCosOuter[i], uSpotCosInner[i], cosA);
		if(cone <= 0.0) continue;
		float atten = cone * (1.0 - dist / uSpotRange[i]);
		vec3 H = normalize(L + V);
		float diff = max(dot(N, L), 0.0);
		float spec = pow(max(dot(N, H), 0.0), shininess);
		Lo += (albedo.rgb * diff + spec * mix(0.04, 1.0, uMetallic)) * uSpotColor[i] * atten;
	}

    // -- Ambient --------------------------------------
    vec3 col = albedo.rgb * uAmbient + Lo;
    FragColor = vec4(col, albedo.a);
}";

        // Grid / wireframe
        public const string GridVert = @"
#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uVP;
void main(){ gl_Position = uVP * vec4(aPos,1.0); }";

        public const string GridFrag = @"
#version 330 core
uniform vec4 uColor;
out vec4 FragColor;
void main(){ FragColor = uColor; }";

        // Flat (for selection highlight, bounding boxes etc.)
        public const string FlatFrag = @"
#version 330 core
uniform vec4 uColor;
out vec4 FragColor;
void main(){ FragColor = uColor; }";
    }
}