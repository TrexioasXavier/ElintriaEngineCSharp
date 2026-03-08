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
            foreach (var r in _roots)
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
    //  Component (abstract base)
    // ═══════════════════════════════════════════════════════════════════════════
    public abstract class Component
    {
        public GameObject? GameObject { get; set; }
        public bool Enabled { get; set; } = true;

        public virtual void OnStart() { }
        public virtual void OnUpdate(double dt) { }
        public virtual void OnDestroy() { }
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
        public string MaterialPath { get; set; } = "";
        public bool CastShadows { get; set; } = true;
        public bool ReceiveShadows { get; set; } = true;
    }

    public class Camera : Component
    {
        public float FieldOfView { get; set; } = 60f;
        public float NearClip { get; set; } = 0.1f;
        public float FarClip { get; set; } = 1000f;
        public bool IsOrthographic { get; set; } = false;
        public float OrthoSize { get; set; } = 5f;
    }

    public class Light : Component
    {
        public string LightType { get; set; } = "Directional";
        public Color4 Color { get; set; } = Color4.White;
        public float Intensity { get; set; } = 1f;
        public float Range { get; set; } = 10f;
        public float SpotAngle { get; set; } = 30f;
    }

    public class Rigidbody : Component
    {
        public float Mass { get; set; } = 1f;
        public bool UseGravity { get; set; } = true;
        public bool IsKinematic { get; set; } = false;
        public float Drag { get; set; } = 0f;
        public float AngularDrag { get; set; } = 0.05f;
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
    //  DynamicScript  –  placeholder for user scripts not yet compiled
    //  Shown in Inspector as a script slot; replaced at build/runtime with real type
    // ═══════════════════════════════════════════════════════════════════════════
    public class DynamicScript : Component
    {
        /// <summary>Fully-qualified or simple class name of the user script.</summary>
        public string ScriptTypeName { get; set; } = "";

        // Public fields exposed in inspector at build time via reflection on the
        // actual compiled type. At editor time we just show the type name.
        public override string ToString() => $"Script: {ScriptTypeName}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Component Registry
    // ═══════════════════════════════════════════════════════════════════════════
    public static class ComponentRegistry
    {
        private static readonly Dictionary<string, Type> _map = new()
        {
            { "MeshFilter",      typeof(MeshFilter)      },
            { "MeshRenderer",    typeof(MeshRenderer)    },
            { "Camera",          typeof(Camera)          },
            { "Light",           typeof(Light)           },
            { "Rigidbody",       typeof(Rigidbody)       },
            { "BoxCollider",     typeof(BoxCollider)     },
            { "SphereCollider",  typeof(SphereCollider)  },
            { "AudioSource",     typeof(AudioSource)     },
            { "AudioListener",   typeof(AudioListener)   },
            { "Canvas",          typeof(CanvasComponent) },
            { "CanvasRenderer",  typeof(CanvasRenderer)  },
            { "Image",           typeof(ImageComponent)  },
            { "Button",          typeof(ButtonComponent) },
            { "Text",            typeof(TextComponent)   },
            { "Slider",          typeof(SliderComponent) },
        };

        public static void Register(string name, Type type) => _map[name] = type;

        public static Component? Create(string name)
        {
            if (_map.TryGetValue(name, out var t))
                return (Component?)Activator.CreateInstance(t);

            // Search loaded assemblies (user scripts)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = asm.GetType(name) ?? asm.GetType("GameScripts." + name);
                if (found != null && typeof(Component).IsAssignableFrom(found))
                    return (Component?)Activator.CreateInstance(found);
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

        public GameObject(string name) => Name = name;

        // ── Component API ──────────────────────────────────────────────────────
        public T AddComponent<T>() where T : Component, new()
        {
            var c = new T { GameObject = this };
            Components.Add(c);
            return c;
        }

        public Component? AddComponentByName(string name)
        {
            var c = ComponentRegistry.Create(name);
            if (c == null) return null;
            c.GameObject = this;
            Components.Add(c);
            return c;
        }

        public T? GetComponent<T>() where T : Component =>
            Components.OfType<T>().FirstOrDefault();

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
            if (Components.Remove(c)) c.OnDestroy();
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
            foreach (var child in Children)
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
            foreach (var c in Components) c.OnDestroy();
            foreach (var child in Children) child.Destroy();
        }
    }
}