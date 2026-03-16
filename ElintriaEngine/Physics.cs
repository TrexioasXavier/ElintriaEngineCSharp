using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    public readonly struct Ray
    {
        public Vector3 Origin { get; }
        public Vector3 Direction { get; }
        public Ray(Vector3 origin, Vector3 direction)
        {
            Origin = origin;
            Direction = direction.LengthSquared > 0 ? Vector3.Normalize(direction) : Vector3.UnitZ;
        }
        public Vector3 GetPoint(float t) => Origin + Direction * t;
    }

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
        public T? GetComponent<T>() where T : Component => GameObject?.GetComponent<T>();
    }

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
    //  MeshTriangleCache
    //
    //  Loads triangle data from .obj files or generates built-in primitive meshes.
    //  Builds a BVH (Bounding Volume Hierarchy) per mesh for fast ray tests.
    //  Each mesh is loaded and cached once; call ClearCache() on project reload.
    //
    //  Supported keys (passed to GetOrLoad):
    //    "cube" / "sphere" / "capsule" / "cylinder" / "plane" / "quad"
    //       → procedurally generated unit-size primitives
    //    any file path ending in .obj
    //       → loaded from disk, all polygons fan-triangulated
    // ═══════════════════════════════════════════════════════════════════════════
    public static class MeshTriangleCache
    {
        public struct Triangle
        {
            public Vector3 A, B, C, Normal;
            public Triangle(Vector3 a, Vector3 b, Vector3 c)
            {
                A = a; B = b; C = c;
                var cross = Vector3.Cross(b - a, c - a);
                Normal = cross.LengthSquared > 1e-10f ? Vector3.Normalize(cross) : Vector3.UnitY;
            }
            public Vector3 Centroid => (A + B + C) * (1f / 3f);
            public void GetAABB(out Vector3 min, out Vector3 max)
            {
                min = Vector3.ComponentMin(A, Vector3.ComponentMin(B, C));
                max = Vector3.ComponentMax(A, Vector3.ComponentMax(B, C));
            }
        }

        public class BvhNode
        {
            public Vector3 Min, Max;
            public BvhNode? Left, Right;
            public int TriStart, TriCount;
            public bool IsLeaf => Left == null;
        }

        public class MeshData
        {
            public Triangle[] Triangles = Array.Empty<Triangle>();
            public BvhNode? Root;
            public Vector3 BoundsMin, BoundsMax;
        }

        private static readonly Dictionary<string, MeshData> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache() => _cache.Clear();

        public static MeshData? GetOrLoad(string meshKey)
        {
            if (string.IsNullOrWhiteSpace(meshKey)) return null;
            if (_cache.TryGetValue(meshKey, out var cached)) return cached;
            var data = meshKey.ToLowerInvariant() switch
            {
                "cube" => BuildCube(),
                "sphere" => BuildSphere(16, 12),
                "capsule" => BuildCapsule(12, 8),
                "cylinder" => BuildCylinder(16),
                "plane" => BuildPlane(),
                "quad" => BuildQuad(),
                _ => LoadObj(meshKey),
            };
            if (data != null) { BuildBVH(data); _cache[meshKey] = data; }
            return data;
        }

        // ── BVH ray test ──────────────────────────────────────────────────────
        public static bool RaycastLocal(MeshData data, Vector3 ro, Vector3 rd,
                                         out float hitT, out Vector3 hitNormal)
        {
            hitT = float.MaxValue; hitNormal = Vector3.UnitY;
            if (data.Root == null || data.Triangles.Length == 0) return false;
            return RayBvh(data.Root, data.Triangles, ro, rd, ref hitT, ref hitNormal);
        }

        private static bool RayBvh(BvhNode node, Triangle[] tris, Vector3 ro, Vector3 rd,
                                     ref float bestT, ref Vector3 bestN)
        {
            if (!RayVsAABB(ro, rd, node.Min, node.Max, out float te)) return false;
            if (te > bestT) return false;
            if (node.IsLeaf)
            {
                bool any = false;
                for (int i = node.TriStart; i < node.TriStart + node.TriCount; i++)
                    if (RayVsTri(ro, rd, tris[i], out float t, out Vector3 n) && t < bestT)
                    { bestT = t; bestN = n; any = true; }
                return any;
            }
            bool l = node.Left != null && RayBvh(node.Left, tris, ro, rd, ref bestT, ref bestN);
            bool r = node.Right != null && RayBvh(node.Right, tris, ro, rd, ref bestT, ref bestN);
            return l || r;
        }

        // Möller–Trumbore
        private static bool RayVsTri(Vector3 ro, Vector3 rd, in Triangle tri,
                                      out float t, out Vector3 normal)
        {
            t = 0f; normal = tri.Normal;
            var ab = tri.B - tri.A; var ac = tri.C - tri.A;
            var pv = Vector3.Cross(rd, ac);
            float det = Vector3.Dot(ab, pv);
            if (MathF.Abs(det) < 1e-7f) return false;
            float inv = 1f / det;
            var tv = ro - tri.A;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0f || u > 1f) return false;
            var qv = Vector3.Cross(tv, ab);
            float v = Vector3.Dot(rd, qv) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(ac, qv) * inv;
            if (t < 1e-5f) return false;
            if (Vector3.Dot(tri.Normal, rd) > 0f) normal = -tri.Normal;
            return true;
        }

        private static bool RayVsAABB(Vector3 ro, Vector3 rd, Vector3 bMin, Vector3 bMax,
                                        out float tEntry)
        {
            tEntry = float.NegativeInfinity;
            float tExit = float.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? ro.X : i == 1 ? ro.Y : ro.Z;
                float d = i == 0 ? rd.X : i == 1 ? rd.Y : rd.Z;
                float mn = i == 0 ? bMin.X : i == 1 ? bMin.Y : bMin.Z;
                float mx = i == 0 ? bMax.X : i == 1 ? bMax.Y : bMax.Z;
                if (MathF.Abs(d) < 1e-7f) { if (o < mn || o > mx) return false; }
                else
                {
                    float t1 = (mn - o) / d, t2 = (mx - o) / d;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tEntry = MathF.Max(tEntry, t1);
                    tExit = MathF.Min(tExit, t2);
                    if (tEntry > tExit) return false;
                }
            }
            return tExit >= 0f;
        }

        // ── BVH build ─────────────────────────────────────────────────────────
        private static void BuildBVH(MeshData data)
        {
            if (data.Triangles.Length == 0) return;
            data.BoundsMin = new Vector3(float.MaxValue);
            data.BoundsMax = new Vector3(float.MinValue);
            foreach (var tri in data.Triangles)
            {
                tri.GetAABB(out var mn, out var mx);
                data.BoundsMin = Vector3.ComponentMin(data.BoundsMin, mn);
                data.BoundsMax = Vector3.ComponentMax(data.BoundsMax, mx);
            }
            var work = new List<Triangle>(data.Triangles);
            data.Root = Split(work, 0, work.Count, 0);
            data.Triangles = work.ToArray();
        }

        private static BvhNode Split(List<Triangle> tris, int start, int end, int depth)
        {
            var node = new BvhNode();
            ComputeAABB(tris, start, end, out node.Min, out node.Max);
            int count = end - start;
            if (count <= 4 || depth >= 24) { node.TriStart = start; node.TriCount = count; return node; }
            var ext = node.Max - node.Min;
            int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
            tris.Sort(start, count, Comparer<Triangle>.Create((a, b) =>
            {
                float ca = axis == 0 ? a.Centroid.X : axis == 1 ? a.Centroid.Y : a.Centroid.Z;
                float cb = axis == 0 ? b.Centroid.X : axis == 1 ? b.Centroid.Y : b.Centroid.Z;
                return ca.CompareTo(cb);
            }));
            int mid = start + count / 2;
            node.Left = Split(tris, start, mid, depth + 1);
            node.Right = Split(tris, mid, end, depth + 1);
            return node;
        }

        private static void ComputeAABB(List<Triangle> tris, int start, int end,
                                          out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
            for (int i = start; i < end; i++)
            {
                tris[i].GetAABB(out var mn, out var mx);
                min = Vector3.ComponentMin(min, mn); max = Vector3.ComponentMax(max, mx);
            }
        }

        // ── OBJ loader ────────────────────────────────────────────────────────
        private static MeshData? LoadObj(string path)
        {
            if (!File.Exists(path)) { Console.WriteLine($"[MeshCollider] Not found: {path}"); return null; }
            var pos = new List<Vector3>();
            var tris = new List<Triangle>();
            try
            {
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("v "))
                    {
                        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 4 &&
                            float.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                            float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                            float.TryParse(p[3], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float z))
                            pos.Add(new Vector3(x, y, z));
                    }
                    else if (line.StartsWith("f "))
                    {
                        var tok = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var vi = new List<int>();
                        for (int ti = 1; ti < tok.Length; ti++)
                        {
                            var idx = tok[ti].Split('/')[0];
                            if (int.TryParse(idx, out int i))
                            { if (i < 0) i = pos.Count + i + 1; vi.Add(i - 1); }
                        }
                        for (int fi = 1; fi < vi.Count - 1; fi++)
                        {
                            int i0 = vi[0], i1 = vi[fi], i2 = vi[fi + 1];
                            if (i0 >= 0 && i0 < pos.Count && i1 >= 0 && i1 < pos.Count && i2 >= 0 && i2 < pos.Count)
                                tris.Add(new Triangle(pos[i0], pos[i1], pos[i2]));
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[MeshCollider] Error: {ex.Message}"); return null; }
            if (tris.Count == 0) { Console.WriteLine($"[MeshCollider] No tris in {path}"); return null; }
            Console.WriteLine($"[MeshCollider] {tris.Count} tris from {Path.GetFileName(path)}");
            return new MeshData { Triangles = tris.ToArray() };
        }

        // ── Primitive generators ──────────────────────────────────────────────
        private static MeshData BuildCube()
        {
            var t = new List<Triangle>();
            t.Add(new(new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f)));
            t.Add(new(new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)));
            t.Add(new(new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f)));
            t.Add(new(new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f)));
            t.Add(new(new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f)));
            t.Add(new(new(-0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)));
            t.Add(new(new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f)));
            t.Add(new(new(0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f)));
            t.Add(new(new(0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f)));
            t.Add(new(new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f)));
            t.Add(new(new(-0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)));
            t.Add(new(new(-0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, -0.5f)));
            return new MeshData { Triangles = t.ToArray() };
        }

        private static MeshData BuildPlane()
        {
            var t = new List<Triangle>
            {
                new(new(-0.5f,0,-0.5f),new( 0.5f,0,-0.5f),new( 0.5f,0,0.5f)),
                new(new(-0.5f,0,-0.5f),new( 0.5f,0, 0.5f),new(-0.5f,0,0.5f)),
                new(new(-0.5f,0, 0.5f),new( 0.5f,0, 0.5f),new( 0.5f,0,-0.5f)),
                new(new(-0.5f,0, 0.5f),new( 0.5f,0,-0.5f),new(-0.5f,0,-0.5f)),
            };
            return new MeshData { Triangles = t.ToArray() };
        }

        private static MeshData BuildQuad()
        {
            var t = new List<Triangle>
            {
                new(new(-0.5f,-0.5f,0),new(0.5f,-0.5f,0),new( 0.5f,0.5f,0)),
                new(new(-0.5f,-0.5f,0),new(0.5f, 0.5f,0),new(-0.5f,0.5f,0)),
                new(new(-0.5f, 0.5f,0),new(0.5f, 0.5f,0),new( 0.5f,-0.5f,0)),
                new(new(-0.5f, 0.5f,0),new(0.5f,-0.5f,0),new(-0.5f,-0.5f,0)),
            };
            return new MeshData { Triangles = t.ToArray() };
        }

        private static MeshData BuildSphere(int segs, int rings)
        {
            var v = new List<Vector3>(); var t = new List<Triangle>();
            for (int r = 0; r <= rings; r++)
            {
                float phi = MathF.PI * r / rings;
                for (int s = 0; s <= segs; s++)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    v.Add(new(MathF.Sin(phi) * MathF.Cos(theta) * 0.5f,
                               MathF.Cos(phi) * 0.5f,
                               MathF.Sin(phi) * MathF.Sin(theta) * 0.5f));
                }
            }
            int w = segs + 1;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segs; s++)
                {
                    int i0 = r * w + s, i1 = i0 + 1, i2 = (r + 1) * w + s, i3 = i2 + 1;
                    t.Add(new(v[i0], v[i2], v[i1])); t.Add(new(v[i1], v[i2], v[i3]));
                }
            return new MeshData { Triangles = t.ToArray() };
        }

        private static MeshData BuildCylinder(int segs)
        {
            var t = new List<Triangle>(); float h = 0.5f, r = 0.5f;
            var top = new Vector3[segs]; var bot = new Vector3[segs];
            for (int i = 0; i < segs; i++)
            {
                float a = 2f * MathF.PI * i / segs;
                top[i] = new(MathF.Cos(a) * r, h, MathF.Sin(a) * r);
                bot[i] = new(MathF.Cos(a) * r, -h, MathF.Sin(a) * r);
            }
            var tc = new Vector3(0, h, 0); var bc = new Vector3(0, -h, 0);
            for (int i = 0; i < segs; i++)
            {
                int n = (i + 1) % segs;
                t.Add(new(top[i], top[n], tc)); t.Add(new(bot[n], bot[i], bc));
                t.Add(new(bot[i], top[i], top[n])); t.Add(new(bot[i], top[n], bot[n]));
            }
            return new MeshData { Triangles = t.ToArray() };
        }

        private static MeshData BuildCapsule(int segs, int rings)
        {
            var sphere = BuildSphere(segs, rings);
            Vector3 S(Vector3 v) => new(v.X, v.Y + (v.Y >= 0 ? 0.5f : -0.5f), v.Z);
            var t = new List<Triangle>();
            foreach (var tri in sphere.Triangles) t.Add(new(S(tri.A), S(tri.B), S(tri.C)));
            return new MeshData { Triangles = t.ToArray() };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Physics
    // ═══════════════════════════════════════════════════════════════════════════
    public static class Physics
    {
        private static Scene? _scene;

        public static void SetScene(Scene? scene)
        {
            _scene = scene;
            if (scene != null)
                Console.WriteLine($"[Physics] Scene set: '{scene.Name}' ({CountColliders(scene)} collider(s))");
        }

        public static Scene? ActiveScene => _scene;

        private static int CountColliders(Scene s)
        {
            int n = 0;
            foreach (var go in s.All())
                foreach (var c in go.Components)
                    if (c is BoxCollider or SphereCollider or CapsuleCollider
                            or MeshCollider or BoxCollider2D or CircleCollider2D) n++;
            return n;
        }

        public static Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

        // ── Raycast ───────────────────────────────────────────────────────────
        /// <summary>
        /// Casts a ray and returns the nearest hit.
        /// ignoreTriggers = false (default) → hits BOTH trigger and non-trigger colliders.
        /// ignoreTriggers = true            → skips trigger colliders (use when you only
        ///                                    want solid geometry, e.g. movement blocking).
        /// </summary>
        public static bool Raycast(
            Vector3 origin, Vector3 direction, out RaycastHit hit,
            float maxDistance = float.MaxValue,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = false)
        {
            hit = null!;
            if (_scene == null) { Console.WriteLine("[Physics] Raycast: scene is null."); return false; }
            var ray = new Ray(origin, direction);
            var best = FindNearest(CastAll(ray, maxDistance, layerMask, ignoreTriggers));
            if (best == null) return false;
            hit = best; return true;
        }

        public static bool Raycast(Ray ray, out RaycastHit hit,
            float maxDistance = float.MaxValue,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = false)
            => Raycast(ray.Origin, ray.Direction, out hit, maxDistance, layerMask, ignoreTriggers);

        public static RaycastHit[] RaycastAll(
            Vector3 origin, Vector3 direction,
            float maxDistance = float.MaxValue,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = false)
        {
            if (_scene == null) return Array.Empty<RaycastHit>();
            var hits = CastAll(new Ray(origin, direction), maxDistance, layerMask, ignoreTriggers);
            hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return hits.ToArray();
        }

        public static bool CheckSphere(Vector3 centre, float radius,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = false)
            => OverlapSphereList(centre, radius, layerMask, ignoreTriggers).Count > 0;

        public static GameObject[] OverlapSphere(Vector3 centre, float radius,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = false)
        {
            var list = OverlapSphereList(centre, radius, layerMask, ignoreTriggers);
            var res = new List<GameObject>();
            foreach (var go in list) if (!res.Contains(go)) res.Add(go);
            return res.ToArray();
        }

        public static bool Linecast(Vector3 start, Vector3 end, out RaycastHit hit,
            int layerMask = LayerMask.Everything,
            bool ignoreTriggers = false)
        {
            var dir = end - start; float dist = dir.Length;
            if (dist < 1e-6f) { hit = null!; return false; }
            return Raycast(start, dir / dist, out hit, dist, layerMask, ignoreTriggers);
        }

        // ── Core cast loop ────────────────────────────────────────────────────
        private static List<RaycastHit> CastAll(Ray ray, float maxDist, int mask, bool ignoreTriggers)
        {
            var results = new List<RaycastHit>();
            foreach (var go in _scene!.All())
            {
                if (!go.ActiveSelf || !LayerMask.Contains(mask, go)) continue;
                foreach (var comp in go.Components)
                {
                    if (!comp.Enabled) continue;
                    RaycastHit? h = comp switch
                    {
                        BoxCollider bc => RayVsBox(ray, go, bc, ignoreTriggers),
                        SphereCollider sc => RayVsSphere(ray, go, sc, ignoreTriggers),
                        CapsuleCollider cc => RayVsCapsule(ray, go, cc, ignoreTriggers),
                        MeshCollider mc => RayVsMesh(ray, go, mc, ignoreTriggers),
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

        private static (Vector3 wc, Quaternion wr, Vector3 ws)
            GetWorldTransform(GameObject go, Vector3 localCenter)
        {
            var t = go.Transform;
            var wr = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(t.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Z));
            var wc = t.LocalPosition + Vector3.Transform(localCenter * t.LocalScale, wr);
            return (wc, wr, t.LocalScale);
        }

        // ── Ray vs OBB (BoxCollider) ──────────────────────────────────────────
        // ignoreTriggers=false means we DO test triggers — the guard only fires when true.
        private static RaycastHit? RayVsBox(Ray ray, GameObject go,
                                             BoxCollider bc, bool ignoreTriggers)
        {
            if (ignoreTriggers && bc.IsTrigger) return null;
            var (wc, wr, ws) = GetWorldTransform(go, bc.Center);
            var half = bc.Size * 0.5f * ws;
            var axX = Vector3.Transform(Vector3.UnitX, wr);
            var axY = Vector3.Transform(Vector3.UnitY, wr);
            var axZ = Vector3.Transform(Vector3.UnitZ, wr);
            var d = ray.Origin - wc;
            float tMin = float.NegativeInfinity, tMax = float.PositiveInfinity;
            var hitN = Vector3.Zero;
            foreach (var (axis, h) in new[] { (axX, half.X), (axY, half.Y), (axZ, half.Z) })
            {
                float e = Vector3.Dot(axis, d), f = Vector3.Dot(axis, ray.Direction);
                if (MathF.Abs(f) > 1e-6f)
                {
                    float t1 = (-e - h) / f, t2 = (-e + h) / f;
                    var n1 = f > 0 ? -axis : axis; var n2 = f > 0 ? axis : -axis;
                    if (t1 > t2) { (t1, t2) = (t2, t1); (n1, n2) = (n2, n1); }
                    if (t1 > tMin) { tMin = t1; hitN = n1; }
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax || tMax < 0f) return null;
                }
                else if (e < -h || e > h) return null;
            }
            float dist = tMin >= 0f ? tMin : tMax;
            if (dist < 0f) return null;
            return new RaycastHit
            {
                Distance = dist,
                Point = ray.GetPoint(dist),
                Normal = Vector3.Normalize(hitN),
                GameObject = go,
                Collider = bc
            };
        }

        // ── Ray vs Sphere ─────────────────────────────────────────────────────
        private static RaycastHit? RayVsSphere(Ray ray, GameObject go,
                                                SphereCollider sc, bool ignoreTriggers)
        {
            if (ignoreTriggers && sc.IsTrigger) return null;
            var (wc, _, ws) = GetWorldTransform(go, sc.Center);
            float r = sc.Radius * MathF.Max(ws.X, MathF.Max(ws.Y, ws.Z));
            var oc = ray.Origin - wc;
            float b = Vector3.Dot(oc, ray.Direction), c = oc.LengthSquared - r * r, disc = b * b - c;
            if (disc < 0f) return null;
            float sq = MathF.Sqrt(disc), t = -b - sq; if (t < 0f) t = -b + sq; if (t < 0f) return null;
            var pt = ray.GetPoint(t);
            return new RaycastHit
            {
                Distance = t,
                Point = pt,
                Normal = Vector3.Normalize(pt - wc),
                GameObject = go,
                Collider = sc
            };
        }

        // ── Ray vs Capsule ────────────────────────────────────────────────────
        private static RaycastHit? RayVsCapsule(Ray ray, GameObject go,
                                                  CapsuleCollider cc, bool ignoreTriggers)
        {
            if (ignoreTriggers && cc.IsTrigger) return null;
            var (wc, wr, ws) = GetWorldTransform(go, cc.Center);
            Vector3 up; float rs, hs;
            switch (cc.Direction)
            {
                case 0: up = Vector3.Transform(Vector3.UnitX, wr); rs = MathF.Max(ws.Y, ws.Z); hs = ws.X; break;
                case 2: up = Vector3.Transform(Vector3.UnitZ, wr); rs = MathF.Max(ws.X, ws.Y); hs = ws.Z; break;
                default: up = Vector3.Transform(Vector3.UnitY, wr); rs = MathF.Max(ws.X, ws.Z); hs = ws.Y; break;
            }
            float radius = cc.Radius * rs, halfH = MathF.Max(0f, cc.Height * hs * 0.5f - radius);
            var capA = wc - up * halfH;
            var capB = wc + up * halfH;
            float bestT = float.MaxValue; RaycastHit? best = null;
            var ab = capB - capA; float len = ab.Length;
            if (len > 1e-6f)
            {
                var abn = ab / len;
                var rd = ray.Direction - abn * Vector3.Dot(ray.Direction, abn);
                var od = (ray.Origin - capA) - abn * Vector3.Dot(ray.Origin - capA, abn);
                float a = rd.LengthSquared;
                if (a > 1e-6f)
                {
                    float bv = Vector3.Dot(rd, od), cv = od.LengthSquared - radius * radius, disc = bv * bv - a * cv;
                    if (disc >= 0f)
                    {
                        float sq = MathF.Sqrt(disc), t0 = (-bv - sq) / a, t1 = (-bv + sq) / a;
                        float t = t0 >= 0f ? t0 : t1;
                        if (t >= 0f)
                        {
                            var pt = ray.GetPoint(t); float pr = Vector3.Dot(pt - capA, abn);
                            if (pr >= 0f && pr <= len)
                            {
                                bestT = t; best = new RaycastHit
                                {
                                    Distance = t,
                                    Point = pt,
                                    Normal = Vector3.Normalize(pt - (capA + abn * pr)),
                                    GameObject = go,
                                    Collider = cc
                                };
                            }
                        }
                    }
                }
            }
            foreach (var cap in new[] { capA, capB })
            {
                var oc = ray.Origin - cap; float b = Vector3.Dot(oc, ray.Direction);
                float c = oc.LengthSquared - radius * radius, disc = b * b - c;
                if (disc < 0f) continue; float sq = MathF.Sqrt(disc), t = -b - sq;
                if (t < 0f) t = -b + sq; if (t < 0f || t >= bestT) continue;
                var pt = ray.GetPoint(t); bestT = t;
                best = new RaycastHit
                {
                    Distance = t,
                    Point = pt,
                    Normal = Vector3.Normalize(pt - cap),
                    GameObject = go,
                    Collider = cc
                };
            }
            return best;
        }

        // ── Ray vs MeshCollider (real triangle BVH) ───────────────────────────
        //
        // Mesh key resolution order:
        //   1. MeshFilter.MeshPath  (file path to .obj)
        //   2. MeshFilter.MeshName  (built-in primitive name: cube/sphere/etc.)
        //   3. Fallback: unit BoxCollider
        //
        // The ray is transformed into the GO's local space, tested against the
        // cached BVH, then the hit is transformed back to world space.
        private static RaycastHit? RayVsMesh(Ray ray, GameObject go,
                                              MeshCollider mc, bool ignoreTriggers)
        {
            if (ignoreTriggers && mc.IsTrigger) return null;

            string meshKey = "";
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null)
                meshKey = !string.IsNullOrWhiteSpace(mf.MeshPath)
                    ? mf.MeshPath : (mf.MeshName?.ToLowerInvariant() ?? "");

            var data = MeshTriangleCache.GetOrLoad(meshKey);
            if (data == null || data.Triangles.Length == 0)
            {
                // Fallback to unit box
                var fake = new BoxCollider
                {
                    Center = Vector3.Zero,
                    Size = Vector3.One,
                    IsTrigger = mc.IsTrigger,
                    Enabled = mc.Enabled
                };
                fake.GameObject = go;
                return RayVsBox(ray, go, fake, ignoreTriggers: false);
            }

            // Transform ray to local space (inverse TRS)
            var t = go.Transform;
            var scale = t.LocalScale;
            var rot = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(t.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Z));
            var invRot = Quaternion.Invert(rot);
            var localOrg = Vector3.Transform(ray.Origin - t.LocalPosition, invRot) / scale;
            var localDir = Vector3.Transform(ray.Direction, invRot) / scale;
            float localLen = localDir.Length;
            if (localLen < 1e-7f) return null;
            var localDirN = localDir / localLen;

            if (!MeshTriangleCache.RaycastLocal(data, localOrg, localDirN,
                    out float localT, out Vector3 localNormal))
                return null;

            float worldT = localT / localLen;
            var worldPt = ray.GetPoint(worldT);
            var worldN = Vector3.Normalize(Vector3.Transform(localNormal, rot));
            return new RaycastHit
            {
                Distance = worldT,
                Point = worldPt,
                Normal = worldN,
                GameObject = go,
                Collider = mc
            };
        }

        // ── 2D helpers ────────────────────────────────────────────────────────
        private static RaycastHit? RayVsBox2D(Ray ray, GameObject go,
                                               BoxCollider2D b2, bool ignoreTriggers)
        {
            if (ignoreTriggers && b2.IsTrigger) return null;
            var fake = new BoxCollider
            {
                Center = b2.Offset,
                Size = new(b2.Width, b2.Height, 0.02f),
                IsTrigger = b2.IsTrigger,
                Enabled = b2.Enabled
            };
            fake.GameObject = go;
            return RayVsBox(ray, go, fake, ignoreTriggers: false);
        }

        private static RaycastHit? RayVsCircle2D(Ray ray, GameObject go,
                                                   CircleCollider2D c2, bool ignoreTriggers)
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
            return RayVsSphere(ray, go, fake, ignoreTriggers: false);
        }

        // ── Sphere overlap ────────────────────────────────────────────────────
        private static List<GameObject> OverlapSphereList(Vector3 c, float r,
                                                            int mask, bool ignoreTriggers)
        {
            var res = new List<GameObject>();
            if (_scene == null) return res;
            foreach (var go in _scene.All())
            {
                if (!go.ActiveSelf || !LayerMask.Contains(mask, go)) continue;
                foreach (var comp in go.Components)
                {
                    if (!comp.Enabled) continue;
                    bool ov = comp switch
                    {
                        BoxCollider bc => SphereVsBox(c, r, go, bc, ignoreTriggers),
                        SphereCollider sc => SphereVsSphere(c, r, go, sc, ignoreTriggers),
                        CapsuleCollider cc => SphereVsCapsule(c, r, go, cc, ignoreTriggers),
                        _ => false,
                    };
                    if (ov) { res.Add(go); break; }
                }
            }
            return res;
        }

        private static bool SphereVsBox(Vector3 c, float r, GameObject go,
                                          BoxCollider bc, bool ignoreTriggers)
        {
            if (ignoreTriggers && bc.IsTrigger) return false;
            var (wc, wr, ws) = GetWorldTransform(go, bc.Center);
            var half = bc.Size * 0.5f * ws;
            var lc = Vector3.Transform(c - wc, Quaternion.Invert(wr));
            return (lc - Vector3.Clamp(lc, -half, half)).LengthSquared <= r * r;
        }

        private static bool SphereVsSphere(Vector3 c, float r, GameObject go,
                                             SphereCollider sc, bool ignoreTriggers)
        {
            if (ignoreTriggers && sc.IsTrigger) return false;
            var (wc, _, ws) = GetWorldTransform(go, sc.Center);
            float rad = sc.Radius * MathF.Max(ws.X, MathF.Max(ws.Y, ws.Z));
            return (c - wc).LengthSquared <= (r + rad) * (r + rad);
        }

        private static bool SphereVsCapsule(Vector3 c, float r, GameObject go,
                                              CapsuleCollider cc, bool ignoreTriggers)
        {
            if (ignoreTriggers && cc.IsTrigger) return false;
            var (wc, wr, ws) = GetWorldTransform(go, cc.Center);
            float rad = cc.Radius * MathF.Max(ws.X, ws.Z);
            float hh = MathF.Max(0f, cc.Height * ws.Y * 0.5f - rad);
            var up = Vector3.Transform(Vector3.UnitY, wr);
            var A = wc - up * hh;
            var B = wc + up * hh;
            var ab = B - A;
            float tv = Math.Clamp(Vector3.Dot(c - A, ab) / MathF.Max(ab.LengthSquared, 1e-6f), 0f, 1f);
            return (c - (A + ab * tv)).LengthSquared <= (r + rad) * (r + rad);
        }

        private static RaycastHit? FindNearest(List<RaycastHit> hits)
        {
            RaycastHit? best = null;
            foreach (var h in hits) if (best == null || h.Distance < best.Distance) best = h;
            return best;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PhysicsDebug
    // ═══════════════════════════════════════════════════════════════════════════
    public static class PhysicsDebug
    {
        public struct DebugLine
        {
            public Vector3 Start, End;
            public Vector4 Color;
            public float ExpiresAt;
            public bool DepthTest;
        }
        private static readonly List<DebugLine> _lines = new();
        private static float _time;

        public static void DrawRay(Vector3 start, Vector3 direction,
            Vector4 color = default, float duration = 0f, bool depthTest = true)
        {
            if (color == default) color = new Vector4(0.2f, 1f, 0.2f, 1f);
            _lines.Add(new DebugLine
            {
                Start = start,
                End = start + direction,
                Color = color,
                ExpiresAt = duration <= 0f ? -1f : _time + duration,
                DepthTest = depthTest
            });
        }
        public static void DrawLine(Vector3 start, Vector3 end,
            Vector4 color = default, float duration = 0f, bool depthTest = true)
        {
            if (color == default) color = new Vector4(0.2f, 1f, 0.2f, 1f);
            _lines.Add(new DebugLine
            {
                Start = start,
                End = end,
                Color = color,
                ExpiresAt = duration <= 0f ? -1f : _time + duration,
                DepthTest = depthTest
            });
        }
        public static void DrawRay(Vector3 s, Vector3 d, System.Drawing.Color c,
            float dur = 0f, bool dt = true)
            => DrawRay(s, d, new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f), dur, dt);
        public static void DrawLine(Vector3 s, Vector3 e, System.Drawing.Color c,
            float dur = 0f, bool dt = true)
            => DrawLine(s, e, new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f), dur, dt);

        internal static void Tick(float dt)
        {
            _time += dt;
            _lines.RemoveAll(l => l.ExpiresAt >= 0f && l.ExpiresAt <= _time);
        }
        internal static void FlushSingleFrame() => _lines.RemoveAll(l => l.ExpiresAt < 0f);
        internal static IReadOnlyList<DebugLine> Lines => _lines;
        public static void Clear() { _lines.Clear(); _time = 0f; }
    }
}