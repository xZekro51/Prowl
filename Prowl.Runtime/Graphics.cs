﻿// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Veldrid;
using Veldrid.StartupUtilities;

using Prowl.Runtime.Rendering;
using Prowl.Runtime.Rendering.Pipelines;
using System.Linq;

namespace Prowl.Runtime;

public static partial class Graphics
{
    private class MeshRenderable : IRenderable
    {
        private AssetRef<Mesh> _mesh;
        private AssetRef<Material> _material;
        private Matrix4x4 _transform;
        private byte _layerIndex;
        private PropertyState _properties;

        public MeshRenderable(AssetRef<Mesh> mesh, AssetRef<Material> material, Matrix4x4 matrix, byte layerIndex, PropertyState? propertyBlock = null)
        {
            _mesh = mesh;
            _material = material;
            _transform = matrix;
            _layerIndex = layerIndex;
            _properties = propertyBlock ?? new();
        }

        public Material GetMaterial() => _material.Res;
        public int GetLayer() => _layerIndex;

        public void GetRenderingData(out PropertyState properties, out IGeometryDrawData drawData, out Matrix4x4 model)
        {
            drawData = _mesh.Res;
            properties = _properties;
            model = _transform;
        }

        public void GetCullingData(out bool isRenderable, out Bounds bounds)
        {
            isRenderable = true;
            bounds = _mesh.Res.bounds.Transform(_transform);
        }
    }

    public static GraphicsDevice Device { get; internal set; }
    public static ResourceFactory Factory => Device.ResourceFactory;

    public static Framebuffer ScreenTarget => Device.SwapchainFramebuffer;

    public static Vector2Int TargetResolution => new Vector2(ScreenTarget.Width, ScreenTarget.Height);

    public static bool IsDX11 => Device.BackendType == GraphicsBackend.Direct3D11;
    public static bool IsOpenGL => Device.BackendType == GraphicsBackend.OpenGL || Device.BackendType == GraphicsBackend.OpenGLES;
    public static bool IsVulkan => Device.BackendType == GraphicsBackend.Vulkan;

    private static readonly List<IDisposable> s_disposables = new();

    public static bool VSync
    {
        get { return Device.SyncToVerticalBlank; }
        set { Device.SyncToVerticalBlank = value; }
    }

    [LibraryImport("Shcore.dll")]
    internal static partial int SetProcessDpiAwareness(int value);

    [LibraryImport("user32.dll")]
    private static partial int GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    /// <summary>
    /// Gets the system DPI scale factor (1.0 = 100% scaling, 1.25 = 125% scaling, etc.)
    /// </summary>
    public static double GetSystemDpiScale()
    {
        if (RuntimeUtils.IsWindows())
        {
            try
            {
                // Get DPI for the main window
                int dpi = GetDpiForWindow(Screen.Handle);
                if (dpi > 0)
                {
                    // Standard DPI is 96, so scale factor = current DPI / 96
                    return dpi / 96.0;
                }
            }
            catch
            {
                // Fallback if Win32 calls fail
            }
        }
        else if (RuntimeUtils.IsMac())
        {
            // On macOS, try to get the native scale from Metal bindings
            try
            {
                var mainScreen = Veldrid.MetalBindings.UIScreen.mainScreen;
                var nativeScale = mainScreen.nativeScale;
                if (nativeScale.Value > 0)
                {
                    return nativeScale.Value;
                }
            }
            catch
            {
                // Fallback if Metal bindings fail
            }
        }

        // Fallback: calculate from framebuffer vs window size ratio
        try
        {
            var targetRes = TargetResolution;
            var screenSize = Screen.Size;
            if (screenSize.x > 0 && screenSize.y > 0)
            {
                // Use the average of X and Y scaling to get a single scale factor
                double scaleX = (double)targetRes.x / screenSize.x;
                double scaleY = (double)targetRes.y / screenSize.y;
                return (scaleX + scaleY) / 2.0;
            }
        }
        catch
        {
            // Final fallback
        }

        return 1.0; // No scaling
    }

