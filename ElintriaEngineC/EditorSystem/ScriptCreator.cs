using System.IO;

namespace Elintria.Editor
{
    // =========================================================================
    // ScriptCreator
    // =========================================================================
    /// <summary>
    /// Creates new C# script files and opens them in Visual Studio via the
    /// generated Build/ElintriaBuild.sln solution file.
    ///
    /// Double-clicking a script in the ProjectPanel calls OpenInEditor(path),
    /// which opens the .sln in Visual Studio — so the full project context
    /// (references, intellisense) is available immediately.
    /// </summary>
    public static class ScriptCreator
    {
        private const string SCRIPTS_DIR = "data/Scripts";
        private const string BUILD_DIR = "Build";

        static ScriptCreator()
        {
            Directory.CreateDirectory(SCRIPTS_DIR);
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Creates a new C# Component script.
        /// Returns the full path of the new file, or null if it already exists.
        /// </summary>
        public static string CreateScript(string name)
        {
            if (!name.EndsWith(".cs")) name += ".cs";
            string path = Path.Combine(SCRIPTS_DIR, name);
            if (File.Exists(path)) return null;

            string className = Path.GetFileNameWithoutExtension(name);
            File.WriteAllText(path, ComponentTemplate(className));
            Console.WriteLine($"[Scripts] Created {path}");

            // Regenerate the Build project so VS sees the new file immediately
            EnsureProjectExists();

            return path;
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Opens a script file in Visual Studio by opening the solution.
        /// Falls back to the system default editor if VS cannot be found.
        /// </summary>
        public static void OpenInEditor(string scriptPath)
        {
            if (!File.Exists(scriptPath)) return;

            // Make sure a .sln exists
            EnsureProjectExists();

            string slnPath = BuildSystem.LastSolutionPath
                          ?? Path.GetFullPath(Path.Combine(BUILD_DIR, "ElintriaBuild.sln"));

            if (File.Exists(slnPath))
            {
                // Try to open the solution in Visual Studio.
                // Passing the script path as a second argument tells VS to
                // navigate directly to that file once the solution loads.
                bool opened = TryOpenWithVisualStudio(slnPath, scriptPath);
                if (opened) return;
            }

            // Fallback: open the script with whatever the OS associates with .cs
            OpenWithShell(scriptPath);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private static void EnsureProjectExists()
        {
            string slnPath = Path.GetFullPath(Path.Combine(BUILD_DIR, "ElintriaBuild.sln"));
            // Always regenerate so new scripts are included
            BuildSystem.GenerateProjectOnly(BUILD_DIR);
        }

        private static bool TryOpenWithVisualStudio(string slnPath, string scriptPath)
        {
            // Common Visual Studio devenv.exe locations (VS 2019, 2022)
            string[] searchPaths =
            {
                // VS 2022 Professional / Community / Enterprise
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe",
                // VS 2019
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\Common7\IDE\devenv.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Common7\IDE\devenv.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\devenv.exe",
            };

            foreach (var devenv in searchPaths)
            {
                if (!File.Exists(devenv)) continue;

                try
                {
                    // devenv.exe "solution.sln" /edit "script.cs"
                    // /edit opens the file in an existing VS instance or starts one
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = devenv,
                        Arguments = $"\"{slnPath}\" /edit \"{scriptPath}\"",
                        UseShellExecute = false
                    };
                    System.Diagnostics.Process.Start(psi);
                    Console.WriteLine($"[Scripts] Opened in Visual Studio: {scriptPath}");
                    return true;
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine($"[Scripts] VS launch failed: {ex.Message}");
                }
            }

            // Try the .sln with shell open (Windows will pick VS if associated)
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = slnPath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
                Console.WriteLine($"[Scripts] Opened solution: {slnPath}");
                return true;
            }
            catch { }

            return false;
        }

        private static void OpenWithShell(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"[Scripts] Could not open: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        private static string ComponentTemplate(string className) => $@"using Elintria.Engine;
using OpenTK.Mathematics;

/// <summary>
/// Custom component — attach to any GameObject via AddComponent<{className}>().
/// </summary>
public class {className} : Component
{{
    // ── Inspector-visible fields ──────────────────────────────────────────
    public float Speed {{ get; set; }} = 1.0f;

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public override void Awake()
    {{
        // Called when the component is first created
    }}

    public override void Start()
    {{
        // Called once before the first Update()
    }}

    public override void Update(float dt)
    {{
        // Called every frame  (dt = delta time in seconds)
    }}

    public override void OnDestroy()
    {{
        // Called when the component or its GameObject is destroyed
    }}
}}
";
    }
}