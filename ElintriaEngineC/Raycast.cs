using Elintria.Engine.Rendering;
using OpenTK.Mathematics;

namespace Elintria.Engine
{
    /// <summary>A ray in world space.</summary>
    public struct Ray
    {
        public Vector3 Origin;
        public Vector3 Direction;   // unit length

        public Ray(Vector3 origin, Vector3 direction)
        {
            Origin = origin;
            Direction = Vector3.Normalize(direction);
        }

        public Vector3 GetPoint(float t) => Origin + Direction * t;
    }

    /// <summary>Result of a single raycast test.</summary>
    public struct RaycastHit
    {
        public GameObject GameObject;
        public Vector3 Point;
        public float Distance;
        public bool Hit => GameObject != null;
    }

    /// <summary>
    /// Picking utilities.
    ///
    ///   Ray ray = Raycast.ScreenPointToRay(mousePos, view, proj, w, h);
    ///   var hit = Raycast.AgainstScene(ray, scene);
    ///   if (hit.Hit) Console.WriteLine(hit.GameObject.Name);
    /// </summary>
    public static class Raycast
    {
        // ------------------------------------------------------------------
        // Screen → world ray
        // ------------------------------------------------------------------
        public static Ray ScreenPointToRay(Vector2 screenPos,
                                           Matrix4 view, Matrix4 proj,
                                           float screenW, float screenH)
        {
            float ndcX = (2f * screenPos.X / screenW) - 1f;
            float ndcY = -((2f * screenPos.Y / screenH) - 1f);

            Matrix4 invProj = proj.Inverted();
            Matrix4 invView = view.Inverted();

            Vector4 rayClip = new Vector4(ndcX, ndcY, -1f, 1f);
            Vector4 rayView = rayClip * invProj;
            rayView = new Vector4(rayView.X, rayView.Y, -1f, 0f); // direction, not point

            Vector4 rayWorld = rayView * invView;
            Vector3 dir = Vector3.Normalize(rayWorld.Xyz);

            // Origin = camera position (extract from inverse view matrix)
            Vector3 origin = invView.Row3.Xyz;

            return new Ray(origin, dir);
        }

        // ------------------------------------------------------------------
        // Cast against every active GameObject in a scene → nearest hit
        // ------------------------------------------------------------------
        public static RaycastHit AgainstScene(Ray ray, Scene scene)
        {
            RaycastHit best = default;
            best.Distance = float.MaxValue;

            foreach (var go in scene.GameObjects)
            {
                if (!go.ActiveInHierarchy) continue;

                Bounds bounds = GetWorldBounds(go);
                float t = IntersectAABB(ray, bounds);

                if (t >= 0f && t < best.Distance)
                {
                    best.Distance = t;
                    best.Point = ray.GetPoint(t);
                    best.GameObject = go;
                }
            }

            return best.Hit ? best : default;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private static Bounds GetWorldBounds(GameObject go)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr?.Mesh != null)
            {
                var lb = mr.Mesh.Bounds;
                Vector3 center = go.Transform.Position +
                                 go.Transform.Rotation * (lb.Center * go.Transform.LossyScale);
                Vector3 size = lb.Size * go.Transform.LossyScale;
                // Expand slightly so thin objects are still clickable
                return new Bounds(center, size + Vector3.One * 0.05f);
            }
            // Fallback: 1-unit AABB centred on the object
            return new Bounds(go.Transform.Position, Vector3.One);
        }

        public static float IntersectAABB(Ray ray, Bounds b)
        {
            const float eps = 1e-7f;
            float tMin = float.MinValue, tMax = float.MaxValue;

            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? ray.Origin.X : i == 1 ? ray.Origin.Y : ray.Origin.Z;
                float d = i == 0 ? ray.Direction.X : i == 1 ? ray.Direction.Y : ray.Direction.Z;
                float mn = i == 0 ? b.Min.X : i == 1 ? b.Min.Y : b.Min.Z;
                float mx = i == 0 ? b.Max.X : i == 1 ? b.Max.Y : b.Max.Z;

                if (MathF.Abs(d) < eps)
                {
                    if (o < mn || o > mx) return -1f;
                    continue;
                }
                float t1 = (mn - o) / d;
                float t2 = (mx - o) / d;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax) return -1f;
            }

            return tMax < 0f ? -1f : (tMin < 0f ? tMax : tMin);
        }
    }
}