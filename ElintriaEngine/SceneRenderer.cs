using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using ElintriaEngine.Core;

namespace ElintriaEngine.Rendering.Scene
{
    /// <summary>
    /// Renders a Scene to a GL viewport rectangle using the standard 3D shader.
    /// Owned by SceneViewPanel. Call Render() every frame.
    /// </summary>
    public class SceneRenderer : IDisposable
    {
        // ── Shader ────────────────────────────────────────────────────────────
        private SceneShader _stdShader = null!;
        private SceneShader _gridShader = null!;
        private SceneShader _flatShader = null!;

        // ── Primitive mesh cache ──────────────────────────────────────────────
        private readonly Dictionary<string, Mesh> _meshCache = new();
        private Mesh? _gridMesh;
        private Mesh? _axisMesh;

        // ── Default material ──────────────────────────────────────────────────
        private Material? _defaultMat;

        // ── Editor camera ─────────────────────────────────────────────────────
        public EditorCamera Camera { get; } = new();

        // ── Game camera override (set by game runtime, overrides EditorCamera) ─
        /// <summary>When non-null, used instead of EditorCamera for view.</summary>
        public Matrix4? GameViewMatrix { get; set; }
        /// <summary>When non-null, used instead of EditorCamera for projection.</summary>
        public Matrix4? GameProjMatrix { get; set; }

        // ── Selection outline ─────────────────────────────────────────────────
        public GameObject? Selected { get; set; }

        private bool _ready;

        // ── Init ──────────────────────────────────────────────────────────────
        public void Init()
        {
            if (_ready) return;
            _stdShader = SceneShader.Compile(BuiltinShaderSource.StandardVert, BuiltinShaderSource.StandardFrag);
            _gridShader = SceneShader.Compile(BuiltinShaderSource.GridVert, BuiltinShaderSource.GridFrag);
            _flatShader = SceneShader.Compile(BuiltinShaderSource.GridVert, BuiltinShaderSource.FlatFrag);
            _defaultMat = new Material(_stdShader);

            _meshCache["Cube"] = Mesh.Cube();
            _meshCache["Sphere"] = Mesh.Sphere();
            _meshCache["Plane"] = Mesh.Plane();
            _meshCache["Capsule"] = Mesh.Sphere(16, 12);   // approx
            _meshCache["Cylinder"] = Mesh.Sphere(16, 16);   // approx

            _gridMesh = BuildGrid(20, 1f);
            _axisMesh = BuildAxisLines();
            _ready = true;
        }

        // ── Main render ───────────────────────────────────────────────────────
        /// <param name="viewport">Window-space pixel rect for the scene view.</param>
        /// <param name="winW">Full window width – used for Y-flip.</param>
        /// <param name="winH">Full window height – used for Y-flip.</param>
        public void Render(RectangleF viewport, Core.Scene? scene, int winW, int winH)
        {
            if (!_ready) Init();

            // Flip Y: OpenGL origin is bottom-left, editor UI is top-left
            int vx = (int)viewport.X;
            int vy = winH - (int)(viewport.Y + viewport.Height);
            int vw = (int)viewport.Width;
            int vh = (int)viewport.Height;
            if (vw <= 0 || vh <= 0) return;

            GL.Viewport(vx, vy, vw, vh);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(vx, vy, vw, vh);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.ClearColor(0.15f, 0.16f, 0.17f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            float aspect = vw / (float)vh;
            var view = GameViewMatrix ?? Camera.GetViewMatrix();
            var proj = GameProjMatrix ?? Camera.GetProjectionMatrix(aspect);

            DrawGrid(view, proj);
            DrawAxisLines(view, proj);

            if (scene != null)
                foreach (var go in scene.All())
                    DrawGameObject(go, view, proj);

            // Selection outline
            if (Selected != null)
                DrawSelectionOutline(Selected, view, proj);

            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.DepthTest);
        }

