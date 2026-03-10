using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Project types
    // ═══════════════════════════════════════════════════════════════════════════
    public enum ProjectType { TwoD, ThreeD }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ProjectManifest  –  written as project.elintria in the project root
    // ═══════════════════════════════════════════════════════════════════════════
    public class ProjectManifest
    {
        public string Name { get; set; } = "MyProject";
        public string Description { get; set; } = "";
        public ProjectType Type { get; set; } = ProjectType.ThreeD;
        public string EngineVersion { get; set; } = "1.0.0";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastOpenedAt { get; set; } = DateTime.UtcNow;

        // Absolute path to the folder that contains this file
        [JsonIgnore]
        public string RootPath { get; set; } = "";

        [JsonIgnore]
        public string ManifestPath => Path.Combine(RootPath, "project.elintria");

        // Convenience thumbnails of what's inside
        [JsonIgnore]
        public int SceneCount { get; set; }
        [JsonIgnore]
        public int ScriptCount { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ProjectRegistry  –  the list of all known projects, stored in AppData
    // ═══════════════════════════════════════════════════════════════════════════
    public class ProjectRegistry
    {
        public List<ProjectRegistryEntry> Projects { get; set; } = new();
    }

    public class ProjectRegistryEntry
    {
        public string ManifestPath { get; set; } = "";
        public string Name { get; set; } = "";
        public ProjectType Type { get; set; } = ProjectType.ThreeD;
        public DateTime LastOpenedAt { get; set; } = DateTime.UtcNow;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ProjectManager  –  static API for all project operations
    // ═══════════════════════════════════════════════════════════════════════════
    public static class ProjectManager
    {
        // ── Paths ─────────────────────────────────────────────────────────────
        private static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "ElintriaEngine");

        private static string RegistryPath =>
            Path.Combine(AppDataDir, "projects.json");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // ── Registry ──────────────────────────────────────────────────────────
        public static ProjectRegistry LoadRegistry()
        {
            try
            {
                if (File.Exists(RegistryPath))
                {
                    var json = File.ReadAllText(RegistryPath);
                    return JsonSerializer.Deserialize<ProjectRegistry>(json, _jsonOpts)
                           ?? new ProjectRegistry();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PM] Registry load error: {ex.Message}");
            }
            return new ProjectRegistry();
        }

        private static void SaveRegistry(ProjectRegistry reg)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(RegistryPath, JsonSerializer.Serialize(reg, _jsonOpts));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PM] Registry save error: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns all projects in the registry whose manifest files actually exist,
        /// ordered by most-recently-opened first.
        /// </summary>
        public static List<ProjectManifest> GetRecentProjects()
        {
            var reg = LoadRegistry();
            var results = new List<ProjectManifest>();

            foreach (var entry in reg.Projects)
            {
                if (!File.Exists(entry.ManifestPath)) continue;

                var manifest = LoadManifest(entry.ManifestPath);
                if (manifest == null) continue;

                // Populate runtime-only stats
                RefreshStats(manifest);
                results.Add(manifest);
            }

            results.Sort((a, b) => b.LastOpenedAt.CompareTo(a.LastOpenedAt));
            return results;
        }

        private static void RegisterProject(ProjectManifest manifest)
        {
            var reg = LoadRegistry();

            // Remove stale entry for the same path (if any)
            reg.Projects.RemoveAll(e => e.ManifestPath == manifest.ManifestPath);

            reg.Projects.Insert(0, new ProjectRegistryEntry
            {
                ManifestPath = manifest.ManifestPath,
                Name = manifest.Name,
                Type = manifest.Type,
                LastOpenedAt = manifest.LastOpenedAt,
            });

            SaveRegistry(reg);
        }

        private static void UnregisterProject(string manifestPath)
        {
            var reg = LoadRegistry();
            reg.Projects.RemoveAll(e => e.ManifestPath == manifestPath);
            SaveRegistry(reg);
        }

        // ── Manifest I/O ──────────────────────────────────────────────────────
        public static ProjectManifest? LoadManifest(string manifestPath)
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<ProjectManifest>(json, _jsonOpts);
                if (manifest == null) return null;
                manifest.RootPath = Path.GetDirectoryName(manifestPath)!;
                return manifest;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PM] Manifest load error: {ex.Message}");
                return null;
            }
        }

        public static void SaveManifest(ProjectManifest manifest)
        {
            try
            {
                var json = JsonSerializer.Serialize(manifest, _jsonOpts);
                File.WriteAllText(manifest.ManifestPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PM] Manifest save error: {ex.Message}");
            }
        }

        // ── Create ────────────────────────────────────────────────────────────
        /// <summary>
        /// Creates a new project at <paramref name="rootPath"/>.
        /// Returns the manifest on success, null on failure.
        /// </summary>
        public static ProjectManifest? CreateProject(string name, string rootPath,
                                                     ProjectType type,
                                                     string description = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath))
                    return null;

                // Create the project root directory
                Directory.CreateDirectory(rootPath);

                // Create the standard folder structure
                foreach (var sub in new[]
                {
                    "Assets",
                    Path.Combine("Assets", "Scenes"),
                    Path.Combine("Assets", "Scripts"),
                    Path.Combine("Assets", "Textures"),
                    Path.Combine("Assets", "Materials"),
                    Path.Combine("Assets", "Models"),
                    Path.Combine("Assets", "Audio"),
                    Path.Combine("Assets", "Prefabs"),
                    Path.Combine("Assets", "Fonts"),
                    ".elintria",
                })
                {
                    Directory.CreateDirectory(Path.Combine(rootPath, sub));
                }

                // Write a README inside Assets
                File.WriteAllText(Path.Combine(rootPath, "Assets", "README.txt"),
                    $"Elintria Engine project: {name}\n" +
                    $"Type: {(type == ProjectType.TwoD ? "2D" : "3D")}\n" +
                    $"Created: {DateTime.UtcNow:O}\n");

                // Write a default gitignore
                File.WriteAllText(Path.Combine(rootPath, ".gitignore"),
                    ".elintria/\nBuild/\n*.user\n");

                // Write the manifest
                var manifest = new ProjectManifest
                {
                    Name = name,
                    Description = description,
                    Type = type,
                    RootPath = rootPath,
                    CreatedAt = DateTime.UtcNow,
                    LastOpenedAt = DateTime.UtcNow,
                    EngineVersion = "1.0.0",
                };
                SaveManifest(manifest);
                RegisterProject(manifest);

                Console.WriteLine($"[PM] Created project '{name}' at {rootPath}");
                return manifest;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PM] Create error: {ex.Message}");
                return null;
            }
        }

        // ── Open ──────────────────────────────────────────────────────────────
        /// <summary>
        /// Marks a project as recently opened and returns its manifest.
        /// </summary>
        public static ProjectManifest? OpenProject(string manifestPath)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null) return null;

            manifest.LastOpenedAt = DateTime.UtcNow;
            SaveManifest(manifest);
            RegisterProject(manifest);
            return manifest;
        }

        // ── Delete ────────────────────────────────────────────────────────────
        /// <summary>
        /// Permanently deletes a project folder and removes it from the registry.
        /// Returns true on success.
        /// </summary>
        public static bool DeleteProject(ProjectManifest manifest, bool deleteFiles = true)
        {
            try
            {
                UnregisterProject(manifest.ManifestPath);

                if (deleteFiles && Directory.Exists(manifest.RootPath))
                    Directory.Delete(manifest.RootPath, recursive: true);

                Console.WriteLine($"[PM] Deleted project '{manifest.Name}'");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PM] Delete error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes a project from the launcher list without deleting files.
        /// </summary>
        public static void RemoveFromRegistry(ProjectManifest manifest)
        {
            UnregisterProject(manifest.ManifestPath);
        }

        // ── Import existing project ───────────────────────────────────────────
        /// <summary>
        /// Adds an existing project (by its manifest path) to the registry.
        /// </summary>
        public static ProjectManifest? ImportProject(string manifestPath)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null) return null;
            RegisterProject(manifest);
            return manifest;
        }

        // ── Utilities ─────────────────────────────────────────────────────────
        private static void RefreshStats(ProjectManifest manifest)
        {
            try
            {
                string assets = Path.Combine(manifest.RootPath, "Assets");
                if (Directory.Exists(assets))
                {
                    manifest.SceneCount = Directory.GetFiles(assets, "*.scene",
                                              SearchOption.AllDirectories).Length;
                    manifest.ScriptCount = Directory.GetFiles(assets, "*.cs",
                                              SearchOption.AllDirectories).Length;
                }
            }
            catch { /* best-effort */ }
        }

        public static string DefaultProjectsDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "ElintriaProjects");
    }
}