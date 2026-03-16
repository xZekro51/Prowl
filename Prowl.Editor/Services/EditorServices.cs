// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Services;

/// <summary>
/// A minimal service locator used to resolve editor services.
/// Keeps the editor modular: windows request interfaces, not concrete types.
/// </summary>
public static class EditorServices
{
    private static readonly Dictionary<Type, object> _services = new();

    /// <summary> Registers a service instance for the given interface type. </summary>
    public static void Register<T>(T instance) where T : class
    {
        _services[typeof(T)] = instance;
    }

    /// <summary> Resolves a previously registered service. Throws if not found. </summary>
    public static T Get<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out var service))
            return (T)service;

        throw new InvalidOperationException(
            $"Service '{typeof(T).Name}' has not been registered. " +
            $"Call EditorServices.Register<{typeof(T).Name}>(...) during initialization.");
    }

    /// <summary> Tries to resolve a service. Returns false if not registered. </summary>
    public static bool TryGet<T>(out T? service) where T : class
    {
        if (_services.TryGetValue(typeof(T), out var obj))
        {
            service = (T)obj;
            return true;
        }

        service = null;
        return false;
    }

    /// <summary> Removes all registered services (useful for tests). </summary>
    public static void Clear() => _services.Clear();
}
