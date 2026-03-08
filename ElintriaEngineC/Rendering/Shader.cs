using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.IO;

namespace Elintria.Engine.Rendering
{
    /// <summary>
    /// Compiles and links a GLSL vertex + fragment shader pair.
    /// Exposes typed uniform setters for all types used by Material and MeshRenderer.
    ///
    /// Uniform location lookups are cached on first use so repeated
    /// SetXxx calls in the render loop don't re-query the driver.
    /// </summary>
    public class Shader : System.IDisposable
    {
        public int Handle { get; private set; }

        // Cached uniform locations  (name → location, -1 = not found)
        private readonly System.Collections.Generic.Dictionary<string, int> _locationCache = new();

        // ------------------------------------------------------------------
        // Constructor
        // ------------------------------------------------------------------
        public Shader(string vertPath, string fragPath)
        {
            // File existence check BEFORE reading (gives a clear error message)
            if (!File.Exists(vertPath))
                throw new FileNotFoundException($"Vertex shader not found: {vertPath}");
            if (!File.Exists(fragPath))
                throw new FileNotFoundException($"Fragment shader not found: {fragPath}");

            string vert = File.ReadAllText(vertPath);
            string frag = File.ReadAllText(fragPath);

            int vertex = CompileShader(ShaderType.VertexShader, vert, vertPath);
            int fragment = CompileShader(ShaderType.FragmentShader, frag, fragPath);

            Handle = GL.CreateProgram();
            GL.AttachShader(Handle, vertex);
            GL.AttachShader(Handle, fragment);
            GL.LinkProgram(Handle);

            GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int linkOk);
            if (linkOk == 0)
            {
                string log = GL.GetProgramInfoLog(Handle);
                throw new System.Exception($"Shader link error [{vertPath} / {fragPath}]:\n{log}");
            }

            // Shaders are baked into the program — we don't need the intermediates
            GL.DetachShader(Handle, vertex);
            GL.DetachShader(Handle, fragment);
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
        }

        // ------------------------------------------------------------------
        // Activate
        // ------------------------------------------------------------------
        public void Use() => GL.UseProgram(Handle);

        // ------------------------------------------------------------------
        // Uniform location (cached)
        // ------------------------------------------------------------------
        private int Loc(string name)
        {
            if (_locationCache.TryGetValue(name, out int loc)) return loc;
            loc = GL.GetUniformLocation(Handle, name);
            // loc == -1 means the uniform doesn't exist / was optimised away — silent ignore
            _locationCache[name] = loc;
            return loc;
        }

        // ------------------------------------------------------------------
        // Uniform setters
        // ------------------------------------------------------------------
        public void SetBool(string name, bool v) { int l = Loc(name); if (l >= 0) GL.Uniform1(l, v ? 1 : 0); }
        public void SetInt(string name, int v) { int l = Loc(name); if (l >= 0) GL.Uniform1(l, v); }
        public void SetFloat(string name, float v) { int l = Loc(name); if (l >= 0) GL.Uniform1(l, v); }

        public void SetVector2(string name, Vector2 v) { int l = Loc(name); if (l >= 0) GL.Uniform2(l, v.X, v.Y); }
        public void SetVector3(string name, Vector3 v) { int l = Loc(name); if (l >= 0) GL.Uniform3(l, v.X, v.Y, v.Z); }
        public void SetVector4(string name, Vector4 v) { int l = Loc(name); if (l >= 0) GL.Uniform4(l, v.X, v.Y, v.Z, v.W); }

        // Convenience overloads
        public void SetVector2(string name, float x, float y) => SetVector2(name, new Vector2(x, y));
        public void SetVector3(string name, float x, float y, float z) => SetVector3(name, new Vector3(x, y, z));
        public void SetVector4(string name, float x, float y, float z, float w) => SetVector4(name, new Vector4(x, y, z, w));

        public void SetColor(string name, System.Drawing.Color c)
            => SetVector4(name, c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        public void SetMatrix3(string name, Matrix3 v)
        {
            int l = Loc(name);
            if (l >= 0) GL.UniformMatrix3(l, false, ref v);
        }

        public void SetMatrix4(string name, Matrix4 v)
        {
            int l = Loc(name);
            if (l >= 0) GL.UniformMatrix4(l, false, ref v);
        }

        // ------------------------------------------------------------------
        // Compile helper
        // ------------------------------------------------------------------
        private static int CompileShader(ShaderType type, string source, string debugPath)
        {
            int id = GL.CreateShader(type);
            GL.ShaderSource(id, source);
            GL.CompileShader(id);

            GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                string log = GL.GetShaderInfoLog(id);
                GL.DeleteShader(id);
                throw new System.Exception($"Shader compile error ({type}) [{debugPath}]:\n{log}");
            }

            return id;
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------
        private bool _disposed = false;
        public void Dispose()
        {
            if (_disposed) return;
            GL.DeleteProgram(Handle);
            Handle = 0;
            _disposed = true;
        }
    }
}