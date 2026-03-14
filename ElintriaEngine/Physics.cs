using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Ray
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>A ray defined by an origin and a normalised direction.</summary>
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

        /// <summary>Returns the world-space point at distance <paramref name="t"/> along the ray.</summary>
        public Vector3 GetPoint(float t) => Origin + Direction * t;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  RaycastHit
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>Information about a single raycast intersection.</summary>
    public class RaycastHit
    {
        /// <summary>The world-space point where the ray hit the collider surface.</summary>
        public Vector3 Point { get; internal set; }

        /// <summary>The outward surface normal at the hit point.</summary>
        public Vector3 Normal { get; internal set; }

        /// <summary>Distance along the ray from its origin to the hit point.</summary>
        public float Distance { get; internal set; }

        /// <summary>The GameObject whose collider was hit.</summary>
        public GameObject? GameObject { get; internal set; }

        /// <summary>The specific Collider component that was hit.</summary>
        public Component? Collider { get; internal set; }

        // ── Convenience accessors ─────────────────────────────────────────────
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
    //  LayerMask  (mirrors Unity's int-based bitmask approach)
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Integer bitmask for filtering raycasts by layer.
    /// Use <see cref="Everything"/> to hit all layers (default),
    /// or build a mask with <see cref="GetMask"/>.
    /// </summary>
    public static class LayerMask
    {
        public const int Everything = ~0;   // all bits set
        public const int Nothing = 0;

        /// <summary>
        /// Returns a bitmask that includes every supplied layer name.
        /// Layer index is derived from the project's TagsAndLayers registry
        /// (index 0 → bit 0, index 1 → bit 1, …).
        /// </summary>
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

        /// <summary>Returns true when the GO's layer is included in <paramref name="mask"/>.</summary>
        public static bool Contains(int mask, GameObject go)
        {
            if (mask == Everything) return true;
            var tl = TagsAndLayers.Instance;
            int idx = tl.Layers.IndexOf(go.Layer);
            if (idx < 0) return (mask & 1) != 0;    // unknown layer → bit 0
            return (mask & (1 << idx)) != 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Physics  (static — use from any game script)
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Static physics/raycast utility — mirrors the Unity Physics API.
    ///
    /// <b>Setup (SceneRunner calls this automatically):</b>
    /// <code>Physics.SetScene(scene);</code>
    ///
    /// <b>Usage in a game script:</b>
    /// <code>
    /// if (Physics.Raycast(transform.Position, Vector3.UnitZ, out var hit, 100f))
    /// {
    ///     Console.WriteLine($"Hit {hit.Name} at {hit.Point}");
    ///     if (hit.CompareTag("Enemy")) …
    /// }
    /// </code>
    /// </summary>
    public static class Physics
    {
        private static Scene? _scene;

        /// <summary>Called by SceneRunner when a scene starts/stops.</summary>
        public static void SetScene(Scene? scene) => _scene = scene;

        // ── Gravity ───────────────────────────────────────────────────────────
        public static Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

        // ── Raycast (single hit — nearest) ────────────────────────────────────
        /// <summary>
        /// Casts a ray from <paramref name="origin"/> in <paramref name="direction"/>
        /// and returns true if it hits something within <paramref name="maxDistance"/>.
        /// </summary>
        public static bool Raycast(Vector3 origin, Vector3 direction,
                                   out RaycastHit hit,
                                   float maxDistance = float.MaxValue,
                                   int layerMask = LayerMask.Everything,
                                   bool ignoreTriggers = true)
        {
            hit = null!;
            var ray = new Ray(origin, direction);
            var best = CastRay(ray, maxDistance, layerMask, ignoreTriggers, stopAfterFirst: false);
            if (best == null) return false;
            hit = best;
            return true;
        }

        /// <summary>Overload accepting a <see cref="Ray"/> struct directly.</summary>
        public static bool Raycast(Ray ray,
                                   out RaycastHit hit,
                                   float maxDistance = float.MaxValue,
                                   int layerMask = LayerMask.Everything,
                                   bool ignoreTriggers = true)
            => Raycast(ray.Origin, ray.Direction, out hit, maxDistance, layerMask, ignoreTriggers);

        // ── RaycastAll (all hits sorted nearest-first) ────────────────────────
        /// <summary>
        /// Returns ALL hits along the ray within <paramref name="maxDistance"/>,
        /// sorted by distance (nearest first).
        /// </summary>
        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction,
                                              float maxDistance = float.MaxValue,
                                              int layerMask = LayerMask.Everything,
                                              bool ignoreTriggers = true)
        {
            var ray = new Ray(origin, direction);
            var hits = CastRayAll(ray, maxDistance, layerMask, ignoreTriggers);
            hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return hits.ToArray();
        }

        /// <summary>Overload accepting a <see cref="Ray"/> struct directly.</summary>
        public static RaycastHit[] RaycastAll(Ray ray,
                                              float maxDistance = float.MaxValue,
                                              int layerMask = LayerMask.Everything,
                                              bool ignoreTriggers = true)
            => RaycastAll(ray.Origin, ray.Direction, maxDistance, layerMask, ignoreTriggers);

        // ── CheckSphere ───────────────────────────────────────────────────────
        /// <summary>
        /// Returns true if any collider overlaps a sphere at <paramref name="centre"/>
        /// with the given <paramref name="radius"/>.
        /// </summary>
        public static bool CheckSphere(Vector3 centre, float radius,
                                       int layerMask = LayerMask.Everything,
                                       bool ignoreTriggers = true)
            => OverlapSphereInternal(centre, radius, layerMask, ignoreTriggers).Count > 0;

        // ── OverlapSphere ─────────────────────────────────────────────────────
        /// <summary>Returns all colliders whose bounding volumes overlap the sphere.</summary>
        public static GameObject[] OverlapSphere(Vector3 centre, float radius,
                                                 int layerMask = LayerMask.Everything,
                                                 bool ignoreTriggers = true)
        {
            var result = new List<GameObject>();
            foreach (var go in OverlapSphereInternal(centre, radius, layerMask, ignoreTriggers))
                if (!result.Contains(go)) result.Add(go);
            return result.ToArray();
        }

        // ── Linecast ──────────────────────────────────────────────────────────
        /// <summary>
        /// Like Raycast but defined by two world points.
        /// </summary>
        public static bool Linecast(Vector3 start, Vector3 end,
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
        //  Internal implementation
        // ═════════════════════════════════════════════════════════════════════

        private static RaycastHit? CastRay(Ray ray, float maxDist, int mask,
                                            bool ignoreTriggers, bool stopAfterFirst)
        {
            RaycastHit? best = null;
            foreach (var hit in CastRayAll(ray, maxDist, mask, ignoreTriggers))
                if (best == null || hit.Distance < best.Distance)
                    best = hit;
            return best;
        }

        private static List<RaycastHit> CastRayAll(Ray ray, float maxDist, int mask, bool ignoreTriggers)
        {
            var results = new List<RaycastHit>();
            if (_scene == null) return results;

            foreach (var go in _scene.All())
            {
                if (!go.ActiveSelf) continue;
                if (!LayerMask.Contains(mask, go)) continue;

                foreach (var comp in go.Components)
                {
                    if (!comp.Enabled) continue;
                    RaycastHit? hit = comp switch
                    {
                        BoxCollider bc => RayVsBox(ray, go, bc, ignoreTriggers),
                        SphereCollider sc => RayVsSphere(ray, go, sc, ignoreTriggers),
                        CapsuleCollider cc => RayVsCapsule(ray, go, cc, ignoreTriggers),
                        _ => null,
                    };
                    if (hit != null && hit.Distance <= maxDist)
                        results.Add(hit);
                }
            }
            return results;
        }

        private static List<GameObject> OverlapSphereInternal(Vector3 c, float r,
                                                               int mask, bool ignoreTriggers)
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

        // ── Ray vs Box (OBB) ─────────────────────────────────────────────────
        private static RaycastHit? RayVsBox(Ray ray, GameObject go,
                                             BoxCollider bc, bool ignoreTriggers)
        {
            if (ignoreTriggers && bc.IsTrigger) return null;

            var t = go.Transform;
            var scale = t.LocalScale;

            // Half-extents in local space
            var half = bc.Size * 0.5f * scale;
            var worldCenter = t.LocalPosition + bc.Center * scale;

            // Build OBB axes from the rotation
            var rot = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(t.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Z));

            var ax = Vector3.Transform(Vector3.UnitX, rot);
            var ay = Vector3.Transform(Vector3.UnitY, rot);
            var az = Vector3.Transform(Vector3.UnitZ, rot);

            // Slab method in OBB local space
            var d = ray.Origin - worldCenter;

            float tMin = float.NegativeInfinity;
            float tMax = float.PositiveInfinity;
            Vector3 hitNormal = Vector3.Zero;

            (Vector3 axis, float h)[] slabs =
            {
                (ax, half.X), (ay, half.Y), (az, half.Z)
            };

            foreach (var (axis, h) in slabs)
            {
                float e = Vector3.Dot(axis, d);
                float f = Vector3.Dot(axis, ray.Direction);

                if (MathF.Abs(f) > 1e-6f)
                {
                    float t1 = (e + h) / f;
                    float t2 = (e - h) / f;
                    Vector3 n1 = axis, n2 = -axis;
                    if (t1 > t2) { (t1, t2) = (t2, t1); (n1, n2) = (n2, n1); }
                    if (t1 > tMin) { tMin = t1; hitNormal = n1; }
                    tMax = MathF.Min(tMax, t2);
                    if (tMin > tMax || tMax < 0) return null;
                }
                else if (-e - h > 0 || -e + h < 0)
                    return null;
            }

            float dist = tMin >= 0 ? tMin : tMax;
            if (dist < 0) return null;

            return new RaycastHit
            {
                Distance = dist,
                Point = ray.GetPoint(dist),
                Normal = Vector3.Normalize(hitNormal),
                GameObject = go,
                Collider = bc,
            };
        }

        // ── Ray vs Sphere ─────────────────────────────────────────────────────
        private static RaycastHit? RayVsSphere(Ray ray, GameObject go,
                                                SphereCollider sc, bool ignoreTriggers)
        {
            if (ignoreTriggers && sc.IsTrigger) return null;

            var scale = go.Transform.LocalScale;
            float radius = sc.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
            var center = go.Transform.LocalPosition + sc.Center * scale;

            var oc = ray.Origin - center;
            float b = Vector3.Dot(oc, ray.Direction);
            float c = Vector3.Dot(oc, oc) - radius * radius;
            float disc = b * b - c;
            if (disc < 0) return null;

            float sqrt = MathF.Sqrt(disc);
            float t = -b - sqrt;
            if (t < 0) t = -b + sqrt;
            if (t < 0) return null;

            var point = ray.GetPoint(t);
            var normal = Vector3.Normalize(point - center);

            return new RaycastHit
            {
                Distance = t,
                Point = point,
                Normal = normal,
                GameObject = go,
                Collider = sc,
            };
        }

        // ── Ray vs Capsule ────────────────────────────────────────────────────
        private static RaycastHit? RayVsCapsule(Ray ray, GameObject go,
                                                 CapsuleCollider cc, bool ignoreTriggers)
        {
            if (ignoreTriggers && cc.IsTrigger) return null;

            var scale = go.Transform.LocalScale;
            float radius = cc.Radius * MathF.Max(scale.X, scale.Z);
            float halfH = MathF.Max(0f, cc.Height * 0.5f * scale.Y - radius);
            var center = go.Transform.LocalPosition + cc.Center * scale;

            // Capsule axis = Y (default direction 1)
            var capA = center - Vector3.UnitY * halfH;
            var capB = center + Vector3.UnitY * halfH;

            float best = float.MaxValue;
            RaycastHit? bestHit = null;

            // Ray vs cylinder body
            var ab = capB - capA;
            var ao = ray.Origin - capA;
            float abDot = Vector3.Dot(ab, ab);

            if (abDot > 1e-6f)
            {
                var abn = ab / MathF.Sqrt(abDot);
                var rd = ray.Direction - abn * Vector3.Dot(ray.Direction, abn);
                var od = ao - abn * Vector3.Dot(ao, abn);
                float a = Vector3.Dot(rd, rd);
                float b2 = Vector3.Dot(rd, od);
                float c2 = Vector3.Dot(od, od) - radius * radius;
                float disc = b2 * b2 - a * c2;
                if (disc >= 0 && a > 1e-6f)
                {
                    float t0 = (-b2 - MathF.Sqrt(disc)) / a;
                    float t1 = (-b2 + MathF.Sqrt(disc)) / a;
                    float t = t0 >= 0 ? t0 : t1;
                    if (t >= 0)
                    {
                        var pt = ray.GetPoint(t);
                        float proj = Vector3.Dot(pt - capA, abn);
                        if (proj >= 0 && proj <= MathF.Sqrt(abDot))
                        {
                            var closest = capA + abn * proj;
                            best = t;
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

            // Ray vs end spheres
            foreach (var cap in new[] { capA, capB })
            {
                var oc = ray.Origin - cap;
                float b = Vector3.Dot(oc, ray.Direction);
                float c = Vector3.Dot(oc, oc) - radius * radius;
                float disc = b * b - c;
                if (disc < 0) continue;
                float sqrt = MathF.Sqrt(disc);
                float t = -b - sqrt;
                if (t < 0) t = -b + sqrt;
                if (t < 0 || t >= best) continue;
                var pt = ray.GetPoint(t);
                best = t;
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

        // ── Sphere overlap helpers ────────────────────────────────────────────
        private static bool SphereVsBox(Vector3 c, float r, GameObject go,
                                         BoxCollider bc, bool ignoreTriggers)
        {
            if (ignoreTriggers && bc.IsTrigger) return false;
            var scale = go.Transform.LocalScale;
            var center = go.Transform.LocalPosition + bc.Center * scale;
            var half = bc.Size * 0.5f * scale;
            var rot = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(go.Transform.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(go.Transform.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(go.Transform.LocalEulerAngles.Z));
            // Transform sphere centre into box local space
            var local = Vector3.Transform(c - center, Quaternion.Invert(rot));
            var clamped = Vector3.Clamp(local, -half, half);
            return (local - clamped).LengthSquared <= r * r;
        }

        private static bool SphereVsSphere(Vector3 c, float r, GameObject go,
                                            SphereCollider sc, bool ignoreTriggers)
        {
            if (ignoreTriggers && sc.IsTrigger) return false;
            var scale = go.Transform.LocalScale;
            float radius = sc.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
            var center = go.Transform.LocalPosition + sc.Center * scale;
            float sum = r + radius;
            return (c - center).LengthSquared <= sum * sum;
        }

        private static bool SphereVsCapsule(Vector3 c, float r, GameObject go,
                                             CapsuleCollider cc, bool ignoreTriggers)
        {
            if (ignoreTriggers && cc.IsTrigger) return false;
            var scale = go.Transform.LocalScale;
            float radius = cc.Radius * MathF.Max(scale.X, scale.Z);
            float halfH = MathF.Max(0f, cc.Height * 0.5f * scale.Y - radius);
            var center = go.Transform.LocalPosition + cc.Center * scale;
            var capA = center - Vector3.UnitY * halfH;
            var capB = center + Vector3.UnitY * halfH;
            // Closest point on segment to sphere centre
            var ab = capB - capA;
            float t = Math.Clamp(Vector3.Dot(c - capA, ab) / Vector3.Dot(ab, ab), 0f, 1f);
            var closest = capA + ab * t;
            float sum = r + radius;
            return (c - closest).LengthSquared <= sum * sum;
        }
    }
}