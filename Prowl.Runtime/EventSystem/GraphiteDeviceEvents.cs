// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Graphite;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by the Graphite GPU device throughout its lifecycle.
/// Covers device creation/destruction, swapchain management, per-frame
/// GPU boundaries, upload batching windows, and validation diagnostics.
/// Subscribe via <c>GraphiteDeviceEvents.SubscribeOnXxx(...)</c> or
/// invoke globally with <c>GraphiteDeviceEvents.GlobalInvokeOnXxx(...)</c>.
/// </summary>
[EventDomain(Global = true)]
public static partial class GraphiteDeviceEvents
{
    // ── Device Lifecycle ──────────────────────────────────────────────

    /// <summary>Raised once after the GPU device and all subsystems are fully initialized.</summary>
    [EventArgs(typeof(DeviceReadyArgs))]
    private static readonly EventKey _OnDeviceReady = new();

    /// <summary>Raised when an unrecoverable GPU device loss is detected (e.g. VK_ERROR_DEVICE_LOST).</summary>
    [EventArgs(typeof(DeviceLostArgs))]
    private static readonly EventKey _OnDeviceLost = new();

    /// <summary>Raised at the very start of device teardown, before any GPU resources are destroyed.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnDeviceDisposing = new();

    // ── Swapchain ─────────────────────────────────────────────────────

    /// <summary>Raised after the swapchain has been destroyed and recreated (e.g. window resize).</summary>
    [EventArgs(typeof(SwapchainRecreatedArgs))]
    private static readonly EventKey _OnSwapchainRecreated = new();

    /// <summary>Raised when the window is minimized and the swapchain extent becomes zero.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnSwapchainMinimized = new();

    /// <summary>Raised when the window is restored from a minimized state and the swapchain is usable again.</summary>
    [EventArgs(typeof(SwapchainRestoredArgs))]
    private static readonly EventKey _OnSwapchainRestored = new();

    // ── GPU Frame Boundaries ──────────────────────────────────────────

    /// <summary>Raised at the start of a GPU frame, after fence wait and resource recycling.</summary>
    [EventArgs(typeof(GpuFrameBeginArgs))]
    private static readonly EventKey _OnGpuFrameBegin = new();

    /// <summary>Raised at the end of a GPU frame, after command submission and present.</summary>
    [EventArgs(typeof(GpuFrameEndArgs))]
    private static readonly EventKey _OnGpuFrameEnd = new();

    // ── Upload Batching ───────────────────────────────────────────────

    /// <summary>Raised after <c>BeginUploadBatch()</c> — the upload window is now open for texture/buffer staging.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnUploadWindowOpen = new();

    /// <summary>Raised just before <c>FlushUploadBatch()</c> — last chance to stage uploads this frame.</summary>
    [EventArgs(typeof(UploadWindowClosingArgs))]
    private static readonly EventKey _OnUploadWindowClosing = new();

    // ── Diagnostics ───────────────────────────────────────────────────

    /// <summary>Raised when the Vulkan validation layer (or equivalent) emits a message.</summary>
    [EventArgs(typeof(ValidationMessageArgs))]
    private static readonly EventKey _OnValidationMessage = new();
}

// ── Argument Types ────────────────────────────────────────────────────

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnDeviceReady"/>.
/// </summary>
/// <param name="DeviceName">Human-readable GPU device name (e.g. "NVIDIA GeForce RTX 4090").</param>
/// <param name="Backend">The graphics backend that was initialized.</param>
public readonly record struct DeviceReadyArgs(
    string DeviceName,
    GraphicsBackendType Backend);

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnDeviceLost"/>.
/// Implements <see cref="ICancellable"/> to allow a handler to attempt recovery
/// and prevent further propagation.
/// </summary>
public struct DeviceLostArgs : ICancellable
{
    /// <summary>Whether a handler has handled the device loss and wants to stop propagation.</summary>
    public bool Cancelled { get; set; }

    /// <summary>Human-readable reason for the device loss.</summary>
    public string Reason { get; init; }
}

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnSwapchainRecreated"/>.
/// </summary>
/// <param name="OldWidth">Previous swapchain width in pixels.</param>
/// <param name="OldHeight">Previous swapchain height in pixels.</param>
/// <param name="NewWidth">New swapchain width in pixels.</param>
/// <param name="NewHeight">New swapchain height in pixels.</param>
public readonly record struct SwapchainRecreatedArgs(
    uint OldWidth,
    uint OldHeight,
    uint NewWidth,
    uint NewHeight);

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnSwapchainRestored"/>.
/// </summary>
/// <param name="Width">Restored swapchain width in pixels.</param>
/// <param name="Height">Restored swapchain height in pixels.</param>
public readonly record struct SwapchainRestoredArgs(
    uint Width,
    uint Height);

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnGpuFrameBegin"/>.
/// </summary>
/// <param name="FrameSlot">The frame-in-flight index (0 to MaxFramesInFlight-1).</param>
/// <param name="SwapchainImageIndex">The acquired swapchain image index for this frame.</param>
public readonly record struct GpuFrameBeginArgs(
    int FrameSlot,
    uint SwapchainImageIndex);

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnGpuFrameEnd"/>.
/// </summary>
/// <param name="FrameSlot">The frame-in-flight index that just completed.</param>
/// <param name="PresentSucceeded">Whether the present operation succeeded without errors.</param>
public readonly record struct GpuFrameEndArgs(
    int FrameSlot,
    bool PresentSucceeded);

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnUploadWindowClosing"/>.
/// </summary>
/// <param name="UploadsThisFrame">Number of upload operations batched this frame (0 if tracking not yet implemented).</param>
/// <param name="BytesUploaded">Total bytes staged for upload this frame (0 if tracking not yet implemented).</param>
public readonly record struct UploadWindowClosingArgs(
    int UploadsThisFrame,
    long BytesUploaded);

/// <summary>
/// Arguments for <see cref="GraphiteDeviceEvents.OnValidationMessage"/>.
/// </summary>
/// <param name="Severity">The severity level of the validation message.</param>
/// <param name="MessageId">The message identifier string (e.g. Vulkan message ID name).</param>
/// <param name="Message">The full validation message text.</param>
/// <param name="ObjectName">Optional name of the Vulkan object involved, or null if unknown.</param>
public readonly record struct ValidationMessageArgs(
    ValidationSeverity Severity,
    string MessageId,
    string Message,
    string? ObjectName);

/// <summary>
/// Severity levels for GPU validation/debug messages.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Informational message with no action required.</summary>
    Info,
    /// <summary>Warning that may indicate suboptimal usage.</summary>
    Warning,
    /// <summary>Error indicating incorrect API usage or a bug.</summary>
    Error,
    /// <summary>Performance warning indicating a non-optimal code path.</summary>
    Performance,
}
