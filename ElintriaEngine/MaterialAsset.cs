using ElintriaEngine.Rendering.Scene;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  ShaderPropType
    //  Used by InspectorPanel.DrawMaterialPropertyFields switch statement.
    // ═══════════════════════════════════════════════════════════════════════════
    public enum ShaderPropType { Float, Int, Range, Color, Vector, Texture2D }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ShaderPropDecl
    //  One property parsed from the Properties { } block of a .shader file.
    //  InspectorPanel accesses: Name, DisplayName, Type, DefaultValue, Min, Max
    // ═══════════════════════════════════════════════════════════════════════════
    public class ShaderPropDecl
    {
        public string Name { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public ShaderPropType Type { get; init; }
        public object? DefaultValue { get; init; }
        public float Min { get; init; } = 0f;
        public float Max { get; init; } = 1f;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MaterialProperties
    //  Typed value store for one material instance.
    //  InspectorPanel calls: GetFloat, GetInt, GetColor, GetVector, GetTexture, Set
    // ═══════════════════════════════════════════════════════════════════════════
    public class MaterialProperties
    {
        private readonly Dictionary<string, object?> _d = new();

        public float GetFloat(string n, float def = 0f) =>
            _d.TryGetValue(n, out var v) ? v switch
            { float f => f, double d => (float)d, int i => i, _ => def } : def;

        public int GetInt(string n, int def = 0) =>
            _d.TryGetValue(n, out var v) ? v switch
            { int i => i, float f => (int)f, double d => (int)d, _ => def } : def;

        public Vector4 GetColor(string n, Vector4 def = default) =>
            _d.TryGetValue(n, out var v) && v is Vector4 c ? c : def;

        public Vector4 GetVector(string n, Vector4 def = default) => GetColor(n, def);

        public string GetTexture(string n, string def = "") =>
            _d.TryGetValue(n, out var v) && v is string s ? s : def;

        public void Set(string n, object? v) => _d[n] = v;

        public bool Has(string n) => _d.ContainsKey(n);

        // ── JSON I/O ──────────────────────────────────────────────────────────
        internal void LoadFromJson(JsonObject obj)
        {
            foreach (var kv in obj) _d[kv.Key] = ParseNode(kv.Value);
        }

        internal JsonObject ToJson()
        {
            var o = new JsonObject();
            foreach (var kv in _d)
                switch (kv.Value)
                {
                    case float f: o[kv.Key] = JsonValue.Create(f); break;
                    case int i: o[kv.Key] = JsonValue.Create(i); break;
                    case double d: o[kv.Key] = JsonValue.Create((float)d); break;
                    case Vector4 v: o[kv.Key] = new JsonArray(v.X, v.Y, v.Z, v.W); break;
                    case string s: o[kv.Key] = JsonValue.Create(s); break;
                }
            return o;
        }

        private static object? ParseNode(JsonNode? n)
        {
            if (n is JsonArray a)
            {
                float x = a.Count > 0 ? a[0]?.GetValue<float>() ?? 0f : 0f;
                float y = a.Count > 1 ? a[1]?.GetValue<float>() ?? 0f : 0f;
                float z = a.Count > 2 ? a[2]?.GetValue<float>() ?? 0f : 0f;
                float w = a.Count > 3 ? a[3]?.GetValue<float>() ?? 1f : 1f;
                return new Vector4(x, y, z, w);
            }
            if (n is JsonValue jv)
            {
                if (jv.TryGetValue(out float f)) return f;
                if (jv.TryGetValue(out double d)) return (float)d;
                if (jv.TryGetValue(out int i)) return i;
                if (jv.TryGetValue(out string? s)) return s;
            }
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MaterialAsset
    //
    //  Loaded / saved from a .mat JSON file.
    //
    //  InspectorPanel uses:
    //    asset.ShaderPath
    //    asset.Properties   (MaterialProperties)
    //    asset.DeclaredProperties  (List<ShaderPropDecl>, populated by MaterialCache)
    //    asset.Save(path)
    //    MaterialAsset.ParseShaderProperties(src)  — static, parses .shader source
    //
    //  .mat file format (new):
    //  {
    //    "shader": "Assets/Shaders/Foo.shader",
    //    "properties": { "_Color": [1,1,1,1], "_Metallic": 0.0, "_MainTex": "" }
    //  }
    //
    //  Legacy flat format (albedo/metallic/roughness keys at root) is auto-migrated.
    // ═══════════════════════════════════════════════════════════════════════════
    public class MaterialAsset
    {
        public string ShaderPath { get; set; } = "Standard";
        public MaterialProperties Properties { get; } = new();
        public List<ShaderPropDecl> DeclaredProperties { get; } = new();
        public string? FilePath { get; private set; }

        // ── Load ──────────────────────────────────────────────────────────────
        public static MaterialAsset Load(string filePath)
        {
            var mat = new MaterialAsset { FilePath = filePath };
            if (!File.Exists(filePath)) return mat;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(filePath));
                if (root == null) return mat;
                mat.ShaderPath = root["shader"]?.GetValue<string>() ?? "Standard";
                if (root["properties"] is JsonObject props)
                    mat.Properties.LoadFromJson(props);
                else
                    MigrateLegacy(root, mat);
            }
            catch (Exception ex)
            { Console.WriteLine($"[MaterialAsset] Load failed {filePath}: {ex.Message}"); }
            return mat;
        }

        private static void MigrateLegacy(JsonNode root, MaterialAsset mat)
        {
            if (root["albedo"] is JsonArray a && a.Count >= 3)
                mat.Properties.Set("_Color", new Vector4(
                    a[0]?.GetValue<float>() ?? 1f, a[1]?.GetValue<float>() ?? 1f,
                    a[2]?.GetValue<float>() ?? 1f,
                    a.Count >= 4 ? a[3]?.GetValue<float>() ?? 1f : 1f));
            if (root["metallic"] is JsonValue mv) mat.Properties.Set("_Metallic", mv.GetValue<float>());
            if (root["roughness"] is JsonValue rv) mat.Properties.Set("_Roughness", rv.GetValue<float>());
            if (root["emission"] is JsonArray ea && ea.Count >= 3)
                mat.Properties.Set("_EmissionColor", new Vector4(
                    ea[0]?.GetValue<float>() ?? 0f, ea[1]?.GetValue<float>() ?? 0f,
                    ea[2]?.GetValue<float>() ?? 0f, 1f));
        }

        // ── Save ──────────────────────────────────────────────────────────────
        public void Save(string? path = null)
        {
            path ??= FilePath ?? throw new InvalidOperationException("No file path.");
            FilePath = path;
            var o = new JsonObject
            {
                ["shader"] = ShaderPath,
                ["properties"] = Properties.ToJson(),
            };
            File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // ── ParseShaderProperties ─────────────────────────────────────────────
        //  InspectorPanel calls:
        //    asset.DeclaredProperties.AddRange(Core.MaterialAsset.ParseShaderProperties(src));
        //
        //  Parses Properties { } block from .shader source.
        //  Supported lines:
        //    _Name ("Display", Float)        = 0.0
        //    _Name ("Display", Int)          = 0
        //    _Name ("Display", Range(0,1))   = 0.5
        //    _Name ("Display", Color)        = (1,1,1,1)
        //    _Name ("Display", Vector)       = (0,0,0,0)
        //    _Name ("Display", 2D)           = "white"
        public static List<ShaderPropDecl> ParseShaderProperties(string src)
        {
            var result = new List<ShaderPropDecl>();
            var blockM = Regex.Match(src, @"Properties\s*\{([^}]*)\}", RegexOptions.Singleline);
            if (!blockM.Success) return result;

            var lineRx = new Regex(@"(\w+)\s*\(\s*""([^""]*)""\s*,\s*([^)]+)\)\s*=\s*(.+)", RegexOptions.IgnoreCase);
            var rangeRx = new Regex(@"Range\s*\(\s*([\d.\-]+)\s*,\s*([\d.\-]+)\s*\)", RegexOptions.IgnoreCase);
            var vecRx = new Regex(@"\(\s*([\d.\-]+)\s*,\s*([\d.\-]+)\s*,\s*([\d.\-]+)\s*(?:,\s*([\d.\-]+))?\s*\)");

            foreach (var raw in blockM.Groups[1].Value.Split('\n'))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;
                var m = lineRx.Match(line);
                if (!m.Success) continue;

                string uname = m.Groups[1].Value.Trim();
                string dname = m.Groups[2].Value.Trim();
                string tExpr = m.Groups[3].Value.Trim();
                string dExpr = m.Groups[4].Value.Trim().Trim('"');

                float rMin = 0f, rMax = 1f;
                ShaderPropType ptype;
                var rangeM = rangeRx.Match(tExpr);
                if (rangeM.Success)
                {
                    ptype = ShaderPropType.Range;
                    TryF(rangeM.Groups[1].Value, out rMin);
                    TryF(rangeM.Groups[2].Value, out rMax);
                }
                else ptype = tExpr.ToLowerInvariant() switch
                {
                    "float" => ShaderPropType.Float,
                    "int" => ShaderPropType.Int,
                    "color" => ShaderPropType.Color,
                    "vector" => ShaderPropType.Vector,
                    "2d" or "texture" or "sampler2d" => ShaderPropType.Texture2D,
                    _ => ShaderPropType.Float,
                };

                object? def = ParseDef(dExpr, ptype, vecRx);
                result.Add(new ShaderPropDecl
                {
                    Name = uname,
                    DisplayName = dname,
                    Type = ptype,
                    DefaultValue = def,
                    Min = rMin,
                    Max = rMax,
                });
            }
            return result;
        }

        private static object? ParseDef(string expr, ShaderPropType t, Regex vecRx)
        {
            switch (t)
            {
                case ShaderPropType.Float:
                case ShaderPropType.Int:
                case ShaderPropType.Range:
                    return TryF(expr, out float f) ? f : 0f;
                case ShaderPropType.Color:
                case ShaderPropType.Vector:
                    var vm = vecRx.Match(expr);
                    if (vm.Success)
                    {
                        TryF(vm.Groups[1].Value, out float x); TryF(vm.Groups[2].Value, out float y);
                        TryF(vm.Groups[3].Value, out float z);
                        float w = 1f; if (vm.Groups[4].Success) TryF(vm.Groups[4].Value, out w);
                        return new Vector4(x, y, z, w);
                    }
                    return t == ShaderPropType.Color ? new Vector4(1, 1, 1, 1) : Vector4.Zero;
                case ShaderPropType.Texture2D:
                    return expr.Trim('"');
                default: return null;
            }
        }

        private static bool TryF(string s, out float v) =>
            float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);

        // ── Standard shader built-in properties ───────────────────────────────
        // Used as fallback when shader is "Standard" or the file doesn't exist.
        public static readonly List<ShaderPropDecl> StandardProperties = new()
        {
            new(){ Name="_MainTex",       DisplayName="Albedo (RGB)",   Type=ShaderPropType.Texture2D, DefaultValue="white"              },
            new(){ Name="_Color",         DisplayName="Tint Color",     Type=ShaderPropType.Color,     DefaultValue=new Vector4(1,1,1,1) },
            new(){ Name="_Metallic",      DisplayName="Metallic",       Type=ShaderPropType.Range,     DefaultValue=0f,   Min=0f,Max=1f  },
            new(){ Name="_Roughness",     DisplayName="Roughness",      Type=ShaderPropType.Range,     DefaultValue=0.5f, Min=0f,Max=1f  },
            new(){ Name="_EmissionColor", DisplayName="Emission Color", Type=ShaderPropType.Color,     DefaultValue=new Vector4(0,0,0,1) },
            new(){ Name="_NormalMap",     DisplayName="Normal Map",     Type=ShaderPropType.Texture2D, DefaultValue="bump"              },
        };


    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MaterialCache
    //
    //  Static load-and-cache layer.  InspectorPanel calls:
    //    MaterialCache.Get(path)        — returns loaded+populated MaterialAsset
    //    MaterialCache.Invalidate(path) — evict so next Get() reloads from disk
    // ═══════════════════════════════════════════════════════════════════════════
    public static class MaterialCache
    {
        private static readonly Dictionary<string, MaterialAsset> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the live MaterialAsset for this path, loading it on first call.
        /// The inspector and renderer both get the SAME object — property changes
        /// the inspector makes are visible to the renderer on the next frame with
        /// no invalidation or disk round-trip needed.
        /// </summary>
        public static MaterialAsset Get(string filePath)
        {
            if (_cache.TryGetValue(filePath, out var hit)) return hit;
            var asset = MaterialAsset.Load(filePath);
            if (asset.DeclaredProperties.Count == 0)
                PopulateDecls(asset);
            _cache[filePath] = asset;
            return asset;
        }

        /// <summary>
        /// Called when the shader file or the .mat file itself has changed on disk
        /// (e.g. user picked a new shader). Reloads DeclaredProperties from disk
        /// while preserving any in-memory property values the user has set.
        /// The same object reference is kept so the renderer doesn't miss it.
        /// </summary>
        public static void Invalidate(string filePath)
        {
            if (_cache.TryGetValue(filePath, out var existing))
            {
                // Reload the file to get updated ShaderPath / property defaults
                var fresh = MaterialAsset.Load(filePath);

                // Merge: keep existing in-memory property values (user edits),
                // but update ShaderPath and repopulate DeclaredProperties.
                existing.ShaderPath = fresh.ShaderPath;
                existing.DeclaredProperties.Clear();
                PopulateDecls(existing);

                // Evict textures so they reload from disk on next bind
                Material.ClearTextureCache();
            }
            else
            {
                // Not cached yet — nothing to do, Get() will load fresh.
                Material.ClearTextureCache();
            }
        }

        /// <summary>Clears the entire cache (call on project reload).</summary>
        public static void Clear()
        {
            _cache.Clear();
            Material.ClearTextureCache();
        }

        private static void PopulateDecls(MaterialAsset asset)
        {
            bool useStandard =
                string.IsNullOrEmpty(asset.ShaderPath) ||
                asset.ShaderPath.Equals("Standard", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(asset.ShaderPath);

            if (useStandard)
            {
                asset.DeclaredProperties.AddRange(MaterialAsset.StandardProperties);
                return;
            }

            try
            {
                string src = File.ReadAllText(asset.ShaderPath);
                var props = MaterialAsset.ParseShaderProperties(src);
                asset.DeclaredProperties.AddRange(
                    props.Count > 0 ? props : MaterialAsset.StandardProperties);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MaterialCache] Shader read error: {ex.Message}");
                asset.DeclaredProperties.AddRange(MaterialAsset.StandardProperties);
            }
        }
    }
}