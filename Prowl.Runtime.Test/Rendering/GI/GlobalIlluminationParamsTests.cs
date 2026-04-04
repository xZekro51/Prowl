// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Rendering.GI;
using Prowl.Runtime.Resources;

using Xunit;

namespace Prowl.Runtime.Test.Rendering.GI;

/// <summary>
/// Tests for <see cref="Scene.GlobalIlluminationParams"/> struct defaults
/// and <see cref="GIDebugMode"/> enum values.
/// </summary>
public class GlobalIlluminationParamsTests : IDisposable
{
    private readonly List<Scene> _scenes = [];

    public void Dispose()
    {
        foreach (Scene scene in _scenes)
        {
            if (!scene.IsDisposed)
            {
                if (scene.IsActive)
                    scene.Disable();
                scene.Dispose();
            }
        }
        _scenes.Clear();
    }

    private Scene CreateScene()
    {
        Scene scene = new Scene();
        _scenes.Add(scene);
        return scene;
    }

    [Fact]
    public void Default_ModeIsNone()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(Scene.GlobalIlluminationParams.GIMode.None, gi.Mode);
    }

    [Fact]
    public void Default_IntensityIsOne()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(1.0f, gi.Intensity);
    }

    [Fact]
    public void Default_DistanceIs100()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(100.0f, gi.Distance);
    }

    [Fact]
    public void Default_BounceCountIsOne()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(1, gi.BounceCount);
    }

    [Fact]
    public void Default_VoxelResolutionIs256()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(256, gi.VoxelResolution);
    }

    [Fact]
    public void Default_ConeCountIs6()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(6, gi.ConeCount);
    }

    [Fact]
    public void Default_VoxelAOIsTrue()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.True(gi.VoxelAO);
    }

    [Fact]
    public void Default_SDFCascadeCountIs4()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(4, gi.SDFCascadeCount);
    }

    [Fact]
    public void Default_SDFProbeResolutionIs8()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(8, gi.SDFProbeResolution);
    }

    [Fact]
    public void Default_ResolutionScaleIsHalf()
    {
        Scene.GlobalIlluminationParams gi = new();
        Assert.Equal(0.5f, gi.ResolutionScale);
    }

    [Fact]
    public void Scene_GlobalIllumination_DefaultIsNoneMode()
    {
        Scene scene = CreateScene();
        Assert.Equal(Scene.GlobalIlluminationParams.GIMode.None, scene.GlobalIllumination.Mode);
    }

    [Fact]
    public void Scene_GlobalIllumination_CanSetAndGet()
    {
        Scene scene = CreateScene();

        Scene.GlobalIlluminationParams gi = scene.GlobalIllumination;
        gi.Mode = Scene.GlobalIlluminationParams.GIMode.VoxelGI;
        gi.Intensity = 3.0f;
        gi.VoxelResolution = 512;
        scene.GlobalIllumination = gi;

        Assert.Equal(Scene.GlobalIlluminationParams.GIMode.VoxelGI, scene.GlobalIllumination.Mode);
        Assert.Equal(3.0f, scene.GlobalIllumination.Intensity);
        Assert.Equal(512, scene.GlobalIllumination.VoxelResolution);
    }

    [Fact]
    public void GIMode_HasExpectedValues()
    {
        Assert.Equal(0, (int)Scene.GlobalIlluminationParams.GIMode.None);
        Assert.Equal(1, (int)Scene.GlobalIlluminationParams.GIMode.VoxelGI);
        Assert.Equal(2, (int)Scene.GlobalIlluminationParams.GIMode.SDFGI);
    }

    [Fact]
    public void GIDebugMode_HasExpectedValues()
    {
        Assert.Equal(0, (int)GIDebugMode.None);
        Assert.Equal(1, (int)GIDebugMode.IndirectOnly);
        Assert.Equal(2, (int)GIDebugMode.VoxelGrid);
        Assert.Equal(3, (int)GIDebugMode.SDFSlice);
        Assert.Equal(4, (int)GIDebugMode.ProbeGrid);
    }
}
