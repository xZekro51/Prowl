// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using NSubstitute;

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Rendering.GI;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test.Rendering.GI;

/// <summary>
/// Tests for <see cref="GIUtils.ResolveGISettings"/>.
/// Verifies directional light override semantics and scene fallback.
/// </summary>
public class GIUtilsTests : IDisposable
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
    public void ResolveGISettings_NoLights_ReturnsSceneDefaults()
    {
        Scene scene = CreateScene();
        scene.GlobalIllumination = new Scene.GlobalIlluminationParams
        {
            Mode = Scene.GlobalIlluminationParams.GIMode.VoxelGI,
            Intensity = 2.5f,
        };

        List<IRenderableLight> lights = [];

        (Scene.GlobalIlluminationParams.GIMode mode, float intensity) = GIUtils.ResolveGISettings(scene, lights);

        Assert.Equal(Scene.GlobalIlluminationParams.GIMode.VoxelGI, mode);
        Assert.Equal(2.5f, intensity);
    }

    [Fact]
    public void ResolveGISettings_NonDirectionalLight_ReturnsSceneDefaults()
    {
        Scene scene = CreateScene();
        scene.GlobalIllumination = new Scene.GlobalIlluminationParams
        {
            Mode = Scene.GlobalIlluminationParams.GIMode.SDFGI,
            Intensity = 1.5f,
        };

        IRenderableLight pointLight = Substitute.For<IRenderableLight>();
        pointLight.GetLightType().Returns(LightType.Point);

        List<IRenderableLight> lights = [pointLight];

        (Scene.GlobalIlluminationParams.GIMode mode, float intensity) = GIUtils.ResolveGISettings(scene, lights);

        Assert.Equal(Scene.GlobalIlluminationParams.GIMode.SDFGI, mode);
        Assert.Equal(1.5f, intensity);
    }

    [Fact]
    public void ResolveGISettings_DefaultScene_ReturnsNoneMode()
    {
        Scene scene = CreateScene();

        List<IRenderableLight> lights = [];

        (Scene.GlobalIlluminationParams.GIMode mode, float intensity) = GIUtils.ResolveGISettings(scene, lights);

        Assert.Equal(Scene.GlobalIlluminationParams.GIMode.None, mode);
        Assert.Equal(1.0f, intensity);
    }
}
