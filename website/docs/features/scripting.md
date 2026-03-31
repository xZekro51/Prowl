---
id: scripting
title: Scripting
sidebar_position: 3
description: C# scripting in Prowl — MonoBehaviour, GameObjects, coroutines, and hot reload.
keywords: [prowl, scripting, csharp, monobehaviour, gameobject, coroutines]
---

import Tabs from '@theme/Tabs';
import TabItem from '@theme/TabItem';

# C# Scripting

Prowl uses C# as its scripting language, with a Unity-like API built on .NET 9.

## MonoBehaviour

Scripts inherit from `MonoBehaviour` and attach to `GameObject` instances:

```csharp title="MyScript.cs" showLineNumbers
public class MyScript : MonoBehaviour
{
    public float speed = 5.0f;

    public override void Update()
    {
        // Called every frame
    }

    public override void OnEnable()
    {
        // Called when the component is enabled
    }

    public override void OnDisable()
    {
        // Called when the component is disabled
    }
}
```

## GameObject & Component Architecture

Prowl follows the familiar Unity pattern:

| Concept | Description |
|---------|-------------|
| **GameObject** | A container for components, with a transform |
| **MonoBehaviour** | Base class for user scripts |
| **Components** | Modular pieces of functionality attached to GameObjects |

## Coroutines

Unity-like coroutines are supported:

```csharp title="Using coroutines"
public override void Start()
{
    StartCoroutine(MyCoroutine());
}

// highlight-start
IEnumerator MyCoroutine()
{
    Debug.Log("Starting...");
    yield return new WaitForSeconds(2.0f);
    Debug.Log("2 seconds later!");
}
// highlight-end
```

## ScriptableObjects

Data-driven assets that can be created and configured in the editor.

## Editor Scripts & Custom Editors

:::info

The editor supports custom editor scripts and inspector extensions, allowing you to tailor the development experience for your project.

:::

## Hot Reload

Scripts are compiled at runtime when changes are detected, with assembly reload handled through the [Event System](../architecture/event-system) (`EditorEvents.OnAssemblyChanged`).

:::caution

Hot reload recompiles and reloads your game assemblies while the editor is running. Make sure your scripts handle `OnEnable`/`OnDisable` correctly to restore state after a reload.

:::
