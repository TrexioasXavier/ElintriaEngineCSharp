using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ElintriaEngine.Core
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  SceneRunner  —  Unity-identical ECS lifecycle manager
    //
    //  On Start():
    //    1. LoadUserScripts()      load + register GameScripts.dll
    //    2. ResolveDynamicScripts  replace DynamicScript placeholders with real types
    //    3. For every active GO, every enabled component:
    //         Awake() → OnEnable() → OnStart()
    //
    //  Every Tick(dt):
    //    4. Bootstrap any components added mid-frame (Awake→OnEnable→OnStart)
    //    5. OnFixedUpdate() at fixed 50 Hz
    //    6. OnUpdate(dt)
    //    7. OnLateUpdate(dt)
    //
    //  On Stop():
    //    8. OnDisable() on all enabled components
    //    9. OnDestroy() on all components
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class SceneRunner : IDisposable
    {
        private Scene? _scene;
        private bool _started;

        // Components added mid-frame via AddComponent() during play
        private readonly Queue<Component> _pendingStart = new();

        // Fixed-update accumulator
        private double _fixedAccum;
        private const double FixedStep = 1.0 / 50.0;

        public bool IsRunning => _started;
        public bool IsPaused { get; set; }

        public event Action? Started;
        public event Action? Stopped;

        // ─────────────────────────────────────────────────────────────────────
        //  Start
        // ─────────────────────────────────────────────────────────────────────
        public void Start(Scene scene, string projectRoot = "")
        {
            if (_started) Stop();

            _scene = scene;
            _started = true;
            IsPaused = false;
            _fixedAccum = 0;
            _pendingStart.Clear();

            // Step 1 — load compiled user scripts
            LoadUserScripts(projectRoot);

            // Step 2 — resolve DynamicScript placeholders
            ResolveDynamicScripts();

            // Step 3 — subscribe to ComponentAdded on every GO so mid-play
            // AddComponent() calls get bootstrapped automatically
            foreach (var go in _scene.All())
                SubscribeGO(go);

            // Step 4 — Awake → OnEnable → OnStart for everything
            // Unity calls Awake on ALL objects first, then OnEnable, then Start
            var allComponents = CollectActive();
            foreach (var comp in allComponents)
                SafeCall(comp, "Awake", c => c.Awake());

            foreach (var comp in allComponents)
                SafeCall(comp, "OnEnable", c => c.OnEnable());

            foreach (var comp in allComponents)
                SafeCall(comp, "OnStart", c => c.OnStart());

            Started?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Stop
        // ─────────────────────────────────────────────────────────────────────
        public void Stop()
        {
            if (!_started || _scene == null) return;

            // Unsubscribe events
            foreach (var go in _scene.All())
                UnsubscribeGO(go);

            // OnDisable all enabled first, then OnDestroy all
            foreach (var go in _scene.All())
                foreach (var comp in go.Components.ToArray())
                    if (comp.Enabled)
                        SafeCall(comp, "OnDisable", c => c.OnDisable());

            foreach (var go in _scene.All())
                foreach (var comp in go.Components.ToArray())
                    SafeCall(comp, "OnDestroy", c => c.OnDestroy());

            _pendingStart.Clear();
            _started = false;
            _scene = null;
            Stopped?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tick — call once per frame from the render loop
        // ─────────────────────────────────────────────────────────────────────
        public void Tick(double dt)
        {
            if (!_started || IsPaused || _scene == null) return;

            // Bootstrap components added since last frame
            FlushPending();

            // FixedUpdate (50 Hz)
            _fixedAccum += dt;
            while (_fixedAccum >= FixedStep)
            {
                _fixedAccum -= FixedStep;
                foreach (var go in _scene.All())
                    if (go.ActiveSelf)
                        foreach (var c in go.Components.ToArray())
                            if (c.Enabled)
                                SafeCall(c, "OnFixedUpdate", x => x.OnFixedUpdate(FixedStep));
            }

            // Update
            foreach (var go in _scene.All())
                if (go.ActiveSelf)
                    foreach (var c in go.Components.ToArray())
                        if (c.Enabled)
                            SafeCall(c, "OnUpdate", x => x.OnUpdate(dt));

            // LateUpdate
            foreach (var go in _scene.All())
                if (go.ActiveSelf)
                    foreach (var c in go.Components.ToArray())
                        if (c.Enabled)
                            SafeCall(c, "OnLateUpdate", x => x.OnLateUpdate(dt));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Script loading
        // ─────────────────────────────────────────────────────────────────────

        public static void LoadUserScripts(string projectRoot = "")
        {
            // Locate the compiled GameScripts.dll
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(projectRoot))
                candidates.Add(Path.Combine(projectRoot, ".elintria", "Scripts", "bin", "GameScripts.dll"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "GameScripts.dll"));

            string? dllPath = null;
            foreach (var c in candidates)
            {
                if (File.Exists(c)) { dllPath = c; break; }
                Console.WriteLine($"[ECS] Not found: {c}");
            }

            if (dllPath == null)
            {
                Console.WriteLine("[ECS] No GameScripts.dll found — user scripts will not run.");
                return;
            }

            Console.WriteLine($"[ECS] Loading scripts from: {dllPath}");
            try
            {
                // Copy to a unique temp path on every load.
                // Assembly.LoadFrom caches by file path, so using a unique name
                // guarantees a fresh load each play session without needing a
                // custom AssemblyLoadContext (which has type-identity pitfalls).
                string tempDir = Path.Combine(Path.GetTempPath(), "ElintriaScripts");
                Directory.CreateDirectory(tempDir);

                // Clean up old temp copies (best-effort, ignore failures)
                foreach (var old in Directory.GetFiles(tempDir, "GameScripts_*.dll"))
                    try { File.Delete(old); } catch { }

                string sessionDll = Path.Combine(tempDir, $"GameScripts_{Guid.NewGuid():N}.dll");
                File.Copy(dllPath, sessionDll);

                var asm = Assembly.LoadFrom(sessionDll);
                int count = 0;
                foreach (var type in asm.GetExportedTypes())
                {
                    if (type.IsAbstract || !typeof(Component).IsAssignableFrom(type)) continue;
                    ComponentRegistry.Register(type.Name, type);
                    if (type.FullName != null)
                        ComponentRegistry.Register(type.FullName, type);
                    Console.WriteLine($"[ECS]   Registered: {type.FullName}");
                    count++;
                }
                Console.WriteLine($"[ECS] Loaded {count} script type(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ECS] Failed to load GameScripts.dll: {ex}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// Replace every DynamicScript placeholder with the real compiled type.
        private void ResolveDynamicScripts()
        {
            if (_scene == null) return;
            foreach (var go in _scene.All())
            {
                for (int i = go.Components.Count - 1; i >= 0; i--)
                {
                    if (go.Components[i] is not DynamicScript ds) continue;
                    var real = ComponentRegistry.Create(ds.ScriptTypeName);
                    if (real == null)
                    {
                        Console.WriteLine($"[ECS] Could not resolve script '{ds.ScriptTypeName}' on '{go.Name}'");
                        continue;
                    }
                    real.Enabled = ds.Enabled;
                    real.GameObject = go;
                    CopyPublicFields(ds, real);
                    go.Components[i] = real;
                    Console.WriteLine($"[ECS] Resolved '{ds.ScriptTypeName}' on '{go.Name}'");
                }
            }
        }

        private List<Component> CollectActive()
        {
            var list = new List<Component>();
            if (_scene == null) return list;
            foreach (var go in _scene.All())
                if (go.ActiveSelf)
                    foreach (var c in go.Components)
                        if (c.Enabled) list.Add(c);
            return list;
        }

        private void FlushPending()
        {
            while (_pendingStart.Count > 0)
            {
                var c = _pendingStart.Dequeue();
                if (!c.Enabled) continue;
                SafeCall(c, "Awake", x => x.Awake());
                SafeCall(c, "OnEnable", x => x.OnEnable());
                SafeCall(c, "OnStart", x => x.OnStart());
            }
        }

        private void SubscribeGO(GameObject go)
            => go.ComponentAdded += OnComponentAddedMidPlay;

        private void UnsubscribeGO(GameObject go)
            => go.ComponentAdded -= OnComponentAddedMidPlay;

        private void OnComponentAddedMidPlay(Component comp)
            => _pendingStart.Enqueue(comp);

        private static void SafeCall(Component c, string phase, Action<Component> fn)
        {
            try { fn(c); }
            catch (Exception ex)
            {
                Console.WriteLine($"[ECS] {phase} error on '{c.GameObject?.Name ?? "?"}' ({c.GetType().Name}): {ex.Message}");
            }
        }

        private static void CopyPublicFields(Component src, Component dst)
        {
            var dstType = dst.GetType();
            foreach (var fi in src.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var dfi = dstType.GetField(fi.Name, BindingFlags.Public | BindingFlags.Instance);
                if (dfi != null && dfi.FieldType == fi.FieldType)
                    try { dfi.SetValue(dst, fi.GetValue(src)); } catch { }
            }
        }

        public void Dispose() => Stop();
    }
}