        // ── Draw one GameObject ───────────────────────────────────────────────
        private void DrawGameObject(GameObject go, Matrix4 view, Matrix4 proj)
        {
            if (!go.ActiveSelf) return;

            // Need at least a MeshFilter to know what shape to draw
            var mf = go.GetComponent<Core.MeshFilter>();
            if (mf == null) return;

            string shapeName = string.IsNullOrEmpty(mf.MeshName) ? "Cube" : mf.MeshName;
            if (!_meshCache.TryGetValue(shapeName, out var mesh))
                mesh = _meshCache.TryGetValue("Cube", out var fallback) ? fallback : null;
            if (mesh == null) return;

            var model = go.Transform.LocalMatrix;
            var normalMat = Matrix3.Invert(Matrix3.Transpose(new Matrix3(model)));

            _defaultMat!.Bind();
            _stdShader.SetMat4("uModel", ref model);
            _stdShader.SetMat4("uView", ref view);
            _stdShader.SetMat4("uProjection", ref proj);
            GL.UniformMatrix3(GL.GetUniformLocation(_stdShader.Program, "uNormalMat"),
                false, ref normalMat);
            _stdShader.SetVec3("uLightDir", new Vector3(-0.6f, -1f, -0.5f).Normalized());
            _stdShader.SetVec3("uLightColor", Vector3.One);
            _stdShader.SetVec3("uCamPos", Camera.Position);

            // Per-object colour tint based on the MeshRenderer component
            var mr = go.GetComponent<Core.MeshRenderer>();
            _stdShader.SetVec4("uColor", mr != null ? new Vector4(0.8f, 0.82f, 0.85f, 1f) : Vector4.One);

            mesh.Draw();
        }

