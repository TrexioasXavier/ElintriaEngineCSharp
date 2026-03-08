using System;
using ElintriaEngine.Core;

namespace GameScripts
{
    /// <summary>
    /// Attach to any GameObject via the Inspector or by dragging from the Project panel.
    /// All public fields are automatically shown and editable in the Inspector.
    /// </summary>
    public class NewScript (1) : Component
    {
        // ── Public fields (visible in Inspector) ──────────────────────────────
        public float speed     = 5.0f;
        public bool  isActive  = true;
        public int   health    = 100;

        public override void OnStart()
        {
            // Called once when the scene begins
            Console.WriteLine($"NewScript (1) started on {GameObject?.Name}");
        }

        public override void OnUpdate(double deltaTime)
        {
            // Called every frame
        }

        public override void OnDestroy()
        {
            // Called when the component or GameObject is destroyed
        }
    }
}
