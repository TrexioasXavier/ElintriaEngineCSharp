using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ElintriaEngine.Build
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  ScriptWatcher
    //
    //  Watches the project's Assets/ folder for any .cs file being created,
    //  modified, or renamed. When a change is detected it waits for a short
    //  quiet period (debounce) so that rapid saves / editor auto-saves only
    //  trigger one build, then calls BuildSystem.CompileScriptsAsync.
    //
    //  Usage:
    //      var watcher = new ScriptWatcher(projectRoot);
    //      watcher.CompilationStarted  += () => { /* show spinner */ };
    //      watcher.CompilationFinished += success => { /* hide spinner */ };
    //      watcher.Start();
    //      ...
    //      watcher.Dispose();  // stops watching
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class ScriptWatcher : IDisposable
    {
        private readonly string _projectRoot;
        private FileSystemWatcher? _fsw;

        // How long to wait after the last file-change event before compiling.
        // 800 ms is enough to avoid double-firing on a single VS "Save" press.
        private const int DebounceMs = 800;

        private Timer? _debounceTimer;
        private bool _compiling;
        private bool _pendingCompile;  // another change arrived while compiling

        // ── Events ────────────────────────────────────────────────────────────
        /// <summary>Fired on a background thread just before dotnet build starts.</summary>
        public event Action? CompilationStarted;

        /// <summary>Fired on a background thread when the build finishes.
        /// <c>true</c> = success, <c>false</c> = error.</summary>
        public event Action<bool>? CompilationFinished;

        /// <summary>Log output from the compiler (one line per call).</summary>
        public event Action<string>? Log;

        public bool IsCompiling => _compiling;

        public ScriptWatcher(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        // ── Start / Stop ───────────────────────────────────────────────────────
        public void Start()
        {
            string assetsDir = Path.Combine(_projectRoot, "Assets");
            if (!Directory.Exists(assetsDir))
            {
                Console.WriteLine($"[ScriptWatcher] Assets directory not found: {assetsDir}");
                return;
            }

            _fsw = new FileSystemWatcher(assetsDir, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite
                                      | NotifyFilters.FileName
                                      | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            _fsw.Changed += OnFileEvent;
            _fsw.Created += OnFileEvent;
            _fsw.Renamed += OnFileEvent;
            _fsw.Deleted += OnFileEvent;

            Console.WriteLine($"[ScriptWatcher] Watching {assetsDir} for .cs changes...");

            // Do an initial compile so the DLL is ready before the user even
            // presses Play for the first time.
            ScheduleCompile();
        }

        public void Dispose()
        {
            _debounceTimer?.Dispose();
            _fsw?.Dispose();
        }

        // ── Internal ───────────────────────────────────────────────────────────
        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            // Ignore changes inside obj/ or bin/ — those are our own build artifacts
            string rel = e.FullPath.Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) return;

            Console.WriteLine($"[ScriptWatcher] Detected change: {e.Name}");
            ScheduleCompile();
        }

        private void ScheduleCompile()
        {
            // Reset the debounce timer — the compile only fires after DebounceMs
            // of silence, so rapid saves collapse into a single build.
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => TriggerCompile(), null, DebounceMs, Timeout.Infinite);
        }

        private void TriggerCompile()
        {
            if (_compiling)
            {
                // A build is already in progress — queue one more run for when it finishes
                _pendingCompile = true;
                return;
            }

            _ = RunCompileAsync();
        }

        private async Task RunCompileAsync()
        {
            _compiling = true;
            Log?.Invoke("[ScriptWatcher] Compiling scripts...");
            CompilationStarted?.Invoke();

            bool success = false;
            try
            {
                string? dll = await BuildSystem.CompileScriptsAsync(_projectRoot);
                success = dll != null;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[ScriptWatcher] Exception: {ex.Message}");
            }

            _compiling = false;
            CompilationFinished?.Invoke(success);
            Log?.Invoke(success
                ? "[ScriptWatcher] Scripts compiled successfully."
                : "[ScriptWatcher] Script compilation failed — check build output.");

            // If another change arrived while we were compiling, run again now
            if (_pendingCompile)
            {
                _pendingCompile = false;
                _ = RunCompileAsync();
            }
        }
    }
}