using ElintriaEngine.Core;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Diagnostics;
using ElintriaEngine.Rendering;
using OpenTK.Mathematics;

namespace GameScripts
{

    
    public class NewScript : Component
    {
        // ── Public fields (visible in Inspector) ──────────────────────────────
        public float speed  = 5.0f;
        public bool  active = true;
        


        // Called once before the first frame — like Unity's Start()
        public override void OnStart()
        {
            Console.WriteLine($"NewScript started on {GameObject?.Name}");
            
        }

        // Called every frame — like Unity's Update()
        public override void OnUpdate(double dt)
        {
            // Fires ray in the object's local forward direction
            var origin = Transform.LocalPosition;
            var direction = GetForward();  // use this helper

            if (Physics.Raycast(origin, direction, out RaycastHit hit, 20f))
            {
                if (hit.Name == "TestRayCast")
                { 
                    Console.WriteLine($"Hit {hit.Name} at distance {hit.Distance}");
                }
            }

             

            PhysicsDebug.DrawRay(origin, direction * 20f,
                new Vector4(1, 1, 0, 1), duration: 0.1f, depthTest: false);
        }

        private Vector3 GetForward()
        {
            float yr = MathHelper.DegreesToRadians(Transform.LocalEulerAngles.Y);
            float xr = MathHelper.DegreesToRadians(Transform.LocalEulerAngles.X);
            return Vector3.Normalize(new Vector3(
                 MathF.Sin(yr) * MathF.Cos(xr),
                -MathF.Sin(xr),
                -MathF.Cos(yr) * MathF.Cos(xr)));
        }

        // Called after all Updates — like Unity's LateUpdate()
        public override void OnLateUpdate(double deltaTime)
        {
        }

        // Called at a fixed rate (50 Hz) — like Unity's FixedUpdate()
        public override void OnFixedUpdate(double fixedDeltaTime)
        {
        }

        // Called once when the component is first created/enabled — like Unity's Awake()
        public override void Awake()
        {
        }

        // Called when the component or GameObject is destroyed
        public override void OnDestroy()
        {
        }
    }
}
