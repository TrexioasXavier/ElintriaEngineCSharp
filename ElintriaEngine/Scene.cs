using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Scene
    // ═══════════════════════════════════════════════════════════════════════════
    public class Scene
    {
        public string Name { get; set; } = "Untitled";
        public string FilePath { get; set; } = "";

        private readonly List<GameObject> _roots = new();
        private static int _idCounter = 1;

        public IReadOnlyList<GameObject> RootObjects => _roots;
        public static int NextId() => _idCounter++;

        public void AddGameObject(GameObject go)
        {
            if (go.Parent == null && !_roots.Contains(go))
                _roots.Add(go);
        }

        public void RemoveGameObject(GameObject go)
        {
            _roots.Remove(go);
            go.Parent?.Children.Remove(go);
            go.Destroy();
        }

        public GameObject? Find(string name) => FindIn(_roots, name);

        private static GameObject? FindIn(IEnumerable<GameObject> list, string name)
        {
            foreach (var go in list)
            {
                if (go.Name == name) return go;
                var r = FindIn(go.Children, name);
                if (r != null) return r;
            }
            return null;
        }

        public IEnumerable<GameObject> All()
        {
            foreach (var r in _roots.ToArray())
                foreach (var go in r.SelfAndDescendants())
                    yield return go;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Transform
    // ═══════════════════════════════════════════════════════════════════════════
    public class Transform
    {
        public Vector3 LocalPosition { get; set; } = Vector3.Zero;
        public Vector3 LocalEulerAngles { get; set; } = Vector3.Zero;
        public Vector3 LocalScale { get; set; } = Vector3.One;

        public Matrix4 LocalMatrix =>
            Matrix4.CreateScale(LocalScale) *
            Matrix4.CreateFromQuaternion(
                Quaternion.FromEulerAngles(
                    MathHelper.DegreesToRadians(LocalEulerAngles.X),
                    MathHelper.DegreesToRadians(LocalEulerAngles.Y),
                    MathHelper.DegreesToRadians(LocalEulerAngles.Z))) *
            Matrix4.CreateTranslation(LocalPosition);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Component — full Unity-like lifecycle
    //
    //  Override order (Unity-identical):
    //    Awake()          called once when the component is first created/enabled
    //    OnEnable()       called when the component or its GO becomes active
    //    OnStart()        called before the first frame update (after ALL Awakes)
    //    OnFixedUpdate()  called at a fixed physics rate (default 50 Hz)
    //    OnUpdate()       called once per frame
    //    OnLateUpdate()   called after all OnUpdates each frame
    //    OnDisable()      called when the component or GO is disabled/destroyed
    //    OnDestroy()      called when the component is permanently removed
    // ═══════════════════════════════════════════════════════════════════════════
    public abstract class Component
    {
        public GameObject? GameObject { get; internal set; }

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value) OnEnable();
                else OnDisable();
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────
        public virtual void Awake() { }
        public virtual void OnEnable() { }
        public virtual void OnStart() { }
        public virtual void OnFixedUpdate(double dt) { }
        public virtual void OnUpdate(double dt) { }
        public virtual void OnLateUpdate(double dt) { }
        public virtual void OnDisable() { }
        public virtual void OnDestroy() { }

        // ── Convenience accessors (mirrors Unity's Component shortcuts) ───────
        public Transform? Transform => GameObject?.Transform;

        /// <summary>Find another component on the same GameObject.</summary>
        public T? GetComponent<T>() where T : Component =>
            GameObject?.GetComponent<T>();

        /// <summary>Destroy this component (equivalent to Unity's Destroy(this)).</summary>
        public void DestroySelf() => GameObject?.RemoveComponent(this);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Built-in Components
    // ═══════════════════════════════════════════════════════════════════════════
    public class MeshFilter : Component
    {
        public string MeshPath { get; set; } = "";
        public string MeshName { get; set; } = "Cube";
    }

    public class MeshRenderer : Component
    {
        public string MaterialPath = "";
        public bool CastShadows = true;
        public bool ReceiveShadows = true;
        // Per-object albedo colour (RGB, 0-1)
        public float AlbedoR = 0.8f;
        public float AlbedoG = 0.82f;
        public float AlbedoB = 0.85f;
        public float Metallic = 0f;
        public float Roughness = 0.6f;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Camera  —  used by the game runtime and play-mode Game View.
    //  Attach to any GameObject, position/rotate the GO to move the camera.
    //  The first enabled Camera in the scene is the active camera.
    // ═══════════════════════════════════════════════════════════════════════════
    public class Camera : Component
    {
        /// Vertical field of view in degrees (perspective mode only).
        public float FieldOfView = 60f;
        /// Near clip plane distance.
        public float NearClip = 0.1f;
        /// Far clip plane distance.
        public float FarClip = 1000f;
        /// Switches the camera to orthographic projection.
        public bool IsOrthographic = false;
        /// Half-height of the orthographic frustum in world units.
        public float OrthoSize = 5f;
        /// Solid background colour shown when no skybox is used.
        public float BackgroundR = 0.1f;
        public float BackgroundG = 0.1f;
        public float BackgroundB = 0.1f;

        // ── Runtime helpers ───────────────────────────────────────────────────
        /// World-space forward vector derived from the owner GameObject's rotation.
        public Vector3 Forward
        {
            get
            {
                var e = GameObject?.Transform.LocalEulerAngles ?? Vector3.Zero;
                float yr = MathHelper.DegreesToRadians(e.Y);
                float xr = MathHelper.DegreesToRadians(e.X);
                return new Vector3(
                     MathF.Sin(yr) * MathF.Cos(xr),
                    -MathF.Sin(xr),
                    -MathF.Cos(yr) * MathF.Cos(xr));
            }
        }

        public Vector3 Position => GameObject?.Transform.LocalPosition ?? Vector3.Zero;

        public Matrix4 GetViewMatrix()
        {
            var pos = Position;
            return Matrix4.LookAt(pos, pos + Forward, Vector3.UnitY);
        }

        public Matrix4 GetProjectionMatrix(float aspect)
        {
            if (IsOrthographic)
                return Matrix4.CreateOrthographic(
                    OrthoSize * aspect * 2f, OrthoSize * 2f, NearClip, FarClip);
            return Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(FieldOfView), aspect, NearClip, FarClip);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DirectionalLight  —  infinite parallel light (like the sun).
    //  The direction comes from the GameObject's rotation (local -Z forward).
    // ═══════════════════════════════════════════════════════════════════════════
    public class DirectionalLight : Component
    {
        public float ColorR = 1f;
        public float ColorG = 0.95f;
        public float ColorB = 0.9f;
        public float Intensity = 1.2f;

        public Vector3 Direction
        {
            get
            {
                var e = GameObject?.Transform.LocalEulerAngles ?? new Vector3(-45f, 45f, 0f);
                float yr = MathHelper.DegreesToRadians(e.Y);
                float xr = MathHelper.DegreesToRadians(e.X);
                return new Vector3(
                     MathF.Sin(yr) * MathF.Cos(xr),
                    -MathF.Sin(xr),
                    -MathF.Cos(yr) * MathF.Cos(xr));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SpotLight  —  cone light with position, direction, range, and angle.
    //  Direction comes from the GameObject's rotation like DirectionalLight.
    // ═══════════════════════════════════════════════════════════════════════════
    public class SpotLight : Component
    {
        public float ColorR = 1f;
        public float ColorG = 1f;
        public float ColorB = 1f;
        public float Intensity = 1f;
        /// Radius in world units beyond which the light has zero effect.
        public float Range = 15f;
        /// Half-angle of the spotlight cone in degrees.
        public float SpotAngle = 30f;
        /// Softness of the cone edge (0 = hard, 1 = fully soft).
        public float BlendFraction = 0.15f;

        public Vector3 Position => GameObject?.Transform.LocalPosition ?? Vector3.Zero;
        public Vector3 Direction
        {
            get
            {
                var e = GameObject?.Transform.LocalEulerAngles ?? Vector3.Zero;
                float yr = MathHelper.DegreesToRadians(e.Y);
                float xr = MathHelper.DegreesToRadians(e.X);
                return new Vector3(
                     MathF.Sin(yr) * MathF.Cos(xr),
                    -MathF.Sin(xr),
                    -MathF.Cos(yr) * MathF.Cos(xr));
            }
        }
    }

    // Legacy Light stub kept for scene serialisation compatibility.
    public class Light : Component
    {
        public string LightType = "Directional";
        public float ColorR = 1f;
        public float ColorG = 1f;
        public float ColorB = 1f;
        public float Intensity = 1f;
        public float Range = 10f;
        public float SpotAngle = 30f;
    }

    // ── Physics ────────────────────────────────────────────────────────────────
    public class Rigidbody : Component
    {
        public float Mass { get; set; } = 1f;
        public bool UseGravity { get; set; } = true;
        public bool IsKinematic { get; set; } = false;
        public float Drag { get; set; } = 0f;
        public float AngularDrag { get; set; } = 0.05f;
    }

    public class Rigidbody3D : Component
    {
        public float Mass { get; set; } = 1f;
        public bool UseGravity { get; set; } = true;
        public bool IsKinematic { get; set; } = false;
        public float Drag { get; set; } = 0f;
        public float AngularDrag { get; set; } = 0.05f;
        public bool FreezePositionX { get; set; } = false;
        public bool FreezePositionY { get; set; } = false;
        public bool FreezePositionZ { get; set; } = false;
        public bool FreezeRotationX { get; set; } = false;
        public bool FreezeRotationY { get; set; } = false;
        public bool FreezeRotationZ { get; set; } = false;
        public string CollisionDetection { get; set; } = "Discrete"; // Discrete, Continuous
    }

    public class BoxCollider : Component
    {
        public Vector3 Center { get; set; } = Vector3.Zero;
        public Vector3 Size { get; set; } = Vector3.One;
        public bool IsTrigger { get; set; } = false;
    }

    public class SphereCollider : Component
    {
        public Vector3 Center { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 0.5f;
        public bool IsTrigger { get; set; } = false;
    }

    public class CapsuleCollider : Component
    {
        public Vector3 Center { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 0.5f;
        public float Height { get; set; } = 2f;
        public int Direction { get; set; } = 1;   // 0=X, 1=Y, 2=Z
        public bool IsTrigger { get; set; } = false;
    }

    public class MeshCollider : Component
    {
        public bool IsTrigger { get; set; } = false;
        public bool Convex { get; set; } = false;
    }

    public class BoxCollider2D : Component
    {
        public Vector3 Offset { get; set; } = Vector3.Zero;
        public float Width { get; set; } = 1f;
        public float Height { get; set; } = 1f;
        public bool IsTrigger { get; set; } = false;
    }

    public class CircleCollider2D : Component
    {
        public Vector3 Offset { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 0.5f;
        public bool IsTrigger { get; set; } = false;
    }

    public class AudioSource : Component
    {
        public string AudioClipPath { get; set; } = "";
        public float Volume { get; set; } = 1f;
        public float Pitch { get; set; } = 1f;
        public bool Loop { get; set; } = false;
        public bool PlayOnAwake { get; set; } = true;
    }

    public class AudioListener : Component { }

    // ── UI Components ──────────────────────────────────────────────────────────
    public class CanvasComponent : Component
    {
        public string RenderMode { get; set; } = "ScreenSpaceOverlay";
    }
    public class CanvasRenderer : Component { }
    public class ImageComponent : Component
    {
        public string SpritePath { get; set; } = "";
        public Color4 Color { get; set; } = Color4.White;
        public bool Raycast { get; set; } = true;
    }
    public class ButtonComponent : Component
    {
        public string NormalText { get; set; } = "Button";
        public Color4 NormalColor { get; set; } = new Color4(1, 1, 1, 1);
        public Color4 HighlightColor { get; set; } = new Color4(0.9f, 0.9f, 0.9f, 1);
        public Color4 PressedColor { get; set; } = new Color4(0.7f, 0.7f, 0.7f, 1);
        public bool Interactable { get; set; } = true;
    }
    public class TextComponent : Component
    {
        public string Text { get; set; } = "New Text";
        public float FontSize { get; set; } = 14f;
        public Color4 Color { get; set; } = Color4.White;
        public string Alignment { get; set; } = "Left";
    }
    public class SliderComponent : Component
    {
        public float MinValue { get; set; } = 0f;
        public float MaxValue { get; set; } = 1f;
        public float Value { get; set; } = 0.5f;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DynamicScript — placeholder for a user script not yet compiled into the
    //  editor process. After compilation the editor resolves it to the real type.
    // ═══════════════════════════════════════════════════════════════════════════
    public class DynamicScript : Component
    {
        public string ScriptTypeName { get; set; } = "";

        // Stores public field values edited in the Inspector before/between
        // compilations so they are not lost when we swap to the real type.
        public Dictionary<string, object?> FieldValues { get; } = new();

        public override string ToString() => $"Script: {ScriptTypeName}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ComponentRegistry
    // ═══════════════════════════════════════════════════════════════════════════
    public static class ComponentRegistry
    {
        private static readonly Dictionary<string, Type> _map = new()
        {
            { "MeshFilter",      typeof(MeshFilter)      },
            { "MeshRenderer",    typeof(MeshRenderer)    },
            { "Camera",          typeof(Camera)          },
            { "Light",           typeof(Light)           },
            { "Rigidbody",        typeof(Rigidbody)        },
            { "Rigidbody3D",      typeof(Rigidbody3D)      },
            { "BoxCollider",      typeof(BoxCollider)      },
            { "SphereCollider",   typeof(SphereCollider)   },
            { "CapsuleCollider",  typeof(CapsuleCollider)  },
            { "MeshCollider",     typeof(MeshCollider)     },
            { "BoxCollider2D",    typeof(BoxCollider2D)    },
            { "CircleCollider2D", typeof(CircleCollider2D) },
            { "AudioSource",     typeof(AudioSource)     },
            { "AudioListener",   typeof(AudioListener)   },
            { "Canvas",          typeof(CanvasComponent) },
            { "CanvasRenderer",  typeof(CanvasRenderer)  },
            { "Image",           typeof(ImageComponent)  },
            { "Button",          typeof(ButtonComponent) },
            { "Text",            typeof(TextComponent)   },
            { "Slider",          typeof(SliderComponent) },
            // DynamicScript must be explicitly registered so scene
            // serialization can round-trip script placeholders correctly.
            { "DynamicScript",      typeof(DynamicScript)      },
            { "DirectionalLight",   typeof(DirectionalLight)   },
            { "SpotLight",          typeof(SpotLight)          },
            { "ParticleSystem",     typeof(ParticleSystem)     },
        };

        /// <summary>
        /// The most recently loaded user-script assembly (set by SceneRunner.LoadUserScripts).
        /// UIEditorPanel uses this to enumerate public void methods without scanning all
        /// AppDomain assemblies (which would include stale session DLLs from previous compiles).
        /// </summary>
        public static System.Reflection.Assembly? UserAssembly { get; internal set; }

        public static void Register(string name, Type type) => _map[name] = type;

        /// <summary>Returns the Type for a registered component name, or null if not found.</summary>
        public static Type? TryGetType(string name)
        {
            if (_map.TryGetValue(name, out var t)) return t;
            // Also search loaded assemblies by simple name
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var type in asm.GetTypes())
                    if ((type.Name == name || type.FullName == name)
                        && typeof(Component).IsAssignableFrom(type) && !type.IsAbstract)
                        return type;
            return null;
        }

        public static Component? Create(string name)
        {
            if (_map.TryGetValue(name, out var t))
                return (Component?)Activator.CreateInstance(t);

            // Fallback: search all loaded assemblies by simple name AND full name.
            // GetType(string) requires fully-qualified names, so we iterate instead.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in asm.GetTypes())
                {
                    if ((type.Name == name || type.FullName == name)
                        && typeof(Component).IsAssignableFrom(type)
                        && !type.IsAbstract)
                    {
                        return (Component?)Activator.CreateInstance(type);
                    }
                }
            }
            return null;
        }

        public static bool Exists(string name) => _map.ContainsKey(name);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  GameObject
    // ═══════════════════════════════════════════════════════════════════════════
    public class GameObject
    {
        public int InstanceId { get; } = Scene.NextId();
        public string Name { get; set; }
        public bool ActiveSelf { get; set; } = true;
        public string Tag { get; set; } = "Untagged";
        public string Layer { get; set; } = "Default";
        public Transform Transform { get; } = new();

        public GameObject? Parent { get; private set; }
        public List<GameObject> Children { get; } = new();
        public List<Component> Components { get; } = new();

        // The SceneRunner subscribes to this to bootstrap newly added components
        public event Action<Component>? ComponentAdded;

        public GameObject(string name) => Name = name;

        // ── Component API — mirrors Unity's GetComponent / AddComponent ────────
        public T AddComponent<T>() where T : Component, new()
        {
            var c = new T { GameObject = this };
            Components.Add(c);
            ComponentAdded?.Invoke(c);
            return c;
        }

        public Component? AddComponentByName(string name)
        {
            var c = ComponentRegistry.Create(name);
            if (c == null) return null;
            c.GameObject = this;
            Components.Add(c);
            ComponentAdded?.Invoke(c);
            return c;
        }

        public T? GetComponent<T>() where T : Component =>
            Components.OfType<T>().FirstOrDefault();

        /// <summary>Non-generic variant — used for inspector component drag-drop routing.</summary>
        public Component? GetComponentByType(Type t) =>
            Components.FirstOrDefault(c => t.IsAssignableFrom(c.GetType()));

        public bool HasComponent<T>() where T : Component =>
            Components.OfType<T>().Any();

        public bool HasComponent(string name)
        {
            foreach (var c in Components)
                if (c.GetType().Name == name) return true;
            return false;
        }

        public void RemoveComponent(Component c)
        {
            if (Components.Remove(c))
            {
                c.OnDisable();
                c.OnDestroy();
            }
        }

        // ── Hierarchy ──────────────────────────────────────────────────────────
        public void SetParent(GameObject? newParent)
        {
            Parent?.Children.Remove(this);
            Parent = newParent;
            if (newParent != null && !newParent.Children.Contains(this))
                newParent.Children.Add(this);
        }

        public bool IsDescendantOf(GameObject ancestor)
        {
            var p = Parent;
            while (p != null) { if (p == ancestor) return true; p = p.Parent; }
            return false;
        }

        public IEnumerable<GameObject> SelfAndDescendants()
        {
            yield return this;
            foreach (var child in Children.ToArray())
                foreach (var d in child.SelfAndDescendants())
                    yield return d;
        }

        // ── Duplication ────────────────────────────────────────────────────────
        public GameObject Duplicate()
        {
            var dup = new GameObject(Name + " (Copy)")
            {
                ActiveSelf = ActiveSelf,
                Tag = Tag,
                Layer = Layer
            };
            dup.Transform.LocalPosition = Transform.LocalPosition;
            dup.Transform.LocalEulerAngles = Transform.LocalEulerAngles;
            dup.Transform.LocalScale = Transform.LocalScale;

            foreach (var c in Components)
            {
                var dc = ComponentRegistry.Create(c.GetType().Name);
                if (dc != null) { dc.GameObject = dup; dup.Components.Add(dc); }
            }
            foreach (var child in Children)
            {
                var cd = child.Duplicate();
                cd.SetParent(dup);
            }
            return dup;
        }

        public void Destroy()
        {
            foreach (var c in Components) { try { c.OnDisable(); } catch { } try { c.OnDestroy(); } catch { } }
            foreach (var child in Children.ToArray()) child.Destroy();
        }
    }
}