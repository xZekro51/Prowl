// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Rendering.GI;

using Xunit;

namespace Prowl.Runtime.Test.Rendering.GI;

/// <summary>
/// Tests for <see cref="MeshSDFCache"/> static cache behavior.
/// Since tests run without a Graphite device, GPU-dependent operations
/// (GetOrGenerate) will return null. We test the cache state management.
/// </summary>
public class MeshSDFCacheTests : IDisposable
{
    public MeshSDFCacheTests()
    {
        // Start each test with a clean cache
        MeshSDFCache.Clear();
    }

    public void Dispose()
    {
        MeshSDFCache.Clear();
    }

    [Fact]
    public void MeshSDFResolution_DefaultIs32()
    {
        Assert.Equal(32, MeshSDFCache.MeshSDFResolution);
    }

    [Fact]
    public void MeshSDFResolution_CanBeChanged()
    {
        int original = MeshSDFCache.MeshSDFResolution;
        try
        {
            MeshSDFCache.MeshSDFResolution = 64;
            Assert.Equal(64, MeshSDFCache.MeshSDFResolution);
        }
        finally
        {
            MeshSDFCache.MeshSDFResolution = original;
        }
    }

    [Fact]
    public void Clear_DoesNotThrow()
    {
        // Clearing an empty cache should not throw
        MeshSDFCache.Clear();
    }

    [Fact]
    public void Invalidate_NonExistentID_DoesNotThrow()
    {
        // Invalidating an ID that doesn't exist should be a no-op
        MeshSDFCache.Invalidate(999);
    }
}
