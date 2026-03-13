using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ElintriaEngine.Core
{
    /// <summary>
    /// Per-project tag and layer registry.
    /// Loaded from and saved to Assets/ProjectSettings/TagsAndLayers.json.
    /// Access globally via TagsAndLayers.Instance.
    /// </summary>
    public class TagsAndLayers
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        private static TagsAndLayers? _instance;
        public static TagsAndLayers Instance => _instance ??= new TagsAndLayers();
        public static void LoadForProject(string projectRoot) =>
            _instance = LoadFromProject(projectRoot);

        // ── Data ──────────────────────────────────────────────────────────────
        public List<string> Tags { get; set; } = new() { "Untagged", "Player", "Enemy", "Ground", "Trigger", "Respawn", "Finish", "EditorOnly", "MainCamera", "GameController" };
        public List<string> Layers { get; set; } = new() { "Default", "TransparentFX", "Ignore Raycast", "Water", "UI", "PostProcessing", "Player", "Enemy", "Environment", "Projectile", "Pickup", "Trigger", "Debris", "Ragdoll", "NGUI", "2D Sprite" };

        // Internal path used for save
        [System.Text.Json.Serialization.JsonIgnore]
        public string SavePath { get; private set; } = "";

        private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

        // ── Load / Save ────────────────────────────────────────────────────────
        public static TagsAndLayers LoadFromProject(string projectRoot)
        {
            string path = GetPath(projectRoot);
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<TagsAndLayers>(File.ReadAllText(path), _opts);
                    if (loaded != null) { loaded.SavePath = path; return loaded; }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[TagsLayers] Load error: {ex.Message}"); }

            var defaults = new TagsAndLayers { SavePath = path };
            defaults.Save(); // write defaults to disk
            return defaults;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(SavePath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                File.WriteAllText(SavePath, JsonSerializer.Serialize(this, _opts));
            }
            catch (Exception ex) { Console.WriteLine($"[TagsLayers] Save error: {ex.Message}"); }
        }

        private static string GetPath(string projectRoot) =>
            Path.Combine(projectRoot, "Assets", "ProjectSettings", "TagsAndLayers.json");

        // ── Helpers ───────────────────────────────────────────────────────────
        public bool AddTag(string tag)
        {
            tag = tag.Trim();
            if (string.IsNullOrEmpty(tag) || Tags.Contains(tag)) return false;
            Tags.Add(tag); Save(); return true;
        }

        public bool RenameTag(string old, string @new)
        {
            int i = Tags.IndexOf(old);
            if (i < 0 || Tags.Contains(@new)) return false;
            Tags[i] = @new; Save(); return true;
        }

        public bool RemoveTag(string tag)
        {
            if (tag is "Untagged") return false; // built-in
            bool r = Tags.Remove(tag); if (r) Save(); return r;
        }

        public bool AddLayer(string layer)
        {
            layer = layer.Trim();
            if (string.IsNullOrEmpty(layer) || Layers.Contains(layer)) return false;
            Layers.Add(layer); Save(); return true;
        }

        public bool RenameLayer(string old, string @new)
        {
            int i = Layers.IndexOf(old);
            if (i < 0 || Layers.Contains(@new)) return false;
            Layers[i] = @new; Save(); return true;
        }

        public bool RemoveLayer(string layer)
        {
            if (layer is "Default") return false; // built-in
            bool r = Layers.Remove(layer); if (r) Save(); return r;
        }
    }
}