// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Services;

/// <summary>
/// Abstracts scene management so the editor is decoupled from
/// the concrete engine implementation. Swap this interface to
/// port the editor to another engine.
/// </summary>
public interface ISceneService
{
    /// <summary> The currently loaded scene (may be null). </summary>
    Scene? CurrentScene { get; }

    /// <summary> True when the scene has unsaved modifications. </summary>
    bool IsDirty { get; }

    /// <summary> The file path the scene was last saved to / loaded from, or null if unsaved. </summary>
    string? SceneFilePath { get; set; }

    /// <summary> Per-instance event domain for scene service notifications. </summary>
    SceneServiceEvents Events { get; }

    /// <summary> Marks the current scene as having unsaved changes. </summary>
    void MarkDirty();

    /// <summary> Clears the dirty flag (e.g. after saving). </summary>
    void ClearDirty();

    /// <summary> Creates a new empty scene and makes it current. </summary>
    Scene CreateNewScene(string name = "Untitled");

    /// <summary> Sets an already-loaded scene as the current scene. </summary>
    void SetScene(Scene scene);

    /// <summary> Returns all root GameObjects in the current scene. </summary>
    IEnumerable<GameObject> GetRootGameObjects();

    /// <summary> Creates a new empty GameObject in the current scene. </summary>
    GameObject CreateGameObject(string name = "New GameObject");

    /// <summary> Destroys a GameObject from the current scene. </summary>
    void DestroyGameObject(GameObject go);

    /// <summary>
    /// Snapshots the current scene state so it can be restored later.
    /// Used when entering play mode. Returns an opaque token.
    /// </summary>
    object? SnapshotScene();

    /// <summary>
    /// Restores the scene to the state captured by <see cref="SnapshotScene"/>.
    /// Used when exiting play mode. Pass the token returned by SnapshotScene.
    /// </summary>
    void RestoreScene(object? snapshot);

    /// <summary>
    /// Creates a shallow runtime clone of the current scene suitable for
    /// play-mode simulation. The original scene data is preserved separately.
    /// </summary>
    Scene? CloneCurrentScene();
}
