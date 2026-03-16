// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Editor.Docking;
using Prowl.Editor.Gizmos;
using Prowl.Editor.Rendering;
using Prowl.Editor.Services;

namespace Prowl.Editor.Panels;

/// <summary>
/// 3D scene viewport with orbit camera, render-texture display, and
/// transform gizmos. The render texture is drawn as an ImGui.Image()
/// inside a child window so it respects window clipping and z-order.
/// </summary>
public sealed class ScenePanel : EditorPanel
{
    /// <summary> Viewport bounds in screen pixels (set from ImGui window position). </summary>
    public Rect ViewportRect { get; private set; }

    /// <summary> Whether the mouse is hovering over this viewport. </summary>
    public bool IsHovered { get; private set; }

    public SceneCamera Camera { get; } = new();
    public TransformGizmo Gizmo { get; } = new();

    public ScenePanel() : base("Scene") { }

    protected override void DrawContent()
    {
        var input = EditorServices.Get<IEditorInput>();
        var selService = EditorServices.Get<ISelectionService>();

        // ── Gizmo mode toolbar ─────────────────────────────────
        Gizmo.DrawToolbar();

        ImGui.Separator();

        // ── Viewport area ──────────────────────────────────────
        Vector2 regionAvail = ImGui.GetContentRegionAvail();
        if (regionAvail.X < 1 || regionAvail.Y < 1)
        {
            ViewportRect = default;
            return;
        }

        // Draw the scene render texture inside the window via ImGui.Image().
        // This keeps the image within ImGui's clipping so it does not overlap
        // menus, popups, or other panels (fixes the Z-order issue).
        Vector2 cursorScreen = ImGui.GetCursorScreenPos();

        if (EditorServices.TryGet<IEditorRendering>(out var rendering))
        {
            var rt = rendering!.SceneViewRT;
            if (rt != null && rt.MainTexture != null)
            {
                nint texId = (nint)rt.MainTexture.Handle.Handle;
                // UV flipped vertically because OpenGL framebuffer is bottom-up
                ImGui.Image(texId, regionAvail, new Vector2(0, 1), new Vector2(1, 0));
            }
            else
            {
                // No RT yet — reserve the space with an invisible button
                ImGui.InvisibleButton("##SceneViewport", regionAvail,
                    ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            }
        }
        else
        {
            ImGui.InvisibleButton("##SceneViewport", regionAvail,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        }

        IsHovered = ImGui.IsItemHovered();

        // Compute viewport rect in screen pixels (used for gizmo drawing & rendering)
        ViewportRect = new Rect(
            cursorScreen.X, cursorScreen.Y,
            cursorScreen.X + regionAvail.X,
            cursorScreen.Y + regionAvail.Y);

        // Keyboard shortcuts for gizmo mode
        Gizmo.ProcessShortcuts(input);

        // Camera navigation (orbit / pan / zoom)
        Camera.ProcessInput(input, IsHovered);

        // Focus on selected object with F key
        if (IsHovered && input.IsKeyDown(KeyCode.F))
        {
            if (selService.ActiveObject is GameObject go)
                Camera.FocusOn(go.Transform.Position);
        }

        // Draw gizmo handles over the viewport using ImGui DrawList
        var selectedGo = selService.ActiveObject as GameObject;
        Gizmo.Draw(selectedGo, Camera, ViewportRect, input);

        // Camera info overlay (drawn on top of the viewport via DrawList)
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        Float3 camPos = Camera.GetPosition();
        string info = $"Cam: ({camPos.X:F1}, {camPos.Y:F1}, {camPos.Z:F1})  Mode: {Gizmo.Mode}  [RMB+WASD: Fly | Alt+LMB: Orbit | MMB: Pan]";

        var drawList = ImGui.GetWindowDrawList();
        float x = ViewportRect.Min.X + 6 * Game.DpiScale;
        float y = ViewportRect.Max.Y - 20 * Game.DpiScale;
        drawList.AddText(new Vector2(x, y), ImGui.GetColorU32(new Vector4(0.63f, 0.63f, 0.63f, 0.70f)), info);
    }
}