    public static void Initialize(bool VSync = true, GraphicsBackend preferredBackend = GraphicsBackend.OpenGL)
    {
        GraphicsDeviceOptions deviceOptions = new()
        {
            SyncToVerticalBlank = VSync,
            ResourceBindingModel = ResourceBindingModel.Default,
            HasMainSwapchain = true,
            SwapchainDepthFormat = PixelFormat.D16_UNorm,
            SwapchainSrgbFormat = false,
        };

        Device = VeldridStartup.CreateGraphicsDevice(Screen.InternalWindow, deviceOptions, preferredBackend);

        if (RuntimeUtils.IsWindows())
        {
            Exception? exception = Marshal.GetExceptionForHR(SetProcessDpiAwareness(1));

            if (exception != null)
                Debug.LogException(new Exception("Failed to set DPI awareness", exception));
        }

        GUI.Graphics.UIDrawListRenderer.Initialize(GUI.Graphics.ColorSpaceHandling.Direct);
    }

    public static void EndFrame()
    {
        RenderTexture.UpdatePool();
        RenderPipeline.ClearRenderables();

        if (Device.SwapchainFramebuffer.Width != Screen.Width || Device.SwapchainFramebuffer.Height != Screen.Height)
            Device.ResizeMainWindow((uint)Screen.Width, (uint)Screen.Height);

        Device.WaitForIdle();

        foreach (IDisposable disposable in s_disposables)
            disposable.Dispose();

        s_disposables.Clear();

        Device.SwapBuffers();
    }

    public static CommandList GetCommandList()
    {
        CommandList list = Factory.CreateCommandList();
        list.Begin();

        return list;
    }

    public static void DrawMesh(AssetRef<Mesh> mesh, AssetRef<Material> material, Matrix4x4 matrix, byte layerIndex, PropertyState? propertyBlock = null)
    {
        RenderPipeline.AddRenderable(new MeshRenderable(mesh, material, matrix, layerIndex, propertyBlock));
    }

    public static void Blit(Texture2D source, Framebuffer dest, Material mat = null, int pass = 0)
    {
        CommandBuffer buffer = CommandBufferPool.Get();
        if(mat == null)
            buffer.Blit(source, dest, pass);
        else
            buffer.Blit(source, dest, mat, pass);
        SubmitCommandBuffer(buffer);
        CommandBufferPool.Release(buffer);
    }

    public static void SubmitCommandBuffer(CommandBuffer commandBuffer, GraphicsFence? fence = null)
    {
        commandBuffer.Clear();
        Device.SubmitCommands(commandBuffer._commandList, fence?.Fence);
    }

    public static void SubmitCommandList(CommandList list, GraphicsFence? fence = null)
    {
        list.End();
        Device.SubmitCommands(list, fence?.Fence);
    }

    public static void WaitForFence(GraphicsFence fence, ulong timeout = ulong.MaxValue)
    {
        Device.WaitForFence(fence?.Fence, timeout);
    }

    public static void WaitForFences(GraphicsFence[] fences, bool waitAll, ulong timeout = ulong.MaxValue)
    {
        Device.WaitForFences(fences.Select(x => x.Fence).ToArray(), waitAll, timeout);
    }

    internal static void SubmitResourceForDisposal(IDisposable resource)
    {
        s_disposables.Add(resource);
    }

    internal static void SubmitResourcesForDisposal(IEnumerable<IDisposable> resources)
    {
        s_disposables.AddRange(resources);
    }

    internal static void Dispose()
    {
        ShaderPipelineCache.Dispose();
        GUI.Graphics.UIDrawListRenderer.Dispose();

        Device.Dispose();
    }

    public static Matrix4x4 GetGPUProjectionMatrix(Matrix4x4 projection)
    {
        // On DX11, flip Y - not sure if this works on Metal.
        if (!IsOpenGL && !IsVulkan)
            projection.M22 = -projection.M22;

        return projection;
    }

    public static FrontFace GetFrontFace()
    {
        if (IsOpenGL)
            return FrontFace.Clockwise;

        return FrontFace.CounterClockwise;
    }
}
