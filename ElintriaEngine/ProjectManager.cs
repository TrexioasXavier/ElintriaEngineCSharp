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
    //  EngineSettings  – user prefs stored in AppData/ElintriaEngine/settings.json
    // ═══════════════════════════════════════════════════════════════════════════
    public class EngineSettings
    {
        /// <summary>Root folder under which every new project lives in its own subfolder.</summary>
        public string DefaultProjectsDirectory { get; set; } = "";

        /// <summary>Auto-scan the default folder each time the launcher opens.</summary>
        public bool AutoScanOnStartup { get; set; } = true;

        public int WindowWidth { get; set; } = 1600;
        public int WindowHeight { get; set; } = 900;
    }

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

        [JsonIgnore] public string RootPath { get; set; } = "";
        [JsonIgnore] public string ManifestPath => Path.Combine(RootPath, "project.elintria");
        [JsonIgnore] public int SceneCount { get; set; }
        [JsonIgnore] public int ScriptCount { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ProjectRegistry  –  list of all known projects, stored in AppData
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
        /// <summary>Last scene file opened in this project (absolute path). Auto-loaded on open.</summary>
        public string LastScenePath { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ProjectManager  –  static API for all project + settings operations
    // ═══════════════════════════════════════════════════════════════════════════
    public static class ProjectManager
    {
        // ── AppData paths ─────────────────────────────────────────────────────
        private static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "ElintriaEngine");

        private static string RegistryPath =>
            Path.Combine(AppDataDir, "projects.json");

        private static string SettingsPath =>
            Path.Combine(AppDataDir, "settings.json");

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // ── Settings ──────────────────────────────────────────────────────────
        private static EngineSettings? _settingsCache;

        public static EngineSettings LoadSettings()
        {
            if (_settingsCache != null) return _settingsCache;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonSerializer.Deserialize<EngineSettings>(
                                File.ReadAllText(SettingsPath), _opts);
                    if (s != null) { _settingsCache = s; return s; }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[PM] Settings load: {ex.Message}"); }

            // First run – create defaults
            _settingsCache = new EngineSettings
            {
                DefaultProjectsDirectory = FallbackProjectsDir,
            };
            SaveSettings(_settingsCache);
            return _settingsCache;
        }

        public static void SaveSettings(EngineSettings s)
        {
            _settingsCache = s;
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, _opts));
            }
            catch (Exception ex) { Console.WriteLine($"[PM] Settings save: {ex.Message}"); }
        }

        /// <summary>The current effective default projects root folder.</summary>
        public static string DefaultProjectsDirectory
        {
            get
            {
                var dir = LoadSettings().DefaultProjectsDirectory;
                return string.IsNullOrWhiteSpace(dir) ? FallbackProjectsDir : dir;
            }
            set
            {
                var s = LoadSettings();
                s.DefaultProjectsDirectory = value;
                SaveSettings(s);
            }
        }

        private static string FallbackProjectsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "ElintriaProjects");

        // ── Registry ──────────────────────────────────────────────────────────
        public static ProjectRegistry LoadRegistry()
        {
            try
            {
                if (File.Exists(RegistryPath))
                    return JsonSerializer.Deserialize<ProjectRegistry>(
                               File.ReadAllText(RegistryPath), _opts)
                           ?? new ProjectRegistry();
            }
            catch (Exception ex) { Console.WriteLine($"[PM] Registry load: {ex.Message}"); }
            return new ProjectRegistry();
        }

        private static void SaveRegistry(ProjectRegistry reg)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(RegistryPath, JsonSerializer.Serialize(reg, _opts));
            }
            catch (Exception ex) { Console.WriteLine($"[PM] Registry save: {ex.Message}"); }
        }

        public static List<ProjectManifest> GetRecentProjects()
        {
            var reg = LoadRegistry();
            var results = new List<ProjectManifest>();

            foreach (var entry in reg.Projects)
            {
                if (!File.Exists(entry.ManifestPath)) continue;
                var m = LoadManifest(entry.ManifestPath);
                if (m == null) continue;
                RefreshStats(m);
                results.Add(m);
            }

            results.Sort((a, b) => b.LastOpenedAt.CompareTo(a.LastOpenedAt));
            return results;
        }

        private static void RegisterProject(ProjectManifest manifest)
        {
            var reg = LoadRegistry();
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
                var m = JsonSerializer.Deserialize<ProjectManifest>(
                            File.ReadAllText(manifestPath), _opts);
                if (m == null) return null;
                m.RootPath = Path.GetDirectoryName(manifestPath)!;
                return m;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PM] Manifest load error: {ex.Message}");
                return null;
            }
        }

        public static void SaveManifest(ProjectManifest manifest)
        {
            try { File.WriteAllText(manifest.ManifestPath, JsonSerializer.Serialize(manifest, _opts)); }
            catch (Exception ex) { Console.WriteLine($"[PM] Manifest save: {ex.Message}"); }
        }

        // ── Create ────────────────────────────────────────────────────────────
        /// <summary>
        /// Creates a new project at rootPath (always a subfolder named after the project
        /// inside DefaultProjectsDirectory if rootPath isn't explicitly overridden).
        /// </summary>
        public static ProjectManifest? CreateProject(string name, string rootPath,
                                                     ProjectType type, string description = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath))
                    return null;

                // Always place the project inside its own named subfolder
                // e.g. rootPath = /Projects/MyGame  (caller already includes the name)
                Directory.CreateDirectory(rootPath);

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
                    Directory.CreateDirectory(Path.Combine(rootPath, sub));

                File.WriteAllText(Path.Combine(rootPath, "Assets", "README.txt"),
                    $"Elintria Engine project: {name}\n" +
                    $"Type: {(type == ProjectType.TwoD ? "2D" : "3D")}\n" +
                    $"Created: {DateTime.UtcNow:O}\n");

                File.WriteAllText(Path.Combine(rootPath, ".gitignore"),
                    ".elintria/\nBuild/\n*.user\n");

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
        public static ProjectManifest? OpenProject(string manifestPath)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null) return null;
            manifest.LastOpenedAt = DateTime.UtcNow;
            SaveManifest(manifest);
            RegisterProject(manifest);
            return manifest;
        }

        // ── Per-project last-scene tracking ────────────────────────────────────
        /// <summary>
        /// Saves the path of the last open scene for a project so it can be
        /// auto-loaded next time the project is opened.
        /// </summary>
        public static void SaveLastScene(string projectRoot, string scenePath)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(scenePath)) return;

            // Primary: write a simple .lastscene file directly in the project root.
            // This always works regardless of registry state.
            try
            {
                File.WriteAllText(Path.Combine(projectRoot, ".lastscene"), scenePath);
            }
            catch (Exception ex)
            { Console.WriteLine($"[PM] SaveLastScene (file): {ex.Message}"); }

            // Secondary: also update the registry entry (upsert if missing)
            try
            {
                var reg = LoadRegistry();
                string manifestPath = Path.Combine(projectRoot, "project.elintria");

                ProjectRegistryEntry? found = null;
                foreach (var entry in reg.Projects)
                    if (string.Equals(entry.ManifestPath, manifestPath,
                            StringComparison.OrdinalIgnoreCase))
                    { found = entry; break; }

                if (found == null)
                {
                    // Project not in registry yet — add it
                    found = new ProjectRegistryEntry
                    {
                        ManifestPath = manifestPath,
                        Name = Path.GetFileName(
                            projectRoot.TrimEnd(Path.DirectorySeparatorChar,
                                                Path.AltDirectorySeparatorChar)),
                    };
                    reg.Projects.Add(found);
                }

                found.LastScenePath = scenePath;
                SaveRegistry(reg);
            }
            catch (Exception ex)
            { Console.WriteLine($"[PM] SaveLastScene (registry): {ex.Message}"); }
        }
        

        /// <summary>
        /// Returns the last scene path for a project, or empty string if none saved.
        /// Checks the local .lastscene file first (most reliable), then the registry.
        /// </summary>
        public static string LoadLastScene(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return "";

            // Primary: read .lastscene file written directly in project root
            try
            {
                string localFile = Path.Combine(projectRoot, ".lastscene");
                if (File.Exists(localFile))
                {
                    string path = File.ReadAllText(localFile).Trim();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return path;
                }
            }
            catch { }

            // Fallback: registry lookup
            try
            {
                var reg = LoadRegistry();
                string manifestPath = Path.Combine(projectRoot, "project.elintria");
                foreach (var entry in reg.Projects)
                    if (string.Equals(entry.ManifestPath, manifestPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string p = entry.LastScenePath ?? "";
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
            }
            catch (Exception ex)
            { Console.WriteLine($"[PM] LoadLastScene: {ex.Message}"); }

            return "";
        }

        // ── Delete / Remove ───────────────────────────────────────────────────
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

        public static void RemoveFromRegistry(ProjectManifest manifest) =>
            UnregisterProject(manifest.ManifestPath);

        // ── Import / Scan ─────────────────────────────────────────────────────
        public static ProjectManifest? ImportProject(string manifestPath)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null) return null;
            RegisterProject(manifest);
            return manifest;
        }

        /// <summary>
        /// Scans <paramref name="folder"/> (one level deep) for project.elintria files
        /// and registers any that aren't already known. Returns how many were added.
        /// </summary>
        public static int ScanFolderForProjects(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            int found = 0;
            var reg = LoadRegistry();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in reg.Projects) known.Add(e.ManifestPath);

            // Check root itself
            string rootManifest = Path.Combine(folder, "project.elintria");
            if (File.Exists(rootManifest) && !known.Contains(rootManifest))
            {
                ImportProject(rootManifest);
                found++;
            }

            // Check one level of subdirectories (each project is in its own folder)
            try
            {
                foreach (var sub in Directory.GetDirectories(folder))
                {
                    string mf = Path.Combine(sub, "project.elintria");
                    if (File.Exists(mf) && !known.Contains(mf))
                    {
                        ImportProject(mf);
                        found++;
                    }
                }
            }
            catch { /* best-effort */ }

            if (found > 0)
                Console.WriteLine($"[PM] Auto-scan found {found} new project(s) in {folder}");
            return found;
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
            catch { }
        }
    }
}