using OpenTK.Mathematics;
using System.Collections.Generic;
using System.Linq;

namespace Elintria.Engine
{
    /// <summary>
    /// The fundamental object in the scene.  Mirrors Unity's GameObject API.
    ///
    /// Usage:
    ///   var go = new GameObject("Cube");
    ///   go.Transform.Position = new Vector3(1, 0, 0);
    ///   var mr = go.AddComponent&lt;MeshRenderer&gt;();
    ///   mr.Mesh     = Mesh.CreateCube();
    ///   mr.Material = myMaterial;
    /// </summary>
    public class GameObject
    {
        // ------------------------------------------------------------------
        // Identity
        // ------------------------------------------------------------------
        public string Name { get; set; }
        public string Tag { get; set; } = "Untagged";
        public int Layer { get; set; } = 0;

        private bool _active = true;
        public bool ActiveSelf => _active;
        public bool ActiveInHierarchy => _active && (Transform.Parent?.GameObject.ActiveInHierarchy ?? true);

        // ------------------------------------------------------------------
        // Transform (always present — cannot be removed)
        // ------------------------------------------------------------------
        public Transform Transform { get; } = new Transform();

        // ------------------------------------------------------------------
        // Scene ownership
        // ------------------------------------------------------------------
        public Scene Scene { get; internal set; }

        // ------------------------------------------------------------------
        // Component list
        // ------------------------------------------------------------------
        private readonly List<Component> _components = new();

        // ------------------------------------------------------------------
        // Lifecycle flags
        // ------------------------------------------------------------------
        private bool _started = false;

        // ------------------------------------------------------------------
        // Constructor
        // ------------------------------------------------------------------
        public GameObject(string name = "GameObject")
        {
            Name = name;
            Transform.GameObject = this;
        }

        // ------------------------------------------------------------------
        // Active / Inactive
        // ------------------------------------------------------------------
        public void SetActive(bool value)
        {
            if (_active == value) return;
            _active = value;
            foreach (var c in _components)
            {
                if (!c.Enabled) continue;
                if (_active) c.OnEnable();
                else c.OnDisable();
            }
            // Propagate to children
            foreach (var child in Transform.Children)
                child.GameObject.SetActive(value);
        }

        // ------------------------------------------------------------------
        // AddComponent / GetComponent
        // ------------------------------------------------------------------
        public T AddComponent<T>() where T : Component, new()
        {
            var c = new T();
            c.GameObject = this;
            _components.Add(c);
            c.Awake();
            if (_started) { c.Start(); }
            return c;
        }

        public T GetComponent<T>() where T : Component
            => _components.OfType<T>().FirstOrDefault();

        public IEnumerable<T> GetComponents<T>() where T : Component
            => _components.OfType<T>();

        public T GetOrAddComponent<T>() where T : Component, new()
            => GetComponent<T>() ?? AddComponent<T>();

        public bool TryGetComponent<T>(out T component) where T : Component
        {
            component = GetComponent<T>();
            return component != null;
        }

        public void RemoveComponent<T>() where T : Component
        {
            var c = GetComponent<T>();
            if (c == null) return;
            c.OnDestroy();
            _components.Remove(c);
        }

        // ------------------------------------------------------------------
        // Lifecycle  (called by Scene)
        // ------------------------------------------------------------------
        internal void InternalStart()
        {
            if (_started) return;
            _started = true;
            foreach (var c in _components.ToArray())
                if (c.Enabled) c.Start();
        }

        internal void InternalUpdate(float dt)
        {
            if (!ActiveInHierarchy) return;
            foreach (var c in _components.ToArray())
                if (c.Enabled) c.Update(dt);
        }

        internal void InternalFixedUpdate(float fdt)
        {
            if (!ActiveInHierarchy) return;
            foreach (var c in _components.ToArray())
                if (c.Enabled) c.FixedUpdate(fdt);
        }

        internal void InternalRender(RenderContext ctx)
        {
            if (!ActiveInHierarchy) return;
            foreach (var c in _components.ToArray())
                if (c.Enabled) c.OnRender(ctx);
        }

