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
            var data = new SerializedScene { Name = scene.Name, Version = 1 };
            foreach (var root in scene.RootObjects)
                data.GameObjects.Add(SerializeGO(root));

            string dir = Path.GetDirectoryName(filePath)!;
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(data, _opts));
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
            var data = JsonSerializer.Deserialize<SerializedScene>(json, _opts)!;
            var scene = new Scene { Name = data.Name, FilePath = filePath };
            var lookup = new Dictionary<int, GameObject>();

            // Pass 1: build all GOs without resolving cross-references
            foreach (var sgo in data.GameObjects)
            {
                var go = DeserializeGO(sgo, lookup);
                scene.AddGameObject(go);
            }

            // Pass 2: rewire component cross-references
            ResolveRefs(data.GameObjects, lookup);
            return scene;
        }

        // ── Public prefab helpers ──────────────────────────────────────────────
        public static string GameObjectToJson(GameObject go)
            => JsonSerializer.Serialize(SerializeGO(go), _opts);

        public static GameObject? GameObjectFromJson(string json)
        {
            try
            {
                var sgo = JsonSerializer.Deserialize<SerializedGameObject>(json, _opts);
                if (sgo == null) return null;
                var lookup = new Dictionary<int, GameObject>();
                var result = DeserializeGO(sgo, lookup);
                // Single-root prefab: resolve any self-referencing fields
                var list = new List<SerializedGameObject> { sgo };
                ResolveRefs(list, lookup);
                return result;
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
            => File.Exists(filePath) ? GameObjectFromJson(File.ReadAllText(filePath)) : null;

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

            // Special case: DynamicScript stores user field values in a dict.
            // Serialize each entry individually so refs survive the scene clone.
            if (comp is DynamicScript ds)
            {
                sc.Properties["ScriptTypeName"] = JsonValue.Create(ds.ScriptTypeName);
                foreach (var kv in ds.FieldValues)
                {
                    var node = ValueToNode(kv.Value, kv.Value?.GetType() ?? typeof(object));
                    if (node != null)
                        sc.Properties["_fv_" + kv.Key] = node;
                }
                return sc;
            }

            foreach (var fi in comp.GetType().GetFields(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                try
                {
                    var val = fi.GetValue(comp);
                    var node = ValueToNode(val, fi.FieldType);
                    if (node != null) sc.Properties[fi.Name] = node;
                }
                catch { }
            }

            foreach (var pi in comp.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!pi.CanRead || !pi.CanWrite) continue;
                try
                {
                    var val = pi.GetValue(comp);
                    var node = ValueToNode(val, pi.PropertyType);
                    if (node != null) sc.Properties[pi.Name] = node;
                }
                catch { }
            }

            return sc;
        }

        // ── Deserialise one GameObject (recursive) ────────────────────────────
        private static GameObject DeserializeGO(SerializedGameObject sgo,
                                                 Dictionary<int, GameObject> lookup)
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

            lookup[sgo.InstanceId] = go;

            foreach (var sc in sgo.Components)
            {
                var comp = ComponentRegistry.Create(sc.TypeName);
                if (comp == null) continue;
                comp.Enabled = sc.Enabled;
                comp.GameObject = go;

                if (comp is DynamicScript dsComp)
                {
                    // Restore ScriptTypeName and non-ref FieldValues entries now;
                    // ref entries (_fv_ prefixed refs) are resolved in pass-2.
                    if (sc.Properties.TryGetValue("ScriptTypeName", out var stn))
                        dsComp.ScriptTypeName = stn?.GetValue<string>() ?? "";
                    foreach (var kv in sc.Properties)
                    {
                        if (!kv.Key.StartsWith("_fv_")) continue;
                        if (kv.Value is JsonObject obj &&
                            (obj.ContainsKey("__ref_go") || obj.ContainsKey("__ref_comp")))
                            continue; // resolved in pass-2
                        string fieldName = kv.Key[4..]; // strip "_fv_"
                        dsComp.FieldValues[fieldName] = NodeToValueUntyped(kv.Value);
                    }
                }
                else
                {
                    foreach (var kv in sc.Properties)
                    {
                        try { ApplyPropertyValue(comp, kv.Key, kv.Value); }
                        catch { }
                    }
                }
                go.Components.Add(comp);
            }

            foreach (var childSgo in sgo.Children)
            {
                var child = DeserializeGO(childSgo, lookup);
                child.SetParent(go);
            }

            return go;
        }

        // ── Pass-2: resolve cross-object refs ─────────────────────────────────
        private static void ResolveRefs(List<SerializedGameObject> sgos,
                                         Dictionary<int, GameObject> lookup)
        {
            foreach (var sgo in sgos)
            {
                if (!lookup.TryGetValue(sgo.InstanceId, out var go)) continue;
                for (int ci = 0; ci < sgo.Components.Count && ci < go.Components.Count; ci++)
                {
                    var sc = sgo.Components[ci];
                    var comp = go.Components[ci];
                    ResolveComponentRefs(comp, sc, lookup);
                }
                ResolveRefs(sgo.Children, lookup);
            }
        }

        private static void ResolveComponentRefs(Component comp,
                                                   SerializedComponent sc,
                                                   Dictionary<int, GameObject> lookup)
        {
            // DynamicScript: resolve _fv_ prefixed entries that are ref tokens
            if (comp is DynamicScript ds)
            {
                foreach (var kv in sc.Properties)
                {
                    if (!kv.Key.StartsWith("_fv_")) continue;
                    if (kv.Value is not JsonObject refObj) continue;
                    if (!refObj.ContainsKey("__ref_go") && !refObj.ContainsKey("__ref_comp")) continue;

                    string fieldName = kv.Key[4..];
                    object? resolved = ResolveRefToken(refObj, lookup, targetType: null);
                    if (resolved != null)
                        ds.FieldValues[fieldName] = resolved;
                }
                return;
            }

            foreach (var kv in sc.Properties)
            {
                if (kv.Value is not JsonObject obj) continue;
                if (!obj.ContainsKey("__ref_go") && !obj.ContainsKey("__ref_comp")) continue;

                var fi = comp.GetType().GetField(kv.Key,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var pi = fi == null ? comp.GetType().GetProperty(kv.Key,
                    BindingFlags.Public | BindingFlags.Instance) : null;

                if (fi == null && pi == null) continue;
                var targetType = fi?.FieldType ?? pi!.PropertyType;

                object? resolved = ResolveRefToken(obj, lookup, targetType);
                try
                {
                    if (resolved != null)
                    {
                        fi?.SetValue(comp, resolved);
                        pi?.SetValue(comp, resolved);
                    }
                }
                catch { }
            }
        }

        private static object? ResolveRefToken(JsonObject obj,
                                                Dictionary<int, GameObject> lookup,
                                                Type? targetType)
        {
            if (obj.ContainsKey("__ref_go"))
            {
                int id = obj["__ref_go"]!.GetValue<int>();
                if (lookup.TryGetValue(id, out var refGO))
                    return (targetType == null || targetType == typeof(GameObject))
                           ? (object)refGO : null;
            }
            else if (obj.ContainsKey("__ref_comp"))
            {
                int id = obj["__ref_comp"]!["id"]!.GetValue<int>();
                string typeName = obj["__ref_comp"]!["type"]!.GetValue<string>();
                if (lookup.TryGetValue(id, out var refGO))
                {
                    foreach (var c in refGO.Components)
                        if (c.GetType().Name == typeName &&
                            (targetType == null || targetType.IsAssignableFrom(c.GetType())))
                            return c;
                }
            }
            return null;
        }

        // ── Apply a single property (non-ref values only) ─────────────────────
        private static void ApplyPropertyValue(Component comp, string name, JsonNode? node)
        {
            if (node == null) return;

            // Skip ref-tokens — those are handled in pass 2
            if (node is JsonObject obj &&
                (obj.ContainsKey("__ref_go") || obj.ContainsKey("__ref_comp"))) return;

            var fi = comp.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (fi != null)
            {
                var v = NodeToValue(node, fi.FieldType);
                if (v != null) fi.SetValue(comp, v);
                return;
            }
            var pi = comp.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance);
            if (pi != null && pi.CanWrite)
            {
                var v = NodeToValue(node, pi.PropertyType);
                if (v != null) pi.SetValue(comp, v);
            }
        }

        // ── Serialise a value, emitting ref-tokens for GO/Component refs ───────
        private static JsonNode? ValueToNode(object? val, Type type)
        {
            if (val == null) return null;

            // Cross-object reference: store as token; resolved in pass 2
            if (val is GameObject go)
                return new JsonObject { ["__ref_go"] = go.InstanceId };

            if (val is Component comp && comp.GameObject != null)
                return new JsonObject
                {
                    ["__ref_comp"] = new JsonObject
                    {
                        ["id"] = comp.GameObject.InstanceId,
                        ["type"] = comp.GetType().Name,
                    }
                };

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
                _ => null,   // skip unknown types silently
            };
        }

        // ── Deserialise a JSON node to a CLR value ─────────────────────────────
        private static object? NodeToValue(JsonNode node, Type target)
        {
            if (target == typeof(bool)) return node.GetValue<bool>();
            if (target == typeof(int)) return node.GetValue<int>();
            if (target == typeof(float)) return node.GetValue<float>();
            if (target == typeof(double)) return node.GetValue<double>();
            if (target == typeof(string)) return node.GetValue<string>();
            if (target == typeof(Vector3) && node is JsonArray a3 && a3.Count >= 3)
                return new Vector3(a3[0]!.GetValue<float>(), a3[1]!.GetValue<float>(), a3[2]!.GetValue<float>());
            if (target == typeof(Vector4) && node is JsonArray a4 && a4.Count >= 4)
                return new Vector4(a4[0]!.GetValue<float>(), a4[1]!.GetValue<float>(),
                                   a4[2]!.GetValue<float>(), a4[3]!.GetValue<float>());
            if (target == typeof(Color4) && node is JsonArray ac && ac.Count >= 4)
                return new Color4(ac[0]!.GetValue<float>(), ac[1]!.GetValue<float>(),
                                  ac[2]!.GetValue<float>(), ac[3]!.GetValue<float>());
            return null;
        }

        /// <summary>Best-effort value from a JSON node when the target CLR type is unknown.</summary>
        private static object? NodeToValueUntyped(JsonNode? node)
        {
            if (node == null) return null;
            if (node is JsonValue jv)
            {
                if (jv.TryGetValue(out bool b)) return b;
                if (jv.TryGetValue(out int i)) return i;
                if (jv.TryGetValue(out float f)) return f;
                if (jv.TryGetValue(out double d)) return d;
                if (jv.TryGetValue(out string s)) return s;
            }
            if (node is JsonArray arr)
            {
                if (arr.Count == 3)
                    return new Vector3(arr[0]!.GetValue<float>(),
                                       arr[1]!.GetValue<float>(),
                                       arr[2]!.GetValue<float>());
                if (arr.Count == 4)
                    return new Color4(arr[0]!.GetValue<float>(),
                                      arr[1]!.GetValue<float>(),
                                      arr[2]!.GetValue<float>(),
                                      arr[3]!.GetValue<float>());
            }
            return null;
        }

        private static float[] V3(Vector3 v) => new[] { v.X, v.Y, v.Z };
        private static Vector3 FromV3(float[] a) =>
            a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.Zero;
    }
}