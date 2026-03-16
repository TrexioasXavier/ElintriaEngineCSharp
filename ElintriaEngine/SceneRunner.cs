using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

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
    //    5. PhysicsSimulation.Step() at fixed 50 Hz  ← Rigidbody integration
    //    6. OnFixedUpdate() at fixed 50 Hz
    //    7. OnUpdate(dt)
    //    8. OnLateUpdate(dt)
    //
    //  On Stop():
    //    9. OnDisable() on all enabled components
    //   10. OnDestroy() on all components
    //   11. PhysicsSimulation.ClearAll() — reset velocity/contact state
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

        /// <summary>
        /// The UI document whose button click events will be dispatched to game scripts.
        /// Set this before calling Start() (or after) — will be used at runtime.
        /// </summary>
        public UIDocument? UIDocument { get; set; }

        public event Action? Started;
        public event Action? Stopped;

        /// <summary>
        /// Called by the game runtime (or test) to fire a button's bound script method.
        /// Finds the first active component whose type name matches TargetScriptName
        /// and invokes the public void method named TargetMethodName.
        /// </summary>
        public bool FireButtonClick(UIButtonElement button)
        {
            if (_scene == null || string.IsNullOrEmpty(button.TargetScriptName)) return false;

            foreach (var go in _scene.All())
            {
                foreach (var comp in go.Components)
                {
                    if (comp.GetType().Name != button.TargetScriptName) continue;
                    if (!InvokeMethod(comp, button.TargetMethodName)) continue;
                    return true;
                }
            }

            var scriptType = ComponentRegistry.TryGetType(button.TargetScriptName);
            if (scriptType != null)
            {
                Console.WriteLine($"[SceneRunner] Script '{button.TargetScriptName}' not on any GO — " +
                                  $"creating UIEventSystem singleton.");

                GameObject? esGo = null;
                foreach (var go in _scene.All())
                    if (go.Name == "__UIEventSystem__") { esGo = go; break; }

                if (esGo == null)
                {
                    esGo = new GameObject("__UIEventSystem__");
                    _scene.AddGameObject(esGo);
                    SubscribeGO(esGo);
                }

                Component? comp = null;
                foreach (var c in esGo.Components)
                    if (c.GetType().Name == button.TargetScriptName) { comp = c; break; }

                if (comp == null)
                {
                    comp = ComponentRegistry.Create(button.TargetScriptName);
                    if (comp != null)
                    {
                        comp.GameObject = esGo;
                        esGo.Components.Add(comp);
                        SafeCall(comp, "Awake", x => x.Awake());
                        SafeCall(comp, "OnEnable", x => x.OnEnable());
                        SafeCall(comp, "OnStart", x => x.OnStart());
                    }
                }

                if (comp != null && InvokeMethod(comp, button.TargetMethodName)) return true;
            }

            Console.WriteLine($"[SceneRunner] No script '{button.TargetScriptName}' with method " +
                              $"'{button.TargetMethodName}' found in scene or registry.");
            return false;
        }

        private bool InvokeMethod(Component comp, string methodName)
        {
            if (string.IsNullOrEmpty(methodName)) return false;
            var method = comp.GetType().GetMethod(methodName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (method == null || method.ReturnType != typeof(void) ||
                method.GetParameters().Length != 0) return false;
            try
            {
                method.Invoke(comp, null);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SceneRunner] Button click invoke error on " +
                    $"{comp.GetType().Name}.{methodName}: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

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

            // Clear any leftover physics state from a previous play session
            PhysicsSimulation.ClearAll();

            // Always point Physics at the live scene before any Awake/Start runs.
            Physics.SetScene(scene);

            LoadUserScripts(projectRoot);
            ResolveDynamicScripts();

            foreach (var go in _scene.All())
                SubscribeGO(go);

            var allComponents = CollectActive();
            foreach (var comp in allComponents)
                SafeCall(comp, "Awake", c => c.Awake());

            foreach (var comp in allComponents)
                SafeCall(comp, "OnEnable", c => c.OnEnable());

            foreach (var comp in allComponents)
                SafeCall(comp, "OnStart", c => c.OnStart());

            foreach (var go in _scene.All())
                foreach (var comp in go.Components)
                    if (comp is ParticleSystem ps && ps.PlayOnAwake && ps.Enabled)
                        ps.Play();

            Started?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Stop
        // ─────────────────────────────────────────────────────────────────────
        public void Stop()
        {
            if (!_started || _scene == null) return;

            foreach (var go in _scene.All())
                UnsubscribeGO(go);

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

            // Clear Rigidbody velocity/contact state so the next play session
            // starts clean (no leftover momentum or stale collision events).
            PhysicsSimulation.ClearAll();

            // DO NOT call Physics.SetScene(null) here — EditorLayout restores
            // the editor scene immediately after Stop() and calls SetScene itself.

            PhysicsDebug.Clear();
            Stopped?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tick — call once per frame from the render loop
        // ─────────────────────────────────────────────────────────────────────
        public void Tick(double dt)
        {
            if (!_started || IsPaused || _scene == null) return;

            PhysicsDebug.Tick((float)dt);

            FlushPending();

            _fixedAccum += dt;
            while (_fixedAccum >= FixedStep)
            {
                _fixedAccum -= FixedStep;

                // ── Physics simulation step ───────────────────────────────────
                // Runs BEFORE OnFixedUpdate so scripts can read already-updated
                // velocity/position in their OnFixedUpdate, exactly like Unity.
                PhysicsSimulation.Step(_scene, (float)FixedStep);

                // ── OnFixedUpdate ─────────────────────────────────────────────
                foreach (var go in _scene.All())
                    if (go.ActiveSelf)
                        foreach (var c in go.Components.ToArray())
                            if (c.Enabled)
                                SafeCall(c, "OnFixedUpdate", x => x.OnFixedUpdate(FixedStep));
            }

            foreach (var go in _scene.All())
                if (go.ActiveSelf)
                    foreach (var c in go.Components.ToArray())
                        if (c.Enabled)
                            SafeCall(c, "OnUpdate", x => x.OnUpdate(dt));

            foreach (var go in _scene.All())
                if (go.ActiveSelf)
                    foreach (var c in go.Components.ToArray())
                        if (c.Enabled)
                            SafeCall(c, "OnLateUpdate", x => x.OnLateUpdate(dt));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Editor-time script resolution
        // ─────────────────────────────────────────────────────────────────────
        private static readonly System.Reflection.Assembly _engineAsm =
            typeof(Component).Assembly;

        public static int ResolveEditorScripts(Scene scene)
        {
            int resolved = 0;
            var userAsm = ComponentRegistry.UserAssembly;

            foreach (var go in scene.All())
            {
                for (int i = go.Components.Count - 1; i >= 0; i--)
                {
                    var comp = go.Components[i];
                    string? typeName = null;
                    bool wasEnabled = comp.Enabled;
                    Dictionary<string, object?>? savedValues = null;

                    if (comp is DynamicScript ds)
                    {
                        typeName = ds.ScriptTypeName;
                        savedValues = ds.FieldValues;
                    }
                    else if (userAsm != null
                             && comp.GetType().Assembly != _engineAsm
                             && comp.GetType().Assembly != userAsm)
                    {
                        typeName = comp.GetType().Name;
                        savedValues = CaptureFieldValues(comp);
                    }

                    if (typeName == null) continue;

                    var fresh = ComponentRegistry.Create(typeName);
                    if (fresh == null) continue;

                    fresh.Enabled = wasEnabled;
                    fresh.GameObject = go;

                    if (savedValues != null)
                        ApplyFieldValues(fresh, savedValues);

                    go.Components[i] = fresh;
                    resolved++;
                    Console.WriteLine($"[Editor] Resolved '{typeName}' on '{go.Name}'");
                }
            }
            return resolved;
        }

        private static Dictionary<string, object?> CaptureFieldValues(Component comp)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var fi in comp.GetType().GetFields(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                try { dict[fi.Name] = fi.GetValue(comp); } catch { }
            }
            return dict;
        }

        private static void ApplyFieldValues(Component target,
                                             Dictionary<string, object?> values)
        {
            foreach (var kv in values)
            {
                var fi = target.GetType().GetField(kv.Key,
                    BindingFlags.Public | BindingFlags.Instance);
                if (fi == null || kv.Value == null) continue;
                try { fi.SetValue(target, Convert.ChangeType(kv.Value, fi.FieldType)); }
                catch { }
            }
        }

        public static void LoadUserScripts(string projectRoot = "")
        {
            string binDir = string.IsNullOrEmpty(projectRoot)
                ? AppContext.BaseDirectory
                : Path.Combine(projectRoot, ".elintria", "Scripts", "bin");

            string? dllPath = FindNewestScriptsDll(binDir);
            if (dllPath == null)
                dllPath = FindNewestScriptsDll(AppContext.BaseDirectory);

            if (dllPath == null)
            {
                Console.WriteLine("[ECS] No GameScripts DLL found — scripts not yet compiled.");
                return;
            }

            Console.WriteLine($"[ECS] Loading: {Path.GetFileName(dllPath)}");
            try
            {
                var asm = Assembly.LoadFrom(dllPath);
                ComponentRegistry.UserAssembly = asm;

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
                Console.WriteLine($"[ECS] {count} script type(s) loaded — Inspector will refresh.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ECS] Failed to load scripts: {ex.Message}");
            }
        }

        private static string? FindNewestScriptsDll(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            string? best = null;
            long bestTick = 0;
            foreach (var f in Directory.GetFiles(dir, "GameScripts*.dll"))
            {
                try
                {
                    long t = File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > bestTick) { bestTick = t; best = f; }
                }
                catch { }
            }
            return best;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

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
                Console.WriteLine($"[ECS] {phase} error on '{c.GameObject?.Name ?? "?"}' " +
                    $"({c.GetType().Name}): {ex.Message}");
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