        internal void InternalDestroy()
        {
            foreach (var c in _components.ToArray())
                c.OnDestroy();
            _components.Clear();

            // Detach children
            foreach (var child in Transform.Children.ToArray())
                child.GameObject.InternalDestroy();
        }

        // ------------------------------------------------------------------
        // Shorthand hierarchy
        // ------------------------------------------------------------------
        public void SetParent(GameObject parent)
            => Transform.SetParent(parent?.Transform);

        public IEnumerable<GameObject> GetChildren()
            => Transform.Children.Select(t => t.GameObject);

        public override string ToString() => $"GameObject({Name})";
    }

    // =======================================================================
    // Transform
    // =======================================================================
    /// <summary>
    /// Position, rotation, scale in 3-D space.
    /// Supports parent–child hierarchy with local/world coordinate conversion.
    /// </summary>
    public class Transform
    {
        // ------------------------------------------------------------------
        // Owner
        // ------------------------------------------------------------------
        public GameObject GameObject { get; internal set; }

        // ------------------------------------------------------------------
        // Local space
        // ------------------------------------------------------------------
        public Vector3 LocalPosition { get; set; } = Vector3.Zero;
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        public Vector3 LocalScale { get; set; } = Vector3.One;

        // ------------------------------------------------------------------
        // World space (derived from hierarchy)
        // ------------------------------------------------------------------
        public Vector3 Position
        {
            get => Parent == null ? LocalPosition
                 : Vector3.TransformPosition(LocalPosition, Parent.WorldMatrix);
            set => LocalPosition = Parent == null ? value
                 : Vector3.TransformPosition(value, Parent.WorldMatrix.Inverted());
        }

        public Quaternion Rotation
        {
            get => Parent == null ? LocalRotation : Parent.Rotation * LocalRotation;
            set => LocalRotation = Parent == null ? value : Quaternion.Invert(Parent.Rotation) * value;
        }

        public Vector3 LossyScale =>
            Parent == null ? LocalScale : Parent.LossyScale * LocalScale;

        // ------------------------------------------------------------------
        // Direction vectors
        // ------------------------------------------------------------------
        public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);
        public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
        public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

