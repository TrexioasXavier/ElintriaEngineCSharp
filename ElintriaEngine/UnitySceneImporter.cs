using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;
using ElintriaEngine.Core;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  UnitySceneImporter
    //
    //  Parses a Unity .unity (YAML) scene file and produces an Elintria Scene.
    //
    //  Supported Unity object types:
    //    GameObject    → Core.GameObject  (name, active, tag, layer)
    //    Transform     → Transform         (position, rotation as euler, scale)
    //    MeshFilter    → Core.MeshFilter   (mesh name from built-in GUID table)
    //    MeshRenderer  → Core.MeshRenderer (castShadows, receiveShadows)
    //    Camera        → Core.Camera       (fov, near, far, ortho, orthoSize)
    //    Light         → Core.DirectionalLight / SpotLight / legacy Light
    //    AudioListener → Core.AudioListener
    //    Rigidbody     → Core.Rigidbody3D
    //    BoxCollider   → Core.BoxCollider
    //    SphereCollider→ Core.SphereCollider
    //    CapsuleCollider→Core.CapsuleCollider
    //    MeshCollider  → Core.MeshCollider
    //
    //  Usage:
    //    var scene = UnitySceneImporter.Import("path/to/MyScene.unity");
    //    // or from an already-open stream:
    //    var scene = UnitySceneImporter.Import(text, displayName: "MyScene");
    //
    //  Limitations (by design – mirrors only what Elintria can represent):
    //    • Prefab instances are partially supported: the engine spawns a plain
    //      GameObject with its name and transform but no prefab-resolved components.
    //    • Material GUIDs are not resolved (no .meta files available at import time).
    //    • Scripts are recorded as DynamicScript placeholders with the mono-class
    //      name so they can be resolved when the project's GameScripts.dll is compiled.
    //    • Physics materials, lightmap data, and render-layer masks are ignored.
    // ═══════════════════════════════════════════════════════════════════════════
    public static class UnitySceneImporter
    {
        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Load and parse a .unity file from disk.</summary>
        public static Scene Import(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Unity scene not found: {filePath}");

            string text = File.ReadAllText(filePath);
            string name = Path.GetFileNameWithoutExtension(filePath);
            return Import(text, name, filePath);
        }

        /// <summary>Parse a .unity YAML string that has already been read into memory.</summary>
        public static Scene Import(string yamlText, string displayName = "ImportedScene",
                                   string filePath = "")
        {
            var parser = new UnityYamlParser(yamlText);
            var unityObjects = parser.Parse();
            return BuildScene(unityObjects, displayName, filePath);
        }

        // ── Scene builder ─────────────────────────────────────────────────────

        private static Scene BuildScene(List<UnityObject> objects,
                                        string name, string filePath)
        {
            var scene = new Scene { Name = name, FilePath = filePath };

            // Index all objects by their fileID so we can resolve references
            var byId = new Dictionary<long, UnityObject>();
            foreach (var obj in objects)
                byId[obj.FileId] = obj;

            // First pass: create all GameObjects
            var goMap = new Dictionary<long, GameObject>(); // fileID of GO → Elintria GO

            // Unity stores GameObjects and their Transforms as separate objects.
            // We need the Transform's fileID to find the GO's world position.
            // Map: Transform.fileID → owning GO.fileID
            var transformToGo = new Dictionary<long, long>();

            foreach (var obj in objects)
            {
                if (obj.Type != "GameObject") continue;

                string goName = obj.GetString("m_Name", "GameObject");
                bool active = obj.GetInt("m_IsActive", 1) != 0;
                string tag = MapTag(obj.GetString("m_TagString", "Untagged"));
                int layer = obj.GetInt("m_Layer", 0);

                var go = new GameObject(goName)
                {
                    ActiveSelf = active,
                    Tag = tag,
                    Layer = LayerIndexToName(layer),
                };

                goMap[obj.FileId] = go;

                // Record which Transform belongs to this GO so we can apply it later
                // (the Transform has a back-ref m_GameObject → GO fileID)
            }

            // Second pass: apply Transforms and build parent–child hierarchy
            foreach (var obj in objects)
            {
                if (obj.Type != "Transform") continue;

                long goFileId = obj.GetFileRef("m_GameObject");
                if (goFileId == 0 || !goMap.TryGetValue(goFileId, out var go)) continue;

                transformToGo[obj.FileId] = goFileId;

                // Position / rotation / scale
                go.Transform.LocalPosition = obj.GetVector3("m_LocalPosition");
                go.Transform.LocalScale = obj.GetVector3("m_LocalScale", Vector3.One);

                // Unity stores rotation as a quaternion; convert to Euler angles
                var q = obj.GetQuaternion("m_LocalRotation");
                go.Transform.LocalEulerAngles = QuaternionToEuler(q);
            }

            // Third pass: wire up parent–child (Transform m_Father → parent Transform)
            foreach (var obj in objects)
            {
                if (obj.Type != "Transform") continue;

                long goFileId = obj.GetFileRef("m_GameObject");
                long parentTrId = obj.GetFileRef("m_Father");

                if (goFileId == 0 || !goMap.TryGetValue(goFileId, out var go)) continue;
                if (parentTrId == 0) continue; // root object

                if (transformToGo.TryGetValue(parentTrId, out long parentGoId)
                    && goMap.TryGetValue(parentGoId, out var parentGo))
                {
                    go.SetParent(parentGo);
                }
            }

            // Fourth pass: attach components
            foreach (var obj in objects)
            {
                long goFileId = obj.GetFileRef("m_GameObject");
                if (goFileId == 0) continue;
                if (!goMap.TryGetValue(goFileId, out var go)) continue;

                AttachComponent(obj, go);
            }

            // Fifth pass: PrefabInstance → create stand-in GO
            foreach (var obj in objects)
            {
                if (obj.Type != "PrefabInstance") continue;
                string prefabName = obj.GetModificationString("m_Name", "Prefab");
                var prefabGo = new GameObject(prefabName) { ActiveSelf = true };

                // Apply overridden transform values if present in modifications
                prefabGo.Transform.LocalPosition = obj.GetModificationVector3(
                    "m_LocalPosition", Vector3.Zero);
                prefabGo.Transform.LocalScale = obj.GetModificationVector3(
                    "m_LocalScale", Vector3.One);
                var rotQ = obj.GetModificationQuaternion("m_LocalRotation");
                prefabGo.Transform.LocalEulerAngles = QuaternionToEuler(rotQ);

                goMap[obj.FileId] = prefabGo;
            }

            // Collect root GOs (no parent) and add to scene
            foreach (var kv in goMap)
            {
                var go = kv.Value;
                if (go.Parent == null)
                    scene.AddGameObject(go);
            }

            Console.WriteLine($"[UnityImport] Imported scene '{name}': " +
                              $"{goMap.Count} objects, {scene.RootObjects.Count} roots.");
            return scene;
        }

        // ── Component attachment ──────────────────────────────────────────────

        private static void AttachComponent(UnityObject obj, GameObject go)
        {
            switch (obj.Type)
            {
                // ── Camera ────────────────────────────────────────────────────
                case "Camera":
                    {
                        if (go.GetComponent<Camera>() != null) break;
                        var cam = go.AddComponent<Camera>();
                        cam.FieldOfView = obj.GetFloat("field of view", 60f);
                        cam.NearClip = obj.GetFloat("near clip plane", 0.3f);
                        cam.FarClip = obj.GetFloat("far clip plane", 1000f);
                        cam.IsOrthographic = obj.GetInt("orthographic", 0) != 0;
                        cam.OrthoSize = obj.GetFloat("orthographic size", 5f);

                        // Background colour
                        var bg = obj.GetColor("m_BackGroundColor");
                        cam.BackgroundR = bg.X;
                        cam.BackgroundG = bg.Y;
                        cam.BackgroundB = bg.Z;
                        cam.Enabled = obj.GetInt("m_Enabled", 1) != 0;
                        break;
                    }

                // ── Light ─────────────────────────────────────────────────────
                case "Light":
                    {
                        int type = obj.GetInt("m_Type", 1);
                        float intensity = obj.GetFloat("m_Intensity", 1f);
                        var color = obj.GetColor("m_Color", Vector4.One);
                        bool enabled = obj.GetInt("m_Enabled", 1) != 0;

                        switch (type)
                        {
                            case 1: // Directional
                                {
                                    if (go.GetComponent<DirectionalLight>() != null) break;
                                    var dl = go.AddComponent<DirectionalLight>();
                                    dl.ColorR = color.X;
                                    dl.ColorG = color.Y;
                                    dl.ColorB = color.Z;
                                    dl.Intensity = intensity;
                                    dl.Enabled = enabled;
                                    break;
                                }
                            case 2: // Point  (no dedicated Elintria type — use legacy Light)
                                {
                                    var l = go.AddComponent<Light>();
                                    l.LightType = "Point";
                                    l.ColorR = color.X;
                                    l.ColorG = color.Y;
                                    l.ColorB = color.Z;
                                    l.Intensity = intensity;
                                    l.Range = obj.GetFloat("m_Range", 10f);
                                    l.Enabled = enabled;
                                    break;
                                }
                            case 0: // Spot
                                {
                                    if (go.GetComponent<SpotLight>() != null) break;
                                    var sl = go.AddComponent<SpotLight>();
                                    sl.ColorR = color.X;
                                    sl.ColorG = color.Y;
                                    sl.ColorB = color.Z;
                                    sl.Intensity = intensity;
                                    sl.Range = obj.GetFloat("m_Range", 10f);
                                    sl.SpotAngle = obj.GetFloat("m_SpotAngle", 30f);
                                    sl.Enabled = enabled;
                                    break;
                                }
                        }
                        break;
                    }

                // ── MeshFilter ────────────────────────────────────────────────
                case "MeshFilter":
                    {
                        if (go.GetComponent<MeshFilter>() != null) break;
                        var mf = go.AddComponent<MeshFilter>();

                        // Unity stores mesh as a {fileID, guid, type} reference.
                        // Map well-known built-in GUIDs → primitive names.
                        string meshGuid = obj.GetRefGuid("m_Mesh");
                        int meshId = (int)obj.GetRefFileId("m_Mesh");
                        mf.MeshName = ResolveBuiltinMesh(meshId, meshGuid);
                        mf.Enabled = obj.GetInt("m_Enabled", 1) != 0;
                        break;
                    }

                // ── MeshRenderer ──────────────────────────────────────────────
                case "MeshRenderer":
                    {
                        if (go.GetComponent<MeshRenderer>() != null) break;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.CastShadows = obj.GetInt("m_CastShadows", 1) != 0;
                        mr.ReceiveShadows = obj.GetInt("m_ReceiveShadows", 1) != 0;
                        mr.Enabled = obj.GetInt("m_Enabled", 1) != 0;
                        break;
                    }

                // ── AudioListener ─────────────────────────────────────────────
                case "AudioListener":
                    {
                        if (go.GetComponent<AudioListener>() != null) break;
                        go.AddComponent<AudioListener>();
                        break;
                    }

                // ── Rigidbody ─────────────────────────────────────────────────
                case "Rigidbody":
                    {
                        if (go.GetComponent<Rigidbody3D>() != null) break;
                        var rb = go.AddComponent<Rigidbody3D>();
                        rb.Mass = obj.GetFloat("m_Mass", 1f);
                        rb.Drag = obj.GetFloat("m_Drag", 0f);
                        rb.AngularDrag = obj.GetFloat("m_AngularDrag", 0.05f);
                        rb.UseGravity = obj.GetInt("m_UseGravity", 1) != 0;
                        rb.IsKinematic = obj.GetInt("m_IsKinematic", 0) != 0;
                        rb.Enabled = obj.GetInt("m_Enabled", 1) != 0;

                        // Freeze constraints — Unity uses a bitmask: bit0=px,1=py,2=pz,3=rx,4=ry,5=rz
                        int constraints = obj.GetInt("m_Constraints", 0);
                        rb.FreezePositionX = (constraints & (1 << 1)) != 0;
                        rb.FreezePositionY = (constraints & (1 << 2)) != 0;
                        rb.FreezePositionZ = (constraints & (1 << 3)) != 0;
                        rb.FreezeRotationX = (constraints & (1 << 4)) != 0;
                        rb.FreezeRotationY = (constraints & (1 << 5)) != 0;
                        rb.FreezeRotationZ = (constraints & (1 << 6)) != 0;
                        break;
                    }

                // ── BoxCollider ───────────────────────────────────────────────
                case "BoxCollider":
                    {
                        if (go.GetComponent<BoxCollider>() != null) break;
                        var bc = go.AddComponent<BoxCollider>();
                        bc.Center = obj.GetVector3("m_Center");
                        bc.Size = obj.GetVector3("m_Size", Vector3.One);
                        bc.IsTrigger = obj.GetInt("m_IsTrigger", 0) != 0;
                        bc.Enabled = obj.GetInt("m_Enabled", 1) != 0;
                        break;
                    }

                // ── SphereCollider ────────────────────────────────────────────
                case "SphereCollider":
                    {
                        if (go.GetComponent<SphereCollider>() != null) break;
                        var sc = go.AddComponent<SphereCollider>();
                        sc.Center = obj.GetVector3("m_Center");
                        sc.Radius = obj.GetFloat("m_Radius", 0.5f);
                        sc.IsTrigger = obj.GetInt("m_IsTrigger", 0) != 0;
                        sc.Enabled = obj.GetInt("m_Enabled", 1) != 0;
                        break;
                    }

                // ── CapsuleCollider ───────────────────────────────────────────
                case "CapsuleCollider":
                    {
                        if (go.GetComponent<CapsuleCollider>() != null) break;
                        var cc = go.AddComponent<CapsuleCollider>();
                        cc.Center = obj.GetVector3("m_Center");
                        cc.Radius = obj.GetFloat("m_Radius", 0.5f);
                        cc.Height = obj.GetFloat("m_Height", 2f);
                        cc.Direction = obj.GetInt("m_Direction", 1);
                        cc.IsTrigger = obj.GetInt("m_IsTrigger", 0) != 0;
                        cc.Enabled = obj.GetInt("m_Enabled", 1) != 0;
                        break;
                    }

                // ── MeshCollider ──────────────────────────────────────────────
                case "MeshCollider":
                    {
                        if (go.GetComponent<MeshCollider>() != null) break;
                        var mc = go.AddComponent<MeshCollider>();
                        mc.Convex = obj.GetInt("m_Convex", 0) != 0;
                        mc.IsTrigger = obj.GetInt("m_IsTrigger", 0) != 0;
                        mc.Enabled = obj.GetInt("m_Enabled", 1) != 0;
                        break;
                    }

                // ── MonoBehaviour (user scripts) ──────────────────────────────
                // Unity serialises every script as a MonoBehaviour. We create a
                // DynamicScript placeholder so the inspector can show the fields
                // once the project's GameScripts.dll is compiled.
                case "MonoBehaviour":
                    {
                        // m_Script → {fileID, guid, type}  — the class name is NOT stored
                        // in the YAML body; it comes from the .cs filename referenced by
                        // the guid. We store the guid as ScriptTypeName so a future
                        // meta-file resolver could upgrade it to the real type name.
                        string scriptGuid = obj.GetRefGuid("m_Script");
                        if (string.IsNullOrEmpty(scriptGuid)) break;

                        var ds = new DynamicScript
                        {
                            ScriptTypeName = $"Script_{scriptGuid[..8]}",
                            Enabled = obj.GetInt("m_Enabled", 1) != 0,
                            GameObject = go,
                        };

                        // Copy every non-Unity-internal field into FieldValues so the
                        // user doesn't lose their serialised data.
                        foreach (var kv in obj.Fields)
                        {
                            if (kv.Key.StartsWith("m_")) continue; // skip Unity internals
                            ds.FieldValues[kv.Key] = kv.Value;
                        }

                        go.Components.Add(ds);
                        break;
                    }

                    // All other Unity component types are silently ignored.
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Maps Unity's built-in mesh fileIDs (the well-known IDs from
        /// 0000000000000000e000000000000000) to Elintria primitive names.
        /// </summary>
        private static string ResolveBuiltinMesh(int fileId, string guid)
        {
            // Unity built-in mesh library guid = 0000000000000000e000000000000000
            if (guid == "0000000000000000e000000000000000")
            {
                return fileId switch
                {
                    10202 => "Cube",
                    10206 => "Sphere",
                    10209 => "Plane",
                    10208 => "Capsule",
                    10207 => "Cylinder",
                    10210 => "Quad",
                    _ => "Cube",
                };
            }
            // External mesh — use the guid as a key; the user can re-assign in the inspector
            return !string.IsNullOrEmpty(guid) ? $"mesh_{guid[..8]}" : "Cube";
        }

        private static string MapTag(string unityTag) => unityTag switch
        {
            "Untagged" => "Untagged",
            "Respawn" => "Respawn",
            "Finish" => "Finish",
            "EditorOnly" => "EditorOnly",
            "MainCamera" => "MainCamera",
            "Player" => "Player",
            "GameController" => "GameController",
            _ => unityTag,
        };

        private static string LayerIndexToName(int index) => index switch
        {
            0 => "Default",
            1 => "TransparentFX",
            2 => "Ignore Raycast",
            4 => "Water",
            5 => "UI",
            _ => "Default",
        };

        /// <summary>
        /// Converts a Unity quaternion to Euler angles in degrees.
        /// Uses the same convention as Unity (YXZ extrinsic = ZXY intrinsic).
        /// </summary>
        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            // Normalise to guard against denormalised input
            q.Normalize();

            // Unity uses left-handed Y-up. The intrinsic order is Y → X → Z (YXZ).
            // Extract Tait-Bryan angles: pitch (X), yaw (Y), roll (Z)
            float sinXCosY = 2f * (q.W * q.X + q.Y * q.Z);
            float cosXCosY = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float pitch = MathF.Atan2(sinXCosY, cosXCosY);

            float sinY = 2f * (q.W * q.Y - q.Z * q.X);
            sinY = Math.Clamp(sinY, -1f, 1f);
            float yaw = MathF.Asin(sinY);

            float sinZCosY = 2f * (q.W * q.Z + q.X * q.Y);
            float cosZCosY = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float roll = MathF.Atan2(sinZCosY, cosZCosY);

            return new Vector3(
                MathHelper.RadiansToDegrees(pitch),
                MathHelper.RadiansToDegrees(yaw),
                MathHelper.RadiansToDegrees(roll));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UnityObject  —  one deserialized YAML document block
    // ═══════════════════════════════════════════════════════════════════════════
    internal class UnityObject
    {
        public long FileId { get; set; }
        public string Type { get; set; } = "";

        // Flat key→value bag for all top-level fields in this block.
        // Nested objects (like m_LocalPosition: {x:…,y:…,z:…}) are flattened as
        //   "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z"
        // Object references are stored as:
        //   "m_GameObject.__fileID"    = long
        //   "m_GameObject.__guid"      = string
        public readonly Dictionary<string, object?> Fields = new(StringComparer.Ordinal);

        // Modification overrides from PrefabInstance blocks
        public readonly List<(string Path, string Value)> Modifications = new();

        // ── Typed accessors ───────────────────────────────────────────────────

        public string GetString(string key, string def = "")
        {
            if (Fields.TryGetValue(key, out var v) && v is string s) return s;
            return def;
        }

        public int GetInt(string key, int def = 0)
        {
            if (Fields.TryGetValue(key, out var v))
            {
                if (v is int i) return i;
                if (v is string s && int.TryParse(s, out int p)) return p;
                if (v is double d) return (int)d;
            }
            return def;
        }

        public float GetFloat(string key, float def = 0f)
        {
            if (Fields.TryGetValue(key, out var v))
            {
                if (v is float f) return f;
                if (v is double d) return (float)d;
                if (v is string s && float.TryParse(s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float p)) return p;
            }
            return def;
        }

        public Vector3 GetVector3(string key, Vector3 def = default)
        {
            float x = GetFloat(key + ".x", def.X);
            float y = GetFloat(key + ".y", def.Y);
            float z = GetFloat(key + ".z", def.Z);
            return new Vector3(x, y, z);
        }

        public Vector4 GetColor(string key, Vector4 def = default)
        {
            float r = GetFloat(key + ".r", def.X);
            float g = GetFloat(key + ".g", def.Y);
            float b = GetFloat(key + ".b", def.Z);
            float a = GetFloat(key + ".a", def.W);
            return new Vector4(r, g, b, a);
        }

        public Quaternion GetQuaternion(string key)
        {
            float x = GetFloat(key + ".x", 0f);
            float y = GetFloat(key + ".y", 0f);
            float z = GetFloat(key + ".z", 0f);
            float w = GetFloat(key + ".w", 1f);
            return new Quaternion(x, y, z, w);
        }

        public long GetFileRef(string key)
        {
            if (Fields.TryGetValue(key + ".__fileID", out var v))
            {
                if (v is long l) return l;
                if (v is string s && long.TryParse(s, out long p)) return p;
                if (v is double d) return (long)d;
            }
            return 0;
        }

        public string GetRefGuid(string key)
        {
            if (Fields.TryGetValue(key + ".__guid", out var v) && v is string s)
                return s;
            return "";
        }

        public long GetRefFileId(string key)
        {
            if (Fields.TryGetValue(key + ".__fileID", out var v))
            {
                if (v is long l) return l;
                if (v is string s && long.TryParse(s, out long p)) return p;
            }
            return 0;
        }

        // ── PrefabInstance modification helpers ───────────────────────────────

        public string GetModificationString(string propertyPath, string def = "")
        {
            foreach (var (path, val) in Modifications)
                if (path == propertyPath) return val;
            return def;
        }

        public Vector3 GetModificationVector3(string basePropertyPath, Vector3 def)
        {
            float x = def.X, y = def.Y, z = def.Z;
            foreach (var (path, val) in Modifications)
            {
                if (path == basePropertyPath + ".x"
                    && float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fx)) x = fx;
                if (path == basePropertyPath + ".y"
                    && float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fy)) y = fy;
                if (path == basePropertyPath + ".z"
                    && float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fz)) z = fz;
            }
            return new Vector3(x, y, z);
        }

        public Quaternion GetModificationQuaternion(string basePropertyPath)
        {
            float x = 0, y = 0, z = 0, w = 1;
            foreach (var (path, val) in Modifications)
            {
                if (path == basePropertyPath + ".x"
                    && float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fx)) x = fx;
                if (path == basePropertyPath + ".y"
                    && float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fy)) y = fy;
                if (path == basePropertyPath + ".z"
                    && float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fz)) z = fz;
                if (path == basePropertyPath + ".w"
                    && float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fw)) w = fw;
            }
            return new Quaternion(x, y, z, w);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UnityYamlParser
    //
    //  Unity .unity files use a superset of YAML:
    //    • Multi-document (--- separators)
    //    • Custom tags (!u!NNN)
    //    • fileID anchors (&NNN)
    //    • Object references: {fileID: NNN}  or  {fileID: NNN, guid: XXXX, type: N}
    //    • Inline maps:  {x: 0, y: 0, z: 0}
    //    • Sequences:    - component: {fileID: NNN}
    //
    //  We do NOT use a full YAML library to keep the dependency count zero.
    //  The parser handles exactly the constructs Unity uses in scene files.
    // ═══════════════════════════════════════════════════════════════════════════
    internal class UnityYamlParser
    {
        private readonly string[] _lines;

        // Map Unity class-type IDs (from !u!NNN) to friendly type names
        private static readonly Dictionary<int, string> TypeIdToName = new()
        {
            {   1, "GameObject"    },
            {   4, "Transform"     },
            {  20, "Camera"        },
            {  23, "MeshRenderer"  },
            {  29, "OcclusionCullingSettings" },
            {  33, "MeshFilter"    },
            {  54, "Rigidbody"     },
            {  60, "PolygonCollider2D" },
            {  64, "MeshCollider"  },
            {  65, "BoxCollider"   },
            {  82, "AudioSource"   },
            {  81, "AudioListener" },
            { 104, "RenderSettings"},
            { 108, "Light"         },
            { 114, "MonoBehaviour" },
            { 136, "SphereCollider"},
            { 143, "CapsuleCollider"},
            { 157, "LightmapSettings"},
            { 181, "AudioClip"     },
            { 196, "NavMeshSettings"},
            { 1001,"PrefabInstance"},
        };

        public UnityYamlParser(string text)
        {
            // Normalise line endings
            _lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }

        public List<UnityObject> Parse()
        {
            var result = new List<UnityObject>();
            int i = 0;

            while (i < _lines.Length)
            {
                string line = _lines[i];

                // Look for document start: --- !u!NNN &NNN
                if (!line.StartsWith("---")) { i++; continue; }

                // Try to parse the header line
                var (typeId, fileId) = ParseDocHeader(line);
                if (typeId == 0 || fileId == 0) { i++; continue; }

                // Determine friendly type name
                string typeName = TypeIdToName.TryGetValue(typeId, out var tn)
                    ? tn
                    : $"Unknown{typeId}";

                i++; // move past "---" line

                // Collect all lines belonging to this document block
                // (until the next "---" or EOF)
                int blockStart = i;
                while (i < _lines.Length && !_lines[i].StartsWith("---"))
                    i++;

                var blockLines = _lines[blockStart..i];
                var obj = ParseBlock(typeName, fileId, blockLines);
                result.Add(obj);
            }

            return result;
        }

        // ── Header: --- !u!NNN &NNN ───────────────────────────────────────────

        private static (int typeId, long fileId) ParseDocHeader(string line)
        {
            // e.g.  --- !u!1 &705507993
            //       --- !u!1001 &2092089009
            var m = Regex.Match(line, @"---\s+!u!(\d+)\s+&(\d+)");
            if (!m.Success) return (0, 0);
            int typeId = int.Parse(m.Groups[1].Value);
            long fileId = long.Parse(m.Groups[2].Value);
            return (typeId, fileId);
        }

        // ── Block parser ──────────────────────────────────────────────────────

        private static UnityObject ParseBlock(string typeName, long fileId,
                                              string[] lines)
        {
            var obj = new UnityObject { Type = typeName, FileId = fileId };

            // The first non-empty line is the type name line (e.g. "GameObject:")
            // We skip it and process the indented key-value pairs below it.
            int start = 0;
            while (start < lines.Length && lines[start].Trim().Length == 0) start++;
            if (start < lines.Length && !lines[start].StartsWith(" ")
                && !lines[start].StartsWith("\t") && lines[start].TrimEnd().EndsWith(":"))
                start++;

            // Flatten the YAML block into key → value pairs
            // We track the current key-prefix stack by indentation level
            var indentStack = new Stack<(int indent, string prefix)>();
            indentStack.Push((0, ""));

            bool inModifications = false;
            bool inModTarget = false;
            string? pendingPropPath = null;

            for (int li = start; li < lines.Length; li++)
            {
                string raw = lines[li];
                if (raw.Trim().Length == 0) continue;

                int indent = CountLeadingSpaces(raw);
                string trimmed = raw.TrimStart();

                // Pop the indent stack to the current level
                while (indentStack.Count > 1 && indentStack.Peek().indent >= indent)
                    indentStack.Pop();

                string prefix = indentStack.Peek().prefix;

                // ── Sequence item "  - key: value" ──────────────────────────
                if (trimmed.StartsWith("- "))
                {
                    trimmed = trimmed[2..].TrimStart();

                    // Detect PrefabInstance modification entries
                    if (prefix.StartsWith("m_Modification") && trimmed.StartsWith("target:"))
                    {
                        inModTarget = true;
                        pendingPropPath = null;
                        continue;
                    }
                    if (inModTarget && trimmed.StartsWith("propertyPath:"))
                    {
                        pendingPropPath = ExtractValue(trimmed).Trim();
                        continue;
                    }
                    if (inModTarget && trimmed.StartsWith("value:") && pendingPropPath != null)
                    {
                        string val = ExtractValue(trimmed).Trim();
                        obj.Modifications.Add((pendingPropPath, val));
                        pendingPropPath = null;
                        continue;
                    }
                    if (inModTarget && !trimmed.StartsWith("propertyPath:")
                        && !trimmed.StartsWith("value:")
                        && !trimmed.StartsWith("objectReference:"))
                    {
                        inModTarget = false;
                    }

                    // Inline object reference sequence entry e.g. "- component: {fileID: 123}"
                    if (trimmed.Contains(":"))
                    {
                        ParseKeyValue(obj, prefix, trimmed, indent, indentStack);
                    }
                    continue;
                }

                // ── Regular "key: value" ──────────────────────────────────────
                if (trimmed.Contains(":"))
                {
                    // Track when we enter m_Modification block
                    string k = trimmed.Split(':')[0].Trim();
                    if (k == "m_Modification" || k == "m_Modifications") inModifications = true;

                    ParseKeyValue(obj, prefix, trimmed, indent, indentStack);
                }
            }

            return obj;
        }

        private static void ParseKeyValue(UnityObject obj, string prefix,
                                          string trimmed, int indent,
                                          Stack<(int indent, string prefix)> indentStack)
        {
            int colon = trimmed.IndexOf(':');
            if (colon < 0) return;

            string key = trimmed[..colon].Trim();
            string rest = trimmed[(colon + 1)..].Trim();
            string fullKey = prefix.Length > 0 ? prefix + "." + key : key;

            if (rest.Length == 0)
            {
                // Value is on subsequent lines (sub-object) — push to stack
                indentStack.Push((indent + 2, fullKey));
                return;
            }

            // ── Inline map: {fileID: 123}  or  {x: 0.1, y: 0.2, z: 0.3} ────
            if (rest.StartsWith("{") && rest.EndsWith("}"))
            {
                ParseInlineMap(obj, fullKey, rest);
                return;
            }

            // ── Plain scalar ──────────────────────────────────────────────────
            StoreScalar(obj, fullKey, rest);
        }

        private static void ParseInlineMap(UnityObject obj, string keyPrefix, string mapStr)
        {
            // Strip { and }
            string inner = mapStr[1..^1].Trim();
            if (inner.Length == 0) return;

            // Split on commas that are not inside nested braces
            var parts = SplitTopLevel(inner, ',');
            foreach (var part in parts)
            {
                int c = part.IndexOf(':');
                if (c < 0) continue;
                string k = part[..c].Trim();
                string v = part[(c + 1)..].Trim();
                string fk = keyPrefix.Length > 0 ? keyPrefix + "." + k : k;

                // Nested inline map?
                if (v.StartsWith("{") && v.EndsWith("}"))
                    ParseInlineMap(obj, fk, v);
                else
                    StoreScalar(obj, fk, v);
            }
        }

        private static void StoreScalar(UnityObject obj, string key, string value)
        {
            // Remove inline comment
            int hashIdx = value.IndexOf(" #");
            if (hashIdx >= 0) value = value[..hashIdx].Trim();

            // Try numeric
            if (long.TryParse(value, out long lv))
            { obj.Fields[key] = lv; return; }

            if (double.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double dv))
            { obj.Fields[key] = dv; return; }

            obj.Fields[key] = value;
        }

        private static string ExtractValue(string line)
        {
            int c = line.IndexOf(':');
            return c < 0 ? "" : line[(c + 1)..].Trim();
        }

        private static int CountLeadingSpaces(string s)
        {
            int n = 0;
            foreach (char c in s)
            {
                if (c == ' ') n++;
                else if (c == '\t') n += 2;
                else break;
            }
            return n;
        }

        /// <summary>Splits a string on <paramref name="sep"/> ignoring separators inside braces.</summary>
        private static List<string> SplitTopLevel(string s, char sep)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == sep && depth == 0)
                {
                    parts.Add(s[start..i].Trim());
                    start = i + 1;
                }
            }
            if (start < s.Length) parts.Add(s[start..].Trim());
            return parts;
        }
    }
}