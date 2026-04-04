// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Prowl.Echo;
using Prowl.PaperUI;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Profiling;
using Prowl.Runtime.Rendering;
using Prowl.Vector;

namespace Prowl.Runtime.Resources;

/// <summary>
/// Represents a game scene containing GameObjects and their components.
/// <para>
/// Use <see cref="Scene.Load"/> and <see cref="Scene.Current"/> for simple single-scene workflows.
/// For advanced scenarios (multiplayer servers, editor previews), create and manage Scene instances
/// directly or use <see cref="SceneManager"/> for additive loading.
/// </para>
/// </summary>
public class Scene : EngineObject, ISerializationCallbackReceiver
{
    static Scene()
    {
#if PROWL_PROFILING
        Profiler.RegisterSection("Scene.Enable", "Scene", "Enables a scene and triggers OnEnable for all components");
        Profiler.RegisterSection("Scene.Disable", "Scene", "Disables a scene and triggers OnDisable for all components");
        Profiler.RegisterSection("Scene.Update", "Scene", "Updates all active GameObjects and components");
        Profiler.RegisterSection("PreUpdate", "Scene", "PreUpdate pass for all GameObjects");
        Profiler.RegisterSection("Update", "Scene", "Update pass for all components");
        Profiler.RegisterSection("LateUpdate", "Scene", "LateUpdate pass for all components");
        Profiler.RegisterSection("Scene.FixedUpdate", "Scene", "Physics and FixedUpdate pass");
        Profiler.RegisterSection("Physics.Update", "Scene", "Physics simulation step");
        Profiler.RegisterSection("FixedUpdate", "Scene", "FixedUpdate pass for all components");
        Profiler.RegisterSection("Scene.DrawGizmos", "Scene", "Draws gizmos for all components");
        Profiler.RegisterSection("Scene.OnGui", "Scene", "GUI pass for all components");
        Profiler.RegisterSection("Scene.Render", "Scene", "Renders all cameras in the scene");
#endif
    }

    #region Scene Manager

    /// <summary>
    /// The currently active scene managed by the built-in Scene Manager.
    /// For simple games, use Scene.Load() and Scene.Current for automatic scene management.
    /// For advanced use cases (e.g., multiplayer servers with multiple scenes),
    /// create and manage your own Scene instances directly.
    /// Delegates to <see cref="EngineContext.Current"/>.<see cref="EngineContext.ActiveScene"/>.
    /// </summary>
    public static Scene? Current
    {
        get => EngineContext.Current.ActiveScene;
        private set => EngineContext.Current.ActiveScene = value;
    }

    /// <summary>
    /// Sets <see cref="Current"/> without triggering enable/disable or SceneManager sync.
    /// Used internally by <see cref="SceneManager"/> to avoid re-entrant calls.
    /// </summary>
    internal static void SetCurrentDirect(Scene? scene)
    {
        EngineContext.Current.ActiveScene = scene;
    }