        // ── Selection outline (scale-up + flat colour) ────────────────────────
        private void DrawSelectionOutline(GameObject go, Matrix4 view, Matrix4 proj)
        {
            var mf = go.GetComponent<Core.MeshFilter>();
            if (mf == null) return;
            if (!_meshCache.TryGetValue(mf.MeshName.Length > 0 ? mf.MeshName : "Cube", out var mesh)) return;

            // Slightly scaled-up model
            var t = go.Transform;
            var scl = Matrix4.CreateScale(t.LocalScale + new Vector3(0.04f));
            var rot = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(t.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Z)));
            var tr = Matrix4.CreateTranslation(t.LocalPosition);
            var model = scl * rot * tr;

            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Front);
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

            _flatShader.Use();
            _flatShader.SetMat4("uModel", ref model);
            _flatShader.SetMat4("uView", ref view);
            _flatShader.SetMat4("uProjection", ref proj);
            _flatShader.SetVec4("uColor", new Vector4(1f, 0.6f, 0.1f, 1f));
            mesh.Draw();

            GL.CullFace(CullFaceMode.Back);
            GL.Disable(EnableCap.CullFace);
        }

        // ── Grid ──────────────────────────────────────────────────────────────
        private void DrawGrid(Matrix4 view, Matrix4 proj)
        {
            if (_gridMesh == null) return;
            var vp = view * proj;
            _gridShader.Use();
            GL.UniformMatrix4(GL.GetUniformLocation(_gridShader.Program, "uVP"), false, ref vp);
            GL.Uniform4(GL.GetUniformLocation(_gridShader.Program, "uColor"), 0.35f, 0.35f, 0.35f, 1f);
            GL.LineWidth(1f);
            GL.BindVertexArray(_gridMesh.VAO);
            GL.DrawElements(PrimitiveType.Lines, _gridMesh.IndexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }

        private void DrawAxisLines(Matrix4 view, Matrix4 proj)
        {
            if (_axisMesh == null) return;
            var vp = view * proj;
            _gridShader.Use();
            GL.UniformMatrix4(GL.GetUniformLocation(_gridShader.Program, "uVP"), false, ref vp);
            GL.LineWidth(2f);
            GL.BindVertexArray(_axisMesh.VAO);
            // We draw 6 indices = 3 lines, each a different colour
            // Colour is set per draw call via uColor
            var colors = new[] {
                new Vector4(0.9f,0.2f,0.2f,1), // X red
                new Vector4(0.2f,0.9f,0.2f,1), // Y green
                new Vector4(0.2f,0.5f,0.9f,1), // Z blue
            };
            for (int i = 0; i < 3; i++)
            {
                GL.Uniform4(GL.GetUniformLocation(_gridShader.Program, "uColor"),
                    colors[i].X, colors[i].Y, colors[i].Z, colors[i].W);
                GL.DrawElements(PrimitiveType.Lines, 2, DrawElementsType.UnsignedInt, i * 2 * sizeof(int));
            }
            GL.BindVertexArray(0);
        }

        // ── Mesh builders ─────────────────────────────────────────────────────
        private static Mesh BuildGrid(int halfSize, float spacing)
        {
            var verts = new List<float>();
            var idxs = new List<uint>();
            uint vi = 0;
            for (int i = -halfSize; i <= halfSize; i++)
            {
                float f = i * spacing;
                // horizontal
                verts.AddRange(new float[] { -halfSize * spacing, 0, f, 0, 0, 0, 0, 0 });
                verts.AddRange(new float[] { halfSize * spacing, 0, f, 0, 0, 0, 0, 0 });
                idxs.Add(vi); idxs.Add(vi + 1); vi += 2;
                // vertical
                verts.AddRange(new float[] { f, 0, -halfSize * spacing, 0, 0, 0, 0, 0 });
                verts.AddRange(new float[] { f, 0, halfSize * spacing, 0, 0, 0, 0, 0 });
                idxs.Add(vi); idxs.Add(vi + 1); vi += 2;
            }
            return Mesh.FromArrays("Grid", verts.ToArray(), idxs.ToArray());
        }

        private static Mesh BuildAxisLines()
        {
            float[] v = {
                0,0,0, 0,0,0, 0,0,   5,0,0, 0,0,0, 0,0,   // X
                0,0,0, 0,0,0, 0,0,   0,5,0, 0,0,0, 0,0,   // Y
                0,0,0, 0,0,0, 0,0,   0,0,5, 0,0,0, 0,0,   // Z
            };
            uint[] idx = { 0, 1, 2, 3, 4, 5 };
            return Mesh.FromArrays("Axes", v, idx);
        }

        // winH is now passed directly - no need to query GL

        public void Dispose()
        {
            foreach (var m in _meshCache.Values) m.Dispose();
            _gridMesh?.Dispose(); _axisMesh?.Dispose();
            _stdShader?.Dispose(); _gridShader?.Dispose(); _flatShader?.Dispose();
            _defaultMat?.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EditorCamera  –  orbit / pan / zoom
    // ═══════════════════════════════════════════════════════════════════════════
    public class EditorCamera
    {
        public Vector3 Target { get; set; } = Vector3.Zero;
        public float Distance { get; set; } = 8f;
        public float Yaw { get; set; } = 45f;
        public float Pitch { get; set; } = 25f;
        public float FovDeg { get; set; } = 60f;
        public float Near { get; set; } = 0.05f;
        public float Far { get; set; } = 2000f;

        public Vector3 Position
        {
            get
            {
                float yRad = MathHelper.DegreesToRadians(Yaw);
                float pRad = MathHelper.DegreesToRadians(Pitch);
                return Target + new Vector3(
                    Distance * MathF.Cos(pRad) * MathF.Sin(yRad),
                    Distance * MathF.Sin(pRad),
                    Distance * MathF.Cos(pRad) * MathF.Cos(yRad));
            }
        }

        public Matrix4 GetViewMatrix() => Matrix4.LookAt(Position, Target, Vector3.UnitY);
        public Matrix4 GetProjectionMatrix(float aspect) =>
            Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(FovDeg), aspect, Near, Far);
    }
}