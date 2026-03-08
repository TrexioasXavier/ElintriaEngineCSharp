using Elintria.Engine;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Elintria.Editor
{
    // =========================================================================
    // BuildSystem — compiles the game into a standalone .exe
    // =========================================================================
    public static class BuildSystem
    {
        // Path of the last-generated solution (used by ScriptCreator to open it)
        public static string LastSolutionPath { get; private set; }

        // ------------------------------------------------------------------
        public static void Build(Scene scene, string outputDir)
        {
            Console.WriteLine("[Build] Starting build...");

            string buildDir = Path.GetFullPath(outputDir);
            string srcDir = Path.Combine(buildDir, "src");
            string scriptsDir = Path.GetFullPath("data/Scripts");

            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(buildDir);

            // 1. Gather user scripts
            var userScripts = new List<string>();
            if (Directory.Exists(scriptsDir))
                userScripts.AddRange(
                    Directory.GetFiles(scriptsDir, "*.cs", SearchOption.AllDirectories));

            Console.WriteLine($"[Build] Found {userScripts.Count} user script(s).");

            // 2. Copy user scripts into src/
            foreach (var script in userScripts)
            {
                string dest = Path.Combine(srcDir, Path.GetFileName(script));
                File.Copy(script, dest, overwrite: true);
            }

            // 3. Generate entry-point (explicit class + Main — never ambiguous)
            string sceneName = scene?.Name ?? "Game";
            File.WriteAllText(Path.Combine(srcDir, "Program.cs"),
                GenerateBootstrap(sceneName));

            // 4. Locate engine DLLs next to the running editor
            string editorDir = Path.GetDirectoryName(
                Assembly.GetEntryAssembly()?.Location
                ?? Assembly.GetExecutingAssembly().Location)!;

            var engineDlls = Directory.GetFiles(editorDir, "*.dll")
                .Where(f =>
                {
                    string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return n.StartsWith("elintria") || n == "elintriaenginec";
                })
                .ToList();

            Console.WriteLine($"[Build] Engine DLLs found: {engineDlls.Count}");
            foreach (var d in engineDlls)
                Console.WriteLine($"[Build]   {Path.GetFileName(d)}");

            // 5. Create output folder and pre-copy ALL DLLs from editor dir
            //    (engine libs + OpenTK native libs + anything else needed at runtime)
            string outDir = Path.Combine(buildDir, "out");
            Directory.CreateDirectory(outDir);

            foreach (var dll in Directory.GetFiles(editorDir, "*.dll"))
            {
                string dest = Path.Combine(outDir, Path.GetFileName(dll));
                File.Copy(dll, dest, overwrite: true);
            }
            // Also copy any native .so / .dylib / runtimes folder if present
            string runtimesDir = Path.Combine(editorDir, "runtimes");
            if (Directory.Exists(runtimesDir))
                CopyDirectory(runtimesDir, Path.Combine(outDir, "runtimes"));

            Console.WriteLine($"[Build] Copied {Directory.GetFiles(outDir, "*.dll").Length} DLL(s) to out/");

            // 6. Generate .csproj
            string csprojName = "ElintriaBuild";
            string csprojPath = Path.Combine(buildDir, $"{csprojName}.csproj");
            File.WriteAllText(csprojPath, GenerateCsproj(engineDlls, editorDir));

            // 7. Generate .sln so scripts open the whole project in Visual Studio
            string slnPath = Path.Combine(buildDir, $"{csprojName}.sln");
            File.WriteAllText(slnPath, GenerateSln(csprojName, $"{csprojName}.csproj"));
            LastSolutionPath = slnPath;
            Console.WriteLine($"[Build] Solution: {slnPath}");

            // 8. dotnet build
            Console.WriteLine("[Build] Invoking dotnet build...");
            var result = RunProcess("dotnet",
                $"build \"{csprojPath}\" --output \"{outDir}\" " +
                "--configuration Release --nologo",
                buildDir);

            if (result.ExitCode == 0)
                Console.WriteLine($"[Build] ✔ Build succeeded!  Output: {outDir}");
            else
                Console.WriteLine(
                    $"[Build] ✖ Build failed (exit {result.ExitCode}):\n{result.StdErr}");
        }

        // ------------------------------------------------------------------
        // Also generate just the .sln + .csproj WITHOUT building
        // (called by ScriptCreator so you can open scripts in VS immediately)
        // ------------------------------------------------------------------
        public static void GenerateProjectOnly(string outputDir = "Build")
        {
            string buildDir = Path.GetFullPath(outputDir);
            string srcDir = Path.Combine(buildDir, "src");
            string scriptsDir = Path.GetFullPath("data/Scripts");

            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(buildDir);

            // Copy scripts
            if (Directory.Exists(scriptsDir))
                foreach (var s in Directory.GetFiles(scriptsDir, "*.cs", SearchOption.AllDirectories))
                    File.Copy(s, Path.Combine(srcDir, Path.GetFileName(s)), overwrite: true);

            // Stub Program.cs so the project is valid even before a full build
            string stubProgram = Path.Combine(srcDir, "Program.cs");
            if (!File.Exists(stubProgram))
                File.WriteAllText(stubProgram, GenerateBootstrap("Game"));

            string editorDir = Path.GetDirectoryName(
                Assembly.GetEntryAssembly()?.Location
                ?? Assembly.GetExecutingAssembly().Location)!;

            var engineDlls = Directory.GetFiles(editorDir, "*.dll")
                .Where(f =>
                {
                    string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return n.StartsWith("elintria") || n == "elintriaenginec";
                })
                .ToList();

            string csprojName = "ElintriaBuild";
            string csprojPath = Path.Combine(buildDir, $"{csprojName}.csproj");
            string slnPath = Path.Combine(buildDir, $"{csprojName}.sln");

            File.WriteAllText(csprojPath, GenerateCsproj(engineDlls, editorDir));
            File.WriteAllText(slnPath, GenerateSln(csprojName, $"{csprojName}.csproj"));
            LastSolutionPath = slnPath;

            Console.WriteLine($"[Build] Project generated: {slnPath}");
        }

        // ------------------------------------------------------------------
        private static string GenerateBootstrap(string sceneName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated by Elintria Build System — do not edit.");
            sb.AppendLine("using Elintria.Engine;");
            sb.AppendLine("using ElintriaEngineC.WindowCreation;");
            sb.AppendLine("using OpenTK.Mathematics;");
            sb.AppendLine();
            sb.AppendLine("namespace ElintriaBuild");
            sb.AppendLine("{");
            sb.AppendLine("    internal static class Program");
            sb.AppendLine("    {");
            sb.AppendLine("        private static void Main(string[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            SceneManager.LoadScene(0);");
            sb.AppendLine();
            sb.AppendLine($"            var win = new EWindow(1280, 720, \"{sceneName}\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        private static string GenerateCsproj(List<string> engineDlls, string editorDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <OutputType>Exe</OutputType>");
            sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            // Disable implicit usings (avoids conflicts with engine types)
            sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
            // CRITICAL: stop the SDK auto-globbing .cs files outside src/
            // Without this, Program.cs would be compiled twice and cause CS5001
            sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
            sb.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");
            sb.AppendLine("  </PropertyGroup>");

            // Only compile files in src/
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Compile Include=\"src\\**\\*.cs\" />");
            sb.AppendLine("  </ItemGroup>");

            // NuGet packages — OpenTK 4.8.2 provides all windowing + math
            // (no separate OpenTK.Windowing.Desktop needed; it's included)
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PackageReference Include=\"OpenTK\" Version=\"4.8.2\" />");
            sb.AppendLine("  </ItemGroup>");

            // Engine DLL references
            sb.AppendLine("  <ItemGroup>");
            foreach (var dll in engineDlls)
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                sb.AppendLine($"    <Reference Include=\"{name}\">");
                sb.AppendLine($"      <HintPath>{dll}</HintPath>");
                sb.AppendLine("      <Private>true</Private>");
                sb.AppendLine("    </Reference>");
            }

            // Editor DLL (so user scripts can reference editor-side types if needed)
            string editorDll = FindEditorDll(editorDir, engineDlls);
            if (editorDll != null)
            {
                string edName = Path.GetFileNameWithoutExtension(editorDll);
                sb.AppendLine($"    <Reference Include=\"{edName}\">");
                sb.AppendLine($"      <HintPath>{editorDll}</HintPath>");
                sb.AppendLine("      <Private>true</Private>");
                sb.AppendLine("    </Reference>");
            }

            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        private static string GenerateSln(string projectName, string csprojRelPath)
        {
            // Visual Studio 2022 solution format
            // The GUID types here are standard VS C# project type GUIDs
            string slnProjGuid = Guid.NewGuid().ToString("B").ToUpper();
            string slnGuid = Guid.NewGuid().ToString("B").ToUpper();

            var sb = new StringBuilder();
            sb.AppendLine("");
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");
            sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
            sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
            sb.AppendLine($"Project(\"{{{FAEProjectTypeGuid}}}\") = \"{projectName}\", \"{csprojRelPath}\", \"{slnProjGuid}\"");
            sb.AppendLine("EndProject");
            sb.AppendLine("Global");
            sb.AppendLine("	GlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("		Debug|Any CPU = Debug|Any CPU");
            sb.AppendLine("		Release|Any CPU = Release|Any CPU");
            sb.AppendLine("	EndGlobalSection");
            sb.AppendLine("	GlobalSection(ProjectConfigurationPlatforms) = postSolution");
            sb.AppendLine($"		{slnProjGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"		{slnProjGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"		{slnProjGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"		{slnProjGuid}.Release|Any CPU.Build.0 = Release|Any CPU");
            sb.AppendLine("	EndGlobalSection");
            sb.AppendLine("	GlobalSection(SolutionProperties) = preSolution");
            sb.AppendLine("		HideSolutionNode = FALSE");
            sb.AppendLine("	EndGlobalSection");
            sb.AppendLine("	GlobalSection(ExtensibilityGlobals) = postSolution");
            sb.AppendLine($"		SolutionGuid = {slnGuid}");
            sb.AppendLine("	EndGlobalSection");
            sb.AppendLine("EndGlobal");
            return sb.ToString();
        }

        // FAE04EC0-301F-11D3-BF4B-00C04F79EFBC = C# project type GUID for VS
        private const string FAEProjectTypeGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";

        // ------------------------------------------------------------------
        private static string FindEditorDll(string editorDir, List<string> already)
        {
            string specific = Path.Combine(editorDir, "ElintriaEditor.dll");
            if (File.Exists(specific)) return specific;
            return Directory.GetFiles(editorDir, "Elintria*.dll")
                .FirstOrDefault(f => !already.Contains(f));
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // ------------------------------------------------------------------
        private static (int ExitCode, string StdOut, string StdErr) RunProcess(
            string exe, string args, string workDir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Console.Write(stdout);
            return (proc.ExitCode, stdout, stderr);
        }
    }
}