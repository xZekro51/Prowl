// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Prowl.Echo;
using Prowl.PaperUI;
using Prowl.Runtime.Rendering;
using Prowl.Vector;

namespace Prowl.Runtime.Resources;

public class Scene : EngineObject, ISerializationCallbackReceiver
{
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
            if (oldScene.IsActive)
                oldScene.Disable();
            oldScene.Dispose();
        }

        Current = scene;
        Current.Enable();

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

    [SerializeField]
    private GameObject[] serializeObj = null;

    [SerializeIgnore]
    private HashSet<GameObject> _allObj = new(ReferenceEqualityComparer.Instance);

    private PhysicsWorld _physics = new();

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
        public FogMode Mode = FogMode.ExponentialSquared;
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
        if (_isActive) throw new Exception("Scene is already enabled!");

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
    }

    /// <summary>
    /// Disables this scene, triggering OnDisable callbacks for its components.
    /// For most use cases, prefer using Scene.Unload() or Scene.Load() instead of calling Disable() directly.
    /// </summary>
    public void Disable()
    {
        if (!_isActive) throw new Exception("Scene is not enabled!");

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

            obj.BeginComponentIteration();
            try
            {
                int count = obj._components.Count;

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
            }
            finally
            {
                obj.EndComponentIteration();
            }

            obj.Scene = null;
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
        foreach (GameObject go in AllObjects)
        {
            if (go.InstanceID == id)
                return go as T;
            foreach (MonoBehaviour comp in go.GetComponents<MonoBehaviour>())
                if (comp.InstanceID == id)
                    return comp as T;
        }
        return null;
    }

    public T? FindObjectByIdentifier<T>(Guid identifier) where T : EngineObject
    {
        foreach (GameObject go in AllObjects)
        {
            if (go.Identifier == identifier)
                return go as T;
            foreach (MonoBehaviour comp in go.GetComponents<MonoBehaviour>())
                if (comp.Identifier == identifier)
                    return comp as T;
        }
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

    /// <summary> Unregisters all dead / disposed GameObjects </summary>
    public void Flush()
    {
        List<GameObject> removed = [];
        foreach (GameObject obj in _allObj)
        {
            if (obj.IsDisposed)
                removed.Add(obj);
        }

        _allObj.RemoveWhere(obj => obj.IsDisposed);

        foreach (GameObject obj in removed)
            obj.Scene = null;
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
    }

    public void OnBeforeSerialize()
    {
        serializeObj = [.. AllObjects];
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
        // Clear render tracking at the start of each update
        ClearRenderTracking();

        bool editFilter = !IsPlayMode;

        List<GameObject> activeGOs = GetActiveObjectsNonAlloc();
        foreach (GameObject go in activeGOs)
            go.PreUpdate(editFilter ? ShouldRunInEditMode : null);

        Game.BaseEventManager.InvokeEvent(EventSystem.BaseEvents.OnBeforeUpdate);
        ForeachComponent(activeGOs, (x) => x.Update(), editFilter);
        Game.BaseEventManager.InvokeEvent(EventSystem.BaseEvents.OnAfterUpdate);

        Game.BaseEventManager.InvokeEvent(EventSystem.BaseEvents.OnBeforeLateUpdate);
        ForeachComponent(activeGOs, (x) => x.LateUpdate(), editFilter);
        Game.BaseEventManager.InvokeEvent(EventSystem.BaseEvents.OnAfterLateUpdate);

        Flush();
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
        if (SimulatePhysics)
            Physics.Update();

        bool editFilter = !IsPlayMode;

        List<GameObject> activeGOs = GetActiveObjectsNonAlloc();
        ForeachComponent(activeGOs, (x) => x.FixedUpdate(), editFilter);

        Flush();
    }

    /// <summary>
    /// Draws gizmos for all active GameObjects and their components.
    /// </summary>
    public void DrawGizmos()
    {
        List<GameObject> activeGOs = GetActiveObjectsNonAlloc();
        ForeachComponent(activeGOs, (x) =>
        {
            x.DrawGizmos();
        });

        Flush();
    }

    /// <summary>
    /// Executes GUI update on all active GameObjects and their components.
    /// Calls OnGUI.
    /// </summary>
    public void OnGui(Paper paper)
    {
        List<GameObject> activeGOs = GetActiveObjectsNonAlloc();
        ForeachComponent(activeGOs, (x) =>
        {
            x.OnGui(paper);
        });

        Flush();
    }

    /// <summary>
    /// Renders all cameras in this scene, sorted by depth.
    /// </summary>
    /// <param name="target">Optional render target to render into</param>
    /// <returns>True if any cameras were rendered, false otherwise</returns>
    public bool Render(RenderTexture? target = null)
    {
        _cameraBuffer.Clear();
        foreach (GameObject go in GetActiveObjectsNonAlloc())
            foreach (Camera cam in go.GetComponentsInChildren<Camera>())
                _cameraBuffer.Add(cam);

        _cameraBuffer.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        if (_cameraBuffer.Count == 0)
            return false;

        foreach (Camera cam in _cameraBuffer)
        {
            RenderPipeline pipeline = RenderPipeline.Resolve(cam);

            // If we have a target and the Camera doesnt, draw into the target
            if (target.IsValid() && cam.Target.IsNotValid())
            {
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

        return true;
    }

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
