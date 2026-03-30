// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Runtime.Graphite;

using Graphite = Prowl.Runtime.Graphite;

namespace Prowl.ImGuiIntegration;

/// <summary>
/// Static registry that maps engine <see cref="Graphite.Texture"/> objects to
/// <c>nint</c> texture IDs usable with <c>ImGui.Image()</c> and
/// <c>ImDrawList.AddImage()</c>.
/// <para>
/// Under the hood, each registration creates a Graphite <see cref="BindGroup"/>
/// containing the texture and a sampler. The <see cref="ImGuiRendererGraphite"/>
/// resolves these bind groups at draw time.
/// </para>
/// </summary>
public static class ImGuiTextureRegistry
{
    private static readonly Dictionary<Graphite.Texture, nint> s_registered = new();

    /// <summary>
    /// Returns a texture ID for use with <c>ImGui.Image()</c>.
    /// If the texture has not been registered yet, it is registered now.
    /// Returns <c>0</c> if the renderer is not available.
    /// </summary>
    public static nint GetOrRegister(Graphite.Texture? texture)
    {
        if (texture == null)
            return 0;

        var renderer = ImGuiRendererGraphite.Instance;
        if (renderer == null)
            return 0;

        if (s_registered.TryGetValue(texture, out var id))
            return id;

        id = renderer.RegisterTexture(texture);
        s_registered[texture] = id;
        return id;
    }

    /// <summary>
    /// Unregisters a texture, freeing associated GPU resources.
    /// </summary>
    public static void Unregister(Graphite.Texture? texture)
    {
        if (texture == null)
            return;

        if (!s_registered.TryGetValue(texture, out var id))
            return;

        ImGuiRendererGraphite.Instance?.UnregisterTexture(id);
        s_registered.Remove(texture);
    }

    /// <summary>
    /// Removes all entries whose <see cref="Graphite.Texture"/> has been disposed.
    /// Called by <see cref="ImGuiRendererGraphite"/> during per-frame cleanup so
    /// that stale mappings from replaced/recreated textures are discarded.
    /// </summary>
    internal static void PurgeDisposed()
    {
        List<Graphite.Texture>? stale = null;
        foreach (var kvp in s_registered)
        {
            if (kvp.Key.IsDisposed)
            {
                stale ??= new List<Graphite.Texture>();
                stale.Add(kvp.Key);
            }
        }

        if (stale != null)
        {
            foreach (var tex in stale)
                s_registered.Remove(tex);
        }
    }

    /// <summary>
    /// Clears all registrations. Called during shutdown.
    /// </summary>
    internal static void Clear()
    {
        s_registered.Clear();
    }
}
