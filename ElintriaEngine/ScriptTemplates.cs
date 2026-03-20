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

        // ── Material template ─────────────────────────────────────────────────
        // Produces the new format consumed by MaterialAsset.Load():
        //   { "shader": "...", "properties": { "_Color": [...], ... } }
        // The old flat format (albedo/metallic/roughness) is intentionally
        // retired here; MaterialAsset.Load() still migrates old files on read.
        public static string Material(string shaderPath = "Standard") => $@"{{
  ""shader"": ""{shaderPath}"",
  ""properties"": {{
    ""_Color"":        [1.0, 1.0, 1.0, 1.0],
    ""_Metallic"":     0.0,
    ""_Roughness"":    0.5,
    ""_EmissionColor"": [0.0, 0.0, 0.0, 1.0]
  }}
}}";

        public static string Shader(string name) => $@"// Elintria Engine Shader – {name}
// Properties block defines the fields shown in the Material Inspector.

Properties
{{
    _MainTex       (""Albedo (RGB)"",      2D)          = ""white""
    _Color         (""Tint Color"",        Color)       = (1, 1, 1, 1)
    _Metallic      (""Metallic"",          Range(0, 1)) = 0.0
    _Roughness     (""Roughness"",         Range(0, 1)) = 0.5
    _EmissionColor (""Emission Color"",    Color)       = (0, 0, 0, 1)
    _NormalMap     (""Normal Map"",        2D)          = ""bump""
}}

// -- Vertex ------------------------------------------------------------------
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

// -- Fragment ----------------------------------------------------------------
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
uniform vec3      uCamPos;
uniform float     uAmbient;

#define MAX_DIR_LIGHTS  4
#define MAX_SPOT_LIGHTS 8

uniform int   uDirCount;
uniform vec3  uDirDir  [MAX_DIR_LIGHTS];
uniform vec3  uDirColor[MAX_DIR_LIGHTS];

uniform int   uSpotCount;
uniform vec3  uSpotPos      [MAX_SPOT_LIGHTS];
uniform vec3  uSpotDir      [MAX_SPOT_LIGHTS];
uniform vec3  uSpotColor    [MAX_SPOT_LIGHTS];
uniform float uSpotRange    [MAX_SPOT_LIGHTS];
uniform float uSpotCosInner [MAX_SPOT_LIGHTS];
uniform float uSpotCosOuter [MAX_SPOT_LIGHTS];

out vec4 FragColor;

void main()
{{
    vec4  albedo    = texture(_MainTex, vTexCoord) * _Color;
    vec3  N         = normalize(vNormal);
    vec3  V         = normalize(uCamPos - vFragPos);
    float roughness = max(_Roughness, 0.01);
    float shininess = mix(8.0, 256.0, 1.0 - roughness);

    vec3 Lo = vec3(0.0);

    // Directional lights
    for (int i = 0; i < uDirCount; i++) {{
        vec3  L    = normalize(-uDirDir[i]);
        vec3  H    = normalize(L + V);
        float diff = max(dot(N, L), 0.0);
        float spec = pow(max(dot(N, H), 0.0), shininess);
        Lo += albedo.rgb * diff * uDirColor[i]
            + spec * uDirColor[i] * mix(0.04, 1.0, _Metallic);
    }}

    // Spot lights
    for (int i = 0; i < uSpotCount; i++) {{
        vec3  toFrag = vFragPos - uSpotPos[i];
        float dist   = length(toFrag);

        // Hard cutoff beyond range - no contribution outside range
        if (dist >= uSpotRange[i]) continue;

        // Check the fragment is inside the cone
        float cosA = dot(normalize(toFrag), normalize(uSpotDir[i]));
        float cone = smoothstep(uSpotCosOuter[i], uSpotCosInner[i], cosA);
        if (cone <= 0.0) continue;

        // Inverse square falloff - fades sharply with distance like a real light
        // Normalised so attenuation = 1 at dist=0, fades to 0 at dist=Range
        float normDist = dist / uSpotRange[i];
        float atten = cone / (1.0 + 25.0 * normDist * normDist);

        vec3  L    = normalize(-toFrag);
        vec3  H    = normalize(L + V);
        float diff = max(dot(N, L), 0.0);
        float spec = pow(max(dot(N, H), 0.0), shininess);
        Lo += (albedo.rgb * diff + spec * mix(0.04, 1.0, _Metallic))
              * uSpotColor[i] * atten;
    }}

    vec3 emission = _EmissionColor.rgb;
    vec3 col = albedo.rgb * uAmbient + Lo + emission;
    FragColor = vec4(col, albedo.a);
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
    public static class ScriptProjectGenerator
    {
        private const string NetTarget = "net10.0";

        public static void EnsureProjectForScript(string scriptPath, string projectRoot)
        {
            string scriptsDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
            GenerateProject(scriptsDir, projectRoot);
        }

        public static void GenerateAll(string projectRoot)
        {
            string assetsDir = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assetsDir)) return;

            foreach (var dir in Directory.GetDirectories(assetsDir, "*", SearchOption.AllDirectories))
                if (Directory.GetFiles(dir, "*.cs").Length > 0)
                    GenerateProject(dir, projectRoot);

            if (Directory.GetFiles(assetsDir, "*.cs").Length > 0)
                GenerateProject(assetsDir, projectRoot);
        }

        private static void GenerateProject(string scriptsDir, string projectRoot)
        {
            const string projName = "GameScripts";
            string csprojPath = Path.Combine(scriptsDir, $"{projName}.csproj");
            string slnPath = Path.Combine(scriptsDir, $"{projName}.sln");

            string? engineDll = null;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ElintriaEngine.dll"),
                Path.Combine(projectRoot, "Engine", "ElintriaEngine.dll"),
                Path.Combine(projectRoot, "..", "ElintriaEngine.dll"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) { engineDll = Path.GetFullPath(c); break; }
            engineDll ??= Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "ElintriaEngine.dll"));

            string relDll = Path.GetRelativePath(scriptsDir, engineDll).Replace('/', '\\');
            WriteCsproj(csprojPath, projName, relDll);
            WriteSlnIfAbsent(slnPath, projName, csprojPath);
        }

        private static void WriteCsproj(string path, string projName, string relDll)
        {
            string xml = $@"<Project Sdk=""Microsoft.NET.Sdk"">

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
    <OutputPath>$(MSBuildThisFileDirectory)..\..\.elintria\ScriptsBin\</OutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include=""ElintriaEngine"">
      <HintPath>{relDll}</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include=""OpenTK"" Version=""4.*"" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include=""**\*.cs"" />
  </ItemGroup>

</Project>
";
            File.WriteAllText(path, xml, Encoding.UTF8);
        }

        private static void WriteSlnIfAbsent(string slnPath, string projName, string csprojPath)
        {
            if (File.Exists(slnPath)) return;
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