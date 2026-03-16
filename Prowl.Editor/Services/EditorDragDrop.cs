// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Services;

/// <summary>
/// Simple static carrier for drag-drop data between editor panels.
/// Only one payload can be active at a time.
/// </summary>
public static class EditorDragDrop
{
    /// <summary> The type of data being dragged. </summary>
    public static string? PayloadType { get; private set; }

    /// <summary> The payload data object. </summary>
    public static object? Payload { get; private set; }

    /// <summary> True while a drag operation is in progress. </summary>
    public static bool IsDragging => PayloadType != null;

    /// <summary> Start a drag operation. </summary>
    public static void BeginDrag(string type, object data)
    {
        PayloadType = type;
        Payload = data;
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
    }
}
