// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Collects per-frame rendering statistics. The render pipeline increments
/// counters during a frame; call <see cref="SwapFrames"/> at the end of
/// the frame to make the data available via the read-only properties.
/// </summary>
public sealed class RenderStats
{
    // Double-buffered: write to _current during the frame, read from _display.
    private FrameData _current;
    private FrameData _display;

    /// <summary> Draw calls issued this frame. </summary>
    public int DrawCalls => _display.DrawCalls;

    /// <summary> Number of render batches this frame. </summary>
    public int Batches => _display.Batches;

    /// <summary> Total triangles rendered this frame. </summary>
    public int Triangles => _display.Triangles;

    /// <summary> Total vertices rendered this frame. </summary>
    public int Vertices => _display.Vertices;

    /// <summary> Number of renderable objects submitted (before culling). </summary>
    public int RenderableCount => _display.RenderableCount;

    /// <summary> Increment draw-call counter. </summary>
    public void AddDrawCall(int vertices, int indices)
    {
        _current.DrawCalls++;
        _current.Vertices += vertices;
        _current.Triangles += indices / 3;
    }

    /// <summary> Increment batch counter. </summary>
    public void AddBatch() => _current.Batches++;

    /// <summary> Record total renderables submitted to the pipeline. </summary>
    public void SetRenderableCount(int count) => _current.RenderableCount = count;

    /// <summary>
    /// Swap write and display buffers, then clear the write buffer.
    /// Call once per frame after all rendering is done.
    /// </summary>
    public void SwapFrames()
    {
        _display = _current;
        _current = default;
    }

    /// <summary> Global singleton — one stats tracker for the whole engine. </summary>
    public static RenderStats Instance { get; } = new();

    private struct FrameData
    {
        public int DrawCalls;
        public int Batches;
        public int Triangles;
        public int Vertices;
        public int RenderableCount;
    }
}
