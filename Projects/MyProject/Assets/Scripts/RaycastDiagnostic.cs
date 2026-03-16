using System;
using System.Collections.Generic;
using ElintriaEngine.Core;
using OpenTK.Mathematics;

namespace GameScripts
{
    /// <summary>
    /// Drop this script on ANY GameObject in your scene and press Play.
    /// It will print a full diagnostic to the console every second showing:
    ///   - Whether Physics has a scene
    ///   - Every GameObject and whether it has a collider
    ///   - The exact ray origin/direction
    ///   - What the slab test sees for each collider
    /// </summary>
    public class RaycastDiagnostic : Component
    {
        public float rayLength = 20f;

        // How often to print (seconds) — keeps console readable
        private double _timer = 0;
        private const double Interval = 1.0;

        public override void OnStart()
        {
            Console.WriteLine("[RaycastDiagnostic] Started. Will print diagnostics every second.");
            RunDiagnostic();
        }   




        public override void OnUpdate(double dt)
        {
            _timer += dt;
            if (_timer >= Interval)
            {
                _timer = 0;
                RunDiagnostic();
            }
        }

        private void RunDiagnostic()
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  RAYCAST DIAGNOSTIC                                  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            // ── 1. Check scene reference ──────────────────────────────────────
            var scene = Physics.ActiveScene;
            if (scene == null)
            {
                Console.WriteLine("❌ Physics.ActiveScene is NULL!");
                Console.WriteLine("   Fix: add Physics.SetScene(_scene) in EditorLayout constructor,");
                Console.WriteLine("        LoadSceneFromFile(), ExitPlayMode(), and NewScene handler.");
                return;
            }
            Console.WriteLine($"✓  Physics.ActiveScene = '{scene.Name}'");

            // ── 2. List every GO and its colliders ────────────────────────────
            Console.WriteLine("\n── GameObjects in scene ──────────────────────────────");
            int totalColliders = 0;
            foreach (var go in scene.All())
            {
                var colliders = new List<string>();
                foreach (var comp in go.Components)
                {
                    string? desc = comp switch
                    {
                        BoxCollider bc => $"BoxCollider  center={bc.Center}  size={bc.Size}  trigger={bc.IsTrigger}  enabled={bc.Enabled}",
                        SphereCollider sc => $"SphereCollider  center={sc.Center}  r={sc.Radius}  trigger={sc.IsTrigger}  enabled={sc.Enabled}",
                        CapsuleCollider cc => $"CapsuleCollider  center={cc.Center}  r={cc.Radius}  h={cc.Height}  enabled={cc.Enabled}",
                        MeshCollider mc => $"MeshCollider  trigger={mc.IsTrigger}  enabled={mc.Enabled}",
                        _ => null,
                    };
                    if (desc != null) { colliders.Add(desc); totalColliders++; }
                }

                string pos = $"({go.Transform.LocalPosition.X:F2},{go.Transform.LocalPosition.Y:F2},{go.Transform.LocalPosition.Z:F2})";
                bool isMe = go == GameObject;

                if (colliders.Count > 0)
                    Console.WriteLine($"  [HAS COLLIDER]{(isMe ? " ← THIS OBJECT" : "")} '{go.Name}'  pos={pos}  active={go.ActiveSelf}");
                else
                    Console.WriteLine($"  [no collider]{(isMe ? " ← THIS OBJECT" : "")} '{go.Name}'  pos={pos}  active={go.ActiveSelf}");

                foreach (var c in colliders)
                    Console.WriteLine($"      {c}");
            }

            if (totalColliders == 0)
            {
                Console.WriteLine("❌ NO colliders found in the scene!");
                Console.WriteLine("   Add a BoxCollider component to your target GameObject.");
                return;
            }
            Console.WriteLine($"✓  {totalColliders} collider(s) found");

            // ── 3. Ray info ───────────────────────────────────────────────────
            if (GameObject == null) return;
            var t = GameObject.Transform;
            var origin = t.LocalPosition;

            // Build the forward direction from this GO's rotation (same as Camera.Forward)
            float yr = MathHelper.DegreesToRadians(t.LocalEulerAngles.Y);
            float xr = MathHelper.DegreesToRadians(t.LocalEulerAngles.X);
            var forward = new Vector3(
                 MathF.Sin(yr) * MathF.Cos(xr),
                -MathF.Sin(xr),
                -MathF.Cos(yr) * MathF.Cos(xr));
            forward = Vector3.Normalize(forward);

            Console.WriteLine($"\n── Ray ───────────────────────────────────────────────");
            Console.WriteLine($"  Origin    = {Fmt(origin)}");
            Console.WriteLine($"  Direction = {Fmt(forward)}  (object forward)");
            Console.WriteLine($"  MaxDist   = {rayLength}");

            // Draw the ray so you can see it in the Scene View
            PhysicsDebug.DrawRay(origin, forward * rayLength,
                new Vector4(1, 1, 0, 1), duration: (float)Interval + 0.1f, depthTest: false);

            // ── 4. Manual per-collider slab test with verbose output ──────────
            Console.WriteLine("\n── Per-collider test ─────────────────────────────────");
            var ray = new Ray(origin, forward);

            foreach (var go in scene.All())
            {
                if (!go.ActiveSelf) continue;
                if (go == GameObject) continue; // skip self

                foreach (var comp in go.Components)
                {
                    if (comp is BoxCollider bc && bc.Enabled)
                    {
                        TestBoxCollider(ray, go, bc);
                    }
                    else if (comp is SphereCollider sc && sc.Enabled)
                    {
                        TestSphereCollider(ray, go, sc);
                    }
                }
            }

