// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using ImGuiNET;

namespace Prowl.Editor.Services;

/// <summary>
/// Simple static carrier for drag-drop data between editor panels.
/// Only one payload can be active at a time.
/// Automatically clears one frame after the left mouse button is released,
/// giving all panels a chance to see the drop on the release frame.
/// </summary>
public static class EditorDragDrop
{
    /// <summary> The type of data being dragged. </summary>
    public static string? PayloadType { get; private set; }

    /// <summary> The payload data object. </summary>
    public static object? Payload { get; private set; }

    /// <summary> True while a drag operation is in progress. </summary>
    public static bool IsDragging => PayloadType != null;

    /// <summary>
    /// True on the frame the mouse was released while dragging.
    /// Panels should check this to know when to accept a drop.
    /// </summary>
    public static bool WasDropped { get; private set; }

    // When true, the payload will be cleared on the *next* Update() call.
    private static bool _pendingClear;

    /// <summary> Start a drag operation. </summary>
    public static void BeginDrag(string type, object data)
    {
        PayloadType = type;
        Payload = data;
        WasDropped = false;
        _pendingClear = false;
    }

    /// <summary> Accept and consume the current drag payload. Returns the data if type matches. </summary>
    public static T? AcceptDrop<T>(string expectedType) where T : class
    {
        if (PayloadType == expectedType && Payload is T data)
        {
            Clear();
            return data;
        }
        return null;
    }

    /// <summary> Cancel/clear the current drag. </summary>
    public static void Clear()
    {
        PayloadType = null;
        Payload = null;
        WasDropped = false;
        _pendingClear = false;
    }

    /// <summary>
    /// Call once per frame (early in the ImGui pass) to automatically end
    /// any active drag when the left mouse button is released.
    /// The actual clear is deferred by one frame so that all panels
    /// can detect the drop during the release frame.
    /// </summary>
    public static void Update()
    {
        // Second frame after release: clear the payload now
        if (_pendingClear)
        {
            Clear();
            return;
        }

        // First frame of release: mark as dropped but keep the payload alive.
        // Check both IsMouseReleased (edge-triggered) and !IsMouseDown (level-triggered)
        // to handle cases where the release event is consumed by ImGui's native drag-drop.
        if (IsDragging && (ImGui.IsMouseReleased(ImGuiMouseButton.Left) || !ImGui.IsMouseDown(ImGuiMouseButton.Left)))
        {
            WasDropped = true;
            _pendingClear = true;
        }
    }
}
