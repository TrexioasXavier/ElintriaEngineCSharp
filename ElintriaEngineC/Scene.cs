using Elintria.Engine.Rendering;
using OpenTK.Mathematics;
using System.Collections.Generic;
using System.Linq;

namespace Elintria.Engine
{
    // =========================================================================
    // Scene
    // =========================================================================
    /// <summary>
    /// A Scene is a container of GameObjects.
    /// Mirrors Unity's Scene struct/API.
    ///
    /// Scenes are managed by SceneManager — don't construct them directly;
    /// use SceneManager.CreateScene() or SceneManager.LoadScene().
    /// </summary>
    public class Scene
    {
        // ------------------------------------------------------------------
        // Identity
        // ------------------------------------------------------------------
        public string Name { get; internal set; }
        public int BuildIndex { get; internal set; } = -1;
        public bool IsLoaded { get; internal set; } = false;

        // ------------------------------------------------------------------
        // GameObjects
        // ------------------------------------------------------------------
        private readonly List<GameObject> _objects = new();
        private readonly List<GameObject> _toAdd = new();   // deferred add
        private readonly List<GameObject> _toDestroy = new();   // deferred destroy

        public IReadOnlyList<GameObject> GameObjects => _objects;

        // ------------------------------------------------------------------
        // Root GameObjects (no parent)
        // ------------------------------------------------------------------
        public IEnumerable<GameObject> RootObjects
            => _objects.Where(go => go.Transform.Parent == null);

        // ------------------------------------------------------------------
        // Add / Remove
        // ------------------------------------------------------------------
        /// <summary>
        /// Add a new empty GameObject to the scene and return it.
        /// </summary>
        public GameObject CreateGameObject(string name = "GameObject")
        {
            var go = new GameObject(name) { Scene = this };
            _toAdd.Add(go);
            return go;
        }

        /// <summary>
        /// Add an existing GameObject to this scene (e.g. from a prefab).
        /// </summary>
        public void AddGameObject(GameObject go)
        {
            go.Scene = this;
            _toAdd.Add(go);
        }

        /// <summary>
        /// Schedule a GameObject for destruction at end of frame.
        /// Mirrors Unity's Object.Destroy(go) behaviour.
        /// </summary>
        public void Destroy(GameObject go)
        {
            if (!_toDestroy.Contains(go))
                _toDestroy.Add(go);
        }

        /// <summary>Immediate destroy — use only during scene unload.</summary>
        internal void DestroyImmediate(GameObject go)
        {
            go.InternalDestroy();
            _objects.Remove(go);
        }

        /// <summary>
        /// Immediately commits all pending-add GameObjects into the scene list.
        /// Used by SceneSaver.Load to ensure GOs exist before we set up parenting.
        /// </summary>
        public void FlushPendingAdds()
        {
            _objects.AddRange(_toAdd);
            _toAdd.Clear();
        }

        // ------------------------------------------------------------------
        // Find helpers  (mirrors Unity's GameObject.Find*)
        // ------------------------------------------------------------------
        public GameObject Find(string name)
            => _objects.FirstOrDefault(go => go.Name == name);

        public GameObject FindWithTag(string tag)
            => _objects.FirstOrDefault(go => go.Tag == tag);

        public IEnumerable<GameObject> FindAllWithTag(string tag)
            => _objects.Where(go => go.Tag == tag);

        public T FindObjectOfType<T>() where T : Component
            => _objects.SelectMany(go => go.GetComponents<T>()).FirstOrDefault();

        public IEnumerable<T> FindObjectsOfType<T>() where T : Component
            => _objects.SelectMany(go => go.GetComponents<T>());

        // ------------------------------------------------------------------
        // Lifecycle  (called by SceneManager)
        // ------------------------------------------------------------------
        internal void Load()
        {
            IsLoaded = true;
            OnLoad();
        }

