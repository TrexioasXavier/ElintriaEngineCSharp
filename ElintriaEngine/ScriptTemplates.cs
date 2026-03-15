using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ElintriaEngine.Core
{
    // ── Asset file templates ────────────────────────────────────────────────────
    public static class ScriptTemplates
    {
        public static string CSharpScript(string className) => $@"using System;
using ElintriaEngine.Core;

namespace GameScripts
{{
    public class {className} : Component
    {{
        // ── Public fields (visible in Inspector) ──────────────────────────────
        public float speed  = 5.0f;
        public bool  active = true;

        // Called once before the first frame — like Unity's Start()
        public override void OnStart()
        {{
            Console.WriteLine($""{className} started on {{GameObject?.Name}}"");
        }}

        // Called every frame — like Unity's Update()
        public override void OnUpdate(double deltaTime)
        {{
        }}

        // Called after all Updates — like Unity's LateUpdate()
        public override void OnLateUpdate(double deltaTime)
        {{
        }}

        // Called at a fixed rate (50 Hz) — like Unity's FixedUpdate()
        public override void OnFixedUpdate(double fixedDeltaTime)
        {{
        }}

        // Called once when the component is first created/enabled — like Unity's Awake()
        public override void Awake()
        {{
        }}

        // Called when the component or GameObject is destroyed
        public override void OnDestroy()
        {{
        }}
    }}
}}
";

        public static string Scene(string name = "New Scene") => $@"{{
  ""name"": ""{name}"",
  ""version"": 1,
  ""gameObjects"": []
}}";

        public static string Material() => @"{
  ""shader"": ""Standard"",
  ""albedo"":  [1.0, 1.0, 1.0, 1.0],
  ""metallic"": 0.0,
  ""roughness"": 0.5,
  ""emission"": [0.0, 0.0, 0.0]
}";

        public static string Material(string shaderPath = "Standard") => $@"{{
  ""shader"": ""{shaderPath}"",
  ""properties"": {{
    ""_Color"":     [1.0, 1.0, 1.0, 1.0],
    ""_Metallic"":  0.0,
    ""_Roughness"": 0.5
  }}
}}";

        public static string Shader(string name) => $@"// Elintria Engine Shader – {name}
// Properties block defines the fields shown in the Material Inspector.
// Syntax: _UniformName (""Display Name"", Type) = DefaultValue
//   Types: Float, Int, Range(min,max), Color, Vector, 2D

Properties
{{
    _MainTex    (""Albedo (RGB)"",      2D)          = ""white""
    _Color      (""Tint Color"",        Color)       = (1, 1, 1, 1)
    _Metallic   (""Metallic"",          Range(0, 1)) = 0.0
    _Roughness  (""Roughness"",         Range(0, 1)) = 0.5
    _EmissionColor (""Emission Color"", Color)       = (0, 0, 0, 1)
    _NormalMap  (""Normal Map"",        2D)          = ""bump""
}}

// ── Vertex ──────────────────────────────────────────────────────────────────
#pragma vertex
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMat;

out vec3 vNormal;
out vec2 vTexCoord;
out vec3 vFragPos;

void main()
{{
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vFragPos      = worldPos.xyz;
    vNormal       = uNormalMat * aNormal;
    vTexCoord     = aUV;
    gl_Position   = uProjection * uView * worldPos;
}}

// ── Fragment ─────────────────────────────────────────────────────────────────
#pragma fragment
#version 330 core
in vec3 vNormal;
in vec2 vTexCoord;
in vec3 vFragPos;

uniform sampler2D _MainTex;
uniform vec4      _Color;
uniform float     _Metallic;
uniform float     _Roughness;
uniform vec4      _EmissionColor;

out vec4 FragColor;