            // ── 5. Actual Physics.Raycast call ────────────────────────────────
            Console.WriteLine("\n── Physics.Raycast result ────────────────────────────");
            if (Physics.Raycast(origin, forward, out var hit, rayLength))
            {
                Console.WriteLine($"✓  HIT '{hit.Name}'  dist={hit.Distance:F3}  point={Fmt(hit.Point)}  normal={Fmt(hit.Normal)}");
                PhysicsDebug.DrawRay(origin, forward * hit.Distance, new Vector4(0, 1, 0, 1), (float)Interval + 0.1f, false);
            }
            else
            {
                Console.WriteLine("✗  No hit");
            }

            Console.WriteLine("══════════════════════════════════════════════════════");
        }

        private static void TestBoxCollider(Ray ray, GameObject go, BoxCollider bc)
        {
            var t = go.Transform;
            var euler = t.LocalEulerAngles;
            var scale = t.LocalScale;

            var worldRot = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(euler.X),
                MathHelper.DegreesToRadians(euler.Y),
                MathHelper.DegreesToRadians(euler.Z));

            var scaledOffset = bc.Center * scale;
            var rotOffset = Vector3.Transform(scaledOffset, worldRot);
            var worldCenter = t.LocalPosition + rotOffset;
            var half = bc.Size * 0.5f * scale;

            var axX = Vector3.Transform(Vector3.UnitX, worldRot);
            var axY = Vector3.Transform(Vector3.UnitY, worldRot);
            var axZ = Vector3.Transform(Vector3.UnitZ, worldRot);

            var d = ray.Origin - worldCenter;

            Console.WriteLine($"\n  BoxCollider on '{go.Name}':");
            Console.WriteLine($"    GO.position  = {Fmt(t.LocalPosition)}");
            Console.WriteLine($"    worldCenter  = {Fmt(worldCenter)}");
            Console.WriteLine($"    half-extents = {Fmt(half)}");
            Console.WriteLine($"    d (ray→box)  = {Fmt(d)}  length={d.Length:F3}");

            float tMin = float.NegativeInfinity;
            float tMax = float.PositiveInfinity;
            bool miss = false;

            (Vector3 axis, float h, string name)[] slabs =
            {
                (axX, half.X, "X"),
                (axY, half.Y, "Y"),
                (axZ, half.Z, "Z"),
            };

            foreach (var (axis, h, name) in slabs)
            {
                float e = Vector3.Dot(axis, d);
                float f = Vector3.Dot(axis, ray.Direction);

                if (MathF.Abs(f) > 1e-6f)
                {
                    float t1 = (-e - h) / f;
                    float t2 = (-e + h) / f;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    Console.WriteLine($"    slab {name}: e={e:F3} f={f:F3} h={h:F3}  →  t1={t1:F3} t2={t2:F3}");
                    if (t1 > tMin) tMin = t1;
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax) { Console.WriteLine($"      ↳ MISS: intervals don't overlap (tMin={tMin:F3} > tMax={tMax:F3})"); miss = true; break; }
                    if (tMax < 0) { Console.WriteLine($"      ↳ MISS: box is behind ray (tMax={tMax:F3} < 0)"); miss = true; break; }
                }
                else
                {
                    Console.WriteLine($"    slab {name}: PARALLEL  e={e:F3}  h={h:F3}  in-slab={e >= -h && e <= h}");
                    if (e < -h || e > h) { Console.WriteLine($"      ↳ MISS: ray parallel and outside slab"); miss = true; break; }
                }
            }

            if (!miss)
            {
                float dist = tMin >= 0 ? tMin : tMax;
                Console.WriteLine($"    ✓ HIT at dist={dist:F3}  tMin={tMin:F3}  tMax={tMax:F3}");
            }
            else
            {
                Console.WriteLine($"    ✗ MISS (tMin={tMin:F3}  tMax={tMax:F3})");
            }
        }

        private static void TestSphereCollider(Ray ray, GameObject go, SphereCollider sc)
        {
            var t = go.Transform;
            var scale = t.LocalScale;
            var worldRot = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(t.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Z));
            var scaledOff = sc.Center * scale;
            var worldCenter = t.LocalPosition + Vector3.Transform(scaledOff, worldRot);
            float radius = sc.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));

            var oc = ray.Origin - worldCenter;
            float b = Vector3.Dot(oc, ray.Direction);
            float c = oc.LengthSquared - radius * radius;
            float disc = b * b - c;

            Console.WriteLine($"\n  SphereCollider on '{go.Name}':");
            Console.WriteLine($"    worldCenter = {Fmt(worldCenter)}  radius={radius:F3}");
            Console.WriteLine($"    b={b:F3}  c={c:F3}  discriminant={disc:F3}");
            if (disc < 0)
                Console.WriteLine($"    ✗ MISS (discriminant < 0 → ray doesn't intersect sphere)");
            else
            {
                float t1 = -b - MathF.Sqrt(disc);
                float t2 = -b + MathF.Sqrt(disc);
                Console.WriteLine($"    t1={t1:F3}  t2={t2:F3}");
                float dist = t1 >= 0 ? t1 : t2;
                if (dist >= 0) Console.WriteLine($"    ✓ HIT at dist={dist:F3}");
                else Console.WriteLine($"    ✗ MISS (both t values negative → sphere behind ray)");
            }
        }

        private static string Fmt(Vector3 v) =>
            $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
    }
}