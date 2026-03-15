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
        private SceneShader _particleShader = null!;

        // ── Primitive mesh cache ──────────────────────────────────────────────
        private readonly Dictionary<string, Mesh> _meshCache = new();
        private Mesh? _gridMesh;
        private Mesh? _axisMesh;

        // ── Default material ──────────────────────────────────────────────────
        private Material? _defaultMat;

        // ── Editor camera ─────────────────────────────────────────────────────
        public EditorCamera Camera { get; } = new();

        // ── Gizmo renderer ────────────────────────────────────────────────
        public GizmoRenderer Gizmos { get; } = new();

        // ── Game camera override (set by game runtime, overrides EditorCamera) ─
        /// <summary>When non-null, used instead of EditorCamera for view.</summary>
        public Matrix4? GameViewMatrix { get; set; }
        /// <summary>When non-null, used instead of EditorCamera for projection.</summary>
        public Matrix4? GameProjMatrix { get; set; }

        // ── Selection outline ─────────────────────────────────────────────────
        public GameObject? Selected
        {
            get => _selected;
            set { _selected = value; Gizmos.HandleTarget = value; }
        }
        private GameObject? _selected;

        private bool _ready;

        // ── Init ──────────────────────────────────────────────────────────────
        public void Init()
        {
            if (_ready) return;
            _stdShader = SceneShader.Compile(BuiltinShaderSource.StandardVert, BuiltinShaderSource.StandardFrag);
            _gridShader = SceneShader.Compile(BuiltinShaderSource.GridVert, BuiltinShaderSource.GridFrag);
            _flatShader = SceneShader.Compile(BuiltinShaderSource.GridVert, BuiltinShaderSource.FlatFrag);
            _particleShader = SceneShader.Compile(BuiltinShaderSource.ParticleVert, BuiltinShaderSource.FlatFrag);
            _defaultMat = new Material(_stdShader);

            _meshCache["Cube"] = Mesh.Cube();
            _meshCache["Sphere"] = Mesh.Sphere();
            _meshCache["Plane"] = Mesh.Plane();
            _meshCache["Capsule"] = Mesh.Sphere(16, 12);   // approx
            _meshCache["Cylinder"] = Mesh.Sphere(16, 16);   // approx

            _gridMesh = BuildGrid(20, 1f);
            _axisMesh = BuildAxisLines();
            Gizmos.Init();
            _ready = true;
        }

        // ── Render mode ───────────────────────────────────────────────────────
        /// When true, game-camera rules apply: no grid/axes, black if no camera,
        /// black if no lights. When false, editor camera + hardcoded light.
        public bool IsPlayMode { get; set; } = false;

        // ── Main render ───────────────────────────────────────────────────────
        public void Render(RectangleF viewport, Core.Scene? scene, int winW, int winH)
        {
            if (!_ready) Init();

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

            float aspect = vw / (float)vh;

            // ── Find game camera ───────────────────────────────────────────────
            Core.Camera? gameCam = null;
            if (scene != null)
                foreach (var go in scene.All())
                    if (go.ActiveSelf)
                    {
                        var c = go.GetComponent<Core.Camera>();
                        if (c != null && c.Enabled) { gameCam = c; break; }
                    }

            // ── In play mode: black screen if no camera ────────────────────────
            if (IsPlayMode && gameCam == null && GameViewMatrix == null)
            {
                GL.ClearColor(0f, 0f, 0f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.Disable(EnableCap.ScissorTest);
                GL.Disable(EnableCap.DepthTest);
                return;
            }

            // ── Choose view / projection ───────────────────────────────────────
            Matrix4 view, proj;
            Vector3 camPos;
            if (GameViewMatrix != null)
            {
                view = GameViewMatrix.Value;
                proj = GameProjMatrix ?? Camera.GetProjectionMatrix(aspect);
                // Invert view to get camera world position
                var inv = view; inv.Invert();
                camPos = inv.ExtractTranslation();
            }
            else if (IsPlayMode && gameCam != null)
            {
                view = gameCam.GetViewMatrix();
                proj = gameCam.GetProjectionMatrix(aspect);
                camPos = gameCam.Position;
                // Background colour from camera settings
                GL.ClearColor(gameCam.BackgroundR, gameCam.BackgroundG, gameCam.BackgroundB, 1f);
            }
            else
            {
                view = Camera.GetViewMatrix();
                proj = Camera.GetProjectionMatrix(aspect);
                camPos = Camera.Position;
                GL.ClearColor(0.15f, 0.16f, 0.17f, 1f);
            }

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // ── Collect lights from scene ──────────────────────────────────────
            var dirLights = new List<Core.DirectionalLight>();
            var spotLights = new List<Core.SpotLight>();
            if (scene != null)
                foreach (var go in scene.All())
                    if (go.ActiveSelf)
                    {
                        var dl = go.GetComponent<Core.DirectionalLight>();
                        if (dl != null && dl.Enabled) dirLights.Add(dl);
                        var sl = go.GetComponent<Core.SpotLight>();
                        if (sl != null && sl.Enabled) spotLights.Add(sl);
                        // Legacy Light component -> treat as directional
                        var ll = go.GetComponent<Core.Light>();
                        if (ll != null && ll.Enabled && ll.LightType == "Directional")
                        {
                            // Fake a DirectionalLight from the legacy component
                            dirLights.Add(new Core.DirectionalLight
                            {
                                ColorR = ll.ColorR,
                                ColorG = ll.ColorG,
                                ColorB = ll.ColorB,
                                Intensity = ll.Intensity,
                                GameObject = go,
                                Enabled = true,
                            });
                        }
                    }

            // In play mode with no lights → black ambient (0), not editor default
            float ambient = IsPlayMode
                ? (dirLights.Count == 0 && spotLights.Count == 0 ? 0f : 0.15f)
                : 0.22f;

            // Editor mode uses a hardcoded directional light when scene has none
            bool editorFakeLightNeeded = !IsPlayMode && dirLights.Count == 0 && spotLights.Count == 0;

            if (!IsPlayMode)
            {
                DrawGrid(view, proj);
                DrawAxisLines(view, proj);
            }

            if (scene != null)
                foreach (var go in scene.All())
                    DrawGameObject(go, view, proj, camPos, dirLights, spotLights,
                                   ambient, editorFakeLightNeeded);

            // ── Particle Systems ──────────────────────────────────────────────
            if (scene != null)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.DepthMask(false);
                foreach (var go in scene.All())
                {
                    var ps = go.GetComponent<Core.ParticleSystem>();
                    if (ps == null || !ps.RendererEnabled || !ps.IsPlaying) continue;
                    DrawParticles(ps, go, view, proj);
                }
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
            }

            // Gizmos: scene-space overlays, always on top (depth-test off inside).
            // IMPORTANT: pass the original UI-space 'viewport' rect (top-left origin),
            // NOT (vx,vy,vw,vh) which is GL-space (bottom-left origin). Mouse coords
            // are UI-space, so WorldToScreen must use the same coordinate system.
            if (!IsPlayMode)
                Gizmos.Render(view, proj, camPos, scene, viewport);

            if (Selected != null)
                DrawSelectionOutline(Selected, view, proj);

            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.DepthTest);
        }

        // ── Draw one GameObject ───────────────────────────────────────────────
        private void DrawGameObject(
            GameObject go, Matrix4 view, Matrix4 proj, Vector3 camPos,
            List<Core.DirectionalLight> dirLights,
            List<Core.SpotLight> spotLights,
            float ambient, bool editorFakeLight)
        {
            if (!go.ActiveSelf) return;
            var scale = go.Transform.LocalScale;
            if (scale.X == 0f || scale.Y == 0f || scale.Z == 0f) return;

            var mf = go.GetComponent<Core.MeshFilter>();
            if (mf == null) return;

            string shapeName = string.IsNullOrEmpty(mf.MeshName) ? "Cube" : mf.MeshName;
            if (!_meshCache.TryGetValue(shapeName, out var mesh))
                mesh = _meshCache.TryGetValue("Cube", out var fallback) ? fallback : null;
            if (mesh == null) return;

            var model = go.Transform.LocalMatrix;
            var m3 = new Matrix3(model);
            var normalMat = MathF.Abs(m3.Determinant) > 1e-6f
                ? Matrix3.Invert(Matrix3.Transpose(m3)) : Matrix3.Identity;

            // ── Material / shader selection ───────────────────────────────────
            var mr = go.GetComponent<Core.MeshRenderer>();
            string matPath = mr?.MaterialPath ?? "";

            SceneShader activeShader = _stdShader;
            Material activeMat = _defaultMat!;
            Core.MaterialAsset? matAsset = null;

            if (!string.IsNullOrEmpty(matPath) && System.IO.File.Exists(matPath))
            {
                matAsset = LoadOrGetMaterialAsset(matPath);
                if (matAsset != null)
                {
                    var compiledMat = LoadOrGetCompiledMaterial(matPath, matAsset);
                    if (compiledMat != null) { activeMat = compiledMat; activeShader = compiledMat.Shader; }
                }
            }

            // Bind material (uses asset properties if present, standard uniforms otherwise)
            if (matAsset != null && matAsset.DeclaredProperties.Count > 0)
                activeMat.BindFromAsset(matAsset);
            else
                activeMat.Bind();

            int prog = activeShader.Program;

            activeShader.SetMat4("uModel", ref model);
            activeShader.SetMat4("uView", ref view);
            activeShader.SetMat4("uProjection", ref proj);
            GL.UniformMatrix3(GL.GetUniformLocation(prog, "uNormalMat"), false, ref normalMat);
            // Try setting common uniforms (silently ignored if not in custom shader)
            TrySetUniform(prog, "uCamPos", camPos);
            TrySetUniform(prog, "uAmbient", ambient);
            TrySetUniform(prog, "uMetallic", mr?.Metallic ?? 0f);
            TrySetUniform(prog, "uRoughness", mr?.Roughness ?? 0.5f);

            // Directional lights
            int dirCount = editorFakeLight ? 1 : Math.Min(dirLights.Count, 4);
            GL.Uniform1(GL.GetUniformLocation(prog, "uDirCount"), dirCount);
            if (editorFakeLight)
            {
                var fakeDir = new Vector3(-0.6f, -1f, -0.5f).Normalized();
                GL.Uniform3(GL.GetUniformLocation(prog, "uDirDir[0]"), fakeDir.X, fakeDir.Y, fakeDir.Z);
                GL.Uniform3(GL.GetUniformLocation(prog, "uDirColor[0]"), 1.0f, 1.0f, 1.0f);
            }
            else
            {
                for (int i = 0; i < dirCount; i++)
                {
                    var d = dirLights[i];
                    var dir = d.Direction;
                    GL.Uniform3(GL.GetUniformLocation(prog, $"uDirDir[{i}]"),
                        dir.X, dir.Y, dir.Z);
                    GL.Uniform3(GL.GetUniformLocation(prog, $"uDirColor[{i}]"),
                        d.ColorR * d.Intensity, d.ColorG * d.Intensity, d.ColorB * d.Intensity);
                }
            }

            // Spot lights
            int spotCount = Math.Min(spotLights.Count, 8);
            GL.Uniform1(GL.GetUniformLocation(prog, "uSpotCount"), spotCount);
            for (int i = 0; i < spotCount; i++)
            {
                var s = spotLights[i];
                var pos = s.Position;
                var dir = s.Direction;
                float inner = MathF.Cos(MathHelper.DegreesToRadians(s.SpotAngle * (1f - s.BlendFraction)));
                float outer = MathF.Cos(MathHelper.DegreesToRadians(s.SpotAngle));
                GL.Uniform3(GL.GetUniformLocation(prog, $"uSpotPos[{i}]"), pos.X, pos.Y, pos.Z);
                GL.Uniform3(GL.GetUniformLocation(prog, $"uSpotDir[{i}]"), dir.X, dir.Y, dir.Z);
                GL.Uniform3(GL.GetUniformLocation(prog, $"uSpotColor[{i}]"),
                    s.ColorR * s.Intensity, s.ColorG * s.Intensity, s.ColorB * s.Intensity);
                GL.Uniform1(GL.GetUniformLocation(prog, $"uSpotRange[{i}]"), s.Range);
                GL.Uniform1(GL.GetUniformLocation(prog, $"uSpotCosInner[{i}]"), inner);
                GL.Uniform1(GL.GetUniformLocation(prog, $"uSpotCosOuter[{i}]"), outer);
            }

            // Only override color if no custom material provided it via _Color property
            if (matAsset == null || !matAsset.Properties.Has("_Color"))
            {
                TrySetUniform(prog, "uColor", mr != null
                    ? new Vector4(mr.AlbedoR, mr.AlbedoG, mr.AlbedoB, 1f)
                    : new Vector4(0.8f, 0.82f, 0.85f, 1f));
            }

            mesh.Draw();
        }

        // ── Material asset caches ─────────────────────────────────────────────
        private readonly Dictionary<string, Core.MaterialAsset> _matAssetCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Material> _compiledMatCache = new(StringComparer.OrdinalIgnoreCase);

        private Core.MaterialAsset? LoadOrGetMaterialAsset(string matPath)
        {
            if (!_matAssetCache.TryGetValue(matPath, out var asset))
            {
                asset = Core.MaterialAsset.Load(matPath);
                // Load declared properties from the referenced shader
                if (!string.IsNullOrEmpty(asset.ShaderPath))
                {
                    string src = LoadShaderSource(asset.ShaderPath);
                    if (!string.IsNullOrEmpty(src))
                        asset.DeclaredProperties.AddRange(Core.MaterialAsset.ParseShaderProperties(src));
                }
                _matAssetCache[matPath] = asset;
            }
            return asset;
        }

        private Material? LoadOrGetCompiledMaterial(string matPath, Core.MaterialAsset asset)
        {
            if (_compiledMatCache.TryGetValue(matPath, out var existing)) return existing;
            try
            {
                string src = LoadShaderSource(asset.ShaderPath);
                if (string.IsNullOrEmpty(src)) return null;
                var (vert, frag) = SplitShaderSource(src);
                if (string.IsNullOrEmpty(vert) || string.IsNullOrEmpty(frag)) return null;
                var sh = SceneShader.Compile(vert, frag);
                var mat = new Material(sh) { Name = System.IO.Path.GetFileNameWithoutExtension(matPath) };
                _compiledMatCache[matPath] = mat;
                Console.WriteLine($"[SceneRenderer] Compiled shader for '{System.IO.Path.GetFileName(matPath)}'");
                return mat;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SceneRenderer] Shader compile failed: {ex.Message}");
                return null;
            }
        }

        public void InvalidateMaterial(string matPath)
        {
            _matAssetCache.Remove(matPath);
            if (_compiledMatCache.TryGetValue(matPath, out var old)) { old.Dispose(); _compiledMatCache.Remove(matPath); }
        }

        private string LoadShaderSource(string shaderPathOrName)
        {
            // Full path
            if (System.IO.File.Exists(shaderPathOrName))
                return System.IO.File.ReadAllText(shaderPathOrName);
            // Name only — search standard locations (not built-in names like "Standard")
            return "";
        }

        private static (string Vert, string Frag) SplitShaderSource(string src)
        {
            // Split on #pragma vertex / #pragma fragment markers
            int vIdx = src.IndexOf("#pragma vertex", StringComparison.OrdinalIgnoreCase);
            int fIdx = src.IndexOf("#pragma fragment", StringComparison.OrdinalIgnoreCase);
            if (vIdx < 0 || fIdx < 0) return ("", "");

            // Vertex: from after "#pragma vertex\n" to before "#pragma fragment"
            int vStart = src.IndexOf('\n', vIdx) + 1;
            string vert = src[vStart..fIdx].Trim();

            // Fragment: everything after "#pragma fragment\n"
            int fStart = src.IndexOf('\n', fIdx) + 1;
            string frag = src[fStart..].Trim();

            return (vert, frag);
        }

        // Safely set uniforms — silently no-ops if the uniform doesn't exist in the current shader
        private static void TrySetUniform(int prog, string name, float v)
        {
            int loc = GL.GetUniformLocation(prog, name);
            if (loc >= 0) GL.Uniform1(loc, v);
        }
        private static void TrySetUniform(int prog, string name, Vector3 v)
        {
            int loc = GL.GetUniformLocation(prog, name);
            if (loc >= 0) GL.Uniform3(loc, v);
        }
        private static void TrySetUniform(int prog, string name, Vector4 v)
        {
            int loc = GL.GetUniformLocation(prog, name);
            if (loc >= 0) GL.Uniform4(loc, v.X, v.Y, v.Z, v.W);
        }

        // ── Particle rendering (billboard quads) ──────────────────────────────
        private void DrawParticles(Core.ParticleSystem ps, GameObject go, Matrix4 view, Matrix4 proj)
        {
            if (ps.Particles.Count == 0) return;

            // Get camera right/up vectors from view matrix for billboarding
            var camRight = new Vector3(view.Row0.X, view.Row0.Y, view.Row0.Z);
            var camUp = new Vector3(view.Row1.X, view.Row1.Y, view.Row1.Z);

            GL.UseProgram(_particleShader.Program);
            _particleShader.SetMat4("uView", ref view);
            _particleShader.SetMat4("uProjection", ref proj);

            // World offset for local-space particles
            var worldOffset = ps.SimulationSpace == Core.ParticleSimulationSpace.Local
                ? go.Transform.LocalPosition : Vector3.Zero;

            foreach (var p in ps.Particles)
            {
                float s = p.CurrentSize * 0.5f;
                // Billboard model matrix: right and up from camera, translate to particle pos
                var pos = p.Position + worldOffset;
                var model = new Matrix4(
                    new Vector4(camRight * s, 0f),
                    new Vector4(camUp * s, 0f),
                    new Vector4(-Vector3.Cross(camRight, camUp), 0f),
                    new Vector4(pos.X, pos.Y, pos.Z, 1f));

                _particleShader.SetMat4("uModel", ref model);
                _particleShader.SetVec4("uColor",
                    new Vector4(p.ColorR, p.ColorG, p.ColorB, p.ColorA));

                DrawParticleQuad();
            }
        }

        private int _particleVao = -1;
        private int _particleVbo = -1;

        private void DrawParticleQuad()
        {
            if (_particleVao < 0) InitParticleQuad();
            GL.BindVertexArray(_particleVao);
            GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
            GL.BindVertexArray(0);
        }

        private void InitParticleQuad()
        {
            float[] verts = {
                -1f, -1f, 0f,
                 1f, -1f, 0f,
                 1f,  1f, 0f,
                -1f,  1f, 0f,
            };
            _particleVao = GL.GenVertexArray();
            _particleVbo = GL.GenBuffer();
            GL.BindVertexArray(_particleVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _particleVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
            GL.BindVertexArray(0);
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
            foreach (var m in _compiledMatCache.Values) m.Dispose();
            _gridMesh?.Dispose(); _axisMesh?.Dispose();
            _stdShader?.Dispose(); _gridShader?.Dispose(); _flatShader?.Dispose();
            _defaultMat?.Dispose();
            Gizmos.Dispose();
        }

        /// <summary>
        /// Loads a model file (.obj / .fbx) into the mesh cache keyed by the full path.
        /// Returns the cache key on success, or null with an error logged on failure.
        /// Safe to call multiple times — skips loading if already cached.
        /// </summary>
        public string? LoadModelFromFile(string filePath)
        {
            // Normalise to lowercase for case-insensitive matching across platforms
            string key = filePath.ToLowerInvariant();
            if (_meshCache.ContainsKey(key)) return key;

            var (verts, indices, err) = ModelLoader.Load(filePath);

            if (verts.Length == 0 || indices.Length == 0)
            {
                Console.WriteLine($"[SceneRenderer] Model load failed '{System.IO.Path.GetFileName(filePath)}': {err}");
                return null;   // do NOT cache failures — allow retry on next drop
            }

            var mesh = Mesh.FromArrays(key, verts, indices);
            _meshCache[key] = mesh;
            Console.WriteLine($"[SceneRenderer] Loaded '{System.IO.Path.GetFileName(filePath)}' " +
                              $"— {indices.Length / 3} tris, {verts.Length / 8} verts");
            return key;
        }

        public bool IsModelLoaded(string key) => _meshCache.ContainsKey(key.ToLowerInvariant());
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

        // ── Derived geometry ──────────────────────────────────────────────────
        // Offset from Target to Position in spherical coords
        private Vector3 SphericalOffset
        {
            get
            {
                float yr = MathHelper.DegreesToRadians(Yaw);
                float pr = MathHelper.DegreesToRadians(Pitch);
                return new Vector3(
                    Distance * MathF.Cos(pr) * MathF.Sin(yr),
                    Distance * MathF.Sin(pr),
                    Distance * MathF.Cos(pr) * MathF.Cos(yr));
            }
        }

        public Vector3 Position => Target + SphericalOffset;

        // Forward = direction camera is looking (from Position toward Target)
        public Vector3 Forward
        {
            get
            {
                float yr = MathHelper.DegreesToRadians(Yaw);
                float pr = MathHelper.DegreesToRadians(Pitch);
                return -new Vector3(
                    MathF.Cos(pr) * MathF.Sin(yr),
                    MathF.Sin(pr),
                    MathF.Cos(pr) * MathF.Cos(yr));
            }
        }

        // Right = perpendicular to forward in the horizontal plane
        public Vector3 Right
        {
            get
            {
                float yr = MathHelper.DegreesToRadians(Yaw);
                return new Vector3(MathF.Cos(yr), 0f, -MathF.Sin(yr));
            }
        }

        // ── Orbit (right-click drag) ───────────────────────────────────────────
        // Classic orbit: position orbits around fixed Target point.
        public void Orbit(float dYaw, float dPitch)
        {
            Yaw += dYaw;
            Pitch = Math.Clamp(Pitch + dPitch, -89f, 89f);
        }

        // ── FPS Look (right-click drag, "look from where I stand") ────────────
        // Camera position is fixed; Target re-computed based on new look direction.
        // dYaw > 0  = look RIGHT  (mouse dragged right)
        // dPitch > 0 = look DOWN  (mouse dragged down, natural / non-inverted)
        public void LookAround(float dYaw, float dPitch)
        {
            Vector3 eye = Position;   // save current eye position

            // Yaw: more positive sin(yaw) = camera further in +X = looks more toward -X
            // To look RIGHT (toward +X), we need Yaw to become more negative.
            // So: mouse right (dYaw > 0) → Yaw -= dYaw ✓
            Yaw -= dYaw;

            // Pitch: positive pitch = camera above target = Forward.Y = -sin(pitch) < 0 = looking DOWN.
            // To look DOWN on mouse-down (dPitch > 0), we need pitch to increase → += ✓
            Pitch = Math.Clamp(Pitch + dPitch, -89f, 89f);

            // Recompute Target so camera Position stays at eye
            Target = eye - SphericalOffset;
        }

        // ── Fly movement ──────────────────────────────────────────────────────
        // Moves both Target and Position by the same delta (rigid translation).
        public void Move(Vector3 worldDelta)
        {
            Target += worldDelta;
        }

        // ── Pan (middle-drag) ─────────────────────────────────────────────────
        // Pan perpendicular to look direction (screen-space).
        public void Pan(float dx, float dy)
        {
            var view = GetViewMatrix();
            var right = new Vector3(view.Row0.X, view.Row0.Y, view.Row0.Z);
            var up = new Vector3(view.Row1.X, view.Row1.Y, view.Row1.Z);
            float spd = Distance * 0.002f;
            Target -= right * (dx * spd);
            Target += up * (dy * spd);
        }

        public Matrix4 GetViewMatrix() => Matrix4.LookAt(Position, Target, Vector3.UnitY);
        public Matrix4 GetProjectionMatrix(float aspect) =>
            Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(FovDeg), aspect, Near, Far);
    }
}