void main()
{{
    vec4 albedo   = texture(_MainTex, vTexCoord) * _Color;
    vec3 N        = normalize(vNormal);
    vec3 lightDir = normalize(vec3(0.6, 1.0, 0.5));
    float diff    = max(dot(N, lightDir), 0.0);
    float ambient = 0.2;
    vec3 emission = _EmissionColor.rgb;
    FragColor = vec4((ambient + diff) * albedo.rgb + emission, albedo.a);
}}
";

        public static string PlainText() => "";

        public static string Prefab() => @"{
  ""type"": ""Prefab"",
  ""version"": 1,
  ""root"": null
}";
    }

    // ── Script project / solution generator ────────────────────────────────────
    /// <summary>
    /// Generates <c>.csproj</c> and <c>.sln</c> for a scripts folder so the user
    /// can open and edit scripts in VS / Rider / VS Code with full engine access.
    /// Called by <see cref="ElintriaEngine.UI.Panels.ProjectPanel"/> whenever a
    /// new C# script is created, and by the BuildSystem before compiling.
    /// No static project files are ever shipped – everything is generated here.
    /// </summary>
    public static class ScriptProjectGenerator
    {
        private const string NetTarget = "net10.0";

        // ── Main entry point ───────────────────────────────────────────────────
        /// <summary>
        /// Ensures a <c>GameScripts.csproj</c> and <c>GameScripts.sln</c> exist
        /// in the same folder as <paramref name="scriptPath"/>.
        /// Safe to call multiple times – the .csproj is always regenerated to pick
        /// up new files, while the .sln is only written once.
        /// </summary>
        public static void EnsureProjectForScript(string scriptPath, string projectRoot)
        {
            string scriptsDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
            GenerateProject(scriptsDir, projectRoot);
        }

        /// <summary>
        /// Generates project files for every scripts directory found under
        /// <paramref name="projectRoot"/>/Assets.
        /// Called by the BuildSystem before compilation.
        /// </summary>
        public static void GenerateAll(string projectRoot)
        {
            string assetsDir = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assetsDir)) return;

            // Find all directories containing .cs files
            foreach (var dir in Directory.GetDirectories(assetsDir, "*", SearchOption.AllDirectories))
                if (Directory.GetFiles(dir, "*.cs").Length > 0)
                    GenerateProject(dir, projectRoot);

            // Also handle loose scripts directly in Assets
            if (Directory.GetFiles(assetsDir, "*.cs").Length > 0)
                GenerateProject(assetsDir, projectRoot);
        }

        // ── Core generator ─────────────────────────────────────────────────────
        private static void GenerateProject(string scriptsDir, string projectRoot)
        {
            const string projName = "GameScripts";
            string csprojPath = Path.Combine(scriptsDir, $"{projName}.csproj");
            string slnPath = Path.Combine(scriptsDir, $"{projName}.sln");

            // Locate ElintriaEngine.dll – try several locations in priority order:
            //   1. Next to the running executable (AppContext.BaseDirectory) – normal case
            //   2. projectRoot/Engine/ – user-managed copy
            //   3. projectRoot/../ – project is a sibling of the engine
            string? engineDll = null;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ElintriaEngine.dll"),
                Path.Combine(projectRoot, "Engine", "ElintriaEngine.dll"),
                Path.Combine(projectRoot, "..", "ElintriaEngine.dll"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) { engineDll = Path.GetFullPath(c); break; }

            // Fall back to wherever it runs from so the .csproj is at least syntactically valid
            engineDll ??= Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "ElintriaEngine.dll"));

            string relDll = Path.GetRelativePath(scriptsDir, engineDll)
                               .Replace('/', '\\');

            WriteCsproj(csprojPath, projName, relDll);
            WriteSlnIfAbsent(slnPath, projName, csprojPath);
        }

        // ── .csproj writer ────────────────────────────────────────────────────
        private static void WriteCsproj(string path, string projName, string relDll)
        {
            // Always overwrite so new files are included automatically
            string xml = $@"<Project Sdk=""Microsoft.NET.Sdk"">

  <!--
    ╔═══════════════════════════════════════════════════════╗
    ║  Elintria Engine  –  GameScripts project              ║
    ║  Auto-generated by ScriptProjectGenerator.            ║
    ║  Do NOT commit this file; it is re-generated on save. ║
    ╚═══════════════════════════════════════════════════════╝
  -->

  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>{NetTarget}</TargetFramework>
    <AssemblyName>{projName}</AssemblyName>
    <RootNamespace>GameScripts</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>latest</LangVersion>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <!--  Build output goes to a temp folder so the engine can hot-reload scripts  -->
    <OutputPath>$(MSBuildThisFileDirectory)..\..\.elintria\ScriptsBin\</OutputPath>
  </PropertyGroup>

  <!-- ── Elintria Engine reference ─────────────────────────────────────── -->
  <ItemGroup>
    <Reference Include=""ElintriaEngine"">
      <HintPath>{relDll}</HintPath>
      <Private>false</Private>   <!-- do not copy to output; engine provides it at runtime -->
    </Reference>
  </ItemGroup>

  <!-- ── OpenTK (needed if scripts use math types directly) ──────────── -->
  <ItemGroup>
    <PackageReference Include=""OpenTK"" Version=""4.*"" />
  </ItemGroup>

  <!-- ── Include all .cs files in this directory tree ─────────────────── -->
  <ItemGroup>
    <Compile Include=""**\*.cs"" />
  </ItemGroup>

</Project>
";
            File.WriteAllText(path, xml, Encoding.UTF8);
        }

        // ── .sln writer ───────────────────────────────────────────────────────
        private static void WriteSlnIfAbsent(string slnPath, string projName, string csprojPath)
        {
            if (File.Exists(slnPath)) return;

            // Deterministic GUIDs based on project name so they're stable across machines
            Guid projGuid = DeterministicGuid(projName + ":project");
            Guid slnGuid = DeterministicGuid(projName + ":solution");

            string relCsproj = Path.GetFileName(csprojPath);

            string sln = $@"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.12.35527.113
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{slnGuid.ToString("B").ToUpper()}"") = ""{projName}"", ""{relCsproj}"", ""{projGuid.ToString("B").ToUpper()}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{projGuid.ToString("B").ToUpper()}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{projGuid.ToString("B").ToUpper()}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{projGuid.ToString("B").ToUpper()}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{projGuid.ToString("B").ToUpper()}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
".TrimStart();

            File.WriteAllText(slnPath, sln, Encoding.UTF8);
        }

        private static Guid DeterministicGuid(string seed)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
            return new Guid(hash);
        }
    }
}