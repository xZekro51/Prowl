// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Prowl.ImGuiIntegration;

/// <summary>
/// P/Invoke bindings for the ImGui DockBuilder API which is not exposed by ImGui.NET
/// but is available in the underlying cimgui native library.
/// </summary>
public static class ImGuiDockBuilder
{
    private const string CImGuiLib = "cimgui";

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderAddNode")]
    public static extern uint AddNode(uint nodeId, int flags);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderRemoveNode")]
    public static extern void RemoveNode(uint nodeId);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSetNodeSize")]
    public static extern void SetNodeSize(uint nodeId, Vector2 size);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSetNodePos")]
    public static extern void SetNodePos(uint nodeId, Vector2 pos);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSplitNode")]
    public static extern uint SplitNode(uint nodeId, int splitDir, float sizeRatioForNodeAtDir,
        out uint outIdAtDir, out uint outIdAtOppositeDir);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderDockWindow")]
    public static extern void DockWindow(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderFinish")]
    public static extern void Finish(uint nodeId);
}
