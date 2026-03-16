using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  PhysicsSimulation
    //
    //  Runs once per fixed-update step (50 Hz, matching Unity).
    //  Mirrors Unity's Rigidbody behaviour:
    //
    //    • Gravity               — applied every fixed step when UseGravity = true
    //    • Linear / angular drag — Unity's formula: v *= Clamp01(1 - drag * dt)
    //    • Kinematic             — position/rotation driven externally, no forces
    //    • Freeze constraints    — per-axis position & rotation locks (Rigidbody3D)
    //    • Collision response    — full depenetration + impulse, no tunneling
    //    • Resting contact       — velocity clamped to zero along normal when
    //                              resting on a static surface (prevents sinking)
    //    • Trigger detection     — OnTriggerEnter/Stay/Exit on all scripts
    //    • Collision callbacks   — OnCollisionEnter/Stay/Exit with CollisionData
    //    • AddForce / AddImpulse / AddTorque / MovePosition
    //    • GetVelocity / SetVelocity / GetAngularVelocity / SetAngularVelocity
    // ═══════════════════════════════════════════════════════════════════════════
    public static class PhysicsSimulation
    {
        // ── Per-body runtime state ────────────────────────────────────────────
        private static readonly Dictionary<int, BodyState> _states = new();
        private static readonly Dictionary<long, ContactState> _contacts = new();

        public static void ClearAll()
        {
            _states.Clear();
            _contacts.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API  (mirrors Unity's Rigidbody)
        // ─────────────────────────────────────────────────────────────────────

        public static Vector3 GetVelocity(Rigidbody3D rb) => GetOrCreate(rb).Velocity;
        public static void SetVelocity(Rigidbody3D rb, Vector3 v) => GetOrCreate(rb).Velocity = v;

        public static Vector3 GetAngularVelocity(Rigidbody3D rb) => GetOrCreate(rb).AngularVelocity;
        public static void SetAngularVelocity(Rigidbody3D rb, Vector3 v) => GetOrCreate(rb).AngularVelocity = v;

        /// <summary>ForceMode.Force — accumulated over the step then applied as acceleration.</summary>
        public static void AddForce(Rigidbody3D rb, Vector3 force)
            => GetOrCreate(rb).AccumulatedForce += force;

        /// <summary>ForceMode.Impulse — instant velocity change regardless of mass.</summary>
        public static void AddImpulse(Rigidbody3D rb, Vector3 impulse)
        {
            var s = GetOrCreate(rb);
            float invM = rb.Mass > 0f ? 1f / rb.Mass : 0f;
            s.Velocity += impulse * invM;
        }

        public static void AddTorque(Rigidbody3D rb, Vector3 torque)
            => GetOrCreate(rb).AccumulatedTorque += torque;

        public static void MovePosition(Rigidbody3D rb, Vector3 position)
        {
            if (rb.GameObject == null) return;
            rb.GameObject.Transform.LocalPosition = position;
            if (!rb.IsKinematic) GetOrCreate(rb).Velocity = Vector3.Zero;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Step  — called by SceneRunner inside the fixed-update loop
        // ─────────────────────────────────────────────────────────────────────
        public static void Step(Scene scene, float dt)
        {
            var bodies = CollectBodies(scene);
            if (bodies.Count == 0) return;

            var allColliders = CollectColliders(scene);

            // 1. Integrate forces → velocity → position
            foreach (var (rb, go) in bodies)
                Integrate(rb, go, dt);

            // 2. Collision detection & response (multiple iterations for stability)
            //    Two iterations matches Unity's default solver iteration count.
            for (int iter = 0; iter < 2; iter++)
                ResolveCollisions(bodies, allColliders, dt, scene);

            // 3. Clear per-step force accumulators
            foreach (var (rb, _) in bodies)
            {
                var s = GetOrCreate(rb);
                s.AccumulatedForce = Vector3.Zero;
                s.AccumulatedTorque = Vector3.Zero;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Integration  (semi-implicit Euler, matching Unity)
        // ─────────────────────────────────────────────────────────────────────
        private static void Integrate(Rigidbody3D rb, GameObject go, float dt)
        {
            if (rb.IsKinematic) return;

            var s = GetOrCreate(rb);
            var t = go.Transform;
            float invM = rb.Mass > 0f ? 1f / rb.Mass : 0f;

            // ── Linear ────────────────────────────────────────────────────────
            var totalForce = s.AccumulatedForce;
            if (rb.UseGravity)
                totalForce += Physics.Gravity * rb.Mass;

            s.Velocity += totalForce * invM * dt;

            // Unity drag: velocity *= Clamp01(1 - drag * dt)
            s.Velocity *= Math.Max(0f, 1f - rb.Drag * dt);

            // Apply freeze constraints
            var vel = s.Velocity;
            if (rb.FreezePositionX) vel.X = 0f;
            if (rb.FreezePositionY) vel.Y = 0f;
            if (rb.FreezePositionZ) vel.Z = 0f;
            s.Velocity = vel;

            t.LocalPosition += s.Velocity * dt;

            // ── Angular ───────────────────────────────────────────────────────
            float r = EstimateRadius(go);
            float inertia = 0.4f * rb.Mass * r * r;
            float invI = inertia > 1e-6f ? 1f / inertia : 0f;

            s.AngularVelocity += s.AccumulatedTorque * invI * dt;
            s.AngularVelocity *= Math.Max(0f, 1f - rb.AngularDrag * dt);

            var angVel = s.AngularVelocity;
            if (rb.FreezeRotationX) angVel.X = 0f;
            if (rb.FreezeRotationY) angVel.Y = 0f;
            if (rb.FreezeRotationZ) angVel.Z = 0f;
            s.AngularVelocity = angVel;

            // Convert angular velocity (rad/s) → Euler angle delta (deg/frame)
            t.LocalEulerAngles += s.AngularVelocity * (float)(180.0 / Math.PI) * dt;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Collision resolution
        // ─────────────────────────────────────────────────────────────────────
        private static void ResolveCollisions(
            List<(Rigidbody3D rb, GameObject go)> bodies,
            List<(Component col, GameObject go)> allColliders,
            float dt,
            Scene scene)
        {
            var activeThisStep = new HashSet<long>();

            foreach (var (rb, rbGo) in bodies)
            {
                if (rb.IsKinematic) continue;

                var myCol = GetCollider(rbGo);
                if (myCol == null) continue;

                foreach (var (otherCol, otherGo) in allColliders)
                {
                    if (otherGo == rbGo) continue;
                    if (!otherGo.ActiveSelf) continue;

                    if (!ComputeOverlap(myCol, rbGo, otherCol, otherGo,
                            out Vector3 normal, out float depth))
                        continue;

                    long pairId = MakePairId(rbGo.InstanceId, otherGo.InstanceId);
                    activeThisStep.Add(pairId);

                    bool isTrigger = IsTrigger(myCol) || IsTrigger(otherCol);

                    if (isTrigger)
                    {
                        FireTriggerCallbacks(pairId, rbGo, otherGo);
                        continue;
                    }

                    var otherRb = otherGo.GetComponent<Rigidbody3D>();
                    bool otherKinematic = otherRb == null || otherRb.IsKinematic;

                    // ── Depenetration ─────────────────────────────────────────
                    // Full correction with a tiny slop (0.001f instead of 0.01f
                    // — the old 0.01f slop was larger than the per-step gravity
                    // sink of ~0.004m, causing the cube to slowly tunnel through).
                    const float Slop = 0.001f;
                    const float BaumgarteBeta = 0.8f;   // how aggressively to correct

                    float myMass = rb.Mass;
                    float otherMass = otherKinematic
                        ? float.PositiveInfinity
                        : (otherRb?.Mass ?? float.PositiveInfinity);

                    float invMy = myMass > 0f ? 1f / myMass : 0f;
                    float invOther = float.IsInfinity(otherMass) ? 0f :
                                     otherMass > 0f ? 1f / otherMass : 0f;
                    float totalInvM = invMy + invOther;

                    float correction = totalInvM > 0f
                        ? Math.Max(depth - Slop, 0f) * BaumgarteBeta / totalInvM * invMy
                        : 0f;

                    rbGo.Transform.LocalPosition += normal * correction;

                    if (!otherKinematic && otherRb != null)
                        otherGo.Transform.LocalPosition -= normal *
                            (Math.Max(depth - Slop, 0f) * BaumgarteBeta / totalInvM * invOther);

                    // ── Velocity impulse ──────────────────────────────────────
                    var s = GetOrCreate(rb);
                    var otherS = !otherKinematic && otherRb != null ? GetOrCreate(otherRb) : null;

                    var relVel = s.Velocity - (otherS?.Velocity ?? Vector3.Zero);
                    float vRel = Vector3.Dot(relVel, normal);

                    if (vRel < 0f)  // only resolve when approaching
                    {
                        // Coefficient of restitution: 0 = fully inelastic (Unity default)
                        // For bouncy objects scripts can call AddImpulse manually
                        const float Restitution = 0f;
                        float j = -(1f + Restitution) * vRel / totalInvM;

                        s.Velocity += normal * (j * invMy);

                        if (otherS != null)
                            otherS.Velocity -= normal * (j * invOther);
                    }

                    // ── Resting contact: cancel gravity-induced sink ───────────
                    // If the contact normal points mostly upward and the body still
                    // has a downward velocity component after the impulse, zero it.
                    // This is equivalent to Unity's constraint solver preventing
                    // an object from sinking into a static floor each step.
                    if (normal.Y > 0.7f && s.Velocity.Y < 0f)
                        s.Velocity = new Vector3(s.Velocity.X, 0f, s.Velocity.Z);

                    // Re-apply freeze constraints after impulse
                    var vel = s.Velocity;
                    if (rb.FreezePositionX) vel.X = 0f;
                    if (rb.FreezePositionY) vel.Y = 0f;
                    if (rb.FreezePositionZ) vel.Z = 0f;
                    s.Velocity = vel;

                    FireCollisionCallbacks(pairId, rbGo, otherGo, normal, depth);
                }
            }

            // ── Fire Exit callbacks for pairs no longer in contact ────────────
            var gone = new List<long>();
            foreach (var kv in _contacts)
                if (!activeThisStep.Contains(kv.Key))
                    gone.Add(kv.Key);

            foreach (var id in gone)
            {
                FireExitCallbacks(_contacts[id]);
                _contacts.Remove(id);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Overlap tests
        //  Returns true + outward normal (points away from otherGo) + depth
        // ─────────────────────────────────────────────────────────────────────
        private static bool ComputeOverlap(
            Component myCol, GameObject myGo,
            Component oCol, GameObject oGo,
            out Vector3 normal, out float depth)
        {
            normal = Vector3.UnitY;
            depth = 0f;

            if (myCol is BoxCollider myBox && oCol is BoxCollider oBox)
                return BoxVsBox(myBox, myGo, oBox, oGo, out normal, out depth);

            if (myCol is BoxCollider mb && oCol is SphereCollider os)
                return BoxVsSphere(mb, myGo, os, oGo, out normal, out depth);

            if (myCol is SphereCollider ms && oCol is BoxCollider ob)
            {
                bool hit = BoxVsSphere(ob, oGo, ms, myGo, out normal, out depth);
                normal = -normal;
                return hit;
            }

            if (myCol is SphereCollider mSph && oCol is SphereCollider oSph)
                return SphereVsSphere(mSph, myGo, oSph, oGo, out normal, out depth);

            // Capsule — approximate as sphere at center for now
            if (myCol is CapsuleCollider mCap)
            {
                var fake = new SphereCollider { Center = mCap.Center, Radius = mCap.Radius };
                fake.GameObject = myGo;
                return ComputeOverlap(fake, myGo, oCol, oGo, out normal, out depth);
            }
            if (oCol is CapsuleCollider oCap)
            {
                var fake = new SphereCollider { Center = oCap.Center, Radius = oCap.Radius };
                fake.GameObject = oGo;
                return ComputeOverlap(myCol, myGo, fake, oGo, out normal, out depth);
            }

            return false;
        }

        // ── Box vs Box (world-AABB SAT on 3 axes) ─────────────────────────────
        private static bool BoxVsBox(
            BoxCollider a, GameObject aGo,
            BoxCollider b, GameObject bGo,
            out Vector3 normal, out float depth)
        {
            normal = Vector3.UnitY;
            depth = 0f;

            GetWorldAABB(a, aGo, out var aMin, out var aMax);
            GetWorldAABB(b, bGo, out var bMin, out var bMax);

            float ox = Math.Min(aMax.X, bMax.X) - Math.Max(aMin.X, bMin.X);
            float oy = Math.Min(aMax.Y, bMax.Y) - Math.Max(aMin.Y, bMin.Y);
            float oz = Math.Min(aMax.Z, bMax.Z) - Math.Max(aMin.Z, bMin.Z);

            if (ox <= 0f || oy <= 0f || oz <= 0f) return false;

            var diff = (aMin + aMax) * 0.5f - (bMin + bMax) * 0.5f;

            if (ox <= oy && ox <= oz)
            { depth = ox; normal = new Vector3(diff.X >= 0f ? 1f : -1f, 0f, 0f); }
            else if (oy <= ox && oy <= oz)
            { depth = oy; normal = new Vector3(0f, diff.Y >= 0f ? 1f : -1f, 0f); }
            else
            { depth = oz; normal = new Vector3(0f, 0f, diff.Z >= 0f ? 1f : -1f); }

            return true;
        }

        // ── Box vs Sphere ─────────────────────────────────────────────────────
        private static bool BoxVsSphere(
            BoxCollider box, GameObject boxGo,
            SphereCollider sphere, GameObject sphGo,
            out Vector3 normal, out float depth)
        {
            normal = Vector3.UnitY;
            depth = 0f;

            var scale = sphGo.Transform.LocalScale;
            float r = sphere.Radius * Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
            var center = sphGo.Transform.LocalPosition + sphere.Center * scale;

            GetWorldAABB(box, boxGo, out var bMin, out var bMax);

            var closest = Vector3.Clamp(center, bMin, bMax);
            var diff = center - closest;
            float distSq = diff.LengthSquared;

            if (distSq >= r * r) return false;

            float dist = MathF.Sqrt(distSq);
            depth = r - dist;
            normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
            return true;
        }

        // ── Sphere vs Sphere ──────────────────────────────────────────────────
        private static bool SphereVsSphere(
            SphereCollider a, GameObject aGo,
            SphereCollider b, GameObject bGo,
            out Vector3 normal, out float depth)
        {
            normal = Vector3.UnitY;
            depth = 0f;

            float rA = a.Radius * MaxScale(aGo);
            float rB = b.Radius * MaxScale(bGo);
            var cA = aGo.Transform.LocalPosition + a.Center * aGo.Transform.LocalScale;
            var cB = bGo.Transform.LocalPosition + b.Center * bGo.Transform.LocalScale;
            var diff = cA - cB;
            float dist = diff.Length;
            float sumR = rA + rB;

            if (dist >= sumR) return false;

            depth = sumR - dist;
            normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Collision / Trigger callbacks
        // ─────────────────────────────────────────────────────────────────────
        private static void FireCollisionCallbacks(
            long pairId, GameObject a, GameObject b, Vector3 normal, float depth)
        {
            if (!_contacts.TryGetValue(pairId, out _))
            {
                _contacts[pairId] = new ContactState { GoA = a, GoB = b, IsTrigger = false };
                var infoA = new CollisionData { Normal = normal, Penetration = depth, Other = b };
                var infoB = new CollisionData { Normal = -normal, Penetration = depth, Other = a };
                SendMessage(a, "OnCollisionEnter", infoA);
                SendMessage(b, "OnCollisionEnter", infoB);
            }
            else
            {
                SendMessage(a, "OnCollisionStay", new CollisionData { Normal = normal, Penetration = depth, Other = b });
                SendMessage(b, "OnCollisionStay", new CollisionData { Normal = -normal, Penetration = depth, Other = a });
            }
        }

        private static void FireTriggerCallbacks(long pairId, GameObject a, GameObject b)
        {
            if (!_contacts.TryGetValue(pairId, out _))
            {
                _contacts[pairId] = new ContactState { GoA = a, GoB = b, IsTrigger = true };
                SendMessage(a, "OnTriggerEnter", b);
                SendMessage(b, "OnTriggerEnter", a);
            }
            else
            {
                SendMessage(a, "OnTriggerStay", b);
                SendMessage(b, "OnTriggerStay", a);
            }
        }

        private static void FireExitCallbacks(ContactState cs)
        {
            if (cs.IsTrigger)
            {
                SendMessage(cs.GoA, "OnTriggerExit", cs.GoB);
                SendMessage(cs.GoB, "OnTriggerExit", cs.GoA);
            }
            else
            {
                SendMessage(cs.GoA, "OnCollisionExit", cs.GoB);
                SendMessage(cs.GoB, "OnCollisionExit", cs.GoA);
            }
        }

        private static void SendMessage(GameObject go, string method, object? arg = null)
        {
            foreach (var comp in go.Components)
            {
                if (!comp.Enabled) continue;
                var mi = comp.GetType().GetMethod(method,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);
                if (mi == null) continue;
                try
                {
                    var parms = mi.GetParameters();
                    if (parms.Length == 0) mi.Invoke(comp, null);
                    else if (parms.Length == 1 && arg != null) mi.Invoke(comp, new[] { arg });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Physics] {method} error on '{go.Name}' " +
                        $"({comp.GetType().Name}): {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static BodyState GetOrCreate(Rigidbody3D rb)
        {
            int id = rb.GameObject?.InstanceId ?? -1;
            if (!_states.TryGetValue(id, out var s))
                _states[id] = s = new BodyState();
            return s;
        }

        private static List<(Rigidbody3D, GameObject)> CollectBodies(Scene scene)
        {
            var list = new List<(Rigidbody3D, GameObject)>();
            foreach (var go in scene.All())
                if (go.ActiveSelf && go.GetComponent<Rigidbody3D>() is Rigidbody3D rb && rb.Enabled)
                    list.Add((rb, go));
            return list;
        }

        private static List<(Component, GameObject)> CollectColliders(Scene scene)
        {
            var list = new List<(Component, GameObject)>();
            foreach (var go in scene.All())
            {
                if (!go.ActiveSelf) continue;
                foreach (var c in go.Components)
                    if (c.Enabled && c is BoxCollider or SphereCollider or CapsuleCollider)
                        list.Add((c, go));
            }
            return list;
        }

        private static Component? GetCollider(GameObject go)
        {
            foreach (var c in go.Components)
                if (c.Enabled && c is BoxCollider or SphereCollider or CapsuleCollider)
                    return c;
            return null;
        }

        private static bool IsTrigger(Component col) => col switch
        {
            BoxCollider bc => bc.IsTrigger,
            SphereCollider sc => sc.IsTrigger,
            CapsuleCollider cc => cc.IsTrigger,
            _ => false
        };

        /// <summary>
        /// Returns the world-space AABB for a BoxCollider, correctly applying
        /// both the collider's Center offset and the GO's LocalScale.
        /// </summary>
        private static void GetWorldAABB(BoxCollider bc, GameObject go,
                                         out Vector3 min, out Vector3 max)
        {
            var scale = go.Transform.LocalScale;
            var pos = go.Transform.LocalPosition + bc.Center * scale;
            var half = bc.Size * 0.5f * scale;
            min = pos - half;
            max = pos + half;
        }

        private static float MaxScale(GameObject go)
        {
            var s = go.Transform.LocalScale;
            return Math.Max(s.X, Math.Max(s.Y, s.Z));
        }

        private static float EstimateRadius(GameObject go)
        {
            if (go.GetComponent<SphereCollider>() is SphereCollider sc) return sc.Radius * MaxScale(go);
            if (go.GetComponent<BoxCollider>() is BoxCollider bc) return (bc.Size * 0.5f * go.Transform.LocalScale).Length;
            if (go.GetComponent<CapsuleCollider>() is CapsuleCollider cap) return cap.Radius;
            return 0.5f;
        }

        private static long MakePairId(int a, int b)
            => a < b ? ((long)a << 32) | (uint)b
                     : ((long)b << 32) | (uint)a;

        // ── Internal state classes ────────────────────────────────────────────
        private class BodyState
        {
            public Vector3 Velocity = Vector3.Zero;
            public Vector3 AngularVelocity = Vector3.Zero;
            public Vector3 AccumulatedForce = Vector3.Zero;
            public Vector3 AccumulatedTorque = Vector3.Zero;
        }

        private class ContactState
        {
            public GameObject GoA = null!;
            public GameObject GoB = null!;
            public bool IsTrigger;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CollisionData  (passed to OnCollisionEnter/Stay/Exit)
    // ═══════════════════════════════════════════════════════════════════════════
    public class CollisionData
    {
        /// <summary>Contact normal pointing away from the other collider.</summary>
        public Vector3 Normal { get; internal set; }
        /// <summary>Penetration depth at contact.</summary>
        public float Penetration { get; internal set; }
        /// <summary>The other GameObject involved in the collision.</summary>
        public GameObject? Other { get; internal set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Rigidbody3D extension methods  — mirrors Unity's Rigidbody API
    //
    //  Usage in scripts:
    //    var rb = GetComponent<Rigidbody3D>();
    //    rb.SetVelocity(new Vector3(0, 5f, 0));   // jump
    //    rb.AddForce(new Vector3(0, 0, 10f));      // push
    //    rb.AddImpulse(new Vector3(10f, 0, 0));    // instant impulse
    //    Vector3 v = rb.GetVelocity();
    // ═══════════════════════════════════════════════════════════════════════════
    public static class Rigidbody3DExtensions
    {
        public static Vector3 GetVelocity(this Rigidbody3D rb)
            => PhysicsSimulation.GetVelocity(rb);
        public static void SetVelocity(this Rigidbody3D rb, Vector3 v)
            => PhysicsSimulation.SetVelocity(rb, v);

        public static Vector3 GetAngularVelocity(this Rigidbody3D rb)
            => PhysicsSimulation.GetAngularVelocity(rb);
        public static void SetAngularVelocity(this Rigidbody3D rb, Vector3 v)
            => PhysicsSimulation.SetAngularVelocity(rb, v);

        public static void AddForce(this Rigidbody3D rb, Vector3 force)
            => PhysicsSimulation.AddForce(rb, force);
        public static void AddImpulse(this Rigidbody3D rb, Vector3 impulse)
            => PhysicsSimulation.AddImpulse(rb, impulse);
        public static void AddTorque(this Rigidbody3D rb, Vector3 torque)
            => PhysicsSimulation.AddTorque(rb, torque);
        public static void MovePosition(this Rigidbody3D rb, Vector3 pos)
            => PhysicsSimulation.MovePosition(rb, pos);
    }
}