        // ------------------------------------------------------------------
        // Euler angles convenience (degrees)
        // ------------------------------------------------------------------
        public Vector3 EulerAngles
        {
            get
            {
                var r = LocalRotation.ToEulerAngles();
                return new Vector3(
                    MathHelper.RadiansToDegrees(r.X),
                    MathHelper.RadiansToDegrees(r.Y),
                    MathHelper.RadiansToDegrees(r.Z));
            }
            set => LocalRotation = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(value.X),
                MathHelper.DegreesToRadians(value.Y),
                MathHelper.DegreesToRadians(value.Z));
        }

        // ------------------------------------------------------------------
        // Matrix
        // ------------------------------------------------------------------
        public Matrix4 LocalMatrix =>
            Matrix4.CreateScale(LocalScale)
            * Matrix4.CreateFromQuaternion(LocalRotation)
            * Matrix4.CreateTranslation(LocalPosition);

        public Matrix4 WorldMatrix =>
            Parent == null ? LocalMatrix : LocalMatrix * Parent.WorldMatrix;

        // ------------------------------------------------------------------
        // Hierarchy
        // ------------------------------------------------------------------
        public Transform Parent { get; private set; }
        private readonly List<Transform> _children = new();
        public IReadOnlyList<Transform> Children => _children;

        public void SetParent(Transform newParent, bool keepWorldPosition = true)
        {
            if (Parent == newParent) return;

            Vector3 worldPos = keepWorldPosition ? Position : LocalPosition;
            Quaternion worldRot = keepWorldPosition ? Rotation : LocalRotation;

            Parent?._children.Remove(this);
            Parent = newParent;
            newParent?._children.Add(this);

            if (keepWorldPosition)
            {
                Position = worldPos;
                Rotation = worldRot;
            }
        }

        // ------------------------------------------------------------------
        // Convenience
        // ------------------------------------------------------------------
        public void Translate(Vector3 delta) => LocalPosition += delta;
        public void Rotate(Vector3 eulerDelta)
        {
            EulerAngles = EulerAngles + eulerDelta;
        }
        public void LookAt(Vector3 target, Vector3 up = default)
        {
            if (up == default) up = Vector3.UnitY;
            Vector3 dir = Vector3.Normalize(target - Position);
            if (dir == Vector3.Zero) return;
            LocalRotation = QuaternionHelper.LookRotation(dir, up);
        }
    }

    // =======================================================================
    // QuaternionHelper
    // =======================================================================
    /// <summary>
    /// Extra Quaternion utilities that OpenTK does not provide natively.
    /// </summary>
    public static class QuaternionHelper
    {
        /// <summary>
        /// Creates a rotation that points <paramref name="forward"/> along -Z
        /// with the given <paramref name="up"/> direction, matching Unity's
        /// Quaternion.LookRotation behaviour.
        /// </summary>
        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            forward = Vector3.Normalize(forward);
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
            // Recompute up to make the basis orthonormal
            Vector3 orthoUp = Vector3.Cross(forward, right);

            // Build rotation matrix from the orthonormal basis
            // Column-major: right = col0, orthoUp = col1, forward = col2
            float m00 = right.X, m01 = orthoUp.X, m02 = forward.X;
            float m10 = right.Y, m11 = orthoUp.Y, m12 = forward.Y;
            float m20 = right.Z, m21 = orthoUp.Z, m22 = forward.Z;

            // Convert rotation matrix to quaternion (Shepperd method)
            float trace = m00 + m11 + m22;
            float qw, qx, qy, qz;

            if (trace > 0f)
            {
                float s = 0.5f / MathF.Sqrt(trace + 1f);
                qw = 0.25f / s;
                qx = (m21 - m12) * s;
                qy = (m02 - m20) * s;
                qz = (m10 - m01) * s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = 2f * MathF.Sqrt(1f + m00 - m11 - m22);
                qw = (m21 - m12) / s;
                qx = 0.25f * s;
                qy = (m01 + m10) / s;
                qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = 2f * MathF.Sqrt(1f + m11 - m00 - m22);
                qw = (m02 - m20) / s;
                qx = (m01 + m10) / s;
                qy = 0.25f * s;
                qz = (m12 + m21) / s;
            }
            else
            {
                float s = 2f * MathF.Sqrt(1f + m22 - m00 - m11);
                qw = (m10 - m01) / s;
                qx = (m02 + m20) / s;
                qy = (m12 + m21) / s;
                qz = 0.25f * s;
            }

            return Quaternion.Normalize(new Quaternion(qx, qy, qz, qw));
        }

        /// <summary>
        /// Rotates <paramref name="from"/> towards <paramref name="to"/> by at most
        /// <paramref name="maxDegrees"/> degrees — equivalent to Unity's
        /// Quaternion.RotateTowards.
        /// </summary>
        public static Quaternion RotateTowards(Quaternion from, Quaternion to,
                                               float maxDegrees)
        {
            float angle = Dot(from, to);
            if (MathF.Abs(angle) >= 1f) return to;
            float maxRad = MathHelper.DegreesToRadians(maxDegrees);
            float totalAngle = MathF.Acos(MathF.Min(MathF.Abs(angle), 1f)) * 2f;
            if (totalAngle <= maxRad) return to;
            float t = maxRad / totalAngle;
            return Quaternion.Slerp(from, to, t);

        }

        /// <summary>
        /// Returns the dot product of two quaternions.
        /// OpenTK.Quaternion does not provide a static Dot method in this project,
        /// so provide a small helper here.
        /// </summary>
        public static float Dot(Quaternion a, Quaternion b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;   
        }

        /// <summary>
        /// Returns the dot product of two quaternions.
        /// OpenTK.Quaternion does not provide a static Dot method in this project,
        /// so provide a small helper here.
        /// </summary> 
        /// <summary>
        /// Returns a Quaternion with the same direction but guaranteed unit length.
        /// </summary>
        public static Quaternion SafeNormalize(Quaternion q)
        {
            float len = q.Length;
            return len < 1e-6f ? Quaternion.Identity : Quaternion.Multiply(q, 1f / len);
        }
    }

}