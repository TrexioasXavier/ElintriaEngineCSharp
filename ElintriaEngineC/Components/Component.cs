using ElintriaEngineC.Components;
using OpenTK.Mathematics;

namespace Elintria.Engine
{
    /// <summary>
    /// Base class for all components attached to a GameObject.
    /// Mirrors Unity's Component API.
    /// </summary>
    public abstract class Component
    {
        // ------------------------------------------------------------------
        // Owner
        // ------------------------------------------------------------------
        /// <summary>The GameObject this component is attached to.</summary>
        public GameObject GameObject { get; internal set; }

        /// <summary>Shortcut to the owner's Transform.</summary>
        public Transform Transform => GameObject?.Transform;

        public bool Enabled { get; set; } = true;

        // ------------------------------------------------------------------
        // Lifecycle  (called by GameObject / Scene)
        // ------------------------------------------------------------------

        /// <summary>Called once after the component is attached to a GameObject
        /// and the scene has been loaded.</summary>
        public virtual void Awake() { }

        /// <summary>Called once on the first frame the component is enabled,
        /// after all Awake() calls have completed.</summary>
        public virtual void Start() { }

        /// <summary>Called every frame.</summary>
        public virtual void Update(float deltaTime) { }

        /// <summary>Called every fixed-timestep frame (physics).</summary>
        public virtual void FixedUpdate(float fixedDeltaTime) { }

        /// <summary>Called every render frame — upload uniforms, bind material, draw.</summary>
        public virtual void OnRender(RenderContext ctx) { }

        /// <summary>Called when the component or its GameObject is destroyed.</summary>
        public virtual void OnDestroy() { }

        /// <summary>Called when the owning GameObject becomes active.</summary>
        public virtual void OnEnable() { }

        /// <summary>Called when the owning GameObject becomes inactive.</summary>
        public virtual void OnDisable() { }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        /// <summary>Get another component on the same GameObject.</summary>
        public T GetComponent<T>() where T : Component
            => GameObject?.GetComponent<T>();

        /// <summary>Get a component, or add it if missing.</summary>
        public T GetOrAddComponent<T>() where T : Component, new()
            => GameObject?.GetOrAddComponent<T>();
    }

    // =======================================================================
    // RenderContext  — passed to OnRender each frame
    // =======================================================================
    /// <summary>
    /// Snapshot of per-frame rendering state passed to every component's OnRender.
    /// </summary>
    public class RenderContext
    {
        public Matrix4 View { get; set; }
        public Matrix4 Projection { get; set; }
        public Vector3 CameraPos { get; set; }
        public float DeltaTime { get; set; }

        /// <summary>Convenience: View * Projection.</summary>
        public Matrix4 ViewProjection => View * Projection;
    }
}