        internal void Unload()
        {
            OnUnload();
            foreach (var go in _objects.ToArray())
                DestroyImmediate(go);
            _objects.Clear();
            _toAdd.Clear();
            _toDestroy.Clear();
            IsLoaded = false;
        }

        internal void Start()
        {
            FlushPending();
            foreach (var go in _objects.ToArray())
                go.InternalStart();
        }

        internal void Update(float dt)
        {
            FlushPending();
            foreach (var go in _objects.ToArray())
                go.InternalUpdate(dt);
            ProcessDestructions();
        }

        internal void FixedUpdate(float fdt)
        {
            foreach (var go in _objects.ToArray())
                go.InternalFixedUpdate(fdt);
        }

        internal void Render(RenderContext ctx)
        {
            foreach (var go in _objects.ToArray())
                go.InternalRender(ctx);
        }

        // ------------------------------------------------------------------
        // Deferred add / destroy processing
        // ------------------------------------------------------------------
        private void FlushPending()
        {
            if (_toAdd.Count == 0) return;
            foreach (var go in _toAdd)
            {
                _objects.Add(go);
                go.InternalStart();
            }
            _toAdd.Clear();
        }

        private void ProcessDestructions()
        {
            if (_toDestroy.Count == 0) return;
            foreach (var go in _toDestroy)
                DestroyImmediate(go);
            _toDestroy.Clear();
        }

        // ------------------------------------------------------------------
        // Virtual hooks for subclassing
        // ------------------------------------------------------------------
        /// <summary>Override to populate the scene when it loads.</summary>
        protected virtual void OnLoad() { }

        /// <summary>Override to clean up scene-specific resources.</summary>
        protected virtual void OnUnload() { }
    }

    // =========================================================================
    // SceneManager
    // =========================================================================
    /// <summary>
    /// Global scene manager. Mirrors Unity's SceneManagement.SceneManager.
    ///
    /// Setup (call once in your engine/editor OnLoad):
    ///   SceneManager.RegisterScene&lt;MyGameScene&gt;("Game", buildIndex: 0);
    ///   SceneManager.LoadScene(0);
    ///
    /// Runtime:
    ///   SceneManager.LoadScene("Game");
    ///   SceneManager.LoadScene("Additive", LoadSceneMode.Additive);
    ///   SceneManager.UnloadScene("Additive");
    ///   var active = SceneManager.ActiveScene;
    /// </summary>
    public static class SceneManager
    {
        // ------------------------------------------------------------------
        // Load modes  (matches Unity enum)
        // ------------------------------------------------------------------
        public enum LoadSceneMode { Single, Additive }

        // ------------------------------------------------------------------
        // Registry  (name → factory)
        // ------------------------------------------------------------------
        private static readonly Dictionary<string, System.Func<Scene>> _registry = new();
        private static readonly Dictionary<int, string> _buildMap = new();

        // ------------------------------------------------------------------
        // Loaded scenes
        // ------------------------------------------------------------------
        private static readonly List<Scene> _loadedScenes = new();
        public static IReadOnlyList<Scene> LoadedScenes => _loadedScenes;

        /// <summary>The primary (single-mode) active scene.</summary>
        public static Scene ActiveScene => _loadedScenes.Count > 0 ? _loadedScenes[0] : null;

        public static int SceneCount => _loadedScenes.Count;
        public static int SceneCountInBuildSettings => _registry.Count;

        // ------------------------------------------------------------------
        // Events  (mirrors Unity's SceneManager events)
        // ------------------------------------------------------------------
        public static event System.Action<Scene, LoadSceneMode> SceneLoaded;
        public static event System.Action<Scene> SceneUnloaded;
        public static event System.Action<Scene> ActiveSceneChanged;

