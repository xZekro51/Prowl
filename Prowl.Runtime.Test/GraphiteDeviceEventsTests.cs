// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.EventSystem;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Graphite;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for the <see cref="GraphiteDeviceEvents"/> domain.
/// Exercises subscribe, invoke, priority ordering, cancellation, and typed args.
/// No actual GPU device is required — events are invoked directly.
/// </summary>
public class GraphiteDeviceEventsTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private void Track(IDisposable d) => _disposables.Add(d);

    #region DeviceReady

    [Fact]
    public void OnDeviceReady_DeliversArgs()
    {
        DeviceReadyArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnDeviceReady(args => received = args));

        GraphiteDeviceEvents.InvokeOnDeviceReady(
            new DeviceReadyArgs("Test GPU", GraphicsBackendType.Vulkan));

        Assert.NotNull(received);
        Assert.Equal("Test GPU", received!.Value.DeviceName);
        Assert.Equal(GraphicsBackendType.Vulkan, received!.Value.Backend);
    }

    #endregion

    #region DeviceLost — ICancellable

    [Fact]
    public void OnDeviceLost_Fires_WithReason()
    {
        DeviceLostArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnDeviceLost(args => received = args));

        GraphiteDeviceEvents.InvokeOnDeviceLost(
            new DeviceLostArgs { Reason = "VK_ERROR_DEVICE_LOST" });

        Assert.NotNull(received);
        Assert.Equal("VK_ERROR_DEVICE_LOST", received!.Reason);
    }

    [Fact]
    public void OnDeviceLost_Cancellation_StopsPropagation()
    {
        List<int> order = [];
        Track(GraphiteDeviceEvents.SubscribeOnDeviceLost(args =>
        {
            order.Add(0);
            args.Cancelled = true;
        }, priority: 0));
        Track(GraphiteDeviceEvents.SubscribeOnDeviceLost(args =>
        {
            order.Add(1);
        }, priority: 1));

        DeviceLostArgs payload = new() { Reason = "test" };
        GraphiteDeviceEvents.InvokeOnDeviceLost(payload);

        Assert.Single(order);
        Assert.Equal(0, order[0]);
    }

    #endregion

    #region DeviceDisposing (Unit event)

    [Fact]
    public void OnDeviceDisposing_Fires()
    {
        bool called = false;
        Track(GraphiteDeviceEvents.SubscribeOnDeviceDisposing(() => called = true));

        GraphiteDeviceEvents.InvokeOnDeviceDisposing();

        Assert.True(called);
    }

    #endregion

    #region SwapchainRecreated

    [Fact]
    public void OnSwapchainRecreated_DeliversOldAndNewDimensions()
    {
        SwapchainRecreatedArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnSwapchainRecreated(args => received = args));

        GraphiteDeviceEvents.InvokeOnSwapchainRecreated(
            new SwapchainRecreatedArgs(800, 600, 1920, 1080));

        Assert.NotNull(received);
        Assert.Equal(800u, received!.Value.OldWidth);
        Assert.Equal(600u, received!.Value.OldHeight);
        Assert.Equal(1920u, received!.Value.NewWidth);
        Assert.Equal(1080u, received!.Value.NewHeight);
    }

    #endregion

    #region GpuFrameBegin

    [Fact]
    public void OnGpuFrameBegin_DeliversFrameSlot()
    {
        GpuFrameBeginArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnGpuFrameBegin(args => received = args));

        GraphiteDeviceEvents.InvokeOnGpuFrameBegin(
            new GpuFrameBeginArgs(1, 2));

        Assert.NotNull(received);
        Assert.Equal(1, received!.Value.FrameSlot);
        Assert.Equal(2u, received!.Value.SwapchainImageIndex);
    }

    #endregion

    #region Priority ordering

    [Fact]
    public void PriorityOrdering_LowerRunsFirst()
    {
        List<int> order = [];
        Track(GraphiteDeviceEvents.SubscribeOnDeviceDisposing(() => order.Add(10), priority: 10));
        Track(GraphiteDeviceEvents.SubscribeOnDeviceDisposing(() => order.Add(-5), priority: -5));
        Track(GraphiteDeviceEvents.SubscribeOnDeviceDisposing(() => order.Add(0), priority: 0));

        GraphiteDeviceEvents.InvokeOnDeviceDisposing();

        Assert.Equal([-5, 0, 10], order);
    }

    #endregion

    #region ValidationMessage

    [Fact]
    public void OnValidationMessage_DeliversFullPayload()
    {
        ValidationMessageArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnValidationMessage(args => received = args));

        GraphiteDeviceEvents.InvokeOnValidationMessage(
            new ValidationMessageArgs(
                ValidationSeverity.Warning,
                "VUID-12345",
                "Descriptor set not bound",
                "MyPipeline"));

        Assert.NotNull(received);
        Assert.Equal(ValidationSeverity.Warning, received!.Value.Severity);
        Assert.Equal("VUID-12345", received!.Value.MessageId);
        Assert.Equal("Descriptor set not bound", received!.Value.Message);
        Assert.Equal("MyPipeline", received!.Value.ObjectName);
    }

    [Fact]
    public void OnValidationMessage_NullObjectName_IsAccepted()
    {
        ValidationMessageArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnValidationMessage(args => received = args));

        GraphiteDeviceEvents.InvokeOnValidationMessage(
            new ValidationMessageArgs(ValidationSeverity.Info, "MSG-0", "test", null));

        Assert.NotNull(received);
        Assert.Null(received!.Value.ObjectName);
    }

    #endregion

    #region UploadWindowClosing

    [Fact]
    public void OnUploadWindowClosing_DeliversCounts()
    {
        UploadWindowClosingArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnUploadWindowClosing(args => received = args));

        GraphiteDeviceEvents.InvokeOnUploadWindowClosing(
            new UploadWindowClosingArgs(5, 1024));

        Assert.NotNull(received);
        Assert.Equal(5, received!.Value.UploadsThisFrame);
        Assert.Equal(1024L, received!.Value.BytesUploaded);
    }

    #endregion

    #region GpuFrameEnd

    [Fact]
    public void OnGpuFrameEnd_DeliversPresentStatus()
    {
        GpuFrameEndArgs? received = null;
        Track(GraphiteDeviceEvents.SubscribeOnGpuFrameEnd(args => received = args));

        GraphiteDeviceEvents.InvokeOnGpuFrameEnd(
            new GpuFrameEndArgs(0, true));

        Assert.NotNull(received);
        Assert.Equal(0, received!.Value.FrameSlot);
        Assert.True(received!.Value.PresentSucceeded);
    }

    #endregion

    #region Dispose unsubscribes

    [Fact]
    public void Dispose_PreventsCallback()
    {
        bool called = false;
        IDisposable sub = GraphiteDeviceEvents.SubscribeOnDeviceDisposing(() => called = true);

        sub.Dispose();
        GraphiteDeviceEvents.InvokeOnDeviceDisposing();

        Assert.False(called);
    }

    #endregion
}
