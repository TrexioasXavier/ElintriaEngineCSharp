using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElintriaEngine.Core
{
    public enum AntiAliasMode { None, MSAA2x, MSAA4x, MSAA8x, FXAA, TAA }
    public enum ShadowQuality { Disabled, Low, Medium, High, VeryHigh }
    public enum ShadowResolution { R256, R512, R1024, R2048, R4096 }
    public enum TextureQuality { Full, Half, Quarter, Eighth }
    public enum VSyncMode { Off, On, AdaptiveHalf }
    public enum FullscreenMode { Windowed, FullscreenWindow, ExclusiveFullscreen }
    public enum ColorSpace { Linear, Gamma }
    public enum SpeakerMode { Stereo, Mono, Quad, Surround5point1, Surround7point1 }
    public enum PhysicsBroadphase { SweepAndPrune, MultiBoxPruning, AutomaticBoxPruning }

    /// <summary>
    /// Project-specific settings that affect the built game and editor behaviour.
    /// Saved alongside the project at Assets/ProjectSettings/ProjectSettings.json.
    /// </summary>
    public class ProjectSettings
    {
        // ── Player / Identity ─────────────────────────────────────────────────
        public string ProductName { get; set; } = "My Game";
        public string CompanyName { get; set; } = "My Company";
        public string Version { get; set; } = "1.0.0";
        public string BundleId { get; set; } = "com.mycompany.mygame";
        public string Description { get; set; } = "";
        public string IconPath { get; set; } = "";  // relative to project root
        public string SplashPath { get; set; } = "";
        public string Copyright { get; set; } = "";

        // ── Display / Window ──────────────────────────────────────────────────
        public int DefaultWidth { get; set; } = 1920;
        public int DefaultHeight { get; set; } = 1080;
        public FullscreenMode Fullscreen { get; set; } = FullscreenMode.Windowed;
        public VSyncMode VSync { get; set; } = VSyncMode.On;
        public int TargetFrameRate { get; set; } = -1;  // -1 = unlimited
        public bool AllowResizing { get; set; } = true;
        public int MinWidth { get; set; } = 800;
        public int MinHeight { get; set; } = 600;

        // ── Graphics ──────────────────────────────────────────────────────────
        public AntiAliasMode AntiAliasing { get; set; } = AntiAliasMode.MSAA4x;
        public ShadowQuality Shadows { get; set; } = ShadowQuality.Medium;
        public ShadowResolution ShadowResolution { get; set; } = ShadowResolution.R1024;
        public float ShadowDistance { get; set; } = 50f;
        public bool SoftShadows { get; set; } = true;
        public TextureQuality TextureQuality { get; set; } = TextureQuality.Full;
        public bool Anisotropic { get; set; } = true;
        public int AnisotropicLevel { get; set; } = 16;
        public ColorSpace ColorSpace { get; set; } = ColorSpace.Linear;
        public bool HDR { get; set; } = true;
        public bool SSAO { get; set; } = false;
        public bool Bloom { get; set; } = false;
        public float BloomThreshold { get; set; } = 1.0f;
        public float BloomIntensity { get; set; } = 1.0f;
        public bool MotionBlur { get; set; } = false;
        public float MotionBlurShutter { get; set; } = 180f;
        public bool DepthOfField { get; set; } = false;
        public float DOFFocalLength { get; set; } = 50f;
        public float DOFAperture { get; set; } = 5.6f;
        public bool Fog { get; set; } = false;
        public float FogStart { get; set; } = 10f;
        public float FogEnd { get; set; } = 100f;
        public float FogR { get; set; } = 0.5f;
        public float FogG { get; set; } = 0.5f;
        public float FogB { get; set; } = 0.5f;
        public float AmbientIntensity { get; set; } = 0.5f;
        public float AmbientR { get; set; } = 0.3f;
        public float AmbientG { get; set; } = 0.3f;
        public float AmbientB { get; set; } = 0.4f;
        public bool RealtimeGI { get; set; } = false;
        public bool BakedGI { get; set; } = false;

        // ── Physics ───────────────────────────────────────────────────────────
        public float GravityX { get; set; } = 0f;
        public float GravityY { get; set; } = -9.81f;
        public float GravityZ { get; set; } = 0f;
        public float FixedTimestep { get; set; } = 0.02f;  // 50 Hz
        public float MaxTimestep { get; set; } = 0.1f;
        public int SolverIterations { get; set; } = 6;
        public int SolverVelocityIter { get; set; } = 1;
        public bool AutoSimulation { get; set; } = true;
        public PhysicsBroadphase Broadphase { get; set; } = PhysicsBroadphase.SweepAndPrune;
        public float DefaultFriction { get; set; } = 0.4f;
        public float DefaultBounciness { get; set; } = 0f;
        public float SleepThreshold { get; set; } = 0.005f;

        // ── Audio ─────────────────────────────────────────────────────────────
        public float MasterVolume { get; set; } = 1.0f;
        public float MusicVolume { get; set; } = 1.0f;
        public float SFXVolume { get; set; } = 1.0f;
        public SpeakerMode SpeakerMode { get; set; } = SpeakerMode.Stereo;
        public float DopplerFactor { get; set; } = 1.0f;
        public int SampleRate { get; set; } = 44100;
        public bool SpatialBlend3D { get; set; } = true;

        // ── Time ─────────────────────────────────────────────────────────────
        public float TimeScale { get; set; } = 1.0f;
        public float MaxParticleDeltaTime { get; set; } = 0.03f;

        // ── Input ────────────────────────────────────────────────────────────
        public float MouseSensitivity { get; set; } = 1.0f;
        public float ControllerDeadzone { get; set; } = 0.1f;
        public bool InvertMouseY { get; set; } = false;

        // ── Scripting ────────────────────────────────────────────────────────
        public string ScriptingBackend { get; set; } = "Mono";
        public string ApiCompatibility { get; set; } = "NET_Standard_2_1";

        // ── Persistence ───────────────────────────────────────────────────────
        [JsonIgnore] public string SavePath { get; private set; } = "";

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static ProjectSettings? _instance;
        public static ProjectSettings Instance => _instance ??= new ProjectSettings();

        public static ProjectSettings LoadForProject(string projectRoot)
        {
            string path = GetPath(projectRoot);
            try
            {
                if (File.Exists(path))
                {
                    var p = JsonSerializer.Deserialize<ProjectSettings>(File.ReadAllText(path), _opts);
                    if (p != null) { p.SavePath = path; _instance = p; return p; }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[ProjSettings] Load: {ex.Message}"); }

            var defaults = new ProjectSettings { SavePath = path };
            _instance = defaults;
            defaults.Save();
            return defaults;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(SavePath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                File.WriteAllText(SavePath, JsonSerializer.Serialize(this, _opts));
                Console.WriteLine($"[ProjSettings] Saved to {SavePath}");
            }
            catch (Exception ex) { Console.WriteLine($"[ProjSettings] Save: {ex.Message}"); }
        }

        private static string GetPath(string projectRoot) =>
            Path.Combine(projectRoot, "Assets", "ProjectSettings", "ProjectSettings.json");
    }
}