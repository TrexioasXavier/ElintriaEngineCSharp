using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    // ── Data transfer objects ─────────────────────────────────────────────────
    public class SerializedComponent
    {
        public string TypeName { get; set; } = "";
        public bool Enabled { get; set; } = true;
        // key → JSON value (primitive, array, or object)
        public Dictionary<string, JsonNode?> Properties { get; set; } = new();
    }

    public class SerializedGameObject
    {
        public int InstanceId { get; set; }
        public string Name { get; set; } = "";
        public bool ActiveSelf { get; set; } = true;
        public string Tag { get; set; } = "Untagged";
        public string Layer { get; set; } = "Default";
        public float[] Position { get; set; } = { 0, 0, 0 };
        public float[] Rotation { get; set; } = { 0, 0, 0 };
        public float[] Scale { get; set; } = { 1, 1, 1 };
        public List<SerializedComponent> Components { get; set; } = new();
        public List<SerializedGameObject> Children { get; set; } = new();
    }

    public class SerializedScene
    {
        public string Name { get; set; } = "Untitled";
        public int Version { get; set; } = 1;
        public List<SerializedGameObject> GameObjects { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SceneSerializer
    // ═══════════════════════════════════════════════════════════════════════════
    public static class SceneSerializer
    {
        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
        };

        // ── Save ──────────────────────────────────────────────────────────────
        public static void Save(Scene scene, string filePath)
        {
            var data = new SerializedScene
            {
                Name = scene.Name,
                Version = 1,
            };
            foreach (var root in scene.RootObjects)
                data.GameObjects.Add(SerializeGO(root));

            string dir = Path.GetDirectoryName(filePath)!;
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath,
                JsonSerializer.Serialize(data, _opts));
        }

        public static string ToJson(Scene scene)
        {
            var data = new SerializedScene { Name = scene.Name };
            foreach (var root in scene.RootObjects)
                data.GameObjects.Add(SerializeGO(root));
            return JsonSerializer.Serialize(data, _opts);
        }

        // ── Load ──────────────────────────────────────────────────────────────
        public static Scene Load(string filePath)
        {
            string json = File.ReadAllText(filePath);
            return FromJson(json, filePath);
        }

        public static Scene FromJson(string json, string filePath = "")
        {
            var data = JsonSerializer.Deserialize<SerializedScene>(json, _opts)
                       ?? new SerializedScene();
            var scene = new Scene { Name = data.Name, FilePath = filePath };
            foreach (var sgo in data.GameObjects)
            {
                var go = DeserializeGO(sgo);
                scene.AddGameObject(go);
            }
            return scene;
        }

        // ── Public prefab helpers ──────────────────────────────────────────────
        public static string GameObjectToJson(GameObject go)
        {
            var sgo = SerializeGO(go);
            return JsonSerializer.Serialize(sgo, _opts);
        }

        public static GameObject? GameObjectFromJson(string json)
        {
            try
            {
                var sgo = JsonSerializer.Deserialize<SerializedGameObject>(json, _opts);
                return sgo == null ? null : DeserializeGO(sgo);
            }
            catch { return null; }
        }

        public static void SavePrefab(GameObject go, string filePath)
        {
            string dir = Path.GetDirectoryName(filePath)!;
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, GameObjectToJson(go));
        }

        public static GameObject? LoadPrefab(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            return GameObjectFromJson(File.ReadAllText(filePath));
        }

        // ── Serialise one GameObject (recursive) ──────────────────────────────
        private static SerializedGameObject SerializeGO(GameObject go)
        {
            var sgo = new SerializedGameObject
            {
                InstanceId = go.InstanceId,
                Name = go.Name,
                ActiveSelf = go.ActiveSelf,
                Tag = go.Tag,
                Layer = go.Layer,
                Position = V3(go.Transform.LocalPosition),
                Rotation = V3(go.Transform.LocalEulerAngles),
                Scale = V3(go.Transform.LocalScale),
            };

            foreach (var comp in go.Components)
                sgo.Components.Add(SerializeComponent(comp));

            foreach (var child in go.Children)
                sgo.Children.Add(SerializeGO(child));

            return sgo;
        }

        private static SerializedComponent SerializeComponent(Component comp)
        {
            var sc = new SerializedComponent
            {
                TypeName = comp.GetType().Name,
                Enabled = comp.Enabled,
            };

            // Reflect all public fields and read/write properties
            foreach (var fi in comp.GetType().GetFields(
                BindingFlags.Public | BindingFlags.Instance))
            {
                try { sc.Properties[fi.Name] = ValueToNode(fi.GetValue(comp)); }
                catch { /* skip unserializable */ }
            }

            foreach (var pi in comp.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!pi.CanRead) continue;
                try { sc.Properties[pi.Name] = ValueToNode(pi.GetValue(comp)); }
                catch { }
            }

            return sc;
        }

        // ── Deserialise one GameObject (recursive) ────────────────────────────
        private static GameObject DeserializeGO(SerializedGameObject sgo)
        {
            var go = new GameObject(sgo.Name)
            {
                ActiveSelf = sgo.ActiveSelf,
                Tag = sgo.Tag,
                Layer = sgo.Layer,
            };
            go.Transform.LocalPosition = FromV3(sgo.Position);
            go.Transform.LocalEulerAngles = FromV3(sgo.Rotation);
            go.Transform.LocalScale = FromV3(sgo.Scale);

            foreach (var sc in sgo.Components)
            {
                var comp = ComponentRegistry.Create(sc.TypeName);
                if (comp == null) continue;
                comp.Enabled = sc.Enabled;
                comp.GameObject = go;

                // Restore property values
                foreach (var kv in sc.Properties)
                {
                    try { ApplyProperty(comp, kv.Key, kv.Value); }
                    catch { }
                }
                go.Components.Add(comp);
            }

            foreach (var childSgo in sgo.Children)
            {
                var child = DeserializeGO(childSgo);
                child.SetParent(go);
            }

            return go;
        }

        private static void ApplyProperty(Component comp, string name, JsonNode? node)
        {
            if (node == null) return;
            var type = comp.GetType();

            var fi = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (fi != null) { fi.SetValue(comp, NodeToValue(node, fi.FieldType)); return; }

            var pi = type.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (pi != null && pi.CanWrite)
                pi.SetValue(comp, NodeToValue(node, pi.PropertyType));
        }

        // ── JsonNode ↔ CLR value ──────────────────────────────────────────────
        private static JsonNode? ValueToNode(object? val)
        {
            if (val == null) return null;
            return val switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                float f => JsonValue.Create(f),
                double d => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                Vector3 v => new JsonArray(v.X, v.Y, v.Z),
                Vector4 v => new JsonArray(v.X, v.Y, v.Z, v.W),
                Color4 c => new JsonArray(c.R, c.G, c.B, c.A),
                _ => JsonValue.Create(val.ToString()),
            };
        }

        private static object? NodeToValue(JsonNode node, Type target)
        {
            if (target == typeof(bool)) return node.GetValue<bool>();
            if (target == typeof(int)) return node.GetValue<int>();
            if (target == typeof(float)) return node.GetValue<float>();
            if (target == typeof(double)) return node.GetValue<double>();
            if (target == typeof(string)) return node.GetValue<string>();
            if (target == typeof(Vector3) && node is JsonArray arr3 && arr3.Count >= 3)
                return new Vector3(arr3[0]!.GetValue<float>(),
                                   arr3[1]!.GetValue<float>(),
                                   arr3[2]!.GetValue<float>());
            if (target == typeof(Vector4) && node is JsonArray arr4 && arr4.Count >= 4)
                return new Vector4(arr4[0]!.GetValue<float>(),
                                   arr4[1]!.GetValue<float>(),
                                   arr4[2]!.GetValue<float>(),
                                   arr4[3]!.GetValue<float>());
            if (target == typeof(Color4) && node is JsonArray arrc && arrc.Count >= 4)
                return new Color4(arrc[0]!.GetValue<float>(),
                                  arrc[1]!.GetValue<float>(),
                                  arrc[2]!.GetValue<float>(),
                                  arrc[3]!.GetValue<float>());
            return null;
        }

        private static float[] V3(Vector3 v) => new[] { v.X, v.Y, v.Z };
        private static Vector3 FromV3(float[] a) =>
            a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.Zero;
    }
}