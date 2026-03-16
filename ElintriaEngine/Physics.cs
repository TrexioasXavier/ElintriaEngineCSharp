using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Ray
    // ═══════════════════════════════════════════════════════════════════════════
    public readonly struct Ray
    {
        public Vector3 Origin { get; }
        public Vector3 Direction { get; }   // always normalised

        public Ray(Vector3 origin, Vector3 direction)
        {
            Origin = origin;
            Direction = direction.LengthSquared > 0
                        ? Vector3.Normalize(direction)
                        : Vector3.UnitZ;
        }

        public Vector3 GetPoint(float t) => Origin + Direction * t;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  RaycastHit
    // ═══════════════════════════════════════════════════════════════════════════
    public class RaycastHit
    {
        public Vector3 Point { get; internal set; }
        public Vector3 Normal { get; internal set; }
        public float Distance { get; internal set; }
        public GameObject? GameObject { get; internal set; }
        public Component? Collider { get; internal set; }

        public string Name => GameObject?.Name ?? "";
        public string Tag => GameObject?.Tag ?? "Untagged";
        public string Layer => GameObject?.Layer ?? "Default";

        public bool CompareTag(string tag) =>
            string.Equals(Tag, tag, StringComparison.OrdinalIgnoreCase);
        public bool IsLayer(string layer) =>
            string.Equals(Layer, layer, StringComparison.OrdinalIgnoreCase);
        public T? GetComponent<T>() where T : Component =>
            GameObject?.GetComponent<T>();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  LayerMask
    // ═══════════════════════════════════════════════════════════════════════════
    public static class LayerMask
    {
        public const int Everything = ~0;
        public const int Nothing = 0;

        public static int GetMask(params string[] layerNames)
        {
            int mask = 0;
            var tl = TagsAndLayers.Instance;
            foreach (var name in layerNames)
            {
                int idx = tl.Layers.IndexOf(name);
                if (idx >= 0) mask |= (1 << idx);
            }
            return mask;
        }

        public static bool Contains(int mask, GameObject go)
        {
            if (mask == Everything) return true;
            var tl = TagsAndLayers.Instance;
            int idx = tl.Layers.IndexOf(go.Layer);
            if (idx < 0) return (mask & 1) != 0;
            return (mask & (1 << idx)) != 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Physics
    //
    //  IMPORTANT — scene wiring
    //  ────────────────────────
    //  Physics._scene must always point to the LIVE scene so raycasts
    //  find current object positions.  Two call sites manage this:
    //
    //    • EditorLayout  calls  Physics.SetScene(_scene)  whenever it creates,
    //      loads or restores a scene (including after ExitPlayMode).
    //    • SceneRunner.Start  calls  Physics.SetScene(scene)  with the play-mode
    //      clone just before Awake/Start run.
    //    • SceneRunner.Stop   must NOT call Physics.SetScene(null) — it should
    //      leave the scene pointer alone; EditorLayout will reset it right after.
    // ═══════════════════════════════════════════════════════════════════════════
    public static class Physics
    {
        private static Scene? _scene;

        /// <summary>
        /// Call this whenever the active scene changes — in editor AND in play mode.
        /// EditorLayout must call it on load/new/restore; SceneRunner calls it on Start.
        /// </summary>
        public static void SetScene(Scene? scene)
        {
            _scene = scene;
            if (scene != null)
                Console.WriteLine($"[Physics] Scene set: '{scene.Name}' " +
                                  $"({CountColliders(scene)} collider(s))");
        }

        /// <summary>Current scene the physics system queries. Null = no scene loaded.</summary>
        public static Scene? ActiveScene => _scene;

        private static int CountColliders(Scene s)
        {
            int n = 0;
            foreach (var go in s.All())
                foreach (var c in go.Components)
                    if (c is BoxCollider or SphereCollider or CapsuleCollider
                            or MeshCollider or BoxCollider2D or CircleCollider2D)
                        n++;
            return n;
        }

        // ── Gravity ───────────────────────────────────────────────────────────
        public static Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

        // ── Raycast (nearest hit) ─────────────────────────────────────────────
        public static bool Raycast(
            Vector3 origin,
            Vector3 direction,
            out RaycastHit hit,
            float maxDistance = float.MaxValue,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = true)
        {
            hit = null!;

            // Guard: if scene is null, nothing will ever hit.
            // This usually means SetScene was never called — wire it in EditorLayout.
            if (_scene == null)
            {
                Console.WriteLine("[Physics] Raycast called but Physics.ActiveScene is null. " +
                                  "Call Physics.SetScene(scene) when the scene loads.");
                return false;
            }

            var ray = new Ray(origin, direction);
            var best = FindNearest(CastAll(ray, maxDistance, layerMask, ignoreTriggers));
            if (best == null) return false;
            hit = best;
            return true;
        }

        public static bool Raycast(
            Ray ray,
            out RaycastHit hit,
            float maxDistance = float.MaxValue,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = true)
            => Raycast(ray.Origin, ray.Direction, out hit,
                       maxDistance, layerMask, ignoreTriggers);

        // ── RaycastAll ────────────────────────────────────────────────────────
        public static RaycastHit[] RaycastAll(
            Vector3 origin,
            Vector3 direction,
            float maxDistance = float.MaxValue,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = true)
        {
            if (_scene == null) return Array.Empty<RaycastHit>();
            var ray = new Ray(origin, direction);
            var hits = CastAll(ray, maxDistance, layerMask, ignoreTriggers);
            hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return hits.ToArray();
        }

        // ── CheckSphere / OverlapSphere ───────────────────────────────────────
        public static bool CheckSphere(
            Vector3 centre, float radius,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = true)
            => OverlapSphereList(centre, radius, layerMask, ignoreTriggers).Count > 0;

        public static GameObject[] OverlapSphere(
            Vector3 centre, float radius,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = true)
        {
            var list = OverlapSphereList(centre, radius, layerMask, ignoreTriggers);
            var result = new List<GameObject>();
            foreach (var go in list)
                if (!result.Contains(go)) result.Add(go);
            return result.ToArray();
        }

        // ── Linecast ──────────────────────────────────────────────────────────
        public static bool Linecast(
            Vector3 start, Vector3 end,
            out RaycastHit hit,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = true)
        {
            var dir = end - start;
            float dist = dir.Length;
            if (dist < 1e-6f) { hit = null!; return false; }
            return Raycast(start, dir / dist, out hit, dist, layerMask, ignoreTriggers);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Core cast loop
        // ═════════════════════════════════════════════════════════════════════
        private static List<RaycastHit> CastAll(
            Ray ray, float maxDist, int mask, bool ignoreTriggers)
        {
            var results = new List<RaycastHit>();

            foreach (var go in _scene!.All())
            {
                if (!go.ActiveSelf) continue;
                if (!LayerMask.Contains(mask, go)) continue;

                foreach (var comp in go.Components)
                {
                    if (!comp.Enabled) continue;

                    RaycastHit? h = comp switch
                    {
                        BoxCollider bc => RayVsBox(ray, go, bc, ignoreTriggers),
                        SphereCollider sc => RayVsSphere(ray, go, sc, ignoreTriggers),
                        CapsuleCollider cc => RayVsCapsule(ray, go, cc, ignoreTriggers),
                        MeshCollider mc => RayVsMeshBox(ray, go, mc, ignoreTriggers),
                        BoxCollider2D b2 => RayVsBox2D(ray, go, b2, ignoreTriggers),
                        CircleCollider2D c2 => RayVsCircle2D(ray, go, c2, ignoreTriggers),
                        _ => null,
                    };

                    if (h != null && h.Distance >= 0f && h.Distance <= maxDist)
                        results.Add(h);
                }
            }
            return results;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetWorldTransform
        //
        //  Converts a collider's local-space centre into world space by:
        //    1. Scaling the centre offset by the GO's scale
        //    2. Rotating that scaled offset by the GO's world rotation
        //    3. Adding the GO's world position
        //
        //  This is the same operation as Transform.LocalMatrix * vec4(centre,1),
        //  but extracted so we also get the rotation and scale for slab tests.
        // ═════════════════════════════════════════════════════════════════════
        private static (Vector3 worldCenter, Quaternion worldRot, Vector3 worldScale)
            GetWorldTransform(GameObject go, Vector3 localColliderCenter)
        {
            var t = go.Transform;

            var worldRot = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(t.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Z));

            var worldScale = t.LocalScale;

            // Scale the offset in local space, then rotate into world space
            var scaledOffset = localColliderCenter * worldScale;
            var rotatedOffset = Vector3.Transform(scaledOffset, worldRot);
            var worldCenter = t.LocalPosition + rotatedOffset;

            return (worldCenter, worldRot, worldScale);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Ray vs OBB (BoxCollider)
        //
        //  Slab method for an Oriented Bounding Box.
        //  For each axis:
        //    e = dot(axis,  rayOrigin - boxCenter)   (signed distance of ray origin along axis)
        //    f = dot(axis,  rayDirection)             (rate of change along axis)
        //    entry plane t = (-e - h) / f
        //    exit  plane t = (-e + h) / f
        // ═════════════════════════════════════════════════════════════════════
        private static RaycastHit? RayVsBox(
            Ray ray, GameObject go, BoxCollider bc, bool ignoreTriggers)
        {
            if (ignoreTriggers && bc.IsTrigger) return null;

            var (worldCenter, worldRot, worldScale) = GetWorldTransform(go, bc.Center);

            // Half-extents scale with the GO's world scale
            var half = bc.Size * 0.5f * worldScale;

            // OBB axes derived from the world rotation
            var axX = Vector3.Transform(Vector3.UnitX, worldRot);
            var axY = Vector3.Transform(Vector3.UnitY, worldRot);
            var axZ = Vector3.Transform(Vector3.UnitZ, worldRot);

            // Vector from box-center to ray-origin
            var d = ray.Origin - worldCenter;

            float tMin = float.NegativeInfinity;
            float tMax = float.PositiveInfinity;
            var hitNormal = Vector3.Zero;

            (Vector3 axis, float h)[] slabs =
            {
                (axX, half.X),
                (axY, half.Y),
                (axZ, half.Z),
            };

            foreach (var (axis, h) in slabs)
            {
                float e = Vector3.Dot(axis, d);   // signed dist of ray-origin along this axis
                float f = Vector3.Dot(axis, ray.Direction);

                if (MathF.Abs(f) > 1e-6f)
                {
                    float t1 = (-e - h) / f;   // entry slab plane
                    float t2 = (-e + h) / f;   // exit  slab plane

                    // n1 = the inward normal at the entry plane
                    Vector3 n1 = (f > 0) ? -axis : axis;
                    Vector3 n2 = (f > 0) ? axis : -axis;

                    if (t1 > t2) { (t1, t2) = (t2, t1); (n1, n2) = (n2, n1); }

                    if (t1 > tMin) { tMin = t1; hitNormal = n1; }
                    if (t2 < tMax) tMax = t2;

                    if (tMin > tMax) return null;   // intervals don't overlap → miss
                    if (tMax < 0f) return null;   // box entirely behind ray
                }
                else
                {
                    // Ray is parallel to this slab pair
                    // Miss if origin is outside the slab
                    if (e < -h || e > h) return null;
                }
            }

            // Use tMin (entry) if ahead; tMax (exit) if ray started inside box
            float dist = tMin >= 0f ? tMin : tMax;
            if (dist < 0f) return null;

            return new RaycastHit
            {
                Distance = dist,
                Point = ray.GetPoint(dist),
                Normal = Vector3.Normalize(hitNormal),
                GameObject = go,
                Collider = bc,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Ray vs Sphere
        // ═════════════════════════════════════════════════════════════════════
        private static RaycastHit? RayVsSphere(
            Ray ray, GameObject go, SphereCollider sc, bool ignoreTriggers)
        {
            if (ignoreTriggers && sc.IsTrigger) return null;

            var (worldCenter, _, worldScale) = GetWorldTransform(go, sc.Center);
            float radius = sc.Radius * MathF.Max(worldScale.X,
                                       MathF.Max(worldScale.Y, worldScale.Z));

            var oc = ray.Origin - worldCenter;
            float b = Vector3.Dot(oc, ray.Direction);
            float c = oc.LengthSquared - radius * radius;
            float disc = b * b - c;
            if (disc < 0f) return null;

            float sqrtD = MathF.Sqrt(disc);
            float t = -b - sqrtD;          // entry (smaller t)
            if (t < 0f) t = -b + sqrtD;        // started inside — use exit
            if (t < 0f) return null;

            var point = ray.GetPoint(t);
            var normal = Vector3.Normalize(point - worldCenter);

            return new RaycastHit
            {
                Distance = t,
                Point = point,
                Normal = normal,
                GameObject = go,
                Collider = sc,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Ray vs Capsule
        // ═════════════════════════════════════════════════════════════════════
        private static RaycastHit? RayVsCapsule(
            Ray ray, GameObject go, CapsuleCollider cc, bool ignoreTriggers)
        {
            if (ignoreTriggers && cc.IsTrigger) return null;

            var (worldCenter, worldRot, worldScale) = GetWorldTransform(go, cc.Center);

            // Capsule direction axis and scale factor depend on the Direction property
            Vector3 upAxis;
            float radiusScale, heightScale;
            switch (cc.Direction)
            {
                case 0:
                    upAxis = Vector3.Transform(Vector3.UnitX, worldRot);
                    radiusScale = MathF.Max(worldScale.Y, worldScale.Z);
                    heightScale = worldScale.X;
                    break;
                case 2:
                    upAxis = Vector3.Transform(Vector3.UnitZ, worldRot);
                    radiusScale = MathF.Max(worldScale.X, worldScale.Y);
                    heightScale = worldScale.Z;
                    break;
                default: // Y
                    upAxis = Vector3.Transform(Vector3.UnitY, worldRot);
                    radiusScale = MathF.Max(worldScale.X, worldScale.Z);
                    heightScale = worldScale.Y;
                    break;
            }

            float radius = cc.Radius * radiusScale;
            float halfH = MathF.Max(0f, cc.Height * heightScale * 0.5f - radius);

            var capA = worldCenter - upAxis * halfH;
            var capB = worldCenter + upAxis * halfH;

            float bestDist = float.MaxValue;
            RaycastHit? bestHit = null;

            // Cylinder body
            var ab = capB - capA;
            float len = ab.Length;
            if (len > 1e-6f)
            {
                var abn = ab / len;
                var rd = ray.Direction - abn * Vector3.Dot(ray.Direction, abn);
                var od = (ray.Origin - capA) - abn * Vector3.Dot(ray.Origin - capA, abn);
                float a = rd.LengthSquared;
                if (a > 1e-6f)
                {
                    float bv = Vector3.Dot(rd, od);
                    float cv = od.LengthSquared - radius * radius;
                    float disc = bv * bv - a * cv;
                    if (disc >= 0f)
                    {
                        float sqD = MathF.Sqrt(disc);
                        float t0 = (-bv - sqD) / a;
                        float t1 = (-bv + sqD) / a;
                        float t = t0 >= 0f ? t0 : t1;
                        if (t >= 0f)
                        {
                            var pt = ray.GetPoint(t);
                            float proj = Vector3.Dot(pt - capA, abn);
                            if (proj >= 0f && proj <= len)
                            {
                                var closest = capA + abn * proj;
                                bestDist = t;
                                bestHit = new RaycastHit
                                {
                                    Distance = t,
                                    Point = pt,
                                    Normal = Vector3.Normalize(pt - closest),
                                    GameObject = go,
                                    Collider = cc,
                                };
                            }
                        }
                    }
                }
            }

            // End-sphere caps
            foreach (var cap in new[] { capA, capB })
            {
                var oc = ray.Origin - cap;
                float b = Vector3.Dot(oc, ray.Direction);
                float c = oc.LengthSquared - radius * radius;
                float disc = b * b - c;
                if (disc < 0f) continue;
                float sqD = MathF.Sqrt(disc);
                float t = -b - sqD;
                if (t < 0f) t = -b + sqD;
                if (t < 0f || t >= bestDist) continue;
                var pt = ray.GetPoint(t);
                bestDist = t;
                bestHit = new RaycastHit
                {
                    Distance = t,
                    Point = pt,
                    Normal = Vector3.Normalize(pt - cap),
                    GameObject = go,
                    Collider = cc,
                };
            }

            return bestHit;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MeshCollider approximation as a unit OBB
        // ─────────────────────────────────────────────────────────────────────
        private static RaycastHit? RayVsMeshBox(
            Ray ray, GameObject go, MeshCollider mc, bool ignoreTriggers)
        {
            if (ignoreTriggers && mc.IsTrigger) return null;
            var fake = new BoxCollider
            {
                Center = Vector3.Zero,
                Size = Vector3.One,
                IsTrigger = mc.IsTrigger,
                Enabled = mc.Enabled
            };
            fake.GameObject = go;
            return RayVsBox(ray, go, fake, ignoreTriggers);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  2D collider helpers
        // ─────────────────────────────────────────────────────────────────────
        private static RaycastHit? RayVsBox2D(
            Ray ray, GameObject go, BoxCollider2D b2, bool ignoreTriggers)
        {
            if (ignoreTriggers && b2.IsTrigger) return null;
            var fake = new BoxCollider
            {
                Center = b2.Offset,
                Size = new Vector3(b2.Width, b2.Height, 0.02f),
                IsTrigger = b2.IsTrigger,
                Enabled = b2.Enabled
            };
            fake.GameObject = go;
            return RayVsBox(ray, go, fake, ignoreTriggers);
        }

        private static RaycastHit? RayVsCircle2D(
            Ray ray, GameObject go, CircleCollider2D c2, bool ignoreTriggers)
        {
            if (ignoreTriggers && c2.IsTrigger) return null;
            var fake = new SphereCollider
            {
                Center = c2.Offset,
                Radius = c2.Radius,
                IsTrigger = c2.IsTrigger,
                Enabled = c2.Enabled
            };
            fake.GameObject = go;
            return RayVsSphere(ray, go, fake, ignoreTriggers);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Sphere overlap
        // ═════════════════════════════════════════════════════════════════════
        private static List<GameObject> OverlapSphereList(
            Vector3 c, float r, int mask, bool ignoreTriggers)
        {
            var result = new List<GameObject>();
            if (_scene == null) return result;

            foreach (var go in _scene.All())
            {
                if (!go.ActiveSelf) continue;
                if (!LayerMask.Contains(mask, go)) continue;

                foreach (var comp in go.Components)
                {
                    if (!comp.Enabled) continue;
                    bool overlap = comp switch
                    {
                        BoxCollider bc => SphereVsBox(c, r, go, bc, ignoreTriggers),
                        SphereCollider sc => SphereVsSphere(c, r, go, sc, ignoreTriggers),
                        CapsuleCollider cc => SphereVsCapsule(c, r, go, cc, ignoreTriggers),
                        _ => false,
                    };
                    if (overlap) { result.Add(go); break; }
                }
            }
            return result;
        }

        private static bool SphereVsBox(
            Vector3 c, float r, GameObject go, BoxCollider bc, bool ignoreTriggers)
        {
            if (ignoreTriggers && bc.IsTrigger) return false;
            var (wc, wr, ws) = GetWorldTransform(go, bc.Center);
            var half = bc.Size * 0.5f * ws;
            var localC = Vector3.Transform(c - wc, Quaternion.Invert(wr));
            var clamped = Vector3.Clamp(localC, -half, half);
            return (localC - clamped).LengthSquared <= r * r;
        }

        private static bool SphereVsSphere(
            Vector3 c, float r, GameObject go, SphereCollider sc, bool ignoreTriggers)
        {
            if (ignoreTriggers && sc.IsTrigger) return false;
            var (wc, _, ws) = GetWorldTransform(go, sc.Center);
            float rad = sc.Radius * MathF.Max(ws.X, MathF.Max(ws.Y, ws.Z));
            float sum = r + rad;
            return (c - wc).LengthSquared <= sum * sum;
        }

        private static bool SphereVsCapsule(
            Vector3 c, float r, GameObject go, CapsuleCollider cc, bool ignoreTriggers)
        {
            if (ignoreTriggers && cc.IsTrigger) return false;
            var (wc, wr, ws) = GetWorldTransform(go, cc.Center);
            float rad = cc.Radius * MathF.Max(ws.X, ws.Z);
            float halfH = MathF.Max(0f, cc.Height * ws.Y * 0.5f - rad);
            var up = Vector3.Transform(Vector3.UnitY, wr);
            var capA = wc - up * halfH;
            var capB = wc + up * halfH;
            var ab = capB - capA;
            float t = Math.Clamp(
                Vector3.Dot(c - capA, ab) / MathF.Max(ab.LengthSquared, 1e-6f), 0f, 1f);
            float sum = r + rad;
            return (c - (capA + ab * t)).LengthSquared <= sum * sum;
        }

        // ── Utility ───────────────────────────────────────────────────────────
        private static RaycastHit? FindNearest(List<RaycastHit> hits)
        {
            RaycastHit? best = null;
            foreach (var h in hits)
                if (best == null || h.Distance < best.Distance)
                    best = h;
            return best;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PhysicsDebug  —  Unity-style Debug.DrawRay / DrawLine
    // ═══════════════════════════════════════════════════════════════════════════
    public static class PhysicsDebug
    {
        public struct DebugLine
        {
            public Vector3 Start;
            public Vector3 End;
            public Vector4 Color;
            public float ExpiresAt;   // -1 = single frame
            public bool DepthTest;
        }

        private static readonly List<DebugLine> _lines = new();
        private static float _time;

        /// <summary>
        /// Draws a ray in the Scene View.
        /// direction = direction AND magnitude (NOT normalised) — exactly like Unity.
        /// duration = 0 → visible for one frame.
        /// </summary>
        public static void DrawRay(
            Vector3 start, Vector3 direction,
            Vector4 color = default, float duration = 0f, bool depthTest = true)
        {
            if (color == default) color = new Vector4(0.2f, 1f, 0.2f, 1f);
            _lines.Add(new DebugLine
            {
                Start = start,
                End = start + direction,
                Color = color,
                ExpiresAt = duration <= 0f ? -1f : _time + duration,
                DepthTest = depthTest,
            });
        }

        public static void DrawLine(
            Vector3 start, Vector3 end,
            Vector4 color = default, float duration = 0f, bool depthTest = true)
        {
            if (color == default) color = new Vector4(0.2f, 1f, 0.2f, 1f);
            _lines.Add(new DebugLine
            {
                Start = start,
                End = end,
                Color = color,
                ExpiresAt = duration <= 0f ? -1f : _time + duration,
                DepthTest = depthTest,
            });
        }

        public static void DrawRay(Vector3 start, Vector3 direction,
            System.Drawing.Color color, float duration = 0f, bool depthTest = true)
            => DrawRay(start, direction,
                new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f),
                duration, depthTest);

        public static void DrawLine(Vector3 start, Vector3 end,
            System.Drawing.Color color, float duration = 0f, bool depthTest = true)
            => DrawLine(start, end,
                new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f),
                duration, depthTest);

        internal static void Tick(float dt)
        {
            _time += dt;
            _lines.RemoveAll(l => l.ExpiresAt >= 0f && l.ExpiresAt <= _time);
        }

        internal static void FlushSingleFrame()
            => _lines.RemoveAll(l => l.ExpiresAt < 0f);

        internal static IReadOnlyList<DebugLine> Lines => _lines;

        public static void Clear() { _lines.Clear(); _time = 0f; }
    }
}