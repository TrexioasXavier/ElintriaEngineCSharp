using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Collections.Generic;
using System.Drawing;

namespace Elintria.Engine.Rendering
{
    /// <summary>
    /// A Material owns a Shader and a set of named properties (uniforms + textures).
    /// It mirrors Unity's material API closely.
    ///
    /// Usage:
    ///   var mat = new Material(myShader);
    ///   mat.SetColor("uColor", Color.Red);
    ///   mat.SetFloat("uRoughness", 0.5f);
    ///   mat.SetTexture("uAlbedo", myTexture);
    ///   mat.Bind();        // activates shader + uploads uniforms + binds textures
    ///   mesh.Draw();
    ///   mat.Unbind();
    /// </summary>
    public class Material : System.IDisposable
    {
        // ------------------------------------------------------------------
        // Identity
        // ------------------------------------------------------------------
        public string Name { get; set; }
        public Shader Shader { get; set; }

        // ------------------------------------------------------------------
        // Render state
        // ------------------------------------------------------------------
        public bool DepthTest { get; set; } = true;
        public bool DepthWrite { get; set; } = true;
        public bool AlphaBlend { get; set; } = false;
        public CullFaceMode CullMode { get; set; } = CullFaceMode.Back;
        public bool DoubleSided { get; set; } = false;

        public Texture MainTexture;

        // ------------------------------------------------------------------
        // Built-in PBR-style property names (match your shader uniforms)
        // ------------------------------------------------------------------
        public static class Props
        {
            public const string Color = "uColor";
            public const string Albedo = "uAlbedo";
            public const string NormalMap = "uNormalMap";
            public const string MetallicMap = "uMetallicMap";
            public const string RoughnessMap = "uRoughnessMap";
            public const string EmissiveMap = "uEmissiveMap";
            public const string Metallic = "uMetallic";
            public const string Roughness = "uRoughness";
            public const string EmissiveColor = "uEmissive";
            public const string Tiling = "uTiling";
            public const string Offset = "uOffset";
        }

        // ------------------------------------------------------------------
        // Property stores
        // ------------------------------------------------------------------
        private Dictionary<string, float> _floats = new();
        private Dictionary<string, Vector2> _vec2s = new();
        private Dictionary<string, Vector3> _vec3s = new();
        private Dictionary<string, Vector4> _vec4s = new();
        private Dictionary<string, Matrix4> _mats = new();
        private Dictionary<string, int> _ints = new();
        private Dictionary<string, Texture> _textures = new();

        // ------------------------------------------------------------------
        // Constructors
        // ------------------------------------------------------------------
        public Material(Shader shader, string name = "Material")
        {
            Shader = shader;
            Name = name;

            // Sensible PBR defaults
            SetColor(Props.Color, Color.White);
            SetFloat(Props.Metallic, 0f);
            SetFloat(Props.Roughness, 0.5f);
            SetVector3(Props.EmissiveColor, Vector3.Zero);
            SetVector2(Props.Tiling, Vector2.One);
            SetVector2(Props.Offset, Vector2.Zero);
        }

        // ------------------------------------------------------------------
        // Property setters
        // ------------------------------------------------------------------
        public void SetFloat(string name, float v) => _floats[name] = v;
        public void SetInt(string name, int v) => _ints[name] = v;
        public void SetVector2(string name, Vector2 v) => _vec2s[name] = v;
        public void SetVector3(string name, Vector3 v) => _vec3s[name] = v;
        public void SetVector4(string name, Vector4 v) => _vec4s[name] = v;
        public void SetMatrix4(string name, Matrix4 v) => _mats[name] = v;
        public void SetColor(string name, Color c)
            => _vec4s[name] = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        public void SetTexture(string name, Texture tex) => _textures[name] = tex;

        // ------------------------------------------------------------------
        // Property getters
        // ------------------------------------------------------------------
        public float GetFloat(string name, float def = 0f) => _floats.GetValueOrDefault(name, def);
        public int GetInt(string name, int def = 0) => _ints.GetValueOrDefault(name, def);
        public Vector2 GetVector2(string name, Vector2 def = default) => _vec2s.GetValueOrDefault(name, def);
        public Vector3 GetVector3(string name, Vector3 def = default) => _vec3s.GetValueOrDefault(name, def);
        public Vector4 GetVector4(string name, Vector4 def = default) => _vec4s.GetValueOrDefault(name, def);
        public Matrix4 GetMatrix4(string name, Matrix4 def = default) => _mats.GetValueOrDefault(name, def);
        public Texture GetTexture(string name) => _textures.GetValueOrDefault(name);
        

        public bool HasTexture(string name) => _textures.ContainsKey(name) && _textures[name] != null;

        // ------------------------------------------------------------------
        // Bind / Unbind
        // ------------------------------------------------------------------
        public void Bind()
        {
            if (Shader == null) return;

            // Render state
            if (DepthTest) GL.Enable(EnableCap.DepthTest);
            else GL.Disable(EnableCap.DepthTest);

            GL.DepthMask(DepthWrite);

            if (DoubleSided) GL.Disable(EnableCap.CullFace);
            else { GL.Enable(EnableCap.CullFace); GL.CullFace(CullMode); }

            if (AlphaBlend)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }
            else GL.Disable(EnableCap.Blend);

            Shader.Use();

            // Upload all properties as uniforms
            foreach (var kv in _floats) Shader.SetFloat(kv.Key, kv.Value);
            foreach (var kv in _ints) Shader.SetInt(kv.Key, kv.Value);
            foreach (var kv in _vec2s) Shader.SetVector2(kv.Key, kv.Value);
            foreach (var kv in _vec3s) Shader.SetVector3(kv.Key, kv.Value);
            foreach (var kv in _vec4s) Shader.SetVector4(kv.Key, kv.Value);
            foreach (var kv in _mats) Shader.SetMatrix4(kv.Key, kv.Value);

            // Bind textures in declaration order
            int unit = 0;
            foreach (var kv in _textures)
            {
                if (kv.Value == null) continue;
                kv.Value.Bind(TextureUnit.Texture0 + unit);
                Shader.SetInt(kv.Key, unit);
                unit++;
            }
        }

        /// <summary>
        /// Unbinds the shader. Does NOT reset GL state — the caller (Editor/
        /// RenderLoop) owns all GL state transitions between passes to avoid
        /// unexpected side-effects on the UI or world-text render passes.
        /// </summary>
        public void Unbind()
        {
            GL.UseProgram(0);
        }

        // ------------------------------------------------------------------
        // Clone
        // ------------------------------------------------------------------
        public Material Clone(string newName = null)
        {
            var m = new Material(Shader, newName ?? Name + "_Clone");
            m._floats = new Dictionary<string, float>(_floats);
            m._ints = new Dictionary<string, int>(_ints);
            m._vec2s = new Dictionary<string, Vector2>(_vec2s);
            m._vec3s = new Dictionary<string, Vector3>(_vec3s);
            m._vec4s = new Dictionary<string, Vector4>(_vec4s);
            m._mats = new Dictionary<string, Matrix4>(_mats);
            m._textures = new Dictionary<string, Texture>(_textures);
            m.DepthTest = DepthTest;
            m.DepthWrite = DepthWrite;
            m.AlphaBlend = AlphaBlend;
            m.CullMode = CullMode;
            m.DoubleSided = DoubleSided;
            return m;
        }

        public void Dispose() { /* Shader/Textures owned elsewhere */ }
    }
}