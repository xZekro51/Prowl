// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime.Graphite;

/// <summary>
/// Base class for all GPU resources in Graphite.
/// Resources are immutable after creation and must be explicitly disposed.
/// </summary>
public abstract class GraphiteResource : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Optional debug name for graphics debuggers.
    /// </summary>
    public string? DebugName { get; protected set; }

    /// <summary>
    /// Whether this resource has been disposed.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Disposes the resource, releasing GPU memory.
    /// When <see cref="IsDisposeSuppressed"/> is <c>true</c>, the resource
    /// stays alive (<see cref="IsDisposed"/> remains <c>false</c>) but the
    /// finalizer is suppressed to avoid spurious warnings.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (!IsDisposeSuppressed)
            {
                _disposed = true;
                DisposeResources();
            }
            // Always suppress the finalizer once Dispose() has been called,
            // even for cache-owned resources where disposal is deferred.
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Override to release backend-specific resources.
    /// </summary>
    protected abstract void DisposeResources();

    /// <summary>
    /// When <c>true</c>, <see cref="Dispose"/> becomes a no-op.
    /// Used by resources owned by an internal cache (e.g. sampler cache)
    /// that must not be disposed by external callers.
    /// </summary>
    protected virtual bool IsDisposeSuppressed => false;

    /// <summary>
    /// Throws if the resource has been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    ~GraphiteResource()
    {
        if (!_disposed)
        {
            Debug.LogWarning($"GraphiteResource '{DebugName ?? GetType().Name}' was not disposed before finalization.");
            // Don't dispose here - GPU resources must be released on the main thread
        }
    }
}
