using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ElintriaEngine.Build
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  ScriptWatcher
    //
    //  Detects .cs file changes using TWO complementary strategies:
    //
    //  1. FileSystemWatcher  — low-latency events for editors that write in-place.
    //
    //  2. Polling timer (every 1.5 s) — catches editors that use atomic
    //     "write-temp-then-rename" saves (VS, VS Code, Rider, Notepad++, etc.)
    //     which sometimes bypass FSW events entirely. Compares each file's
    //     LastWriteTimeUtc against a stored snapshot.
    //
    //  Both strategies feed into the same debounce mechanism so rapid changes
    //  still collapse into a single build.
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class ScriptWatcher : IDisposable
    {
        private readonly string _projectRoot;
        private FileSystemWatcher? _fsw;
        private Timer? _debounceTimer;
        private Timer? _pollTimer;

        // How long after the last detected change before compiling.
        private const int DebounceMs = 600;

        // How often the polling fallback runs.
        private const int PollIntervalMs = 1500;

        // Last-seen timestamps used by the poller (path → LastWriteTimeUtc ticks)
        private readonly Dictionary<string, long> _timestamps = new(StringComparer.OrdinalIgnoreCase);

        private volatile bool _compiling;
        private volatile bool _pendingCompile;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action? CompilationStarted;
        public event Action<bool>? CompilationFinished;
        public event Action<string>? Log;

        public bool IsCompiling => _compiling;

        public ScriptWatcher(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        // ── Start / Stop ──────────────────────────────────────────────────────
        public void Start()
        {
            string assetsDir = Path.Combine(_projectRoot, "Assets");
            if (!Directory.Exists(assetsDir))
            {
                Console.WriteLine($"[ScriptWatcher] Assets directory not found: {assetsDir}");
                return;
            }

            // ── Strategy 1: FileSystemWatcher ─────────────────────────────────
            try
            {
                _fsw = new FileSystemWatcher(assetsDir, "*.cs")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite
                                          | NotifyFilters.FileName
                                          | NotifyFilters.Size
                                          | NotifyFilters.Attributes,
                    // Larger buffer to avoid losing events during rapid multi-file saves
                    InternalBufferSize = 65536,
                    EnableRaisingEvents = true,
                };

                _fsw.Changed += OnFswEvent;
                _fsw.Created += OnFswEvent;
                _fsw.Renamed += OnFswEvent;
                _fsw.Deleted += OnFswEvent;
                _fsw.Error += OnFswError;

                Console.WriteLine($"[ScriptWatcher] FSW watching: {assetsDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScriptWatcher] FSW failed ({ex.Message}), polling only.");
            }

            // ── Strategy 2: Polling fallback ──────────────────────────────────
            // Seed the timestamp snapshot so the very first poll doesn't fire
            // immediately for every file that already exists.
            SnapshotTimestamps(assetsDir);

            _pollTimer = new Timer(_ => PollForChanges(assetsDir),
                                   null, PollIntervalMs, PollIntervalMs);

            Console.WriteLine("[ScriptWatcher] Polling fallback active.");

            // Initial compile so the DLL is ready before the user first presses Play.
            ScheduleCompile();
        }

        public void Dispose()
        {
            _debounceTimer?.Dispose();
            _pollTimer?.Dispose();
            _fsw?.Dispose();
        }

        // ── FSW handler ───────────────────────────────────────────────────────
        private void OnFswEvent(object sender, FileSystemEventArgs e)
        {
            string rel = e.FullPath.Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) return;
            Console.WriteLine($"[ScriptWatcher] FSW change: {e.Name}");
            ScheduleCompile();
        }

        private void OnFswError(object sender, ErrorEventArgs e)
        {
            Console.WriteLine($"[ScriptWatcher] FSW error: {e.GetException().Message} — polling will cover.");
        }

        // ── Polling handler ───────────────────────────────────────────────────
        private void PollForChanges(string assetsDir)
        {
            if (!Directory.Exists(assetsDir)) return;

            bool changed = false;
            try
            {
                var files = Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    string rel = f.Replace('\\', '/');
                    if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;

                    long ticks = 0;
                    try { ticks = File.GetLastWriteTimeUtc(f).Ticks; } catch { continue; }

                    if (!_timestamps.TryGetValue(f, out long prev) || prev != ticks)
                    {
                        _timestamps[f] = ticks;
                        if (prev != 0)  // skip first-seen files (they were seeded)
                        {
                            Console.WriteLine($"[ScriptWatcher] Poll detected change: {Path.GetFileName(f)}");
                            changed = true;
                        }
                    }
                }

                // Also detect deletions
                var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                var toRemove = new List<string>();
                foreach (var k in _timestamps.Keys)
                    if (!fileSet.Contains(k)) { toRemove.Add(k); changed = true; }
                foreach (var k in toRemove) _timestamps.Remove(k);
            }
            catch { /* best-effort */ }

            if (changed) ScheduleCompile();
        }

        private void SnapshotTimestamps(string assetsDir)
        {
            _timestamps.Clear();
            try
            {
                foreach (var f in Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories))
                {
                    try { _timestamps[f] = File.GetLastWriteTimeUtc(f).Ticks; } catch { }
                }
            }
            catch { }
        }

        // ── Compile pipeline ──────────────────────────────────────────────────
        private void ScheduleCompile()
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => TriggerCompile(), null, DebounceMs, Timeout.Infinite);
        }

        private void TriggerCompile()
        {
            if (_compiling) { _pendingCompile = true; return; }
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
                : "[ScriptWatcher] Script compilation failed.");

            if (_pendingCompile)
            {
                _pendingCompile = false;
                _ = RunCompileAsync();
            }
        }
    }
}