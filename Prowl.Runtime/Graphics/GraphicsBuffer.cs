// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.Rendering;

using Silk.NET.OpenGL;

using Graphite = Prowl.Runtime.Graphite;

namespace Prowl.Runtime;

public class GraphicsBuffer : IDisposable
{
    public bool IsDisposed { get; protected set; }

    public readonly uint Handle;
    public readonly BufferType OriginalType;
    public readonly BufferTargetARB Target;
    public readonly uint SizeInBytes;

    /// <summary>
    /// Shadow Graphite buffer that mirrors this legacy GL buffer.
    /// Available once data has been uploaded via <see cref="Set"/>.
    /// Will be <c>null</c> when the Graphite device is not ready.
    /// </summary>
    public Graphite.Buffer? GraphiteBuffer { get; private set; }

    public unsafe GraphicsBuffer(BufferType type, uint sizeInBytes, void* data, bool dynamic)
    {
        if (type == BufferType.Count)
            throw new ArgumentOutOfRangeException(nameof(type), type, null);

        SizeInBytes = sizeInBytes;

        OriginalType = type;
        switch (type)
        {
            case BufferType.VertexBuffer:
                Target = BufferTargetARB.ArrayBuffer;
                break;
            case BufferType.ElementsBuffer:
                Target = BufferTargetARB.ElementArrayBuffer;
                break;
            case BufferType.UniformBuffer:
                Target = BufferTargetARB.UniformBuffer;
                break;
            case BufferType.StructuredBuffer:
                Target = BufferTargetARB.ShaderStorageBuffer;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }


        if (Graphics.IsOpenGL)
            Handle = Graphics.GL.GenBuffer();
        Bind();
        if (sizeInBytes != 0)
            Set(sizeInBytes, data, dynamic);
    }

    public unsafe void Set(uint sizeInBytes, void* data, bool dynamic)
    {
        if (Graphics.IsOpenGL)
        {
            Bind();
            BufferUsageARB usage = dynamic ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw;
            Graphics.GL.BufferData(Target, sizeInBytes, data, usage);
        }

        // Shadow: (re)create the Graphite buffer with the same data.
        if (Graphics.IsGraphiteReady)
        {
            // Defer disposal — the GPU may still be reading the old buffer
            // from a previously submitted command buffer.
            if (GraphiteBuffer != null)
                GraphiteMaterialBinder.Retire(GraphiteBuffer);
            var desc = new Graphite.BufferDescriptor
            {
                SizeInBytes = sizeInBytes,
                Usage = GraphiteFormatMapper.MapBufferUsage(OriginalType),
                MemoryAccess = dynamic ? Graphite.MemoryAccess.CpuToGpu : Graphite.MemoryAccess.GpuOnly,
            };
            if (data != null && sizeInBytes > 0)
                desc.InitialData = new ReadOnlySpan<byte>(data, (int)sizeInBytes).ToArray();
            GraphiteBuffer = Graphics.Graphite.CreateBuffer(in desc);
        }
    }

    public unsafe void Update(uint offsetInBytes, uint sizeInBytes, void* data)
    {
        if (Graphics.IsOpenGL)
        {
            Bind();
            Graphics.GL.BufferSubData(Target, (nint)offsetInBytes, sizeInBytes, data);
        }

        // Shadow: sync sub-range to the Graphite buffer.
        if (GraphiteBuffer is not null && data != null && sizeInBytes > 0)
        {
            var span = new ReadOnlySpan<byte>(data, (int)sizeInBytes);
            Graphics.Graphite.UpdateBuffer(GraphiteBuffer, offsetInBytes, span);
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        if (boundBuffers[(int)OriginalType] == Handle)
            boundBuffers[(int)OriginalType] = 0;

        // Defer disposal — the GPU may still be referencing this buffer.
        if (GraphiteBuffer != null)
            GraphiteMaterialBinder.Retire(GraphiteBuffer);
        GraphiteBuffer = null;

        IsDisposed = true;
        if (Graphics.IsOpenGL)
            Graphics.GL.DeleteBuffer(Handle);
    }

    public override string ToString()
    {
        return Handle.ToString();
    }

    private readonly static uint[] boundBuffers = new uint[(int)BufferType.Count];

    private void Bind()
    {
        if (!Graphics.IsOpenGL) return;
        if (boundBuffers[(int)OriginalType] == Handle)
            return;
        Graphics.GL.BindBuffer(Target, Handle);
        boundBuffers[(int)OriginalType] = Handle;
    }
}
