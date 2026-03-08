using Elintria.Engine;
using Elintria.Engine.Rendering;
using OpenTK.Mathematics;
using System.Drawing;

namespace Elintria
{
    /// <summary>
    /// Example scene — registered with SceneManager in Editor.OnLoad().
    /// Override OnLoad() to spawn GameObjects.
    /// </summary>
    public class GameScene : Scene
    {
        // The scene holds a reference to the shared shader / font so we
        // can pass them to materials / world-text without static globals.
        public Shader SharedShader { get; set; }
        public BitmapFont SharedFont { get; set; }

        protected override void OnLoad()
        {
            // SharedShader and SharedFont must be set before the scene loads.
            // The factory in Editor.OnLoad() sets them at construction time,
            // so they are always available here.
            if (SharedShader == null)
                throw new System.InvalidOperationException(
                    "GameScene.SharedShader must be assigned before loading.");

            // ---- Ground plane ------------------------------------------------
            var planeGO = CreateGameObject("Ground");
            planeGO.Transform.LocalPosition = new Vector3(0, -0.01f, 0);
            planeGO.Transform.LocalScale = new Vector3(10, 1, 10);
            var planeMR = planeGO.AddComponent<MeshRenderer>();
            planeMR.Mesh = Mesh.CreatePlane(4, 10f);
            {
                var mat = new Material(SharedShader, "GroundMat");
                mat.SetColor(Material.Props.Color, Color.FromArgb(255, 60, 80, 60));
                planeMR.Material = mat;
            }

            // ---- Cube --------------------------------------------------------
            var cubeGO = CreateGameObject("Cube");
            cubeGO.Transform.LocalPosition = new Vector3(2f, 0.5f, 0f);
            var cubeMR = cubeGO.AddComponent<MeshRenderer>();
            cubeMR.Mesh = Mesh.CreateCube(1f);
            {
                var mat = new Material(SharedShader, "CubeMat");
                mat.SetColor(Material.Props.Color, Color.FromArgb(255, 90, 140, 200));
                cubeMR.Material = mat;
            }
            // Attach a spinner so it rotates each frame
            cubeGO.AddComponent<Spinner>();

            // ---- Sphere ------------------------------------------------------
            var sphereGO = CreateGameObject("Sphere");
            sphereGO.Transform.LocalPosition = new Vector3(-2f, 0.5f, 0f);
            var sphereMR = sphereGO.AddComponent<MeshRenderer>();
            sphereMR.Mesh = Mesh.CreateSphere(24, 24, 0.5f);
            {
                var mat = new Material(SharedShader, "SphereMat");
                mat.SetColor(Material.Props.Color, Color.FromArgb(255, 200, 100, 80));
                sphereMR.Material = mat;
            }

            // ---- World-space label on the sphere ----------------------------
            {
                var label = new WorldText
                {
                    Content = "Sphere",
                    Font = SharedFont,
                    WorldPosition = sphereGO.Transform.LocalPosition,
                    TextColor = Color.LightCyan,
                    BackgroundColor = Color.FromArgb(130, 10, 10, 20),
                    Scale = 0.10f,
                    YOffset = 0.8f,
                    DropShadow = true
                };
                WorldText.Register(label);
            }
        }

        protected override void OnUnload()
        { 
            
        }
    }

    // =========================================================================
    // Example component: Spinner
    // =========================================================================
    /// <summary>Rotates the parent GameObject around the Y axis.</summary>
    public class Spinner : Component
    {
        public float SpeedDegrees { get; set; } = 45f;

        public override void Update(float dt)
        {
            Transform.Rotate(new Vector3(0, SpeedDegrees * dt, 0));
        }
    }
}