        // ------------------------------------------------------------------
        // Registration
        // ------------------------------------------------------------------
        /// <summary>
        /// Register a scene type so it can be loaded by name or build index.
        /// Call this from your engine initialisation before any LoadScene.
        /// </summary>
        public static void RegisterScene<T>(string name, int buildIndex = -1)
            where T : Scene, new()
        {
            _registry[name] = () =>
            {
                var s = new T();
                s.Name = name;
                s.BuildIndex = buildIndex;
                return s;
            };
            if (buildIndex >= 0) _buildMap[buildIndex] = name;
        }

        /// <summary>Register a scene via a factory lambda (useful for anonymous scenes).</summary>
        public static void RegisterScene(string name, System.Func<Scene> factory,
                                         int buildIndex = -1)
        {
            _registry[name] = () =>
            {
                var s = factory();
                s.Name = name;
                s.BuildIndex = buildIndex;
                return s;
            };
            if (buildIndex >= 0) _buildMap[buildIndex] = name;
        }

        // ------------------------------------------------------------------
        // Load
        // ------------------------------------------------------------------
        public static Scene LoadScene(string name,
                                      LoadSceneMode mode = LoadSceneMode.Single)
        {
            if (!_registry.TryGetValue(name, out var factory))
            {
                Console.Error.WriteLine($"[SceneManager] Scene '{name}' is not registered. " +
                    $"Registered: [{string.Join(", ", _registry.Keys)}]");
                return null;
            }

            if (mode == LoadSceneMode.Single)
            {
                // Unload all current scenes
                foreach (var old in _loadedScenes.ToArray())
                    UnloadInternal(old);
                _loadedScenes.Clear();
            }

            var scene = factory();
            scene.Load();
            scene.Start();
            _loadedScenes.Add(scene);

            SceneLoaded?.Invoke(scene, mode);
            if (mode == LoadSceneMode.Single)
                ActiveSceneChanged?.Invoke(scene);

            return scene;
        }

        public static Scene LoadScene(int buildIndex,
                                      LoadSceneMode mode = LoadSceneMode.Single)
        {
            if (!_buildMap.TryGetValue(buildIndex, out var name))
            {
                Console.Error.WriteLine($"[SceneManager] No scene at build index {buildIndex}.");
                return null;
            }
            return LoadScene(name, mode);
        }

        // ------------------------------------------------------------------
        // Unload
        // ------------------------------------------------------------------
        public static void UnloadScene(string name)
        {
            var scene = _loadedScenes.FirstOrDefault(s => s.Name == name);
            if (scene == null) return;
            UnloadInternal(scene);
            _loadedScenes.Remove(scene);
        }

        public static void UnloadScene(Scene scene)
        {
            if (!_loadedScenes.Contains(scene)) return;
            UnloadInternal(scene);
            _loadedScenes.Remove(scene);
        }

        private static void UnloadInternal(Scene scene)
        {
            scene.Unload();
            SceneUnloaded?.Invoke(scene);
        }

        // ------------------------------------------------------------------
        // Per-frame calls  (call these from your EWindow loop)
        // ------------------------------------------------------------------
        public static void Update(float dt)
        {
            foreach (var scene in _loadedScenes.ToArray())
                scene.Update(dt);
        }

        public static void FixedUpdate(float fdt)
        {
            foreach (var scene in _loadedScenes.ToArray())
                scene.FixedUpdate(fdt);
        }

        public static void Render(RenderContext ctx)
        {
            foreach (var scene in _loadedScenes.ToArray())
                scene.Render(ctx);
        }

        // ------------------------------------------------------------------
        // Utility
        // ------------------------------------------------------------------
        public static Scene GetSceneByName(string name)
            => _loadedScenes.FirstOrDefault(s => s.Name == name);

        public static Scene GetSceneAt(int index)
            => index >= 0 && index < _loadedScenes.Count ? _loadedScenes[index] : null;

        public static bool IsSceneLoaded(string name)
            => _loadedScenes.Any(s => s.Name == name);

        public static void MoveGameObjectToScene(GameObject go, Scene target)
        {
            go.Scene?.Destroy(go);
            go.Scene = target;
            target.AddGameObject(go);
        }
    }
}