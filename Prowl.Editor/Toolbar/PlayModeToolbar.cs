// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Editor.Core;
using Prowl.Editor.Project;
using Prowl.Editor.Services;

using Prowl.Runtime;

namespace Prowl.Editor.Toolbar;

/// <summary>
/// Draws the Play / Pause / Step toolbar inline within the main menu bar
/// or at the top of the editor. Not a dockable window — it's always visible.
/// Communicates with <see cref="EditorPlayMode"/> for state transitions
/// and shows script compilation status with a manual recompile button.
/// </summary>
public sealed class PlayModeToolbar
{
    private static readonly Vector4 PlayActive  = new(0.20f, 0.52f, 0.32f, 1f);
    private static readonly Vector4 PauseActive = new(0.72f, 0.56f, 0.16f, 1f);
    private static readonly Vector4 InfoCol     = new(0.56f, 0.56f, 0.56f, 1f);

    private static readonly Vector4 CompileSuccessCol = new(0.40f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 CompileErrorCol   = new(0.95f, 0.30f, 0.30f, 1f);
    private static readonly Vector4 CompilingCol      = new(0.72f, 0.56f, 0.16f, 1f);

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
        float barHeight = 33 * Game.DpiScale;
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

        float yPad = (barHeight - Sz(0, 23).Y) * 0.5f;
        ImGui.SetCursorPosY(yPad);

        // Play / Stop
        if (isPlaying)
            ImGui.PushStyleColor(ImGuiCol.Button, PlayActive);
        if (EditorIcons.ImageButtonWithLabel("PlayStop", isPlaying ? EditorIconType.Stop : EditorIconType.Play, isPlaying ? "Stop" : "Play", Sz(64, 25)))
            _playMode.TogglePlay();
        if (isPlaying)
            ImGui.PopStyleColor();

        ImGui.SameLine();

        // Pause
        bool pauseEnabled = isPlaying;
        if (!pauseEnabled) ImGui.BeginDisabled();
        if (isPaused)
            ImGui.PushStyleColor(ImGuiCol.Button, PauseActive);
        if (EditorIcons.ImageButtonWithLabel("PauseBtn", EditorIconType.Pause, "Pause", Sz(64, 25)))
            _playMode.TogglePause();
        if (isPaused)
            ImGui.PopStyleColor();
        if (!pauseEnabled) ImGui.EndDisabled();

        ImGui.SameLine();

        // Step
        bool stepEnabled = isPaused;
        if (!stepEnabled) ImGui.BeginDisabled();
        if (EditorIcons.ImageButtonWithLabel("StepBtn", EditorIconType.StepForward, "Step", Sz(56, 25)))
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

        // ── Compile button + status (right-aligned) ──────────────
        DrawCompileSection(barHeight, yPad);

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Draws a Compile button and compilation status indicator on the
    /// right side of the toolbar. Acts as a manual recompile fallback
    /// when <see cref="FileSystemWatcher"/> misses changes.
    /// </summary>
    private static void DrawCompileSection(float barHeight, float yPad)
    {
        var mgr = EditorApplication.ScriptAssemblyManager;
        if (mgr == null) return;

        // Compute the right-aligned position
        float rightPad = 8 * Game.DpiScale;
        Vector2 btnSize = Sz(80, 25);
        float statusTextWidth = 0;
        string statusText = "";
        Vector4 statusColor = InfoCol;

        if (mgr.IsCompiling)
        {
            statusText = "Compiling...";
            statusColor = CompilingCol;
        }
        else if (mgr.LastCompilationResult != null)
        {
            if (mgr.LastCompilationResult.Success)
            {
                int errorCount = mgr.LastCompilationResult.Errors.Count;
                int warnCount = mgr.LastCompilationResult.Warnings.Count;
                if (warnCount > 0)
                {
                    statusText = $"{warnCount} warning(s)";
                    statusColor = CompilingCol;
                }
                else
                {
                    statusText = "OK";
                    statusColor = CompileSuccessCol;
                }
            }
            else
            {
                int errorCount = mgr.LastCompilationResult.Errors.Count;
                statusText = $"{errorCount} error(s)";
                statusColor = CompileErrorCol;
            }
        }

        if (statusText.Length > 0)
            statusTextWidth = ImGui.CalcTextSize(statusText).X + ImGui.GetStyle().ItemSpacing.X;

        float totalWidth = btnSize.X + statusTextWidth + rightPad;
        float xPos = ImGui.GetWindowWidth() - totalWidth;
        if (xPos < 0) xPos = 0;

        ImGui.SetCursorPosX(xPos);
        ImGui.SetCursorPosY(yPad);

        // Status text (before button)
        if (statusText.Length > 0)
        {
            float textY = yPad + (btnSize.Y - ImGui.GetFontSize()) * 0.5f;
            ImGui.SetCursorPosY(textY);
            ImGui.TextColored(statusColor, statusText);

            // Tooltip with error details on hover
            if (ImGui.IsItemHovered() && mgr.LastCompilationResult != null)
            {
                var result = mgr.LastCompilationResult;
                if (result.Errors.Count > 0 || result.Warnings.Count > 0)
                {
                    ImGui.BeginTooltip();
                    int shown = 0;
                    foreach (string err in result.Errors)
                    {
                        ImGui.TextColored(CompileErrorCol, err);
                        if (++shown >= 10) { ImGui.Text("..."); break; }
                    }
                    shown = 0;
                    foreach (string warn in result.Warnings)
                    {
                        ImGui.TextColored(CompilingCol, warn);
                        if (++shown >= 10) { ImGui.Text("..."); break; }
                    }
                    ImGui.EndTooltip();
                }
            }

            ImGui.SameLine();
            ImGui.SetCursorPosY(yPad);
        }

        // Compile button
        bool compileDisabled = mgr.IsCompiling;
        if (compileDisabled) ImGui.BeginDisabled();
        if (EditorIcons.ImageButtonWithLabel("CompileBtn", EditorIconType.Compile, "Compile", btnSize))
            mgr.CompileAndLoad();
        if (compileDisabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Recompile project scripts (Ctrl+B)");
    }
}
