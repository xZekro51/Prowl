// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.ImGuiIntegration;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Editor.Docking;
using Prowl.Editor.Gizmos;
using Prowl.Editor.Rendering;
using Prowl.Editor.Services;
using Prowl.Editor.Undo;
using Prowl.Editor.Undo.Commands;
using Prowl.Editor.Utilities;

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

    // Click-to-select: track mouse-down position to distinguish click vs drag
    private bool _lmbPressedOnViewport;
    private Float2 _lmbDownPos;
    private const float ClickDragThreshold = 4f; // pixels

    // Fullscreen toggle
    private bool _isMaximized;

    // Gizmo visibility toggle
    private bool _showGizmos = true;

    /// <summary> True when the scene view is maximized (other panels should be hidden). </summary>
    public bool IsMaximized => _isMaximized;

    /// <summary> Current debug rendering mode for the scene viewport. </summary>
    public SceneViewMode ViewMode { get; set; } = SceneViewMode.Lit;

    public ScenePanel() : base("Scene") { }

    public override Vector2 GetPanelPadding() => Vector2.Zero;

    protected override void DrawContent()
    {
        var input = EditorServices.Get<IEditorInput>();
        var selService = EditorServices.Get<ISelectionService>();

        // ── Viewport area (scene fills the entire content region) ──
        Vector2 regionAvail = ImGui.GetContentRegionAvail();
        if (regionAvail.X < 1 || regionAvail.Y < 1)
        {
            ViewportRect = default;
            return;
        }

        Vector2 contentScreenPos = ImGui.GetCursorScreenPos();
        Vector2 contentLocalPos = ImGui.GetCursorPos();

        // ── Scene render texture fills the entire content area (background) ──
        var windowDrawList = ImGui.GetWindowDrawList();
        if (EditorServices.TryGet<IEditorRendering>(out var rendering))
        {
            var rt = rendering!.SceneViewRT;
            if (rt != null && rt.MainTexture != null)
            {
                var graphiteTex = rt.MainTexture?.Handle?.GraphiteTexture;
                nint texId = ImGuiTextureRegistry.GetOrRegister(graphiteTex);
                if (texId != 0)
                {
                    Vector2 imgMax = new(contentScreenPos.X + regionAvail.X, contentScreenPos.Y + regionAvail.Y);
                    // OpenGL framebuffers are bottom-up and need a V-flip; Vulkan framebuffers are top-down.
                    Vector2 uv0 = Graphics.IsOpenGL ? new Vector2(0, 1) : new Vector2(0, 0);
                    Vector2 uv1 = Graphics.IsOpenGL ? new Vector2(1, 0) : new Vector2(1, 1);
                    windowDrawList.AddImage(texId, contentScreenPos, imgMax, uv0, uv1);
                }
            }
        }

        // ViewportRect covers the full content area (used for rendering, gizmos, outline)
        ViewportRect = new Rect(
            contentScreenPos.X, contentScreenPos.Y,
            contentScreenPos.X + regionAvail.X,
            contentScreenPos.Y + regionAvail.Y);

        // ── Overlay toolbar (semi-transparent, drawn on top of the scene) ──
        float tbPad = 4 * Game.DpiScale;
        float tbBtnH = 23 * Game.DpiScale;
        float tbTotalH = tbBtnH + tbPad * 2;


        // ── Viewport interaction area (below toolbar) ──────────
        ImGui.SetCursorPos(new Vector2(contentLocalPos.X, contentLocalPos.Y + tbTotalH));
        Vector2 vpInteractionSize = new(regionAvail.X, Math.Max(1, regionAvail.Y - tbTotalH));

        // Transparent styles so the invisible button shows no hover/active glow
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);

        ImGui.InvisibleButton("##SceneViewport", vpInteractionSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        // Track active state: once mouse is pressed over the viewport, keep it active
        // even as mouse moves (important for orbit/fly camera).
        bool isActive = ImGui.IsItemActive();
        IsHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) || isActive;

        // Keyboard shortcuts for gizmo mode (W/E/R, only when not in fly mode)
        if (IsHovered && !input.IsMouseButton(1))
            Gizmo.ProcessShortcuts(input);

        // Camera navigation — skip when the gizmo is being dragged
        // so that gizmo movement doesn't also orbit/pan the camera.
        bool gizmoActive = Gizmo.IsActive;
        Float2 vpMouseLocal = input.MousePosition - ViewportRect.Min;
        Camera.SetViewportInfo(ViewportRect.Size.X, ViewportRect.Size.Y, vpMouseLocal);
        Camera.ProcessInput(input, IsHovered && !gizmoActive);

        // Focus on selected object with F key
        if (IsHovered && input.IsKeyDown(KeyCode.F))
        {
            if (selService.ActiveObject is GameObject go)
                Camera.FocusOn(go.Transform.Position);
        }

        // ── Click-to-select (raycast picking) ──────────────────
        HandleClickToSelect(input, selService);

        // Draw transform gizmo handles (always visible; the toggle controls component gizmos)
        var selectedGo = selService.ActiveObject as GameObject;
        Gizmo.Draw(selectedGo, Camera, ViewportRect, input);

        uint tbBgCol = ImGui.GetColorU32(new Vector4(0.10f, 0.10f, 0.10f, 0.70f));
        windowDrawList.AddRectFilled(
            contentScreenPos,
            new Vector2(contentScreenPos.X + regionAvail.X, contentScreenPos.Y + tbTotalH),
            tbBgCol);

        ImGui.SetCursorPos(new Vector2(contentLocalPos.X + tbPad, contentLocalPos.Y + tbPad));
        Gizmo.DrawToolbar();
        ImGui.SameLine(0, 16 * Game.DpiScale);
        DrawMaximizeButton();
        ImGui.SameLine(0, 8 * Game.DpiScale);
        DrawViewModeDropdown();
        ImGui.SameLine(0, 8 * Game.DpiScale);
        DrawGizmoToggle();

        // ── Drag-drop target: accept assets from the Project panel ──
        AcceptAssetDrop();

        // Camera info overlay (drawn on top of the viewport via DrawList)
        DrawOverlay();
    }

    private void AcceptAssetDrop()
    {
        if (!EditorDragDrop.IsDragging || EditorDragDrop.PayloadType != "AssetEntry")
            return;

        // Manual mouse-in-rect check — ImGui's IsItemHovered() is unreliable
        // during cross-panel drags because the drag tooltip captures g.HoveredWindow.
        var mousePos = ImGui.GetMousePos();
        bool mouseOverViewport = mousePos.X >= ViewportRect.Min.X && mousePos.X <= ViewportRect.Max.X &&
                                 mousePos.Y >= ViewportRect.Min.Y && mousePos.Y <= ViewportRect.Max.Y;

        if (!mouseOverViewport) return;

        // Visual feedback: highlight the viewport border
        var drawList = ImGui.GetWindowDrawList();
        uint highlightCol = ImGui.GetColorU32(new System.Numerics.Vector4(0.28f, 0.56f, 1.0f, 0.35f));
        drawList.AddRectFilled(
            new System.Numerics.Vector2(ViewportRect.Min.X, ViewportRect.Min.Y),
            new System.Numerics.Vector2(ViewportRect.Max.X, ViewportRect.Max.Y),
            highlightCol);

        if (EditorDragDrop.WasDropped)
        {
            var entry = EditorDragDrop.AcceptDrop<AssetEntry>("AssetEntry");
            if (entry != null)
            {
                string absPath = entry.FullPath;
                string name = Path.GetFileNameWithoutExtension(absPath);

                Float3 dropPos = ComputeDropPosition();

                if (EditorServices.TryGet<UndoRedoService>(out var undo))
                    undo!.Execute(new InstantiateAssetCommand(absPath, name, dropPos));
                else
                    new InstantiateAssetCommand(absPath, name, dropPos).Execute();

                Debug.Log($"[Scene] Dropped asset: {name}");
            }
        }
    }

    private void DrawOverlay()
    {
        Float3 camPos = Camera.GetPosition();
        string backendInfo = Graphics.IsGraphiteReady
            ? $"{Graphics.Graphite.BackendType}"
            : "N/A";
        string info = $"Cam: ({camPos.X:F1}, {camPos.Y:F1}, {camPos.Z:F1})  Mode: {Gizmo.Mode}  Backend: {backendInfo}  [RMB+WASD: Fly | Alt+LMB: Orbit | MMB: Pan | Scroll: Zoom | W/E/R: Tool]";

        var drawList = ImGui.GetWindowDrawList();
        float x = ViewportRect.Min.X + 6 * Game.DpiScale;
        float y = ViewportRect.Max.Y - 20 * Game.DpiScale;
        drawList.AddText(new Vector2(x, y), ImGui.GetColorU32(new Vector4(0.63f, 0.63f, 0.63f, 0.70f)), info);
    }

    // ── Click-to-select via raycast ────────────────────────────

    private void HandleClickToSelect(IEditorInput input, ISelectionService selService)
    {
        if (!IsHovered) return;

        // Don't pick while Alt is held (orbit) or RMB (fly) or gizmo is active
        bool alt = input.IsKey(KeyCode.AltLeft) || input.IsKey(KeyCode.AltRight);
        if (alt || input.IsMouseButton(1) || Gizmo.IsActive || Gizmo.IsSceneGizmoHovered) return;

        // Track LMB press start
        if (input.IsMouseButtonDown(0) && IsHovered)
        {
            _lmbPressedOnViewport = true;
            _lmbDownPos = input.MousePosition;
        }

        // On LMB release, if we didn't drag far, treat it as a click → pick
        if (_lmbPressedOnViewport && input.IsMouseButtonUp(0))
        {
            _lmbPressedOnViewport = false;

            float dragDist = Float2.Length(input.MousePosition - _lmbDownPos);
            if (dragDist <= ClickDragThreshold * Game.DpiScale)
            {
                Float2 vpLocal = input.MousePosition - ViewportRect.Min;
                float vpW = ViewportRect.Size.X;
                float vpH = ViewportRect.Size.Y;

                var result = SceneRaycaster.Pick(vpLocal, vpW, vpH, Camera);
                selService.ActiveObject = result.Hit;
            }
        }
    }

    // ── Maximize / restore toggle button ───────────────────────

    private void DrawMaximizeButton()
    {
        var icon = _isMaximized ? EditorIconType.Restore : EditorIconType.Maximize;
        string label = _isMaximized ? "Restore" : "Maximize";
        if (EditorIcons.ImageButtonWithLabel("MaxBtn", icon, label, new Vector2(90 * Game.DpiScale, 23 * Game.DpiScale)))
        {
            _isMaximized = !_isMaximized;
        }

        if (_isMaximized)
        {
            ImGui.SetWindowFocus();
        }
    }

    // ── View-mode dropdown (Lit, Wireframe, Depth, …) ─────────

    private static readonly string[] s_viewModeNames =
        Enum.GetNames<SceneViewMode>();

    private void DrawViewModeDropdown()
    {
        float btnW = 100 * Game.DpiScale;
        float btnH = 23 * Game.DpiScale;

        ImGui.SetNextItemWidth(btnW);

        int current = (int)ViewMode;
        if (ImGui.Combo("##ViewMode", ref current, s_viewModeNames, s_viewModeNames.Length))
        {
            ViewMode = (SceneViewMode)current;
        }
    }

    private void DrawGizmoToggle()
    {
        var icon = _showGizmos ? EditorIconType.Gizmos : EditorIconType.EyeOff;

        if (!_showGizmos)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.40f, 0.20f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.50f, 0.25f, 0.25f, 1f));
        }

        if (EditorIcons.ImageButtonWithLabel("GizmoToggle", icon, "Gizmos"))
        {
            _showGizmos = !_showGizmos;
            EditorApplication.ShowComponentGizmos = _showGizmos;
        }

        if (!_showGizmos)
            ImGui.PopStyleColor(2);
    }

    // ── Drop position for asset drag-drop ──────────────────────

    private Float3 ComputeDropPosition()
    {
        var input = EditorServices.Get<IEditorInput>();
        float vpW = ViewportRect.Size.X;
        float vpH = ViewportRect.Size.Y;

        if (vpW > 0 && vpH > 0)
        {
            Float2 vpLocal = input.MousePosition - ViewportRect.Min;

            // Try to hit an existing object and place near it
            var result = SceneRaycaster.Pick(vpLocal, vpW, vpH, Camera);
            if (result.Hit != null && result.Distance < float.MaxValue)
            {
                var (origin, direction) = Camera.ViewportToRay(vpLocal, vpW, vpH);
                return origin + direction * result.Distance;
            }
        }

        // Fallback: place 5 units in front of the camera
        Float3 camPos = Camera.GetPosition();
        Float3 camFwd = Float3.Normalize(Camera.Pivot - camPos);
        return camPos + camFwd * 5f;
    }
}
