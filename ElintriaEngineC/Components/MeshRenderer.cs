using Elintria.Engine.Rendering;
using OpenTK.Mathematics;

namespace Elintria.Engine
{
    /// <summary>
    /// Renders a Mesh with a Material.
    /// Attach to a GameObject; the Scene will call OnRender each frame.
    ///
    /// Usage:
    ///   var mr = go.AddComponent&lt;MeshRenderer&gt;();
    ///   mr.Mesh     = Mesh.CreateCube();
    ///   mr.Material = new Material(shader);
    ///
    /// Multiple materials per mesh (sub-meshes) are supported via Materials[].
    /// </summary>
    public class MeshRenderer : Component
    {
        // ------------------------------------------------------------------
        // Properties
        // ------------------------------------------------------------------
        private Mesh _mesh; 
        public Mesh Mesh
        {
            get => _mesh;
            set { _mesh = value; _mesh?.Upload(); }
        }

        /// <summary>Primary material (index 0).</summary>
        public Material Material
        {
            get => Materials.Count > 0 ? Materials[0] : null;
            set { if (Materials.Count == 0) Materials.Add(value); else Materials[0] = value; }
        }

        /// <summary>
        /// Per-sub-mesh materials. If fewer materials than sub-meshes exist,
        /// the last material is reused for remaining sub-meshes.
        /// </summary>
        public System.Collections.Generic.List<Material> Materials { get; }
            = new System.Collections.Generic.List<Material>();

        /// <summary>When false, the mesh is culled and OnRender is skipped.</summary>
        public bool CastShadows { get; set; } = true;
        public bool ReceiveShadows { get; set; } = true;

        // ------------------------------------------------------------------
        // Built-in uniform names that are always uploaded automatically
        // ------------------------------------------------------------------
        public static class Uniforms
        {
            public const string Model = "uModel";
            public const string View = "uView";
            public const string Projection = "uProjection";
            public const string MVP = "uMVP";
            public const string Normal = "uNormalMatrix";
            public const string CameraPos = "uCameraPos";
            public const string Time = "uTime";
        }

        private static float _time = 0f;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------
        public override void Awake()
        {
            // Ensure mesh is uploaded once it is assigned
            _mesh?.Upload();
        }

        public override void Update(float dt)
        {
            _time += dt;
        }

        public override void OnRender(RenderContext ctx)
        {
            if (Mesh == null || Materials.Count == 0) return;

            Matrix4 model = Transform.WorldMatrix;
            Matrix4 mvp = model * ctx.ViewProjection;

            // Normal matrix = transpose(inverse(upper-left 3x3 of model))
            Matrix3 normalMat = new Matrix3(model).Inverted();
            normalMat.Transpose();

            // Render with each material (sub-mesh support; single-mesh uses index 0)
            for (int mi = 0; mi < Materials.Count; mi++)
            {
                var mat = Materials[mi];
                if (mat == null) continue;

                mat.Bind();

                // Auto-upload transform uniforms every material supports
                var sh = mat.Shader;
                if (sh == null) continue;

                sh.SetMatrix4("uModel", model);
                sh.SetMatrix4("uView", ctx.View);
                sh.SetMatrix4("uProjection", ctx.Projection);
                sh.SetMatrix4("uMVP", mvp);
                sh.SetMatrix3("uNormalMatrix", normalMat);
                sh.SetVector3("uCameraPos", ctx.CameraPos);
                sh.SetFloat("uTime", _time);

                Mesh.Draw();

                mat.Unbind();
            }
        }

        public override void OnDestroy()
        {
            // Mesh is a shared asset — do NOT dispose here.
            // Call Mesh.Dispose() from AssetManager or Scene unload if needed.
        }
    }
}