using System;
using ElintriaEngine.Core;

namespace GameScripts
{
    public class NewScript : Component
    {
        // ── Public fields (visible in Inspector) ──────────────────────────────
        public float speed  = 5.0f;
        public bool  active = true;
        public bool  debug  = false;  

        // Called once before the first frame — like Unity's Start()
        public override void OnStart()
        {
            Console.WriteLine($"NewScript started on {GameObject?.Name}");
        }

        // Called every frame — like Unity's Update()
        public override void OnUpdate(double deltaTime)
        {
            Console.WriteLine($"NewScript updating on {GameObject?.Name} with deltaTime {deltaTime}");
        }

        // Called after all Updates — like Unity's LateUpdate()
        public override void OnLateUpdate(double deltaTime)
        {
            Console.WriteLine($"NewScript late updating on {GameObject?.Name} with deltaTime {deltaTime}");


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
