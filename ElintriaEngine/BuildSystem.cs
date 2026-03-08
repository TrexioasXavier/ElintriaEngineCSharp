using ElintriaEngine.Core;
using ElintriaEngine.UI.Panels;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ElintriaEngine.Build
{
    // ── Build configuration ────────────────────────────────────────────────────
    public class BuildSettings
    {
        public string ProjectName { get; set; } = "ElintriaTestGame";
        public string ProjectRoot { get; set; } = "";
        public string OutputDirectory { get; set; } = "Build/Output";
        public BuildTarget Target { get; set; } = BuildTarget.Windows;
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 720;
        public bool Fullscreen { get; set; } = false;
        public string StartScene { get; set; } = "";  // relative to Assets/Scenes/
    }

    public enum BuildTarget { Windows, Linux, macOS }

    // ── Build log entry ───────────────────────────────────────────────────────
    public enum LogLevel { Info, Warning, Error, Success }

    public class LogEntry
    {
        public LogLevel Level { get; }
        public string Message { get; }
        public LogEntry(LogLevel l, string m) { Level = l; Message = m; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BuildSystem  –  the main pipeline
    // ═══════════════════════════════════════════════════════════════════════════
    public static class BuildSystem
    {
        public static event Action<LogEntry>? OnLog;

        private static void Log(string msg, LogLevel level = LogLevel.Info)
        {
            Console.WriteLine($"[Build/{level}] {msg}");
            OnLog?.Invoke(new LogEntry(level, msg));
        }

        // ── Entry point ────────────────────────────────────────────────────────
        public static async Task<bool> BuildAsync(
            BuildSettings settings, Core.Scene activeScene, bool runAfterBuild)
        {
            Log($"=== Building '{settings.ProjectName}' ===");

            try
            {
                // ── Step 1: Validate ──────────────────────────────────────────
                Log("Step 1/6 — Validating project...");
                if (string.IsNullOrEmpty(settings.ProjectRoot) ||
                    !Directory.Exists(settings.ProjectRoot))
                {
                    Log($"Project root not found: '{settings.ProjectRoot}'", LogLevel.Error);
                    return false;
                }

                // ── Step 2: Save scene ────────────────────────────────────────
                Log("Step 2/6 — Saving scene...");
                string scenesDir = Path.Combine(settings.ProjectRoot, "Assets", "Scenes");
                Directory.CreateDirectory(scenesDir);
                string sceneName = string.IsNullOrEmpty(activeScene.Name) ? "MainScene" : activeScene.Name;
                string sceneFile = Path.Combine(scenesDir, sceneName + ".scene");
                SceneSerializer.Save(activeScene, sceneFile);
                Log($"  Scene saved → {sceneFile}");

                // ── Step 3: Compile user scripts ──────────────────────────────
                Log("Step 3/6 — Compiling user scripts...");
                string scriptsDir = Path.Combine(settings.ProjectRoot, "Assets");
                string[] csFiles = Directory.GetFiles(scriptsDir, "*.cs",
                    SearchOption.AllDirectories);

                string scriptsDll = "";
                if (csFiles.Length > 0)
                {
                    ScriptProjectGenerator.GenerateAll(settings.ProjectRoot);
                    string scriptCsproj = Path.Combine(settings.ProjectRoot, "GameScripts.csproj");
                    if (File.Exists(scriptCsproj))
                    {
                        bool ok = await RunDotnet($"build \"{scriptCsproj}\" -c Release -o \"{settings.ProjectRoot}/ScriptsBin\"");
                        if (!ok) { Log("Script compilation failed.", LogLevel.Error); return false; }
                        scriptsDll = Path.Combine(settings.ProjectRoot, "ScriptsBin", "GameScripts.dll");
                        Log($"  Scripts compiled → {scriptsDll}");
                    }
                    else { Log("  No GameScripts.csproj found, skipping script compilation.", LogLevel.Warning); }
                }
                else { Log("  No .cs scripts found, skipping.", LogLevel.Info); }

                // ── Step 4: Generate host project ─────────────────────────────
                Log("Step 4/6 — Generating host project...");
                string hostDir = Path.Combine(settings.ProjectRoot, ".elintria", "HostProject");
                Directory.CreateDirectory(hostDir);

                string engineDll = Path.Combine(AppContext.BaseDirectory, "ElintriaEngine.dll");
                if (!File.Exists(engineDll))
                    engineDll = Path.Combine(AppContext.BaseDirectory, "../", "ElintriaEngine.dll");

                WriteHostCsproj(hostDir, settings, engineDll, scriptsDll);
                WriteHostProgram(hostDir, settings, sceneName);
                WriteGameRuntime(hostDir);
                Log($"  Host project generated → {hostDir}");

                // ── Step 5: Copy assets ───────────────────────────────────────
                Log("Step 5/6 — Copying assets...");
                string outDir = Path.Combine(settings.ProjectRoot, settings.OutputDirectory);
                Directory.CreateDirectory(outDir);
                string outAssets = Path.Combine(outDir, "Assets");
                CopyDirectory(Path.Combine(settings.ProjectRoot, "Assets"), outAssets);
                Log($"  Assets copied → {outAssets}");

                // ── Step 6: Publish ───────────────────────────────────────────
                Log("Step 6/6 — Publishing...");
                string rid = settings.Target switch
                {
                    BuildTarget.Linux => "linux-x64",
                    BuildTarget.macOS => "osx-x64",
                    _ => "win-x64",
                };
                string hostCsproj = Path.Combine(hostDir, "HostProject.csproj");
                bool pub = await RunDotnet(
                    $"publish \"{hostCsproj}\" -c Release -r {rid} --self-contained true " +
                    $"-o \"{outDir}\"");
                if (!pub) { Log("Publish step failed.", LogLevel.Error); return false; }

                Log($"Build complete → {outDir}", LogLevel.Success);

                if (runAfterBuild)
                {
                    string exe = settings.Target == BuildTarget.Windows
                        ? Path.Combine(outDir, settings.ProjectName + ".exe")
                        : Path.Combine(outDir, settings.ProjectName);
                    if (File.Exists(exe))
                    {
                        Log($"Launching: {exe}", LogLevel.Info);
                        Process.Start(new ProcessStartInfo(exe)
                        { UseShellExecute = true, WorkingDirectory = outDir });
                    }
                    else { Log($"Executable not found: {exe}", LogLevel.Warning); }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Build exception: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        // ── Host project file generators ───────────────────────────────────────

        /// <summary>
        /// Writes the .csproj for the host project.
        /// Uses an explicit &lt;Compile Include="Game.cs" /&gt; so the SDK never
        /// has to glob – there is exactly one source file and it always has a Main.
        /// </summary>
        private static void WriteHostCsproj(string dir, BuildSettings settings,
            string engineDll, string scriptsDll)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <OutputType>Exe</OutputType>");
            sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            sb.AppendLine($"    <AssemblyName>{EscapeXml(settings.ProjectName)}</AssemblyName>");
            // Disable implicit compile-item globbing – we declare the file explicitly
            sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <Optimize>true</Optimize>");
            sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <!-- Exactly one source file: no ambiguity about the entry point -->");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Compile Include=\"Game.cs\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PackageReference Include=\"OpenTK\" Version=\"4.*\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <ItemGroup>");
            if (File.Exists(engineDll))
                sb.AppendLine($"    <Reference Include=\"ElintriaEngine\"><HintPath>{EscapeXml(engineDll)}</HintPath></Reference>");
            if (!string.IsNullOrEmpty(scriptsDll) && File.Exists(scriptsDll))
                sb.AppendLine($"    <Reference Include=\"GameScripts\"><HintPath>{EscapeXml(scriptsDll)}</HintPath></Reference>");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");
            File.WriteAllText(Path.Combine(dir, "HostProject.csproj"), sb.ToString());
        }

        // WriteHostProgram and WriteGameRuntime are replaced by a single method that
        // generates one file (Game.cs) containing both the Program class and GameRuntime.
        // This eliminates every top-level-statement / file-ordering / Vector2i-tuple
        // issue that caused CS5001 in the previous design.
        private static void WriteHostProgram(string dir, BuildSettings settings, string sceneName)
            => WriteAllGameFiles(dir, settings, sceneName);

        private static void WriteGameRuntime(string dir) { /* merged into WriteAllGameFiles */ }

        /// <summary>
        /// Generates a single <c>Game.cs</c> with an explicit <c>static Main</c> and
        /// the full <c>GameRuntime</c> class in one compilation unit.
        /// </summary>
        private static void WriteAllGameFiles(string dir, BuildSettings settings, string sceneName)
        {
            string sceneRelPath = "Assets/Scenes/" + sceneName + ".scene";
            string projName = settings.ProjectName;
            int winW = settings.WindowWidth;
            int winH = settings.WindowHeight;
            bool fullscreen = settings.Fullscreen;

            // Build the content as a regular C# string so we have full control —
            // no $@"..." interpolation to accidentally break string literals inside.
            var sb = new StringBuilder();
            sb.AppendLine("// Generated by Elintria Engine BuildSystem — do not edit manually.");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Drawing;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using OpenTK.Graphics.OpenGL4;");
            sb.AppendLine("using OpenTK.Mathematics;");
            sb.AppendLine("using OpenTK.Windowing.Common;");
            sb.AppendLine("using OpenTK.Windowing.Desktop;");
            sb.AppendLine("using OpenTK.Windowing.GraphicsLibraryFramework;");
            sb.AppendLine("using ElintriaEngine.Core;");
            sb.AppendLine("using ElintriaEngine.Rendering.Scene;");
            sb.AppendLine();

            // ── Program (entry point) ──────────────────────────────────────────
            sb.AppendLine("internal static class Program");
            sb.AppendLine("{");
            sb.AppendLine("    [STAThread]");
            sb.AppendLine("    static void Main()");
            sb.AppendLine("    {");
            sb.AppendLine("        var gs = GameWindowSettings.Default;");
            sb.AppendLine("        var ns = new NativeWindowSettings");
            sb.AppendLine("        {");
            sb.AppendLine($"            Title      = \"{EscapeCs(projName)}\",");
            sb.AppendLine($"            ClientSize = new Vector2i({winW}, {winH}),");
            sb.AppendLine("            APIVersion = new Version(3, 3),");
            sb.AppendLine("            Profile    = ContextProfile.Core,");
            sb.AppendLine("        };");
            sb.AppendLine($"        using var win = new GameRuntime(gs, ns, \"{EscapeCs(sceneRelPath)}\", {(fullscreen ? "true" : "false")});");
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
            sb.AppendLine("        // Load compiled user scripts if present");
            sb.AppendLine("        string scriptDll = Path.Combine(AppContext.BaseDirectory, \"GameScripts.dll\");");
            sb.AppendLine("        if (File.Exists(scriptDll))");
            sb.AppendLine("        {");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                var asm = Assembly.LoadFrom(scriptDll);");
            sb.AppendLine("                foreach (var t in asm.GetExportedTypes())");
            sb.AppendLine("                    if (typeof(Component).IsAssignableFrom(t) && !t.IsAbstract)");
            sb.AppendLine("                        ComponentRegistry.Register(t.Name, t);");
            sb.AppendLine("            }");
            sb.AppendLine("            catch (Exception ex) { Console.WriteLine(\"Scripts load warning: \" + ex.Message); }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Load scene");
            sb.AppendLine("        string full = Path.IsPathRooted(_scenePath)");
            sb.AppendLine("            ? _scenePath");
            sb.AppendLine("            : Path.Combine(AppContext.BaseDirectory, _scenePath);");
            sb.AppendLine("        if (File.Exists(full))");
            sb.AppendLine("            _scene = SceneSerializer.Load(full);");
            sb.AppendLine("        else");
            sb.AppendLine("            Console.WriteLine(\"Scene not found: \" + full);");
            sb.AppendLine();
            sb.AppendLine("        // Resolve DynamicScript placeholders");
            sb.AppendLine("        foreach (var go in _scene.All())");
            sb.AppendLine("            for (int i = go.Components.Count - 1; i >= 0; i--)");
            sb.AppendLine("                if (go.Components[i] is DynamicScript ds)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var real = ComponentRegistry.Create(ds.ScriptTypeName);");
            sb.AppendLine("                    if (real != null) { real.Enabled = ds.Enabled; real.GameObject = go; go.Components[i] = real; }");
            sb.AppendLine("                }");
            sb.AppendLine();
            sb.AppendLine("        // Call OnStart");
            sb.AppendLine("        foreach (var go in _scene.All())");
            sb.AppendLine("            foreach (var comp in go.Components)");
            sb.AppendLine("                try { if (comp.Enabled) comp.OnStart(); }");
            sb.AppendLine("                catch (Exception ex) { Console.WriteLine(\"OnStart error: \" + ex.Message); }");
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
            sb.AppendLine("        if (w != _lastW || h != _lastH) { _lastW = w; _lastH = h; GL.Viewport(0, 0, w, h); }");
            sb.AppendLine();
            sb.AppendLine("        foreach (var go in _scene.All())");
            sb.AppendLine("            foreach (var comp in go.Components)");
            sb.AppendLine("                try { if (comp.Enabled) comp.OnUpdate(args.Time); }");
            sb.AppendLine("                catch (Exception ex) { Console.WriteLine(\"OnUpdate error: \" + ex.Message); }");
            sb.AppendLine();
            sb.AppendLine("        SetSceneCamera(w, h);");
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
            sb.AppendLine("            var t = go.Transform;");
            sb.AppendLine("            float yr = MathHelper.DegreesToRadians(t.LocalEulerAngles.Y);");
            sb.AppendLine("            float xr = MathHelper.DegreesToRadians(t.LocalEulerAngles.X);");
            sb.AppendLine("            var fwd = new Vector3(MathF.Sin(yr)*MathF.Cos(xr), -MathF.Sin(xr), -MathF.Cos(yr)*MathF.Cos(xr));");
            sb.AppendLine("            var pos = t.LocalPosition;");
            sb.AppendLine("            var view = Matrix4.LookAt(pos, pos + fwd, Vector3.UnitY);");
            sb.AppendLine("            Matrix4 proj;");
            sb.AppendLine("            if (cam.IsOrthographic)");
            sb.AppendLine("            {");
            sb.AppendLine("                float half = cam.OrthoSize; float ratio = w / (float)h;");
            sb.AppendLine("                proj = Matrix4.CreateOrthographic(half*ratio*2, half*2, cam.NearClip, cam.FarClip);");
            sb.AppendLine("            }");
            sb.AppendLine("            else");
            sb.AppendLine("                proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(cam.FieldOfView), w/(float)h, cam.NearClip, cam.FarClip);");
            sb.AppendLine("            _renderer.GameViewMatrix = view;");
            sb.AppendLine("            _renderer.GameProjMatrix = proj;");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine("        _renderer.GameViewMatrix = null;");
            sb.AppendLine("        _renderer.GameProjMatrix = null;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnResize(ResizeEventArgs e)");
            sb.AppendLine("    { base.OnResize(e); GL.Viewport(0, 0, e.Width, e.Height); }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnKeyDown(KeyboardKeyEventArgs e)");
            sb.AppendLine("    {");
            sb.AppendLine("        base.OnKeyDown(e);");
            sb.AppendLine("        if (e.Key == Keys.Escape || (e.Alt && e.Key == Keys.F4)) Close();");
            sb.AppendLine("        if (e.Key == Keys.F11)");
            sb.AppendLine("            WindowState = WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnUnload()");
            sb.AppendLine("    {");
            sb.AppendLine("        base.OnUnload();");
            sb.AppendLine("        foreach (var go in _scene.All())");
            sb.AppendLine("            foreach (var comp in go.Components)");
            sb.AppendLine("                try { comp.OnDestroy(); } catch { }");
            sb.AppendLine("        _renderer.Dispose();");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(Path.Combine(dir, "Game.cs"), sb.ToString(), Encoding.UTF8);
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        private static async Task<bool> RunDotnet(string args)
        {
            Log($"  > dotnet {args}");
            var psi = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string stdout = await p.StandardOutput.ReadToEndAsync();
            string stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(stdout))
                foreach (var line in stdout.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Log("  " + line.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Log("  " + line.TrimEnd(), LogLevel.Warning);

            bool ok = p.ExitCode == 0;
            if (!ok) Log($"  dotnet exited with code {p.ExitCode}", LogLevel.Error);
            return ok;
        }

        private static void CopyDirectory(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
            foreach (var sub in Directory.GetDirectories(src))
                CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
        }

        private static string EscapeXml(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
             .Replace("\"", "&quot;");

        /// Escapes a string for embedding inside a C# double-quoted string literal.
        private static string EscapeCs(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

        private static string Str(string s) => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BuildSettingsPanel  –  UI for build settings + log
    // ═══════════════════════════════════════════════════════════════════════════
    public class BuildSettingsPanel : Panel
    {
        private BuildSettings _settings;
        private Core.Scene? _scene;
        private bool _building;
        private readonly List<LogEntry> _log = new();

        // Field edit state
        private string? _editId;
        private string _editBuf = "";
        private Action<string>? _editCommit;

        // Cached UI rects
        private RectangleF _buildBtn, _runBtn, _closeBtn;

        private static readonly Color CErr = Color.FromArgb(255, 220, 60, 60);
        private static readonly Color CWarn = Color.FromArgb(255, 195, 155, 40);
        private static readonly Color COk = Color.FromArgb(255, 60, 185, 60);
        private static readonly Color CInfo = Color.FromArgb(255, 180, 180, 180);

        public BuildSettingsPanel(RectangleF bounds, BuildSettings settings)
            : base("Build Settings", bounds)
        {
            _settings = settings;
            MinWidth = 400f; MinHeight = 360f;

            BuildSystem.OnLog += entry =>
            {
                _log.Add(entry);
                if (_log.Count > 300) _log.RemoveAt(0);
                // Scroll to bottom
                float logH = _log.Count * 16f;
                float avail = Bounds.Height - 280f;
                if (logH > avail) ScrollOffset = logH - avail;
            };
        }

        public void SetScene(Core.Scene s) => _scene = s;

        public void StartBuild(bool runAfter)
        {
            if (_building) return;
            _building = true;
            _log.Clear();
            ScrollOffset = 0;
            _ = Task.Run(async () =>
            {
                await BuildSystem.BuildAsync(_settings, _scene ?? new Core.Scene(), runAfter);
                _building = false;
            });
        }

        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            DrawHeader(r);

            var cr = ContentRect;
            r.FillRect(cr, ColBg);
            r.PushClip(cr);

            float y = cr.Y + 6f;
            float lx = cr.X + 8f;
            float fw = cr.Width - 16f;
            float fh = 20f;

            // Close button (top right of header)
            _closeBtn = new RectangleF(cr.Right + 4f, Bounds.Y + 4f, 14f, 14f);
            r.FillRect(_closeBtn, Color.FromArgb(255, 140, 35, 35));
            r.DrawText("X", new PointF(_closeBtn.X + 2f, _closeBtn.Y + 2f), Color.White, 9f);

            // ── Fields ────────────────────────────────────────────────────────
            DrawField(r, lx, ref y, fw, "Project Name", "pname", _settings.ProjectName, v => _settings.ProjectName = v);
            DrawField(r, lx, ref y, fw, "Output Directory", "outdir", _settings.OutputDirectory, v => _settings.OutputDirectory = v);
            DrawField(r, lx, ref y, fw, "Start Scene", "scene", _settings.StartScene, v => _settings.StartScene = v);
            DrawIntField(r, lx, ref y, fw, "Width", "ww", _settings.WindowWidth, v => _settings.WindowWidth = v);
            DrawIntField(r, lx, ref y, fw, "Height", "wh", _settings.WindowHeight, v => _settings.WindowHeight = v);
            DrawBoolField(r, lx, ref y, fw, "Fullscreen", _settings.Fullscreen, v => _settings.Fullscreen = v);

            // Target platform dropdown (simplified)
            r.DrawText("Target:", new PointF(lx, y + 4f), ColText, 10f);
            float tx = lx + 80f;
            string[] targets = { "Windows", "Linux", "macOS" };
            for (int i = 0; i < targets.Length; i++)
            {
                var tb = new RectangleF(tx, y, 58f, 18f);
                bool sel = (int)_settings.Target == i;
                r.FillRect(tb, sel ? ColAccent : Color.FromArgb(255, 46, 46, 46));
                r.DrawRect(tb, ColBorder);
                r.DrawText(targets[i], new PointF(tx + 4f, y + 4f), ColText, 9f);
                tx += 62f;
            }
            y += 24f;

            r.DrawLine(new PointF(lx, y), new PointF(cr.Right - 8f, y),
                Color.FromArgb(255, 55, 55, 55)); y += 6f;

            // ── Buttons ───────────────────────────────────────────────────────
            _buildBtn = new RectangleF(lx, y, fw / 2f - 4f, 26f);
            _runBtn = new RectangleF(lx + fw / 2f + 4f, y, fw / 2f - 4f, 26f);

            bool idle = !_building;
            r.FillRect(_buildBtn, idle ? Color.FromArgb(255, 40, 100, 40) : Color.FromArgb(255, 40, 60, 40));
            r.DrawRect(_buildBtn, ColBorder);
            r.DrawText(_building ? "Building..." : "Build", new PointF(_buildBtn.X + 10f, _buildBtn.Y + 7f), Color.White, 11f);

            r.FillRect(_runBtn, idle ? Color.FromArgb(255, 40, 60, 130) : Color.FromArgb(255, 28, 28, 50));
            r.DrawRect(_runBtn, ColBorder);
            r.DrawText("Build & Run", new PointF(_runBtn.X + 8f, _runBtn.Y + 7f), Color.White, 11f);

            y += 30f;

            // ── Build log ─────────────────────────────────────────────────────
            r.DrawText("Build Log:", new PointF(lx, y), ColTextDim, 10f); y += 16f;

            float logAreaH = cr.Bottom - y - 4f;
            var logRect = new RectangleF(lx, y, fw, Math.Max(40f, logAreaH));
            r.FillRect(logRect, Color.FromArgb(255, 18, 18, 18));
            r.DrawRect(logRect, ColBorder);
            r.PushClip(logRect);

            float ly = y + 2f - ScrollOffset;
            for (int i = 0; i < _log.Count; i++)
            {
                var entry = _log[i];

                if (ly + 15f > logRect.Y && ly < logRect.Bottom)
                {
                    Color tc = entry.Level switch
                    {
                        LogLevel.Error => CErr,
                        LogLevel.Warning => CWarn,
                        LogLevel.Success => COk,
                        _ => CInfo,
                    };

                    r.DrawText(entry.Message, new PointF(lx + 2f, ly), tc, 9f);
                }
                ly += 14f;
            }
            ContentHeight = (ly + ScrollOffset) - y + 4f;

            r.PopClip();
            r.PopClip();
            DrawScrollBar(r);
        }

        // ── Field helpers ─────────────────────────────────────────────────────
        private void DrawField(IEditorRenderer r, float lx, ref float y, float fw,
            string label, string id, string value, Action<string> setter)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColText, 10f);
            bool ed = _editId == id;
            var fr = new RectangleF(lx + 110f, y, fw - 112f, 18f);
            r.FillRect(fr, ed ? Color.FromArgb(255, 36, 56, 90) : Color.FromArgb(255, 34, 34, 34));
            r.DrawRect(fr, ed ? ColAccent : ColBorder);
            r.DrawText(ed ? _editBuf + "|" : value, new PointF(fr.X + 4f, y + 4f), ColText, 10f);
            y += 22f;
        }

        private void DrawIntField(IEditorRenderer r, float lx, ref float y, float fw,
            string label, string id, int value, Action<int> setter)
        {
            DrawField(r, lx, ref y, fw, label, id, value.ToString(),
                s => { if (int.TryParse(s, out int v)) setter(v); });
        }

        private void DrawBoolField(IEditorRenderer r, float lx, ref float y, float fw,
            string label, bool value, Action<bool> setter)
        {
            r.DrawText(label + ":", new PointF(lx, y + 4f), ColText, 10f);
            var cb = new RectangleF(lx + 110f, y + 2f, 14f, 14f);
            r.FillRect(cb, value ? Color.FromArgb(255, 55, 155, 55) : Color.FromArgb(255, 48, 48, 48));
            r.DrawRect(cb, ColBorder);
            if (value) r.DrawText("ok", new PointF(cb.X + 1f, cb.Y + 2f), Color.White, 8f);
            y += 22f;
        }

        // ── Input ──────────────────────────────────────────────────────────────
        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible) return;

            if (_closeBtn.Contains(pos)) { IsVisible = false; return; }

            if (_editId != null)
            {
                CommitEdit(); return;
            }

            if (_buildBtn.Contains(pos)) { StartBuild(false); return; }
            if (_runBtn.Contains(pos)) { StartBuild(true); return; }

            // Platform buttons
            float lx = ContentRect.X + 8f;
            float y = ContentRect.Y + 6f + 6 * 22f + 24f;  // after 6 fields + target label row
            y -= 24f; // target row
            float tx = lx + 80f;
            for (int i = 0; i < 3; i++)
            {
                if (new RectangleF(tx, y, 58f, 18f).Contains(pos))
                { _settings.Target = (BuildTarget)i; return; }
                tx += 62f;
            }

            // Text field clicks — simple positional approximation
            float fy = ContentRect.Y + 6f;
            string[] ids = { "pname", "outdir", "scene", "ww", "wh" };
            string[] labels = { "Project Name", "Output Directory", "Start Scene", "Width", "Height" };
            for (int i = 0; i < ids.Length; i++)
            {
                var fr = new RectangleF(lx + 110f, fy, ContentRect.Width - 122f, 18f);
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
                    StartEdit(ids[idx], cur, s =>
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
        { if (_editId != null) _editBuf += e.AsString; }

        private void StartEdit(string id, string initial, Action<string> commit)
        { _editId = id; _editBuf = initial; _editCommit = commit; }

        private void CommitEdit()
        { _editCommit?.Invoke(_editBuf); _editId = null; }
    }
}