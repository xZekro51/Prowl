// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Graphite;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Persists the Vulkan pipeline cache to disk so that subsequent launches
/// skip shader compilation and pipeline creation stutter.
/// <para>
/// Subscribes to <see cref="GraphiteDeviceEvents.OnDeviceReady"/> to load
/// a previously saved cache, and to <see cref="GraphiteDeviceEvents.OnDeviceDisposing"/>
/// to save the current cache before the device is torn down.
/// </para>
/// <para>
/// No-op for non-Vulkan backends (OpenGL does not expose a pipeline cache).
/// </para>
/// </summary>
internal static class PipelineCacheManager
{
    private static readonly string s_cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Prowl", "Cache");

    private static readonly string s_cachePath = Path.Combine(s_cacheDir, "pipeline_cache.bin");

    /// <summary>
    /// Registers event subscriptions for pipeline cache load/save.
    /// Must be called once after the graphics device is initialized,
    /// but before <see cref="GraphiteDeviceEvents.OnDeviceReady"/> is fired.
    /// </summary>
    internal static void InitializeEventSubscriptions()
    {
        GraphiteDeviceEvents.SubscribeOnDeviceReady(args =>
        {
            if (args.Backend != GraphicsBackendType.Vulkan)
                return;

            try
            {
                if (File.Exists(s_cachePath))
                {
                    byte[] data = File.ReadAllBytes(s_cachePath);
                    Graphics.Graphite.LoadPipelineCacheData(data);
                    Debug.Log($"[PipelineCache] Loaded {data.Length} bytes from {s_cachePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PipelineCache] Failed to load: {ex.Message}");
            }
        }, priority: 100);

        GraphiteDeviceEvents.SubscribeOnDeviceDisposing(() =>
        {
            if (!Graphics.IsGraphiteReady)
                return;

            if (Graphics.Graphite.BackendType != GraphicsBackendType.Vulkan)
                return;

            try
            {
                byte[]? data = Graphics.Graphite.GetPipelineCacheData();
                if (data != null && data.Length > 0)
                {
                    Directory.CreateDirectory(s_cacheDir);
                    File.WriteAllBytes(s_cachePath, data);
                    Debug.Log($"[PipelineCache] Saved {data.Length} bytes to {s_cachePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PipelineCache] Failed to save: {ex.Message}");
            }
        }, priority: -100);
    }
}
