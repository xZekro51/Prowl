// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.EventSystem;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for the extended <see cref="RenderingEvents"/> domain: per-stage pipeline
/// events, GI pass events, and render stats delivery.
/// Events are invoked directly — no actual GPU or render pipeline is required.
/// </summary>
public class RenderingEventsExtendedTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private void Track(IDisposable d) => _disposables.Add(d);

    #region Per-stage event firing order

    [Fact]
    public void PerStageEvents_FireInCorrectOrder()
    {
        List<string> order = [];
        Track(RenderingEvents.SubscribeOnCameraRenderBegin(_ => order.Add("CameraBegin")));
        Track(RenderingEvents.SubscribeOnGBufferPassBegin(_ => order.Add("GBufferBegin")));
        Track(RenderingEvents.SubscribeOnGBufferPassEnd(_ => order.Add("GBufferEnd")));
        Track(RenderingEvents.SubscribeOnLightingPassBegin(_ => order.Add("LightingBegin")));
        Track(RenderingEvents.SubscribeOnLightingPassEnd(_ => order.Add("LightingEnd")));
        Track(RenderingEvents.SubscribeOnCompositionComplete(_ => order.Add("Composition")));
        Track(RenderingEvents.SubscribeOnTransparentPassBegin(_ => order.Add("Transparent")));
        Track(RenderingEvents.SubscribeOnCameraRenderEnd(_ => order.Add("CameraEnd")));

        // Simulate the pipeline firing order
        RenderingEvents.InvokeOnCameraRenderBegin(new CameraRenderBeginArgs(1920, 1080));
        RenderingEvents.InvokeOnGBufferPassBegin(new GBufferPassArgs(null!));
        RenderingEvents.InvokeOnGBufferPassEnd(new GBufferPassArgs(null!));
        RenderingEvents.InvokeOnLightingPassBegin(new LightingPassArgs(null!, null!, 4));
        RenderingEvents.InvokeOnLightingPassEnd(new LightingPassArgs(null!, null!, 4));
        RenderingEvents.InvokeOnCompositionComplete(new CompositionCompleteArgs(null!, null!));
        RenderingEvents.InvokeOnTransparentPassBegin(new TransparentPassArgs(null!));
        RenderingEvents.InvokeOnCameraRenderEnd(new CameraRenderEndArgs(true));

        string[] expected =
        [
            "CameraBegin", "GBufferBegin", "GBufferEnd",
            "LightingBegin", "LightingEnd", "Composition",
            "Transparent", "CameraEnd"
        ];
        Assert.Equal(expected, order);
    }

    #endregion

    #region CameraRenderBegin / End

    [Fact]
    public void OnCameraRenderBegin_DeliversDimensions()
    {
        CameraRenderBeginArgs? received = null;
        Track(RenderingEvents.SubscribeOnCameraRenderBegin(args => received = args));

        RenderingEvents.InvokeOnCameraRenderBegin(new CameraRenderBeginArgs(2560, 1440));

        Assert.NotNull(received);
        Assert.Equal(2560u, received!.Value.PixelWidth);
        Assert.Equal(1440u, received!.Value.PixelHeight);
    }

    [Fact]
    public void OnCameraRenderEnd_DeliversSwapchainFlag()
    {
        CameraRenderEndArgs? received = null;
        Track(RenderingEvents.SubscribeOnCameraRenderEnd(args => received = args));

        RenderingEvents.InvokeOnCameraRenderEnd(new CameraRenderEndArgs(false));

        Assert.NotNull(received);
        Assert.False(received!.Value.RenderedToSwapchain);
    }

    #endregion

    #region Lighting pass

    [Fact]
    public void OnLightingPassBegin_DeliversLightCount()
    {
        LightingPassArgs? received = null;
        Track(RenderingEvents.SubscribeOnLightingPassBegin(args => received = args));

        RenderingEvents.InvokeOnLightingPassBegin(new LightingPassArgs(null!, null!, 8));

        Assert.NotNull(received);
        Assert.Equal(8, received!.Value.LightCount);
    }

    #endregion

    #region RenderStatsReady

    [Fact]
    public void OnRenderStatsReady_DeliversNonZeroValues()
    {
        RenderStatsReadyArgs? received = null;
        Track(RenderingEvents.SubscribeOnRenderStatsReady(args => received = args));

        RenderingEvents.InvokeOnRenderStatsReady(
            new RenderStatsReadyArgs(120, 50000, 30000, 8.5f));

        Assert.NotNull(received);
        Assert.Equal(120, received!.Value.DrawCalls);
        Assert.Equal(50000, received!.Value.Triangles);
        Assert.Equal(30000, received!.Value.Vertices);
        Assert.Equal(8.5f, received!.Value.GpuTimeMs);
    }

    #endregion

    #region GI Pass events

    [Fact]
    public void OnGIPassBegin_DeliversModeAndIntensity()
    {
        GIPassBeginArgs? received = null;
        Track(RenderingEvents.SubscribeOnGIPassBegin(args => received = args));

        RenderingEvents.InvokeOnGIPassBegin(new GIPassBeginArgs(
            Resources.Scene.GlobalIlluminationParams.GIMode.VoxelGI, 1.5f));

        Assert.NotNull(received);
        Assert.Equal(Resources.Scene.GlobalIlluminationParams.GIMode.VoxelGI, received!.Value.Mode);
        Assert.Equal(1.5f, received!.Value.GIIntensity);
    }

    [Fact]
    public void OnGIPassEnd_DeliversMode()
    {
        GIPassEndArgs? received = null;
        Track(RenderingEvents.SubscribeOnGIPassEnd(args => received = args));

        RenderingEvents.InvokeOnGIPassEnd(new GIPassEndArgs(
            Resources.Scene.GlobalIlluminationParams.GIMode.SDFGI));

        Assert.NotNull(received);
        Assert.Equal(Resources.Scene.GlobalIlluminationParams.GIMode.SDFGI, received!.Value.Mode);
    }

    #endregion

    #region Original events still work

    [Fact]
    public void OnBeginRender_Fires()
    {
        bool called = false;
        Track(RenderingEvents.SubscribeOnBeginRender(() => called = true));

        RenderingEvents.InvokeOnBeginRender();

        Assert.True(called);
    }

    [Fact]
    public void OnEndRender_Fires()
    {
        bool called = false;
        Track(RenderingEvents.SubscribeOnEndRender(() => called = true));

        RenderingEvents.InvokeOnEndRender();

        Assert.True(called);
    }

    #endregion

    #region Dispose unsubscribes

    [Fact]
    public void Dispose_PreventsCallback()
    {
        bool called = false;
        IDisposable sub = RenderingEvents.SubscribeOnCameraRenderBegin(_ => called = true);

        sub.Dispose();
        RenderingEvents.InvokeOnCameraRenderBegin(new CameraRenderBeginArgs(100, 100));

        Assert.False(called);
    }

    #endregion
}
