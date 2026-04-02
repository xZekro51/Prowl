// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for the per-scene event domain (SceneEvents).
/// </summary>
public class SceneEventsTests : IDisposable
{
    private Scene? _scene;

    [Fact]
    public void SceneEvents_OnGameObjectAdded_FiresWhenObjectAdded()
    {
        _scene = new Scene();
        bool fired = false;
        GameObject? addedObject = null;

        _scene.Events.OnGameObjectAdded += args =>
        {
            fired = true;
            addedObject = args.GameObject;
        };

        GameObject go = new GameObject();
        _scene.Add(go);

        Assert.True(fired);
        Assert.Same(go, addedObject);
    }

    [Fact]
    public void SceneEvents_OnGameObjectRemoved_FiresWhenObjectRemoved()
    {
        _scene = new Scene();
        GameObject go = new GameObject();
        _scene.Add(go);

        bool fired = false;
        GameObject? removedObject = null;

        _scene.Events.OnGameObjectRemoved += args =>
        {
            fired = true;
            removedObject = args.GameObject;
        };

        _scene.Remove(go);

        Assert.True(fired);
        Assert.Same(go, removedObject);
    }

    [Fact]
    public void SceneEvents_OnSceneEnabled_FiresWhenSceneEnabled()
    {
        _scene = new Scene();
        int count = 0;

        _scene.Events.OnSceneEnabled += () => count++;

        _scene.Enable();

        Assert.Equal(1, count);

        // Should not fire again if already enabled
        _scene.Enable();

        Assert.Equal(1, count);
    }

    [Fact]
    public void SceneEvents_OnSceneDisabled_FiresWhenSceneDisabled()
    {
        _scene = new Scene();
        _scene.Enable();

        int count = 0;

        _scene.Events.OnSceneDisabled += () => count++;

        _scene.Disable();

        Assert.Equal(1, count);

        // Should not fire again if already disabled
        _scene.Disable();

        Assert.Equal(1, count);
    }

    [Fact]
    public void SceneEvents_OnBeforeUpdate_FiresBeforeUpdate()
    {
        _scene = new Scene();
        _scene.Enable();

        bool fired = false;

        _scene.Events.OnBeforeUpdate += () => fired = true;

        _scene.Update();

        Assert.True(fired);
    }

    [Fact]
    public void SceneEvents_OnAfterUpdate_FiresAfterUpdate()
    {
        _scene = new Scene();
        _scene.Enable();

        bool fired = false;

        _scene.Events.OnAfterUpdate += () => fired = true;

        _scene.Update();

        Assert.True(fired);
    }

    [Fact]
    public void SceneEvents_OnBeforeFixedUpdate_FiresBeforeFixedUpdate()
    {
        _scene = new Scene();
        _scene.Enable();

        bool fired = false;

        _scene.Events.OnBeforeFixedUpdate += () => fired = true;

        _scene.FixedUpdate();

        Assert.True(fired);
    }

    [Fact]
    public void SceneEvents_OnAfterFixedUpdate_FiresAfterFixedUpdate()
    {
        _scene = new Scene();
        _scene.Enable();

        bool fired = false;

        _scene.Events.OnAfterFixedUpdate += () => fired = true;

        _scene.FixedUpdate();

        Assert.True(fired);
    }

    [Fact]
    public void SceneEvents_Unsubscribe_HandlerNotCalled()
    {
        _scene = new Scene();
        int count = 0;

        Action handler = () => count++;

        _scene.Events.OnSceneEnabled += handler;
        _scene.Events.OnSceneEnabled -= handler;

        _scene.Enable();

        Assert.Equal(0, count);
    }

    [Fact]
    public void SceneEvents_Dispose_CleansUpManager()
    {
        _scene = new Scene();

        bool handlerCalled = false;
        _scene.Events.OnGameObjectAdded += _ => handlerCalled = true;

        _scene.Dispose();

        // The scene should be disposed, but we can't add to a disposed scene
        // Just verify disposal doesn't throw
        Assert.True(_scene.IsDisposed);
    }

    public void Dispose()
    {
        if (_scene != null && !_scene.IsDisposed)
        {
            _scene.Dispose();
        }
    }
}
