// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using Prowl.Editor.Scripting;
using Prowl.Runtime;

namespace Prowl.Editor;

/// <summary>Mark a static void method to be called when the scene is saved.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class OnSceneSavedAttribute : Attribute { }

/// <summary>Mark a static void method to be called after undo or redo is performed.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class OnUndoRedoAttribute : Attribute { }

/// <summary>
/// Discovers and registers static callback methods marked with editor callback attributes.
/// Call Initialize() once at editor startup.
/// </summary>
public static class EditorCallbacks
{
    private static bool _initialized;
    private static readonly List<Action> _sceneSavedDelegates = [];
    private static readonly List<Action> _undoRedoDelegates = [];

    public static void Reinitialize()
    {
        // Detach all previously registered delegates to avoid dangling references
        // to types from the old (unloaded) script assemblies.
        foreach (var del in _sceneSavedDelegates)
            EditorSceneManager.OnSceneSaved -= del;
        foreach (var del in _undoRedoDelegates)
            Undo.OnUndoRedo -= del;

        _sceneSavedDelegates.Clear();
        _undoRedoDelegates.Clear();
        _initialized = false;
        Initialize();
    }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        int sceneSaved = 0, undoRedo = 0;

        foreach (var type in ScriptAssemblyManager.GetAllTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<OnSceneSavedAttribute>() != null)
                {
                    var del = (Action)Delegate.CreateDelegate(typeof(Action), method);
                    EditorSceneManager.OnSceneSaved += del;
                    _sceneSavedDelegates.Add(del);
                    sceneSaved++;
                }

                if (method.GetCustomAttribute<OnUndoRedoAttribute>() != null)
                {
                    var del = (Action)Delegate.CreateDelegate(typeof(Action), method);
                    Undo.OnUndoRedo += del;
                    _undoRedoDelegates.Add(del);
                    undoRedo++;
                }
            }
        }

        Debug.Log($"[EditorCallbacks] Registered {sceneSaved} OnSceneSaved, {undoRedo} OnUndoRedo callbacks.");
    }
}