    /// <summary>
    /// Loads a scene as the current active scene, replacing any previously loaded scene.
    /// The previous scene will be disabled and disposed.
    /// Also kept in sync with <see cref="SceneManager"/> so additively loaded scenes
    /// are tracked correctly.
    /// </summary>
    /// <param name="scene">The scene to load as the current scene.</param>
    public static void Load(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        Scene? oldScene = Current;

        // Disable and dispose the current scene if one exists
        if (oldScene != null)
        {
            try
            {
                if (oldScene.IsActive)
                    oldScene.Disable();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Scene] Error disabling previous scene: {ex.Message}");
            }

            try
            {
                oldScene.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Scene] Error disposing previous scene: {ex.Message}");
            }
        }

        Current = scene;

        try
        {
            if (!scene.IsActive)
                Current.Enable();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Scene] Error enabling new scene: {ex.Message}");
        }

        // Keep SceneManager in sync
        SceneManager.OnPrimarySceneLoaded(oldScene, scene);
    }

    /// <summary>
    /// Unloads the current scene, disabling and disposing it.
    /// After calling this, Scene.Current will be null.
    /// </summary>
    public static void Unload()
    {
        if (Current != null)
        {
            Scene toUnload = Current;
            if (toUnload.IsActive)
                toUnload.Disable();
            toUnload.Dispose();
            Current = null;

            // Keep SceneManager in sync
            SceneManager.OnPrimarySceneUnloaded(toUnload);
        }
    }

    #endregion

    /// <summary>
    /// Per-scene event manager for lifecycle notifications.
    /// Subscribe to GameObject add/remove, enable/disable, and update/render events.
    /// </summary>
    public SceneEvents Events { get; } = new();

    [SerializeField]
    private GameObject[] serializeObj = null;

    [SerializeIgnore]
    private HashSet<GameObject> _allObj = new(ReferenceEqualityComparer.Instance);

    private PhysicsWorld _physics = new();

    /// <summary>
    /// The physics simulation for this scene.
    /// </summary>
    public PhysicsWorld Physics => _physics;

    // Rendering tracking - cleared each frame before Update, populated during Update
    [SerializeIgnore]
    private readonly List<IRenderable> _renderables = [];
    [SerializeIgnore]
    private readonly List<IRenderableLight> _lights = [];

    // Reusable lists for hot-path iteration to avoid per-frame allocations
    [SerializeIgnore]
    private readonly List<GameObject> _activeGOsBuffer = [];
    [SerializeIgnore]
    private readonly List<Camera> _cameraBuffer = [];

    // Tracked cameras — registered/unregistered via RegisterCamera/UnregisterCamera
    // so Render() doesn't need to walk the entire scene tree each frame.
    [SerializeIgnore]
    private readonly HashSet<Camera> _trackedCameras = new(ReferenceEqualityComparer.Instance);

    // Indexed lookups for O(1) FindObjectByID / FindObjectByIdentifier (�4.2.3)
    [SerializeIgnore]
    private readonly Dictionary<int, EngineObject> _idLookup = [];
    [SerializeIgnore]
    private readonly Dictionary<Guid, EngineObject> _identifierLookup = [];

    [SerializeIgnore]
    private bool _isActive = false;

    public int RenderableCount => _renderables.Count;
    public IReadOnlyList<IRenderable> Renderables => _renderables;
    public IReadOnlyList<IRenderableLight> Lights => _lights;

    public struct FogParams
    {
        public enum FogMode
        {
            Off,
            Linear,
            Exponential,
            ExponentialSquared
        }
        public FogMode Mode = FogMode.Off;
        public Color Color = new(0.5f, 0.5f, 0.5f, 1.0f);
        public float Start = 20;
        public float End = 100;
        public float Density = 0.01f;

        public bool IsFogLinear => Mode == FogMode.Linear;

        public FogParams()
        {
        }
    }

    public FogParams Fog = new();

    public struct AmbientLightParams
    {
        public enum AmbientMode
        {
            Uniform,
            Hemisphere
        }

        public AmbientMode Mode = AmbientMode.Uniform;

        public float Strength = 1f;

        // Uniform ambient
        public Float4 Color = new(0.2f, 0.2f, 0.2f, 1.0f);

        // Hemisphere ambient
        public Float4 SkyColor = new(0.3f, 0.3f, 0.4f, 1.0f);
        public Float4 GroundColor = new(0.2f, 0.2f, 0.2f, 1.0f);

        public bool UseHemisphere => Mode == AmbientMode.Hemisphere;

        public AmbientLightParams()
        {
        }
    }

    public AmbientLightParams Ambient = new();

    public struct SkyboxParams
    {
        public bool Enabled = true;

        public SkyboxParams()
        {
        }
    }

    public SkyboxParams Skybox = new();

    /// <summary> The number of registered objects. </summary>
    public int Count => _allObj.Count;

    /// <summary> Enumerates all registered objects. </summary>
    public IEnumerable<GameObject> AllObjects => _allObj.Where(o => !o.IsDisposed);

    /// <summary> Enumerates all registered objects that are currently active and saveable. </summary>
    public IEnumerable<GameObject> SaveableObjects => _allObj.Where(o => !o.IsDisposed && !o.HideFlags.HasFlag(HideFlags.DontSave) && !o.HideFlags.HasFlag(HideFlags.HideAndDontSave));

    /// <summary> Enumerates all registered objects that are currently active. </summary>
    public IEnumerable<GameObject> ActiveObjects => _allObj.Where(o => !o.IsDisposed && o.EnabledInHierarchy);

    /// <summary> Enumerates all root GameObjects, i.e. all GameObjects without a parent object. </summary>
    public IEnumerable<GameObject> RootObjects => _allObj.Where(o => !o.IsDisposed && o.Transform.Parent == null);

    /// <summary> Enumerates all <see cref="RootObjects"/> that are currently active. </summary>
    public IEnumerable<GameObject> ActiveRootObjects => _allObj.Where(o => !o.IsDisposed && o.Transform.Parent == null && o.EnabledInHierarchy);

    /// <summary> Returns whether this Scene is completely empty. </summary>
    public bool IsEmpty => !AllObjects.Any();

    /// <summary> Returns whether this scene is currently active. </summary>
    public bool IsActive => _isActive;

    /// <summary>
    /// Creates a new, empty scene which does not contain any <see cref="GameObject">GameObjects</see>.
    /// </summary>
    public Scene()
    {
    }

    /// <summary>
    /// Enables this scene, triggering OnEnable callbacks for its components.
    /// For most use cases, prefer using Scene.Load() instead of calling Enable() directly.
    /// </summary>
    public void Enable()
    {
        using (Profiler.Section("Scene.Enable"))
        {
            if (_isActive)
            {
                Debug.LogWarning("[Scene] Enable() called on an already-enabled scene — skipping.");
                return;
            }

            _isActive = true;

            // Create a copy to avoid collection modification during enumeration
            List<GameObject> allObjectsCopy = [.. AllObjects];

            // Trigger OnEnable for all enabled components in the scene
            foreach (GameObject go in allObjectsCopy)
            {
                if (go.IsDisposed) continue;

                if (go.EnabledInHierarchy)
                {
                    go.BeginComponentIteration();
                    try
                    {
                        int count = go._components.Count;
                        for (int i = 0; i < count; i++)
                        {
                            MonoBehaviour component = go._components[i];
                            if (component.IsDisposed) continue;
                            if (component.Enabled && component.EnabledInHierarchy)
                                component.InternalOnEnable();
                        }
                    }
                    finally
                    {
                        go.EndComponentIteration();
                    }
                }
            }

            Events.InvokeOnSceneEnabled();
        }
    }

    /// <summary>
    /// Disables this scene, triggering OnDisable callbacks for its components.
    /// For most use cases, prefer using Scene.Unload() or Scene.Load() instead of calling Disable() directly.
    /// </summary>
    public void Disable()
    {
        using (Profiler.Section("Scene.Disable"))
        {
            if (!_isActive)
            {
                Debug.LogWarning("[Scene] Disable() called on an already-disabled scene — skipping.");
                return;
            }

            // Create a copy to avoid collection modification during enumeration
            List<GameObject> allObjectsCopy = [.. AllObjects];

            // Trigger OnDisable for all enabled components in the scene
            foreach (GameObject go in allObjectsCopy)
            {
                if (go.IsDisposed) continue;

                if (go.EnabledInHierarchy)
                {
                    go.BeginComponentIteration();
                    try
                    {
                        int count = go._components.Count;
                        for (int i = 0; i < count; i++)
                        {
                            MonoBehaviour component = go._components[i];
                            if (component.IsDisposed) continue;
                            if (component.Enabled && component.EnabledInHierarchy)
                                component.OnDisable();
                        }
                    }
                    finally
                    {
                        go.EndComponentIteration();
                    }
                }
            }

            _isActive = false;

            Events.InvokeOnSceneDisabled();
        }
    }

    /// <summary>
    /// Adds a renderable to the scene's render list. Called by components during Update.
    /// </summary>
    public void PushRenderable(IRenderable renderable)
    {
        _renderables.Add(renderable);
    }

    /// <summary>
    /// Adds a light to the scene's light list. Called by light components during Update.
    /// </summary>
    public void PushLight(IRenderableLight light)
    {
        _lights.Add(light);
    }

    /// <summary>
    /// Clears the renderable and light tracking lists. Called at the start of each Update.
    /// </summary>
    private void ClearRenderTracking()
    {
        _renderables.Clear();
        _lights.Clear();
    }

    /// <summary>
    /// Registers a camera so it participates in scene rendering without a full tree scan.
    /// Called from Camera.OnEnable / OnAddedToScene.
    /// </summary>
    internal void RegisterCamera(Camera cam) => _trackedCameras.Add(cam);

    /// <summary>
    /// Unregisters a camera. Called from Camera.OnDisable / OnRemovedFromScene.
    /// </summary>
    internal void UnregisterCamera(Camera cam) => _trackedCameras.Remove(cam);

    /// <summary>
    /// Handles lifecycle callbacks for a component that was added to a
    /// <see cref="GameObject"/> already registered in this scene.
    /// Mirrors the per-component logic inside <see cref="AddObject"/>:
    /// registers the component in indexed lookups, calls
    /// <see cref="MonoBehaviour.OnAddedToScene"/>, and — when the scene is
    /// active and the component is enabled — calls
    /// <see cref="MonoBehaviour.InternalOnEnable"/>.
    /// </summary>
    internal void OnComponentAdded(MonoBehaviour component)
    {
        // Register in indexed lookups so FindObjectByID / FindObjectByIdentifier work
        _idLookup[component.InstanceID] = component;
        _identifierLookup[component.Identifier] = component;

        component.OnAddedToScene();

        if (_isActive && component.GameObject.EnabledInHierarchy
            && component.Enabled && component.EnabledInHierarchy)
        {
            component.InternalOnEnable();
        }
    }

    /// <summary>
    /// Handles lifecycle callbacks for a component that is being removed
    /// from a <see cref="GameObject"/> registered in this scene.
    /// Removes the component from indexed lookups and, if the component
    /// was previously enabled, calls <see cref="MonoBehaviour.OnRemovedFromScene"/>.
    /// </summary>
    internal void OnComponentRemoved(MonoBehaviour component)
    {
        _idLookup.Remove(component.InstanceID);
        _identifierLookup.Remove(component.Identifier);

        component.OnRemovedFromScene();
    }

    /// <summary>
    /// Fills <see cref="_activeGOsBuffer"/> with all non-disposed, hierarchy-enabled
    /// objects. Reuses the same list instance to avoid per-frame allocations.
    /// </summary>
    private List<GameObject> GetActiveObjectsNonAlloc()
    {
        _activeGOsBuffer.Clear();
        foreach (GameObject o in _allObj)
        {
            if (!o.IsDisposed && o.EnabledInHierarchy)
                _activeGOsBuffer.Add(o);
        }
        return _activeGOsBuffer;
    }

    /// <summary>
    /// Registers a GameObject and all of its children.
    /// </summary>
    public void Add(GameObject obj)
    {
        if (obj.Scene.IsValid() && obj.Scene != this) obj.Scene.Remove(obj);
        AddObject(obj);
    }

    /// <summary>
    /// Unregisters a GameObject and all of its children
    /// </summary>
    public void Remove(GameObject obj)
    {
        if (obj.Scene != this) return;
        if (obj.Parent.IsValid() && obj.Parent.Scene == this)
        {
            obj.SetParent(null);
        }
        RemoveObject(obj);
    }

    private void AddObject(GameObject obj)
    {
        if (_allObj.Add(obj))
        {
            obj.Scene = this;

            // Populate indexed lookups
            _idLookup[obj.InstanceID] = obj;
            _identifierLookup[obj.Identifier] = obj;

            obj.BeginComponentIteration();
            try
            {
                int count = obj._components.Count;

                // Index components in lookup dictionaries
                for (int i = 0; i < count; i++)
                {
                    MonoBehaviour component = obj._components[i];
                    if (component.IsDisposed) continue;
                    _idLookup[component.InstanceID] = component;
                    _identifierLookup[component.Identifier] = component;
                }

                // Call OnAddedToScene for all components
                for (int i = 0; i < count; i++)
                {
                    MonoBehaviour component = obj._components[i];
                    if (component.IsDisposed) continue;
                    component.OnAddedToScene();
                }

                // Call OnEnable for enabled components, but only if the scene is active
                if (IsActive && obj.EnabledInHierarchy)
                {
                    for (int i = 0; i < count; i++)
                    {
                        MonoBehaviour component = obj._components[i];
                        if (component.IsDisposed) continue;
                        if (component.Enabled && component.EnabledInHierarchy)
                            component.InternalOnEnable();
                    }
                }
            }
            finally
            {
                obj.EndComponentIteration();
            }

            // Fire event after the GameObject is fully added
            Events.InvokeOnGameObjectAdded(new GameObjectAddedArgs(obj));
        }

        // Create a copy to avoid modification during enumeration
        List<GameObject> children = [.. obj.Children];
        foreach (GameObject child in children)
            AddObject(child);
    }

    private void RemoveObject(GameObject obj)
    {
        // Create a copy to avoid modification during enumeration
        List<GameObject> children = [.. obj.Children];
        foreach (GameObject child in children)
            RemoveObject(child);

        if (_allObj.Remove(obj))
        {
            obj.BeginComponentIteration();
            try
            {
                int count = obj._components.Count;

                // Call OnDisable for currently enabled components (only if scene is active)
                if (IsActive && obj.EnabledInHierarchy)
                {
                    for (int i = 0; i < count; i++)
                    {
                        MonoBehaviour component = obj._components[i];
                        if (component.IsDisposed) continue;
                        if (component.Enabled && component.EnabledInHierarchy)
                            component.OnDisable();
                    }
                }

                // Call OnRemovedFromScene for all components
                for (int i = 0; i < count; i++)
                {
                    MonoBehaviour component = obj._components[i];
                    if (component.IsDisposed) continue;
                    component.OnRemovedFromScene();
                }

                // Remove components from indexed lookups
                for (int i = 0; i < count; i++)
                {
                    MonoBehaviour component = obj._components[i];
                    _idLookup.Remove(component.InstanceID);
                    _identifierLookup.Remove(component.Identifier);
                }
            }
            finally
            {
                obj.EndComponentIteration();
            }

            // Remove the GO itself from indexed lookups
            _idLookup.Remove(obj.InstanceID);
            _identifierLookup.Remove(obj.Identifier);

            obj.Scene = null;

            // Fire event after the GameObject is fully removed
            Events.InvokeOnGameObjectRemoved(new GameObjectRemovedArgs(obj));
        }
    }

    public T?[] FindObjectsOfType<T>() where T : EngineObject
    {
        List<T> objects = [];
        foreach (GameObject go in AllObjects)
        {
            if (go is T t)
                objects.Add(t);

            foreach (MonoBehaviour comp in go.GetComponents<MonoBehaviour>())
                if (comp is T t2)
                    objects.Add(t2);
        }
        return [.. objects];
    }

    public T? FindObjectByID<T>(int id) where T : EngineObject
    {
        if (_idLookup.TryGetValue(id, out EngineObject? obj) && obj is T t && !obj.IsDisposed)
            return t;
        return null;
    }

    public T? FindObjectByIdentifier<T>(Guid identifier) where T : EngineObject
    {
        if (_identifierLookup.TryGetValue(identifier, out EngineObject? obj) && obj is T t && !obj.IsDisposed)
            return t;
        return null;
    }

    /// <summary> Unregisters all GameObjects. </summary>
    public void Clear()
    {
        // Create a copy to iterate over since RemoveObject modifies the collection
        List<GameObject> rootObjects = [.. RootObjects];
        foreach (GameObject obj in rootObjects)
        {
            Remove(obj);
        }
    }

    // Reusable buffer for Flush() to avoid per-frame allocations.
    [SerializeIgnore]
    private readonly List<GameObject> _flushBuffer = [];

    /// <summary> Unregisters all dead / disposed GameObjects </summary>
    public void Flush()
    {
        _flushBuffer.Clear();
        foreach (GameObject obj in _allObj)
        {
            if (obj.IsDisposed)
            {
                _flushBuffer.Add(obj);
                _idLookup.Remove(obj.InstanceID);
                _identifierLookup.Remove(obj.Identifier);
            }
        }

        if (_flushBuffer.Count > 0)
        {
            _allObj.RemoveWhere(obj => obj.IsDisposed);

            for (int i = 0; i < _flushBuffer.Count; i++)
                _flushBuffer[i].Scene = null;
        }
    }

    public override void OnDispose()
    {
        base.OnDispose();

        // Clear the current scene reference if this is the current scene
        if (Current == this)
            Current = null;

        // Clear the physics world
        _physics.Clear();

        // Dispose all GameObjects which will also remove them from the scene
        List<GameObject> allObjects = [.. AllObjects];
        foreach (GameObject g in allObjects)
            g.OnDispose();

        // Clear any remaining references
        _allObj.Clear();
        _idLookup.Clear();
        _identifierLookup.Clear();
        _trackedCameras.Clear();

        // Dispose the event manager to unregister from global tracking
        Events.Manager.Dispose();
    }

    public void OnBeforeSerialize()
    {
        // Only persist saveable root objects. Children are already serialized
        // recursively inside each root's GameObject.Serialize() (the "Children"
        // list), so including them here would duplicate every child in the file.
        // SaveableObjects filters out DontSave / HideAndDontSave objects;
        // RootObjects filters to objects with no parent.
        serializeObj = [.. _allObj.Where(o => !o.IsDisposed
            && o.Transform.Parent == null
            && !o.HideFlags.HasFlag(HideFlags.DontSave)
            && !o.HideFlags.HasFlag(HideFlags.HideAndDontSave))];
    }

    public void OnAfterDeserialize()
    {
        if (serializeObj != null)
            foreach (GameObject obj in serializeObj)
                Add(obj);
    }

    /// <summary>
    /// Updates all active GameObjects and their components in this scene.
    /// Calls PreUpdate, Update, and LateUpdate.
    /// In edit mode (<see cref="IsPlayMode"/> == false), only components that
    /// qualify via <see cref="ShouldRunInEditMode"/> are executed.
    /// </summary>
    public void Update()
    {
        using (Profiler.Section("Scene.Update"))
        {
            // Clear render tracking at the start of each update
            ClearRenderTracking();

            bool editFilter = !IsPlayMode;

            List<GameObject> activeGOs = GetActiveObjectsNonAlloc();

            Events.InvokeOnBeforeUpdate();

            using (Profiler.Section("PreUpdate"))
            {
                foreach (GameObject go in activeGOs)
                    go.PreUpdate(editFilter ? ShouldRunInEditMode : null);
            }

            EventSystem.BaseEvents.InvokeOnBeforeUpdate();

            using (Profiler.Section("Update"))
            {
                ForeachComponent(activeGOs, s_updateAction, editFilter);
            }

            EventSystem.BaseEvents.InvokeOnAfterUpdate();

            EventSystem.BaseEvents.InvokeOnBeforeLateUpdate();

            using (Profiler.Section("LateUpdate"))
            {
                ForeachComponent(activeGOs, s_lateUpdateAction, editFilter);
            }

            EventSystem.BaseEvents.InvokeOnAfterLateUpdate();

            Events.InvokeOnAfterUpdate();

            Flush();
        }
    }

    /// <summary>
    /// When false, <see cref="FixedUpdate"/> will skip the physics step.
    /// The editor sets this to false during edit mode and true during play mode.
    /// Delegates to <see cref="EngineContext.Current"/>.<see cref="EngineContext.SimulatePhysics"/>.
    /// </summary>
    public static bool SimulatePhysics
    {
        get => EngineContext.Current.SimulatePhysics;
        set => EngineContext.Current.SimulatePhysics = value;
    }

    /// <summary>
    /// When true, all component lifecycle methods (Start, Update, FixedUpdate,
    /// LateUpdate) execute normally. When false (edit mode), only components
    /// marked with <see cref="ExecuteInEditModeAttribute"/> or implementing
    /// <see cref="IRenderable"/>/<see cref="IRenderableLight"/> will execute.
    /// Defaults to true so standalone (non-editor) games run without changes.
    /// Delegates to <see cref="EngineContext.Current"/>.<see cref="EngineContext.IsPlayMode"/>.
    /// </summary>
    public static bool IsPlayMode
    {
        get => EngineContext.Current.IsPlayMode;
        set => EngineContext.Current.IsPlayMode = value;
    }

    /// <summary>
    /// Per-type cache for whether a MonoBehaviour subclass should execute in edit mode.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, bool> _editModeTypeCache = new();

    /// <summary>
    /// Returns true if the component should execute its lifecycle methods while
    /// in edit mode. A component qualifies if its type has the
    /// <see cref="ExecuteInEditModeAttribute"/> or if it implements
    /// <see cref="IRenderable"/> or <see cref="IRenderableLight"/>.
    /// </summary>
    internal static bool ShouldRunInEditMode(MonoBehaviour comp)
    {
        Type type = comp.GetType();
        return _editModeTypeCache.GetOrAdd(type, static t =>
            t.IsDefined(typeof(ExecuteInEditModeAttribute), true)
            || typeof(IRenderable).IsAssignableFrom(t)
            || typeof(IRenderableLight).IsAssignableFrom(t));
    }

    /// <summary>
    /// Executes physics update on all active GameObjects and their components.
    /// Physics only steps when <see cref="SimulatePhysics"/> is true.
    /// In edit mode, only <see cref="ExecuteInEditModeAttribute"/> components
    /// receive FixedUpdate calls.
    /// </summary>
    public void FixedUpdate()
    {
        using (Profiler.Section("Scene.FixedUpdate"))
        {
            Events.InvokeOnBeforeFixedUpdate();

            if (SimulatePhysics)
            {
                using (Profiler.Section("Physics.Update"))
                {
                    Physics.Update();
                }
            }

            bool editFilter = !IsPlayMode;

            List<GameObject> activeGOs = GetActiveObjectsNonAlloc();

            using (Profiler.Section("FixedUpdate"))
            {
                ForeachComponent(activeGOs, s_fixedUpdateAction, editFilter);
            }

            Events.InvokeOnAfterFixedUpdate();

            Flush();
        }
    }

    /// <summary>
    /// Draws gizmos for all active GameObjects and their components.
    /// </summary>
    public void DrawGizmos()
    {
        using (Profiler.Section("Scene.DrawGizmos"))
        {
            List<GameObject> activeGOs = GetActiveObjectsNonAlloc();
            ForeachComponent(activeGOs, s_drawGizmosAction);

            Flush();
        }
    }

    // Cached delegate + thread-static Paper reference for OnGui to avoid per-frame closure allocation.
    private static readonly Action<MonoBehaviour> s_onGuiAction = static x => x.OnGui(t_guiPaper!);
    [ThreadStatic] private static Paper? t_guiPaper;

    /// <summary>
    /// Executes GUI update on all active GameObjects and their components.
    /// Calls OnGUI.
    /// </summary>
    public void OnGui(Paper paper)
    {
        using (Profiler.Section("Scene.OnGui"))
        {
            t_guiPaper = paper;
            List<GameObject> activeGOs = GetActiveObjectsNonAlloc();
            ForeachComponent(activeGOs, s_onGuiAction);
            t_guiPaper = null;

            Flush();
        }
    }

    /// <summary>
    /// Renders all cameras in this scene, sorted by depth.
    /// </summary>
    /// <param name="target">Optional render target to render into</param>
    /// <returns>True if any cameras were rendered, false otherwise</returns>
    public bool Render(RenderTexture? target = null)
    {
        using (Profiler.Section("Scene.Render"))
        {
            _cameraBuffer.Clear();

            // Use tracked camera set — O(cameras) instead of O(all GameObjects × children).
            foreach (Camera cam in _trackedCameras)
            {
                if (!cam.IsDisposed && cam.EnabledInHierarchy)
                    _cameraBuffer.Add(cam);
            }

            _cameraBuffer.Sort(s_cameraDepthComparison);

            int cameraCount = _cameraBuffer.Count;

            if (cameraCount == 0)
                return false;

            Events.InvokeOnBeforeRender(new RenderingArgs(cameraCount, false));

            // Pre-identify the highest-priority (highest Depth) camera without its
            // own target.  Only this camera renders into the provided target; all
            // other cameras without their own target are skipped to prevent
            // multiple cameras fighting over the same render texture.
            int targetCameraIndex = -1;
            if (target.IsValid())
            {
                for (int i = _cameraBuffer.Count - 1; i >= 0; i--)
                {
                    if (_cameraBuffer[i].Target.IsNotValid())
                    {
                        targetCameraIndex = i;
                        break;
                    }
                }
            }

            for (int i = 0; i < _cameraBuffer.Count; i++)
            {
                Camera cam = _cameraBuffer[i];
                RenderPipeline pipeline = RenderPipeline.Resolve(cam);

                if (target.IsValid() && cam.Target.IsNotValid())
                {
                    if (i != targetCameraIndex)
                        continue;

                    cam.Target = target;
                    pipeline.Render(cam, new());
                    cam.Target = null;
                }
                else
                {
                    // Have no target or the camera has its own target
                    pipeline.Render(cam, new());
                }
            }

            Events.InvokeOnAfterRender(new RenderingArgs(cameraCount, true));

            return true;
        }
    }

    // Cached static delegates to avoid per-frame Action<MonoBehaviour> allocation.
    private static readonly Action<MonoBehaviour> s_updateAction = static x => x.Update();
    private static readonly Action<MonoBehaviour> s_lateUpdateAction = static x => x.LateUpdate();
    private static readonly Action<MonoBehaviour> s_fixedUpdateAction = static x => x.FixedUpdate();
    private static readonly Action<MonoBehaviour> s_drawGizmosAction = static x => x.DrawGizmos();
    private static readonly Comparison<Camera> s_cameraDepthComparison = static (a, b) => a.Depth.CompareTo(b.Depth);

    /// <summary>
    /// Helper method to iterate over all MonoBehaviour components in a collection of GameObjects
    /// and execute an action on each enabled component.
    /// When <paramref name="editModeFilter"/> is true, only components that pass
    /// <see cref="ShouldRunInEditMode"/> are included.
    /// Uses the deferred add/remove mechanism on each <see cref="GameObject"/> so
    /// the underlying component list can be iterated directly without copies.
    /// </summary>
    private void ForeachComponent(List<GameObject> objs, Action<MonoBehaviour> action, bool editModeFilter = false)
    {
        foreach (GameObject go in objs)
        {
            go.BeginComponentIteration();
            try
            {
                int count = go._components.Count;
                for (int i = 0; i < count; i++)
                {
                    MonoBehaviour comp = go._components[i];
                    if (comp.IsDisposed) continue;
                    if (comp.EnabledInHierarchy && (!editModeFilter || ShouldRunInEditMode(comp)))
                        action.Invoke(comp);
                }
            }
            finally
            {
                go.EndComponentIteration();
            }
        }
    }
}
