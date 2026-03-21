// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Silk.NET.OpenGL;

using Graphite = Prowl.Runtime.Graphite;

using static Prowl.Runtime.VertexFormat;

namespace Prowl.Runtime;

public unsafe class GraphicsVertexArray : IDisposable
{
    public uint Handle { get; private set; }

    /// <summary>
    /// Graphite-compatible vertex layout descriptor that mirrors the legacy VAO layout.
    /// </summary>
    public Graphite.VertexLayoutDescriptor? GraphiteVertexLayout { get; private set; }

    /// <summary>
    /// Reference to the vertex buffer bound to this VAO.
    /// Used by <see cref="Prowl.Runtime.Rendering.RenderCommandBuffer"/> to access Graphite shadow buffers.
    /// </summary>
    public GraphicsBuffer? VertexBuffer { get; private set; }

    /// <summary>
    /// Reference to the index buffer bound to this VAO (may be null for non-indexed meshes).
    /// </summary>
    public GraphicsBuffer? IndexBuffer { get; private set; }

    public GraphicsVertexArray(
        VertexFormat format,
        GraphicsBuffer vertices,
        GraphicsBuffer? indices,
        VertexFormat? instanceFormat = null,
        GraphicsBuffer? instanceBuffer = null)
    {
        VertexBuffer = vertices;
        IndexBuffer = indices;

        if (Graphics.IsOpenGL)
        {
            Handle = Graphics.GL.GenVertexArray();

            if (Handle == 0)
            {
                throw new System.Exception("Failed to create VAO - glGenVertexArray returned 0");
            }

            Graphics.GL.BindVertexArray(Handle);

            // Bind vertex buffer and set up per-vertex attributes
            Graphics.GL.BindBuffer(BufferTargetARB.ArrayBuffer, vertices.Handle);
            BindFormat(format);

            // Bind instance buffer and set up per-instance attributes (if provided)
            if (instanceFormat != null && instanceBuffer != null)
            {
                Graphics.GL.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer.Handle);
                BindFormat(instanceFormat);
            }

            // Bind index buffer if present
            if (indices != null)
                Graphics.GL.BindBuffer(BufferTargetARB.ElementArrayBuffer, indices.Handle);

            Graphics.GL.BindVertexArray(0);
        }

        // Shadow: build the equivalent Graphite vertex layout.
        if (Graphics.IsGraphiteReady)
        {
            var layouts = new List<Graphite.VertexBufferLayout>();
            layouts.Add(GraphiteFormatMapper.MapVertexBufferLayout(format, Graphite.VertexStepMode.Vertex));
            if (instanceFormat != null)
                layouts.Add(GraphiteFormatMapper.MapVertexBufferLayout(instanceFormat, Graphite.VertexStepMode.Instance));
            GraphiteVertexLayout = new Graphite.VertexLayoutDescriptor(layouts.ToArray());
        }
    }

    void BindFormat(VertexFormat format)
    {
        for (int i = 0; i < format.Elements.Length; i++)
        {
            Element element = format.Elements[i];
            uint index = element.Semantic;
            Graphics.GL.EnableVertexAttribArray(index);
            int offset = element.Offset;
            unsafe
            {
                if (element.Type == VertexType.Float)
                    Graphics.GL.VertexAttribPointer(index, element.Count, (GLEnum)element.Type, element.Normalized, (uint)format.Size, (void*)offset);
                else
                    Graphics.GL.VertexAttribIPointer(index, element.Count, (GLEnum)element.Type, (uint)format.Size, (void*)offset);

                // Set divisor for instancing (0 = per-vertex, 1+ = per-instance)
                if (element.Divisor > 0)
                {
                    Graphics.GL.VertexAttribDivisor(index, (uint)element.Divisor);
                }
            }
        }
    }

    public bool IsDisposed { get; protected set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        if (Graphics.IsOpenGL)
            Graphics.GL.DeleteVertexArray(Handle);
        IsDisposed = true;
    }

    public override string ToString()
    {
        return Handle.ToString();
    }
}
