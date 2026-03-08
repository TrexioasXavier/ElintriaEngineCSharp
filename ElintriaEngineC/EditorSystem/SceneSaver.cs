using Elintria.Engine;
using Elintria.Engine.Rendering;
using OpenTK.Mathematics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elintria.Editor
{
    // =========================================================================
    // SceneSaver / SceneLoader
    // =========================================================================
    /// <summary>
    /// Saves and loads scenes in a JSON format mirroring Unity's .unity file logic:
    ///
    ///   data/Scenes/<SceneName>.scene.json
    ///
    /// Format:
    /// {
    ///   "name": "Game",
    ///   "gameObjects": [
    ///     {
    ///       "id": 1,
    ///       "name": "Cube",
    ///       "active": true,
    ///       "tag": "Untagged",
    ///       "parentId": 0,          // 0 = no parent
    ///       "transform": {
    ///         "localPosition": [0,0,0],
    ///         "localRotation": [0,0,0,1],
    ///         "localScale":    [1,1,1]
    ///       },
    ///       "components": [
    ///         {
    ///           "type": "MeshRenderer",
    ///           "enabled": true,
    ///           "fields": { "FieldName": "value", ... }
    ///         }
    ///       ]
    ///     }
    ///   ]
    /// }
    /// </summary>
    public static class SceneSaver
    {
        private const string SCENES_DIR = "data/Scenes";

        static SceneSaver() => Directory.CreateDirectory(SCENES_DIR);

        // -----------------------------------------------------------------------
        // SAVE
        // -----------------------------------------------------------------------
        public static void Save(Scene scene)
        {
            var data = new SceneData();
            data.Name = scene.Name;

            // Assign stable IDs: index in a BFS ordering
            var all = BfsOrder(scene.RootObjects);
            var idMap = all.Select((go, i) => (go, id: i + 1))
                             .ToDictionary(t => t.go, t => t.id);

            foreach (var go in all)
            {
                var god = new GameObjectData();
                god.Id = idMap[go];
                god.Name = go.Name;
                god.Active = go.ActiveSelf;
                god.Tag = go.Tag;
                god.ParentId = go.Transform.Parent != null
                                   ? idMap.GetValueOrDefault(go.Transform.Parent.GameObject, 0)
                                   : 0;

                var t = go.Transform;
                god.Transform = new TransformData
                {
                    LocalPosition = Vec3(t.LocalPosition),
                    LocalRotation = Quat(t.LocalRotation),
                    LocalScale = Vec3(t.LocalScale)
                };

                foreach (var comp in go.GetComponents<Component>())
                {
                    // Skip internal engine components that can't survive serialization
                    if (comp is Transform) continue;

                    var cd = new ComponentData();
                    cd.Type = comp.GetType().FullName ?? comp.GetType().Name;
                    cd.Enabled = comp.Enabled;
                    cd.Fields = SerializeFields(comp);

                    god.Components.Add(cd);
                }

                data.GameObjects.Add(god);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string path = ScenePath(scene.Name);
            File.WriteAllText(path, JsonSerializer.Serialize(data, options));
            Console.WriteLine($"[Scene] Saved → {path}");
        }

        // -----------------------------------------------------------------------
        // LOAD
        // -----------------------------------------------------------------------
        /// <summary>
        /// Restores scene contents from disk into <paramref name="scene"/>.
        /// Call AFTER scene.Load() and BEFORE gameplay starts.
        /// </summary>
        public static void Load(Scene scene)
        {
            string path = ScenePath(scene.Name);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Scene] No save file found at {path}");
                return;
            }

            SceneData data;
            try
            {
                data = JsonSerializer.Deserialize<SceneData>(File.ReadAllText(path))!;
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"[Scene] Load failed: {ex.Message}");
                return;
            }

            // Map id → GameObject so we can set up parent links after
            var goMap = new Dictionary<int, GameObject>();

            foreach (var god in data.GameObjects)
            {
                var go = scene.CreateGameObject(god.Name);
                go.Tag = god.Tag ?? "Untagged";
                go.SetActive(false);   // will re-activate after full restore

                var t = go.Transform;
                if (god.Transform != null)
                {
                    t.LocalPosition = ToVec3(god.Transform.LocalPosition);
                    t.LocalRotation = ToQuat(god.Transform.LocalRotation);
                    t.LocalScale = ToVec3(god.Transform.LocalScale, Vector3.One);
                }

                goMap[god.Id] = go;
            }

            // Flush the pending-add queue so the GOs exist in the scene now
            scene.FlushPendingAdds();

            // Set parents
            foreach (var god in data.GameObjects)
            {
                if (god.ParentId != 0 && goMap.TryGetValue(god.ParentId, out var parent)
                                      && goMap.TryGetValue(god.Id, out var child))
                    child.Transform.SetParent(parent.Transform, keepWorldPosition: false);
            }

            // Restore components + active state
            foreach (var god in data.GameObjects)
            {
                if (!goMap.TryGetValue(god.Id, out var go)) continue;

                foreach (var cd in god.Components)
                {
                    var compType = ResolveType(cd.Type);
                    if (compType == null)
                    {
                        Console.WriteLine($"[Scene] Unknown component type: {cd.Type}");
                        continue;
                    }

                    // AddComponent via reflection (we don't have a generic T at compile time)
                    var addMethod = typeof(GameObject)
                        .GetMethod(nameof(GameObject.AddComponent))!
                        .MakeGenericMethod(compType);
                    var comp = (Component)addMethod.Invoke(go, null)!;
                    comp.Enabled = cd.Enabled;

                    if (cd.Fields != null)
                        DeserializeFields(comp, cd.Fields);
                }

                go.SetActive(god.Active);
            }

            Console.WriteLine($"[Scene] Loaded {data.GameObjects.Count} objects from {path}");
        }

        // -----------------------------------------------------------------------
        // Public helpers
        // -----------------------------------------------------------------------
        public static bool HasSave(string sceneName)
            => File.Exists(ScenePath(sceneName));

        public static string ScenePath(string sceneName)
            => Path.Combine(SCENES_DIR, sceneName + ".scene.json");

        // -----------------------------------------------------------------------
        // Reflection helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Serializes all PUBLIC instance fields and properties (excl. engine-internals).
        /// </summary>
        public static Dictionary<string, string> SerializeFields(Component comp)
        {
            var dict = new Dictionary<string, string>();
            var type = comp.GetType();

            // Public instance fields (user-declared, e.g. public float Speed;)
            foreach (var fi in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.IsSpecialName) continue;
                if (IsEngineInternal(fi.Name)) continue;
                var val = fi.GetValue(comp);
                dict[fi.Name] = ValueToString(val);
            }

            // Public instance properties with getter+setter
            foreach (var pi in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite) continue;
                if (pi.GetIndexParameters().Length > 0) continue;
                if (IsEngineInternal(pi.Name)) continue;
                var val = pi.GetValue(comp);
                dict[pi.Name] = ValueToString(val);
            }

            return dict;
        }

        private static bool IsEngineInternal(string name)
        {
            // Skip properties defined on Component or higher that would cause cycles
            return name is "GameObject" or "Transform" or "Enabled"
                       or "Scene" or "Tag" or "Layer" or "Name"
                       or "ActiveSelf" or "ActiveInHierarchy";
        }

        private static void DeserializeFields(Component comp,
                                              Dictionary<string, string> dict)
        {
            var type = comp.GetType();
            foreach (var (key, strVal) in dict)
            {
                // Try field first
                var fi = type.GetField(key, BindingFlags.Public | BindingFlags.Instance);
                if (fi != null)
                {
                    var val = StringToValue(strVal, fi.FieldType);
                    if (val != null) fi.SetValue(comp, val);
                    continue;
                }
                // Try property
                var pi = type.GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
                if (pi != null && pi.CanWrite)
                {
                    var val = StringToValue(strVal, pi.PropertyType);
                    if (val != null) pi.SetValue(comp, val);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Value → string / string → value  (invariant culture, parseable)
        // -----------------------------------------------------------------------
        private static string ValueToString(object val)
        {
            if (val == null) return "";
            var ic = CultureInfo.InvariantCulture;
            return val switch
            {
                Vector2 v2 => $"{v2.X.ToString(ic)},{v2.Y.ToString(ic)}",
                Vector3 v3 => $"{v3.X.ToString(ic)},{v3.Y.ToString(ic)},{v3.Z.ToString(ic)}",
                Vector4 v4 => $"{v4.X.ToString(ic)},{v4.Y.ToString(ic)},{v4.Z.ToString(ic)},{v4.W.ToString(ic)}",
                Quaternion q => $"{q.X.ToString(ic)},{q.Y.ToString(ic)},{q.Z.ToString(ic)},{q.W.ToString(ic)}",
                float f => f.ToString("G9", ic),
                double d => d.ToString("G17", ic),
                bool b => b.ToString(),
                _ => val.ToString() ?? ""
            };
        }

        private static object StringToValue(string str, System.Type t)
        {
            if (str == null) return null;
            var ic = CultureInfo.InvariantCulture;
            try
            {
                if (t == typeof(float)) return float.Parse(str, ic);
                if (t == typeof(double)) return double.Parse(str, ic);
                if (t == typeof(int)) return int.Parse(str, ic);
                if (t == typeof(bool)) return bool.Parse(str);
                if (t == typeof(string)) return str;
                if (t == typeof(Vector2)) { var p = Parts(str, 2); return new Vector2(p[0], p[1]); }
                if (t == typeof(Vector3)) { var p = Parts(str, 3); return new Vector3(p[0], p[1], p[2]); }
                if (t == typeof(Vector4)) { var p = Parts(str, 4); return new Vector4(p[0], p[1], p[2], p[3]); }
                if (t == typeof(Quaternion)) { var p = Parts(str, 4); return new Quaternion(p[0], p[1], p[2], p[3]); }
            }
            catch { /* ignore bad values */ }
            return null;
        }

        private static float[] Parts(string s, int n)
            => s.Split(',').Select(p => float.Parse(p, CultureInfo.InvariantCulture)).Take(n).ToArray();

        // -----------------------------------------------------------------------
        // Helper: BFS walk over the GO hierarchy
        // -----------------------------------------------------------------------
        private static List<GameObject> BfsOrder(IEnumerable<GameObject> roots)
        {
            var result = new List<GameObject>();
            var queue = new Queue<GameObject>(roots);
            while (queue.Count > 0)
            {
                var go = queue.Dequeue();
                result.Add(go);
                foreach (var child in go.Transform.Children)
                    queue.Enqueue(child.GameObject);
            }
            return result;
        }

        // -----------------------------------------------------------------------
        // Resolve a type name across all loaded assemblies
        // -----------------------------------------------------------------------
        private static System.Type ResolveType(string typeName)
        {
            // Try exact name first
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName);
                if (t != null) return t;
            }
            // Fall back to simple name
            string simple = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == simple);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // Compact float-array helpers
        // -----------------------------------------------------------------------
        private static float[] Vec3(Vector3 v) => new[] { v.X, v.Y, v.Z };
        private static float[] Quat(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };

        private static Vector3 ToVec3(float[] a, Vector3 def = default)
            => a?.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : def;
        private static Quaternion ToQuat(float[] a)
            => a?.Length >= 4 ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.Identity;
    }

    // =========================================================================
    // JSON data model
    // =========================================================================

    public class SceneData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("gameObjects")]
        public List<GameObjectData> GameObjects { get; set; } = new();
    }

    public class GameObjectData
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("active")] public bool Active { get; set; } = true;
        [JsonPropertyName("tag")] public string Tag { get; set; }
        [JsonPropertyName("parentId")] public int ParentId { get; set; }

        [JsonPropertyName("transform")]
        public TransformData Transform { get; set; }

        [JsonPropertyName("components")]
        public List<ComponentData> Components { get; set; } = new();
    }

    public class TransformData
    {
        [JsonPropertyName("localPosition")] public float[] LocalPosition { get; set; }
        [JsonPropertyName("localRotation")] public float[] LocalRotation { get; set; }
        [JsonPropertyName("localScale")] public float[] LocalScale { get; set; }
    }

    public class ComponentData
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

        [JsonPropertyName("fields")]
        public Dictionary<string, string> Fields { get; set; }
    }
}