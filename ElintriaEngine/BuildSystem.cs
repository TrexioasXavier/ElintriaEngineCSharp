using ElintriaEngine.Core;
using ElintriaEngine.UI.Panels;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElintriaEngine.Build
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Data types
    // ═══════════════════════════════════════════════════════════════════════════

    public enum BuildTarget { Windows, Linux, macOS }
    public enum LogLevel { Info, Warning, Error, Success }

    public class LogEntry
    {
        public LogLevel Level { get; }
        public string Message { get; }
        public LogEntry(LogLevel level, string message) { Level = level; Message = message; }
    }

    public class BuildSettings
    {
        public string ProjectName { get; set; } = "MyGame";
        public string ProjectRoot { get; set; } = "";
        public string OutputDirectory { get; set; } = "Build/Output";
        public BuildTarget Target { get; set; } = BuildTarget.Windows;
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 720;
        public bool Fullscreen { get; set; } = false;
        public string StartScene { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BuildSystem
    // ═══════════════════════════════════════════════════════════════════════════
    public static class BuildSystem
    {
        // ── Logging ────────────────────────────────────────────────────────────
        public static event Action<LogEntry>? OnLog;

        private static void Log(string msg, LogLevel level = LogLevel.Info)
        {
            Console.WriteLine($"[Build/{level}] {msg}");
            OnLog?.Invoke(new LogEntry(level, msg));
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Compiles user scripts from Assets/ into .elintria/Scripts/bin/GameScripts.dll.
        /// Called automatically before entering play mode so you don't need a full build first.
        /// Returns the DLL path on success, or null on failure.
        /// </summary>
        public static async Task<string?> CompileScriptsAsync(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot))
                return null;

            string[] csFiles = GetScriptFiles(projectRoot);
            if (csFiles.Length == 0)
            {
                Console.WriteLine("[Build] No user scripts found in Assets/");
                return null;
            }

            string scriptsProjDir = Path.Combine(projectRoot, ".elintria", "Scripts");
            Directory.CreateDirectory(scriptsProjDir);

            string csprojPath = Path.Combine(scriptsProjDir, "GameScripts.csproj");
            string binDir = Path.Combine(scriptsProjDir, "bin");
            string objDir = Path.Combine(scriptsProjDir, "obj");

            WriteScriptsCsproj(csprojPath, csFiles, FindEngineDll());

            // Wipe obj/ so stale auto-generated AssemblyInfo files don't cause CS0579
            if (Directory.Exists(objDir))
                Directory.Delete(objDir, recursive: true);

            Console.WriteLine($"[Build] Compiling {csFiles.Length} script file(s)...");
            bool ok = await RunDotnet($"build \"{csprojPath}\" -c Release -o \"{binDir}\"",
                                      silent: true);
            if (!ok)
            {
                Console.WriteLine("[Build] Script compilation failed.");
                return null;
            }

            string dll = Path.Combine(binDir, "GameScripts.dll");
            if (!File.Exists(dll))
            {
                Console.WriteLine("[Build] Build succeeded but GameScripts.dll not found.");
                return null;
            }

            Console.WriteLine($"[Build] Scripts compiled → {dll}");
            return dll;
        }

        /// <summary>
        /// Full 6-step build pipeline: validate → save scene → compile scripts →
        /// generate host project → copy assets → publish executable.
        /// </summary>
        public static async Task<bool> BuildAsync(
            BuildSettings settings, Scene activeScene, bool runAfterBuild)
        {
            Log($"=== Building '{settings.ProjectName}' ===");

            try
            {
                // ── Step 1: Validate ──────────────────────────────────────────
                Log("Step 1/6 - Validating project...");
                if (string.IsNullOrEmpty(settings.ProjectRoot) ||
                    !Directory.Exists(settings.ProjectRoot))
                {
                    Log($"Project root not found: '{settings.ProjectRoot}'", LogLevel.Error);
                    return false;
                }

                // ── Step 2: Save scene ────────────────────────────────────────
                Log("Step 2/6 - Saving scene...");
                string scenesDir = Path.Combine(settings.ProjectRoot, "Assets", "Scenes");
                Directory.CreateDirectory(scenesDir);
                string sceneName = string.IsNullOrEmpty(activeScene.Name) ? "MainScene" : activeScene.Name;
                string sceneFile = Path.Combine(scenesDir, sceneName + ".scene");
                SceneSerializer.Save(activeScene, sceneFile);
                Log($"  Scene saved → {sceneFile}");

                // ── Step 3: Compile user scripts ──────────────────────────────
                Log("Step 3/6 - Compiling user scripts...");
                string[] csFiles = GetScriptFiles(settings.ProjectRoot);
                string scriptsDll = "";

                if (csFiles.Length > 0)
                {
                    string scriptsProjDir = Path.Combine(settings.ProjectRoot, ".elintria", "Scripts");
                    Directory.CreateDirectory(scriptsProjDir);
                    string csprojPath = Path.Combine(scriptsProjDir, "GameScripts.csproj");
                    string binDir = Path.Combine(scriptsProjDir, "bin");
                    string objDir = Path.Combine(scriptsProjDir, "obj");

                    WriteScriptsCsproj(csprojPath, csFiles, FindEngineDll());

                    if (Directory.Exists(objDir))
                        Directory.Delete(objDir, recursive: true);

                    bool ok = await RunDotnet($"build \"{csprojPath}\" -c Release -o \"{binDir}\"");
                    if (!ok) { Log("Script compilation failed.", LogLevel.Error); return false; }

                    scriptsDll = Path.Combine(binDir, "GameScripts.dll");
                    if (File.Exists(scriptsDll))
                        Log($"  Scripts compiled → {scriptsDll}");
                    else
                    {
                        Log("  GameScripts.dll not found after build.", LogLevel.Warning);
                        scriptsDll = "";
                    }
                }
                else
                {
                    Log("  No .cs scripts found in Assets/, skipping.");
                }

                // ── Step 4: Generate host project ─────────────────────────────
                Log("Step 4/6 - Generating host project...");
                string hostDir = Path.Combine(settings.ProjectRoot, ".elintria", "HostProject");
                Directory.CreateDirectory(hostDir);
                WriteHostCsproj(hostDir, settings, FindEngineDll(), scriptsDll);
                WriteGameCs(hostDir, settings, sceneName);
                Log($"  Host project generated → {hostDir}");

                // ── Step 5: Copy assets ───────────────────────────────────────
                Log("Step 5/6 - Copying assets...");
                string outDir = Path.Combine(settings.ProjectRoot, settings.OutputDirectory);
                string outAssets = Path.Combine(outDir, "Assets");
                Directory.CreateDirectory(outDir);
                CopyDirectory(Path.Combine(settings.ProjectRoot, "Assets"), outAssets);
                Log($"  Assets copied → {outAssets}");

                // ── Step 6: Publish ───────────────────────────────────────────
                Log("Step 6/6 - Publishing...");
                string rid = settings.Target switch
                {
                    BuildTarget.Linux => "linux-x64",
                    BuildTarget.macOS => "osx-x64",
                    _ => "win-x64",
                };
                string hostCsproj = Path.Combine(hostDir, "HostProject.csproj");
                bool published = await RunDotnet(
                    $"publish \"{hostCsproj}\" -c Release -r {rid} --self-contained true -o \"{outDir}\"");

                if (!published) { Log("Publish step failed.", LogLevel.Error); return false; }

                Log($"Build complete → {outDir}", LogLevel.Success);

                // ── Launch if requested ───────────────────────────────────────
                if (runAfterBuild)
                {
                    string exe = settings.Target == BuildTarget.Windows
                        ? Path.Combine(outDir, settings.ProjectName + ".exe")
                        : Path.Combine(outDir, settings.ProjectName);

                    if (File.Exists(exe))
                    {
                        Log($"Launching: {exe}");
                        Process.Start(new ProcessStartInfo(exe)
                        { UseShellExecute = true, WorkingDirectory = outDir });
                    }
                    else
                    {
                        Log($"Executable not found: {exe}", LogLevel.Warning);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Build exception: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  File generators
        // ═══════════════════════════════════════════════════════════════════════

        private static void WriteScriptsCsproj(string path, string[] csFiles, string? engineDll)
        {
            string dir = Path.GetDirectoryName(path)!;
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <OutputType>Library</OutputType>");
            sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            sb.AppendLine("    <AssemblyName>GameScripts</AssemblyName>");
            sb.AppendLine("    <RootNamespace>GameScripts</RootNamespace>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
            sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
            sb.AppendLine("    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <ItemGroup>");
            foreach (string cs in csFiles)
                sb.AppendLine($"    <Compile Include=\"{XmlEsc(Path.GetRelativePath(dir, cs).Replace("/", "\\"))}\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PackageReference Include=\"OpenTK\" Version=\"4.*\" />");
            sb.AppendLine("  </ItemGroup>");
            if (!string.IsNullOrEmpty(engineDll) && File.Exists(engineDll))
            {
                sb.AppendLine("  <ItemGroup>");
                sb.AppendLine($"    <Reference Include=\"ElintriaEngine\"><HintPath>{XmlEsc(engineDll)}</HintPath></Reference>");
                sb.AppendLine("  </ItemGroup>");
            }
            sb.AppendLine("</Project>");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void WriteHostCsproj(string dir, BuildSettings s,
                                            string? engineDll, string scriptsDll)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <OutputType>Exe</OutputType>");
            sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            sb.AppendLine($"    <AssemblyName>{XmlEsc(s.ProjectName)}</AssemblyName>");
            sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
            sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <Optimize>true</Optimize>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Compile Include=\"Game.cs\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PackageReference Include=\"OpenTK\" Version=\"4.*\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <ItemGroup>");
            if (!string.IsNullOrEmpty(engineDll) && File.Exists(engineDll))
                sb.AppendLine($"    <Reference Include=\"ElintriaEngine\"><HintPath>{XmlEsc(engineDll)}</HintPath></Reference>");
            if (!string.IsNullOrEmpty(scriptsDll) && File.Exists(scriptsDll))
                sb.AppendLine($"    <Reference Include=\"GameScripts\"><HintPath>{XmlEsc(scriptsDll)}</HintPath></Reference>");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");
            File.WriteAllText(Path.Combine(dir, "HostProject.csproj"), sb.ToString());
        }

        /// <summary>
        /// Generates a single Game.cs containing both Program (entry point) and GameRuntime.
        /// Having one file avoids top-level-statement ambiguity and CS5001 "no Main" errors.
        /// </summary>
        private static void WriteGameCs(string dir, BuildSettings s, string sceneName)
        {
            string scene = CsEsc("Assets/Scenes/" + sceneName + ".scene");
            string title = CsEsc(s.ProjectName);
            bool fullscreen = s.Fullscreen;
            int w = s.WindowWidth;
            int h = s.WindowHeight;

            var sb = new StringBuilder();
            sb.AppendLine("// Generated by Elintria Engine — do not edit manually.");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Drawing;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using OpenTK.Graphics.OpenGL4;");
            sb.AppendLine("using OpenTK.Mathematics;");
            sb.AppendLine("using OpenTK.Windowing.Common;");
            sb.AppendLine("using OpenTK.Windowing.Desktop;");
            sb.AppendLine("using OpenTK.Windowing.GraphicsLibraryFramework;");
            sb.AppendLine("using ElintriaEngine.Core;");
            sb.AppendLine("using ElintriaEngine.Rendering.Scene;");
            sb.AppendLine();

            // ── Program ───────────────────────────────────────────────────────
            sb.AppendLine("internal static class Program");
            sb.AppendLine("{");
            sb.AppendLine("    [STAThread]");
            sb.AppendLine("    static void Main()");
            sb.AppendLine("    {");
            sb.AppendLine("        var gs = GameWindowSettings.Default;");
            sb.AppendLine("        var ns = new NativeWindowSettings");
            sb.AppendLine("        {");
            sb.AppendLine($"            Title      = \"{title}\",");
            sb.AppendLine($"            ClientSize = new Vector2i({w}, {h}),");
            sb.AppendLine("            APIVersion = new Version(3, 3),");
            sb.AppendLine("            Profile    = ContextProfile.Core,");
            sb.AppendLine("        };");
            sb.AppendLine($"        using var win = new GameRuntime(gs, ns, \"{scene}\", {(fullscreen ? "true" : "false")});");
            sb.AppendLine("        win.Run();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            // ── GameRuntime ───────────────────────────────────────────────────
            sb.AppendLine("internal sealed class GameRuntime : GameWindow");
            sb.AppendLine("{");
            sb.AppendLine("    private readonly string _scenePath;");
            sb.AppendLine("    private readonly bool   _fullscreen;");
            sb.AppendLine("    private Scene         _scene    = new Scene();");
            sb.AppendLine("    private SceneRunner   _runner   = new SceneRunner();");
            sb.AppendLine("    private SceneRenderer _renderer = new SceneRenderer();");
            sb.AppendLine("    private int _lastW, _lastH;");
            sb.AppendLine();
            sb.AppendLine("    public GameRuntime(GameWindowSettings gs, NativeWindowSettings ns,");
            sb.AppendLine("                       string scenePath, bool fullscreen) : base(gs, ns)");
            sb.AppendLine("    {");
            sb.AppendLine("        _scenePath  = scenePath;");
            sb.AppendLine("        _fullscreen = fullscreen;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnLoad()");
            sb.AppendLine("    {");
            sb.AppendLine("        base.OnLoad();");
            sb.AppendLine("        if (_fullscreen) WindowState = WindowState.Fullscreen;");
            sb.AppendLine("        GL.ClearColor(0.08f, 0.08f, 0.10f, 1f);");
            sb.AppendLine("        GL.Enable(EnableCap.DepthTest);");
            sb.AppendLine("        _renderer.Init();");
            sb.AppendLine();
            sb.AppendLine("        string full = Path.IsPathRooted(_scenePath)");
            sb.AppendLine("            ? _scenePath");
            sb.AppendLine("            : Path.Combine(AppContext.BaseDirectory, _scenePath);");
            sb.AppendLine("        if (File.Exists(full))");
            sb.AppendLine("            _scene = SceneSerializer.Load(full);");
            sb.AppendLine("        else");
            sb.AppendLine("            Console.WriteLine(\"[Game] Scene not found: \" + full);");
            sb.AppendLine();
            sb.AppendLine("        // SceneRunner handles script loading, DynamicScript resolution,");
            sb.AppendLine("        // and the Awake -> OnEnable -> OnStart lifecycle.");
            sb.AppendLine("        _runner.Start(_scene);");
            sb.AppendLine();
            sb.AppendLine("        _lastW = FramebufferSize.X;");
            sb.AppendLine("        _lastH = FramebufferSize.Y;");
            sb.AppendLine("        GL.Viewport(0, 0, _lastW, _lastH);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnRenderFrame(FrameEventArgs args)");
            sb.AppendLine("    {");
            sb.AppendLine("        base.OnRenderFrame(args);");
            sb.AppendLine("        int w = FramebufferSize.X, h = FramebufferSize.Y;");
            sb.AppendLine("        if (w != _lastW || h != _lastH)");
            sb.AppendLine("            { _lastW = w; _lastH = h; GL.Viewport(0, 0, w, h); }");
            sb.AppendLine();
            sb.AppendLine("        _runner.Tick(args.Time);");
            sb.AppendLine("        SetSceneCamera(w, h);");
            sb.AppendLine();
            sb.AppendLine("        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);");
            sb.AppendLine("        _renderer.Render(new RectangleF(0, 0, w, h), _scene, w, h);");
            sb.AppendLine("        SwapBuffers();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private void SetSceneCamera(int w, int h)");
            sb.AppendLine("    {");
            sb.AppendLine("        foreach (var go in _scene.All())");
            sb.AppendLine("        {");
            sb.AppendLine("            var cam = go.GetComponent<Camera>();");
            sb.AppendLine("            if (cam == null || !cam.Enabled) continue;");
            sb.AppendLine("            var t   = go.Transform;");
            sb.AppendLine("            float yr  = MathHelper.DegreesToRadians(t.LocalEulerAngles.Y);");
            sb.AppendLine("            float xr  = MathHelper.DegreesToRadians(t.LocalEulerAngles.X);");
            sb.AppendLine("            var fwd   = new Vector3(");
            sb.AppendLine("                MathF.Sin(yr) * MathF.Cos(xr),");
            sb.AppendLine("               -MathF.Sin(xr),");
            sb.AppendLine("               -MathF.Cos(yr) * MathF.Cos(xr));");
            sb.AppendLine("            var view  = Matrix4.LookAt(t.LocalPosition, t.LocalPosition + fwd, Vector3.UnitY);");
            sb.AppendLine("            Matrix4 proj;");
            sb.AppendLine("            if (cam.IsOrthographic)");
            sb.AppendLine("            {");
            sb.AppendLine("                float ratio = w / (float)h;");
            sb.AppendLine("                proj = Matrix4.CreateOrthographic(");
            sb.AppendLine("                    cam.OrthoSize * ratio * 2, cam.OrthoSize * 2,");
            sb.AppendLine("                    cam.NearClip, cam.FarClip);");
            sb.AppendLine("            }");
            sb.AppendLine("            else");
            sb.AppendLine("            {");
            sb.AppendLine("                proj = Matrix4.CreatePerspectiveFieldOfView(");
            sb.AppendLine("                    MathHelper.DegreesToRadians(cam.FieldOfView),");
            sb.AppendLine("                    w / (float)h, cam.NearClip, cam.FarClip);");
            sb.AppendLine("            }");
            sb.AppendLine("            _renderer.GameViewMatrix = view;");
            sb.AppendLine("            _renderer.GameProjMatrix = proj;");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine("        _renderer.GameViewMatrix = null;");
            sb.AppendLine("        _renderer.GameProjMatrix = null;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnResize(ResizeEventArgs e)");
            sb.AppendLine("    {");
            sb.AppendLine("        base.OnResize(e);");
            sb.AppendLine("        GL.Viewport(0, 0, e.Width, e.Height);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnKeyDown(KeyboardKeyEventArgs e)");
            sb.AppendLine("    {");
            sb.AppendLine("        base.OnKeyDown(e);");
            sb.AppendLine("        if (e.Key == Keys.Escape || (e.Alt && e.Key == Keys.F4)) Close();");
            sb.AppendLine("        if (e.Key == Keys.F11)");
            sb.AppendLine("            WindowState = WindowState == WindowState.Fullscreen");
            sb.AppendLine("                ? WindowState.Normal : WindowState.Fullscreen;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnUnload()");
            sb.AppendLine("    {");
            sb.AppendLine("        base.OnUnload();");
            sb.AppendLine("        _runner.Dispose();");
            sb.AppendLine("        _renderer.Dispose();");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(Path.Combine(dir, "Game.cs"), sb.ToString(), Encoding.UTF8);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Utilities
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Returns all user .cs files under Assets/, excluding obj/ and bin/ folders.</summary>
        private static string[] GetScriptFiles(string projectRoot)
        {
            string assetsDir = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assetsDir)) return Array.Empty<string>();

            return Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var parts = f.Replace('\\', '/').Split('/');
                    return !parts.Any(p =>
                        p.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        p.Equals("bin", StringComparison.OrdinalIgnoreCase));
                })
                .ToArray();
        }

        /// <summary>Locates ElintriaEngine.dll relative to the running executable.</summary>
        private static string? FindEngineDll()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "ElintriaEngine.dll"),
                Path.Combine(AppContext.BaseDirectory, "..", "ElintriaEngine.dll"),
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        private static async Task<bool> RunDotnet(string args, bool silent = false)
        {
            if (!silent) Log($"  > dotnet {args}");

            var psi = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (!silent)
            {
                foreach (string line in stdout.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Log("  " + line.TrimEnd());
                foreach (string line in stderr.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Log("  " + line.TrimEnd(), LogLevel.Warning);
            }
            else
            {
                // In silent mode still forward errors so the user sees what went wrong
                foreach (string line in stdout.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("  " + line.TrimEnd());
                foreach (string line in stderr.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("  " + line.TrimEnd());
            }

            bool ok = proc.ExitCode == 0;
            if (!ok && !silent) Log($"  dotnet exited with code {proc.ExitCode}", LogLevel.Error);
            return ok;
        }

        private static void CopyDirectory(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (string sub in Directory.GetDirectories(src))
                CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
        }

        private static string XmlEsc(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static string CsEsc(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BuildSettingsPanel  —  floating UI panel for build configuration and log
    // ═══════════════════════════════════════════════════════════════════════════
    public class BuildSettingsPanel : Panel
    {
        private BuildSettings _settings;
        private Scene? _scene;
        private bool _building;
        private readonly List<LogEntry> _log = new();

        // Text field editing state
        private string? _editId;
        private string _editBuf = "";
        private Action<string>? _editCommit;

        // Cached hit-test rects
        private RectangleF _buildBtn, _runBtn, _closeBtn;

        private static readonly Color CLogInfo = Color.FromArgb(255, 180, 180, 180);
        private static readonly Color CLogWarning = Color.FromArgb(255, 195, 155, 40);
        private static readonly Color CLogError = Color.FromArgb(255, 220, 60, 60);
        private static readonly Color CLogSuccess = Color.FromArgb(255, 60, 185, 60);

        public BuildSettingsPanel(RectangleF bounds, BuildSettings settings)
            : base("Build Settings", bounds)
        {
            _settings = settings;
            MinWidth = 400f;
            MinHeight = 360f;

            BuildSystem.OnLog += entry =>
            {
                _log.Add(entry);
                if (_log.Count > 400) _log.RemoveAt(0);
                // Auto-scroll to bottom
                float logH = _log.Count * 15f;
                float avail = Bounds.Height - 280f;
                if (logH > avail) ScrollOffset = logH - avail;
            };
        }

        public void SetScene(Scene s) => _scene = s;

        public void StartBuild(bool runAfter)
        {
            if (_building) return;
            _building = true;
            _log.Clear();
            ScrollOffset = 0;
            _ = Task.Run(async () =>
            {
                await BuildSystem.BuildAsync(_settings, _scene ?? new Scene(), runAfter);
                _building = false;
            });
        }

        // ── Render ─────────────────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            DrawHeader(r);

            var cr = ContentRect;
            r.FillRect(cr, ColBg);
            r.PushClip(cr);

            float y = cr.Y + 8f;
            float lx = cr.X + 8f;
            float fw = cr.Width - 16f;

            // Close button
            _closeBtn = new RectangleF(Bounds.Right - 20f, Bounds.Y + 4f, 14f, 14f);
            r.FillRect(_closeBtn, Color.FromArgb(255, 140, 35, 35));
            r.DrawText("X", new PointF(_closeBtn.X + 2f, _closeBtn.Y + 2f), Color.White, 9f);

            // ── Settings fields ───────────────────────────────────────────────
            DrawTextField(r, lx, ref y, fw, "Project Name", "pname", _settings.ProjectName, v => _settings.ProjectName = v);
            DrawTextField(r, lx, ref y, fw, "Output Directory", "outdir", _settings.OutputDirectory, v => _settings.OutputDirectory = v);
            DrawTextField(r, lx, ref y, fw, "Start Scene", "scene", _settings.StartScene, v => _settings.StartScene = v);
            DrawIntField(r, lx, ref y, fw, "Width", "ww", _settings.WindowWidth, v => _settings.WindowWidth = v);
            DrawIntField(r, lx, ref y, fw, "Height", "wh", _settings.WindowHeight, v => _settings.WindowHeight = v);
            DrawBoolToggle(r, lx, ref y, fw, "Fullscreen", _settings.Fullscreen, v => _settings.Fullscreen = v);

            // Platform selector
            r.DrawText("Target:", new PointF(lx, y + 4f), ColText, 10f);
            string[] platforms = { "Windows", "Linux", "macOS" };
            float px = lx + 80f;
            for (int i = 0; i < platforms.Length; i++)
            {
                var btn = new RectangleF(px, y, 60f, 18f);
                bool sel = (int)_settings.Target == i;
                r.FillRect(btn, sel ? ColAccent : Color.FromArgb(255, 46, 46, 46));
                r.DrawRect(btn, ColBorder);
                r.DrawText(platforms[i], new PointF(px + 4f, y + 4f), ColText, 9f);
                px += 64f;
            }
            y += 26f;

            // Divider
            r.DrawLine(new PointF(lx, y), new PointF(cr.Right - 8f, y),
                Color.FromArgb(255, 55, 55, 55));
            y += 8f;

            // ── Action buttons ────────────────────────────────────────────────
            float half = fw / 2f - 4f;
            _buildBtn = new RectangleF(lx, y, half, 26f);
            _runBtn = new RectangleF(lx + half + 8f, y, half, 26f);

            bool idle = !_building;
            r.FillRect(_buildBtn, idle ? Color.FromArgb(255, 40, 100, 40) : Color.FromArgb(255, 30, 55, 30));
            r.DrawRect(_buildBtn, ColBorder);
            r.DrawText(_building ? "Building..." : "Build",
                new PointF(_buildBtn.X + 10f, _buildBtn.Y + 7f), Color.White, 11f);

            r.FillRect(_runBtn, idle ? Color.FromArgb(255, 40, 60, 130) : Color.FromArgb(255, 25, 30, 60));
            r.DrawRect(_runBtn, ColBorder);
            r.DrawText("Build & Run", new PointF(_runBtn.X + 8f, _runBtn.Y + 7f), Color.White, 11f);
            y += 32f;

            // ── Build log ─────────────────────────────────────────────────────
            r.DrawText("Build Log:", new PointF(lx, y), ColTextDim, 10f);
            y += 16f;

            float logAreaH = Math.Max(40f, cr.Bottom - y - 4f);
            var logRect = new RectangleF(lx, y, fw, logAreaH);
            r.FillRect(logRect, Color.FromArgb(255, 18, 18, 18));
            r.DrawRect(logRect, ColBorder);
            r.PushClip(logRect);

            float ly = logRect.Y + 2f - ScrollOffset;
            for (int i = 0; i < _log.Count; i++)
            {
                var entry = _log[i];

                if (ly + 14f > logRect.Y && ly < logRect.Bottom)
                {
                    Color tc = entry.Level switch
                    {
                        LogLevel.Error => CLogError,
                        LogLevel.Warning => CLogWarning,
                        LogLevel.Success => CLogSuccess,
                        _ => CLogInfo,
                    };
                    r.DrawText(entry.Message, new PointF(logRect.X + 4f, ly), tc, 9f);
                }
                ly += 15f;
            }
            ContentHeight = (ly + ScrollOffset) - y + 4f;

            r.PopClip();
            r.PopClip();
            DrawScrollBar(r);
        }

        // ── Field drawing helpers ───────────────────────────────────────────────
        private void DrawTextField(IEditorRenderer r, float lx, ref float y, float fw,
            string label, string id, string value, Action<string> setter)
        {
            bool ed = _editId == id;
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 10f);
            var fr = new RectangleF(lx + 114f, y, fw - 116f, 18f);
            r.FillRect(fr, ed ? Color.FromArgb(255, 36, 56, 90) : Color.FromArgb(255, 34, 34, 34));
            r.DrawRect(fr, ed ? ColAccent : ColBorder);
            r.DrawText(ed ? _editBuf + "|" : value, new PointF(fr.X + 4f, y + 4f), ColText, 10f);
            y += 22f;
        }

        private void DrawIntField(IEditorRenderer r, float lx, ref float y, float fw,
            string label, string id, int value, Action<int> setter)
            => DrawTextField(r, lx, ref y, fw, label, id, value.ToString(),
                s => { if (int.TryParse(s, out int v)) setter(v); });

        private void DrawBoolToggle(IEditorRenderer r, float lx, ref float y, float fw,
            string label, bool value, Action<bool> setter)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColTextDim, 10f);
            var cb = new RectangleF(lx + 114f, y + 2f, 14f, 14f);
            r.FillRect(cb, value ? Color.FromArgb(255, 55, 155, 55) : Color.FromArgb(255, 48, 48, 48));
            r.DrawRect(cb, ColBorder);
            if (value) r.DrawText("✓", new PointF(cb.X + 1f, cb.Y + 1f), Color.White, 9f);
            y += 22f;
        }

        // ── Input ──────────────────────────────────────────────────────────────
        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible) return;

            // Close button
            if (_closeBtn.Contains(pos)) { IsVisible = false; return; }

            // Commit any in-progress edit on outside click
            if (_editId != null) { CommitEdit(); return; }

            // Action buttons
            if (_buildBtn.Contains(pos)) { StartBuild(false); return; }
            if (_runBtn.Contains(pos)) { StartBuild(true); return; }

            // Platform buttons
            var cr = ContentRect;
            float py = cr.Y + 8f + 6 * 22f + 26f;  // after 6 fields + platform row start
            float px = cr.X + 8f + 80f;
            for (int i = 0; i < 3; i++)
            {
                if (new RectangleF(px, py, 60f, 18f).Contains(pos))
                { _settings.Target = (BuildTarget)i; return; }
                px += 64f;
            }

            // Fullscreen toggle
            float toggleY = cr.Y + 8f + 5 * 22f;
            var toggleR = new RectangleF(cr.X + 8f + 114f, toggleY + 2f, 14f, 14f);
            if (toggleR.Contains(pos)) { _settings.Fullscreen = !_settings.Fullscreen; return; }

            // Text field clicks
            string[] fieldIds = { "pname", "outdir", "scene", "ww", "wh" };
            float fy = cr.Y + 8f;
            for (int i = 0; i < fieldIds.Length; i++)
            {
                var fr = new RectangleF(cr.X + 8f + 114f, fy, cr.Width - 124f, 18f);
                if (fr.Contains(pos))
                {
                    int idx = i;
                    string cur = idx switch
                    {
                        0 => _settings.ProjectName,
                        1 => _settings.OutputDirectory,
                        2 => _settings.StartScene,
                        3 => _settings.WindowWidth.ToString(),
                        4 => _settings.WindowHeight.ToString(),
                        _ => "",
                    };
                    StartEdit(fieldIds[idx], cur, s =>
                    {
                        switch (idx)
                        {
                            case 0: _settings.ProjectName = s; break;
                            case 1: _settings.OutputDirectory = s; break;
                            case 2: _settings.StartScene = s; break;
                            case 3: if (int.TryParse(s, out int ww)) _settings.WindowWidth = ww; break;
                            case 4: if (int.TryParse(s, out int wh)) _settings.WindowHeight = wh; break;
                        }
                    });
                    return;
                }
                fy += 22f;
            }

            base.OnMouseDown(e, pos);
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (_editId == null) return;
            switch (e.Key)
            {
                case Keys.Enter: CommitEdit(); break;
                case Keys.Escape: _editId = null; break;
                case Keys.Backspace when _editBuf.Length > 0:
                    _editBuf = _editBuf[..^1]; break;
            }
        }

        public override void OnTextInput(TextInputEventArgs e)
        {
            if (_editId != null) _editBuf += e.AsString;
        }

        private void StartEdit(string id, string initial, Action<string> onCommit)
        {
            _editId = id;
            _editBuf = initial;
            _editCommit = onCommit;
        }

        private void CommitEdit()
        {
            _editCommit?.Invoke(_editBuf);
            _editId = null;
        }
    }
}