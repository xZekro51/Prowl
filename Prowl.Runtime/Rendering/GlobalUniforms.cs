// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using Prowl.Runtime.Graphite;
using Prowl.Vector;

using GBuffer = Prowl.Runtime.Graphite.Buffer;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Structure matching the layout of global uniforms in ShaderVariables.glsl
/// Uses std140 layout for uniform buffer compatibility
/// Contains only per-frame data that is constant across all draw calls
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GlobalUniformsData
{
    public static int SizeInBytes => Marshal.SizeOf<GlobalUniformsData>();

    // Suppress IDE0130 warning about naming rule violation, as these are meant to match the naming of the Shader code
#pragma warning disable IDE1006 // Naming Styles

    // Camera matrices (each mat4 = 64 bytes)
    public Float4x4 prowl_MatV;               // 64 bytes
    public Float4x4 prowl_MatIV;              // 64 bytes
    public Float4x4 prowl_MatP;               // 64 bytes
    public Float4x4 prowl_MatVP;              // 64 bytes
    public Float4x4 prowl_PrevViewProj;       // 64 bytes

    // Camera parameters
    public Float3 _WorldSpaceCameraPos;       // 12 bytes
    public float _padding0;                   // 4 bytes (padding)

    public Float4 _ProjectionParams;          // 16 bytes
    public Float4 _ScreenParams;              // 16 bytes
    public Float2 _CameraJitter;              // 8 bytes
    public Float2 _CameraPreviousJitter;      // 8 bytes

    // Time parameters
    public Float4 _Time;                      // 16 bytes
    public Float4 _SinTime;                   // 16 bytes
    public Float4 _CosTime;                   // 16 bytes
    public Float4 prowl_DeltaTime;            // 16 bytes

    // Graphics API parameters
    // x = 1.0 on Vulkan (need to flip UV Y for NDC conversion), 0.0 on OpenGL
    // y, z, w = reserved for future use
    public Float4 _GraphicsParams;            // 16 bytes

#pragma warning restore IDE1006 // Naming Styles
}

/// <summary>
/// Manages the global uniform buffer for efficient shader data upload
/// </summary>
public static class GlobalUniforms
{
    private static GlobalUniformsData s_data;
    private static bool s_isDirty = true;

    /// <summary>
    /// Per-upload Graphite buffer. Each <see cref="Upload"/> call
    /// creates a new buffer so that bind groups from different camera renders
    /// reference independent GPU memory, preventing data races when multiple
    /// command buffers are in flight.
    /// </summary>
    private static GBuffer? s_graphiteBuffer;

    /// <summary>
    /// Updates the GPU buffer if data has changed.
    /// A new buffer is created for each upload so that bind groups from the
    /// previous camera render keep referencing their own immutable copy of
    /// the data.
    /// </summary>
    public static void Upload()
    {
        if (!s_isDirty || !Graphics.IsGraphiteReady)
            return;

        var prev = s_graphiteBuffer;

        GlobalUniformsData[] arr = [s_data];
        var bytes = MemoryMarshal.AsBytes(arr.AsSpan()).ToArray();

        var desc = new BufferDescriptor
        {
            SizeInBytes = (uint)GlobalUniformsData.SizeInBytes,
            Usage = BufferUsage.Uniform,
            MemoryAccess = MemoryAccess.CpuToGpu,
            InitialData = bytes,
        };
        s_graphiteBuffer = Graphics.Graphite.CreateBuffer(in desc);

        if (prev != null)
            GraphiteMaterialBinder.Retire(prev);

        s_isDirty = false;
    }

    /// <summary>
    /// Gets the per-upload Graphite buffer for bind group creation and GL UBO binding.
    /// Returns <c>null</c> before the first <see cref="Upload"/> call.
    /// </summary>
    public static GBuffer? GetBuffer()
    {
        return s_graphiteBuffer;
    }

    /// <summary>
    /// Cleans up the global uniform buffer resources
    /// </summary>
    public static void Dispose()
    {
        s_graphiteBuffer?.Dispose();
        s_graphiteBuffer = null;
    }

    // Camera matrix setters (per-frame data)
    public static void SetMatrixV(Float4x4 value)
    {
        s_data.prowl_MatV = (Float4x4)value;
        s_isDirty = true;
    }

    public static void SetMatrixIV(Float4x4 value)
    {
        s_data.prowl_MatIV = (Float4x4)value;
        s_isDirty = true;
    }

    public static void SetMatrixP(Float4x4 value)
    {
        s_data.prowl_MatP = (Float4x4)value;
        s_isDirty = true;
    }

    public static void SetMatrixVP(Float4x4 value)
    {
        s_data.prowl_MatVP = (Float4x4)value;
        s_isDirty = true;
    }

    public static void SetPrevViewProj(Float4x4 value)
    {
        s_data.prowl_PrevViewProj = (Float4x4)value;
        s_isDirty = true;
    }

    // Camera parameters
    public static void SetWorldSpaceCameraPos(Float3 value)
    {
        s_data._WorldSpaceCameraPos = (Float3)value;
        s_isDirty = true;
    }

    public static void SetProjectionParams(Float4 value)
    {
        s_data._ProjectionParams = (Float4)value;
        s_isDirty = true;
    }

    public static void SetScreenParams(Float4 value)
    {
        s_data._ScreenParams = (Float4)value;
        s_isDirty = true;
    }

    public static void SetCameraJitter(Float2 value)
    {
        s_data._CameraJitter = (Float2)value;
        s_isDirty = true;
    }

    public static void SetCameraPreviousJitter(Float2 value)
    {
        s_data._CameraPreviousJitter = (Float2)value;
        s_isDirty = true;
    }

    // Time parameters
    public static void SetTime(Float4 value)
    {
        s_data._Time = (Float4)value;
        s_isDirty = true;
    }

    public static void SetSinTime(Float4 value)
    {
        s_data._SinTime = (Float4)value;
        s_isDirty = true;
    }

    public static void SetCosTime(Float4 value)
    {
        s_data._CosTime = (Float4)value;
        s_isDirty = true;
    }

    public static void SetDeltaTime(Float4 value)
    {
        s_data.prowl_DeltaTime = (Float4)value;
        s_isDirty = true;
    }

    public static void SetGraphicsParams(Float4 value)
    {
        s_data._GraphicsParams = (Float4)value;
        s_isDirty = true;
    }

    /// <summary>
    /// Resets all data to defaults
    /// </summary>
    public static void Clear()
    {
        s_data = new GlobalUniformsData();
        s_isDirty = true;
    }
}
