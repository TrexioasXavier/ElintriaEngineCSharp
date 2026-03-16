using ElintriaEngine.Core;
using OpenTK.Mathematics;
using System.Collections.Generic;
using System;

/// <summary>
/// Attach this script to the Cube (the one with Rigidbody3D + BoxCollider).
/// It prints a detailed trace every fixed step so you can see exactly where
/// the physics is failing. Remove it once the issue is resolved.
/// </summary>
public class PhysicsDiagnostic : Component
{
    public bool active = true;
    private Rigidbody3D? _rb;
    private int _step = 0;
    

    public override void OnStart()
    {
        _rb = GetComponent<Rigidbody3D>();

        Console.WriteLine("╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║  PHYSICS DIAGNOSTIC                                  ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝");

        // ── Check this GO has what it needs ──────────────────────────────────
        if (_rb == null)
            Console.WriteLine("✗  NO Rigidbody3D on this GameObject!");
        else
            Console.WriteLine($"√  Rigidbody3D  mass={_rb.Mass}  gravity={_rb.UseGravity}  kinematic={_rb.IsKinematic}");

        var myCol = GameObject?.GetComponent<BoxCollider>()
                 ?? (Component?)GameObject?.GetComponent<SphereCollider>()
                 ?? GameObject?.GetComponent<CapsuleCollider>();
        if (myCol == null)
            Console.WriteLine("✗  NO collider on this GameObject!  Physics will NEVER stop it.");
        else
            Console.WriteLine($"√  Collider: {myCol.GetType().Name}");

        // ── Check the scene for a floor collider ─────────────────────────────
        var scene = ElintriaEngine.Core.Physics.ActiveScene;
        if (scene == null)
        {
            Console.WriteLine("✗  Physics.ActiveScene is NULL — SetScene was never called!");
            return;
        }

        Console.WriteLine("── All colliders in scene ───────────────────────────");
        int colCount = 0;
        foreach (var go in scene.All())
        {
            foreach (var c in go.Components)
            {
                if (c is BoxCollider bc)
                {
                    var scale = go.Transform.LocalScale;
                    var worldPos = go.Transform.LocalPosition + bc.Center * scale;
                    var half = bc.Size * 0.5f * scale;
                    Console.WriteLine($"  BoxCollider on '{go.Name}'");
                    Console.WriteLine($"    GO.pos={go.Transform.LocalPosition}  scale={scale}");
                    Console.WriteLine($"    BC.center={bc.Center}  BC.size={bc.Size}  trigger={bc.IsTrigger}");
                    Console.WriteLine($"    WorldAABB  min={worldPos - half}  max={worldPos + half}");
                    colCount++;
                }
                else if (c is SphereCollider sc)
                {
                    Console.WriteLine($"  SphereCollider on '{go.Name}'  r={sc.Radius}  trigger={sc.IsTrigger}");
                    colCount++;
                }
            }
        }
        if (colCount == 0)
            Console.WriteLine("  ✗  NO colliders found at all in the scene!");
        Console.WriteLine($"  Total: {colCount} collider(s)");
    }

    public override void OnFixedUpdate(double dt)
    {
        // Only print for first 120 steps (about 2.4 seconds at 50Hz)
        // to avoid console spam once it's resting
        /*if (_step > 120 || _rb == null || GameObject == null) return;
        _step++;*/

        var pos = GameObject.Transform.LocalPosition;
        var vel = PhysicsSimulation.GetVelocity(_rb);

        // Check overlap with every collider in the scene
        var scene = ElintriaEngine.Core.Physics.ActiveScene;
        string overlapInfo = "no overlaps";
        if (scene != null)
        {
            var myCol = GameObject.GetComponent<BoxCollider>()
                     ?? (Component?)GameObject.GetComponent<SphereCollider>()
                     ?? GameObject.GetComponent<CapsuleCollider>();

            if (myCol != null)
            {
                foreach (var go in scene.All())
                {
                    if (go == GameObject || !go.ActiveSelf) continue;
                    foreach (var c in go.Components)
                    {
                        if (c is not (BoxCollider or SphereCollider or CapsuleCollider)) continue;

                        // Quick AABB check
                        if (c is BoxCollider otherBc)
                        {
                            var scale = go.Transform.LocalScale;
                            var worldPos = go.Transform.LocalPosition + otherBc.Center * scale;
                            var half = otherBc.Size * 0.5f * scale;
                            var oMin = worldPos - half;
                            var oMax = worldPos + half;

                            var myScale = GameObject.Transform.LocalScale;
                            var myBc = myCol as BoxCollider;
                            if (myBc != null)
                            {
                                var myWorldPos = pos + myBc.Center * myScale;
                                var myHalf = myBc.Size * 0.5f * myScale;
                                var myMin = myWorldPos - myHalf;
                                var myMax = myWorldPos + myHalf;

                                float oy = Math.Min(myMax.Y, oMax.Y) - Math.Max(myMin.Y, oMin.Y);
                                float ox = Math.Min(myMax.X, oMax.X) - Math.Max(myMin.X, oMin.X);
                                float oz = Math.Min(myMax.Z, oMax.Z) - Math.Max(myMin.Z, oMin.Z);

                                if (ox > 0 && oy > 0 && oz > 0)
                                    overlapInfo = $"OVERLAPPING '{go.Name}' depth=({ox:F4},{oy:F4},{oz:F4})";
                            }
                        }
                    }
                }
            }
            else
            {
                overlapInfo = "✗ no collider on cube!";
            }
        }

        Console.WriteLine($"[step {_step:D3}] pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3})  vel=({vel.X:F3},{vel.Y:F3},{vel.Z:F3})  {overlapInfo}");
    }
}