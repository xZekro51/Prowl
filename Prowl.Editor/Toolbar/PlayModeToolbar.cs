// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Editor.Core;
using Prowl.Editor.Services;

using Prowl.Runtime;

namespace Prowl.Editor.Toolbar;

/// <summary>
/// Draws the Play / Pause / Step toolbar inline within the main menu bar
/// or at the top of the editor. Not a dockable window — it's always visible.
/// Communicates with <see cref="EditorPlayMode"/> for state transitions.
/// </summary>
public sealed class PlayModeToolbar
{
    private static readonly Vector4 PlayActive  = new(0.20f, 0.52f, 0.32f, 1f);
    private static readonly Vector4 PauseActive = new(0.72f, 0.56f, 0.16f, 1f);
    private static readonly Vector4 InfoCol     = new(0.56f, 0.56f, 0.56f, 1f);

    private static Vector2 Sz(float x, float y) => new(x * Game.DpiScale, y * Game.DpiScale);

    private readonly EditorPlayMode _playMode;

    public PlayModeToolbar(EditorPlayMode playMode)
    {
        _playMode = playMode;
    }

    /// <summary>
    /// Draws the toolbar as a fixed bar above the dockspace.
    /// Call this from the host window, after the menu bar and before the dockspace.
    /// </summary>
    public void Draw()
    {
        float barHeight = 32 * Game.DpiScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.16f, 0.16f, 0.16f, 1f));

        ImGui.BeginChild("##ToolbarBar", new Vector2(0, barHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        bool isPlaying = _playMode.State != PlayModeState.Stopped;
        bool isPaused  = _playMode.State == PlayModeState.Paused;

        // Center the buttons horizontally
        float totalBtnWidth = Sz(64, 0).X * 2 + Sz(56, 0).X + ImGui.GetStyle().ItemSpacing.X * 2;
        float availW = ImGui.GetContentRegionAvail().X;
        float startX = (availW - totalBtnWidth) * 0.5f;
        if (startX < 0) startX = 0;
        ImGui.SetCursorPosX(startX);

        float yPad = (barHeight - Sz(0, 22).Y) * 0.5f;
        ImGui.SetCursorPosY(yPad);

        // Play / Stop
        if (isPlaying)
            ImGui.PushStyleColor(ImGuiCol.Button, PlayActive);
        if (EditorIcons.ImageButtonWithLabel("PlayStop", isPlaying ? EditorIconType.Stop : EditorIconType.Play, isPlaying ? "Stop" : "Play", Sz(64, 24)))
            _playMode.TogglePlay();
        if (isPlaying)
            ImGui.PopStyleColor();

        ImGui.SameLine();

        // Pause
        bool pauseEnabled = isPlaying;
        if (!pauseEnabled) ImGui.BeginDisabled();
        if (isPaused)
            ImGui.PushStyleColor(ImGuiCol.Button, PauseActive);
        if (EditorIcons.ImageButtonWithLabel("PauseBtn", EditorIconType.Pause, "Pause", Sz(64, 24)))
            _playMode.TogglePause();
        if (isPaused)
            ImGui.PopStyleColor();
        if (!pauseEnabled) ImGui.EndDisabled();

        ImGui.SameLine();

        // Step
        bool stepEnabled = isPaused;
        if (!stepEnabled) ImGui.BeginDisabled();
        if (EditorIcons.ImageButtonWithLabel("StepBtn", EditorIconType.StepForward, "Step", Sz(56, 24)))
            _playMode.StepFrame();
        if (!stepEnabled) ImGui.EndDisabled();

        // Status info
        if (isPlaying && EditorServices.TryGet<IEditorTime>(out var time))
        {
            ImGui.SameLine();
            string status = isPaused
                ? $"PAUSED  F:{time!.FrameCount}"
                : $"T:{time!.SimulationTime:F1}s  F:{time.FrameCount}";
            ImGui.TextColored(InfoCol, status);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }
}
