using Elintria.Engine;
using OpenTK.Mathematics;
using System;

/// <summary>
/// Custom component — attach to any GameObject via AddComponent<NewScript>().
/// </summary>
public class NewScript : Component
{
    // ── Inspector-visible fields ─────────────────────────────────
    public float Speed { get; set; } = 1.0f;

    // ── Lifecycle ────────────────────────────────────────────────
    public override void Awake()
    {
        
    }

    public override void Start()
    {
        // Called once before the first Update()
    }

    public override void Update(float dt)
    {
        // Called every frame  (dt = delta time in seconds)
    }

    public override void OnDestroy()
    {
        // Called when the component or its GameObject is destroyed
    }
}
