using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    // ── Property types (mirrors Unity) ────────────────────────────────────────
    public enum ShaderPropType { Float, Int, Color, Vector, Texture2D, Range }

    // ── A single declared property from the shader's Properties block ─────────
    public class ShaderProperty
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public ShaderPropType Type { get; set; } = ShaderPropType.Float;
        public float Min { get; set; } = 0f;   // for Range
        public float Max { get; set; } = 1f;   // for Range
        public object? DefaultValue { get; set; }         // float/int/Vector4/string("")
    }

    // ── Runtime value store for one material instance ─────────────────────────
    public class MaterialPropertyBlock
    {
        private readonly Dictionary<string, object?> _values = new();

        public void Set(string name, object? value) => _values[name] = value;

        public float GetFloat(string name, float def = 0f) => _values.TryGetValue(name, out var v) && v is float f ? f : def;
        public int GetInt(string name, int def = 0) => _values.TryGetValue(name, out var v) && v is int i ? i : def;
        public Vector4 GetColor(string name, Vector4 def = default) => _values.TryGetValue(name, out var v) && v is Vector4 c ? c : def;
        public Vector4 GetVector(string name, Vector4 def = default) => _values.TryGetValue(name, out var v) && v is Vector4 c ? c : def;
        public string GetTexture(string name, string def = "") => _values.TryGetValue(name, out var v) && v is string s ? s : def;
        public bool Has(string name) => _values.ContainsKey(name);

        public IEnumerable<(string Key, object? Val)> All()
        {
            foreach (var kv in _values) yield return (kv.Key, kv.Value);
        }

        public void CopyFrom(MaterialPropertyBlock other)
        {
            foreach (var (k, v) in other.All()) _values[k] = v;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MaterialAsset  —  the .mat file in the project
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Represents a .mat asset file.  Stores:
    ///   - ShaderPath: path to the .shader file (or built-in name like "Standard")
    ///   - Properties: per-instance overrides of shader-declared uniforms
    ///   - ParsedProperties: the shader's declared property list (filled at load time)
    /// </summary>
    public class MaterialAsset
    {
        // ── Persistent data ───────────────────────────────────────────────────
        public string ShaderPath { get; set; } = "Standard";
        public MaterialPropertyBlock Properties { get; } = new();

        // ── Runtime data (not serialized) ─────────────────────────────────────
        /// <summary>Declared properties parsed from the shader file.</summary>
        public List<ShaderProperty> DeclaredProperties { get; } = new();

        // ── IO ────────────────────────────────────────────────────────────────
        private static readonly JsonSerializerOptions _opts =
            new() { WriteIndented = true };

        public static MaterialAsset Load(string filePath)
        {
            var mat = new MaterialAsset();
            if (!File.Exists(filePath)) return mat;

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(filePath));
                if (root == null) return mat;

                mat.ShaderPath = root["shader"]?.GetValue<string>() ?? "Standard";

                var props = root["properties"]?.AsObject();
                if (props != null)
                {
                    foreach (var kv in props)
                    {
                        var val = NodeToValue(kv.Value);
                        if (val != null) mat.Properties.Set(kv.Key, val);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MaterialAsset] Load failed '{filePath}': {ex.Message}");
            }
            return mat;
        }

        public void Save(string filePath)
        {
            var root = new JsonObject
            {
                ["shader"] = ShaderPath,
                ["properties"] = BuildPropsNode(),
            };
            string dir = Path.GetDirectoryName(filePath)!;
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, root.ToJsonString(_opts));
        }

        private JsonObject BuildPropsNode()
        {
            var obj = new JsonObject();
            foreach (var (k, v) in Properties.All())
            {
                var node = ValueToNode(v);
                if (node != null) obj[k] = node;
            }
            return obj;
        }

        private static object? NodeToValue(JsonNode? node)
        {
            if (node == null) return null;
            if (node is JsonArray arr && arr.Count == 4)
                return new Vector4(arr[0]!.GetValue<float>(), arr[1]!.GetValue<float>(),
                                   arr[2]!.GetValue<float>(), arr[3]!.GetValue<float>());
            if (node is JsonValue jv)
            {
                if (jv.TryGetValue(out float f)) return f;
                if (jv.TryGetValue(out int i)) return i;
                if (jv.TryGetValue(out double d)) return (float)d;
                if (jv.TryGetValue(out string s)) return s;
            }
            return null;
        }

        private static JsonNode? ValueToNode(object? val) => val switch
        {
            float f => JsonValue.Create(f),
            int i => JsonValue.Create(i),
            string s => JsonValue.Create(s),
            Vector4 v => new JsonArray(v.X, v.Y, v.Z, v.W),
            _ => null,
        };

        // ── Shader property parser ─────────────────────────────────────────────
        /// <summary>
        /// Parses the Properties { } block from a .shader source file.
        ///
        /// Syntax (Unity-compatible):
        ///   _Name ("Display Name", Type) = DefaultValue
        ///
        /// Supported types:
        ///   Float, Int, Range(min,max), Color, Vector, 2D
        /// </summary>
        public static List<ShaderProperty> ParseShaderProperties(string shaderSource)
        {
            var result = new List<ShaderProperty>();
            int start = shaderSource.IndexOf("Properties", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return result;
            int brace = shaderSource.IndexOf('{', start);
            if (brace < 0) return result;
            int depth = 1, end = brace + 1;
            while (end < shaderSource.Length && depth > 0)
            {
                if (shaderSource[end] == '{') depth++;
                else if (shaderSource[end] == '}') depth--;
                end++;
            }
            string block = shaderSource[(brace + 1)..(end - 1)];

            foreach (var rawLine in block.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                var prop = ParsePropertyLine(line);
                if (prop != null) result.Add(prop);
            }
            return result;
        }

        private static ShaderProperty? ParsePropertyLine(string line)
        {
            // _Name ("Display Name", Type) = DefaultValue
            int paren = line.IndexOf('(');
            if (paren < 0) return null;
            string name = line[..paren].Trim();
            if (!name.StartsWith("_") && !char.IsLetter(name[0])) return null;

            int close = line.IndexOf(')', paren);
            if (close < 0) return null;
            string inner = line[(paren + 1)..close];

            // Split display name and type
            int lastComma = inner.LastIndexOf(',');
            if (lastComma < 0) return null;
            string dispName = inner[..lastComma].Trim().Trim('"', '\'', ' ');
            string typePart = inner[(lastComma + 1)..].Trim();

            // Default value after '='
            object? defVal = null;
            int eq = line.IndexOf('=', close);
            if (eq >= 0)
            {
                string defStr = line[(eq + 1)..].Trim();
                defVal = ParseDefaultValue(defStr);
            }

            var prop = new ShaderProperty
            {
                Name = name,
                DisplayName = string.IsNullOrEmpty(dispName) ? name : dispName,
                DefaultValue = defVal,
            };

            if (typePart.StartsWith("Range", StringComparison.OrdinalIgnoreCase))
            {
                prop.Type = ShaderPropType.Range;
                var rp = typePart.IndexOf('(');
                var rc = typePart.IndexOf(')');
                if (rp >= 0 && rc > rp)
                {
                    var parts = typePart[(rp + 1)..rc].Split(',');
                    if (parts.Length >= 2)
                    {
                        // Can't pass properties as out parameters; parse into locals and assign.
                        float minVal = prop.Min;
                        float maxVal = prop.Max;
                        float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out minVal);
                        float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out maxVal);
                        prop.Min = minVal;
                        prop.Max = maxVal;
                    }
                } 
                prop.DefaultValue ??= prop.Min;
            }
            else if (typePart.Equals("Float", StringComparison.OrdinalIgnoreCase))
                prop.Type = ShaderPropType.Float;
            else if (typePart.Equals("Int", StringComparison.OrdinalIgnoreCase))
                prop.Type = ShaderPropType.Int;
            else if (typePart.Equals("Color", StringComparison.OrdinalIgnoreCase))
                prop.Type = ShaderPropType.Color;
            else if (typePart.Equals("Vector", StringComparison.OrdinalIgnoreCase))
                prop.Type = ShaderPropType.Vector;
            else if (typePart.Equals("2D", StringComparison.OrdinalIgnoreCase)
                  || typePart.StartsWith("Texture", StringComparison.OrdinalIgnoreCase))
                prop.Type = ShaderPropType.Texture2D;
            else return null;

            return prop;
        }

        private static object? ParseDefaultValue(string s)
        {
            // Color/Vector: (r, g, b, a)
            if (s.StartsWith("("))
            {
                var inner = s.Trim('(', ')');
                var parts = inner.Split(',');
                if (parts.Length == 4)
                {
                    float[] vals = new float[4];
                    bool ok = true;
                    for (int i = 0; i < 4; i++)
                        if (!float.TryParse(parts[i].Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out vals[i]))
                        { ok = false; break; }
                    if (ok) return new Vector4(vals[0], vals[1], vals[2], vals[3]);
                }
            }
            // Texture default: "white" etc.
            if (s.StartsWith("\"")) return s.Trim('"');
            // Number
            if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float fv))
                return fv;
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MaterialCache — keeps loaded MaterialAssets alive (engine-wide)
    // ═══════════════════════════════════════════════════════════════════════════
    public static class MaterialCache
    {
        private static readonly Dictionary<string, MaterialAsset> _cache = new(StringComparer.OrdinalIgnoreCase);

        public static MaterialAsset Get(string filePath)
        {
            if (_cache.TryGetValue(filePath, out var existing)) return existing;
            var mat = MaterialAsset.Load(filePath);
            _cache[filePath] = mat;
            return mat;
        }

        public static void Invalidate(string filePath) => _cache.Remove(filePath);
        public static void Clear() => _cache.Clear();
    }
}