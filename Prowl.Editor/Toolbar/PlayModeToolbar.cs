// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Editor.Core;
using Prowl.Editor.Services;

using Prowl.Runtime;

namespace Prowl.Editor.Toolbar;

/// <summary>
/// Draws the Play / Pause / Step toolbar as a small dockable ImGui window.
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

    public void Draw()
    {
        ImGui.SetNextWindowSize(Sz(320, 46), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Toolbar", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            return;
        }

        bool isPlaying = _playMode.State != PlayModeState.Stopped;
        bool isPaused  = _playMode.State == PlayModeState.Paused;

        // ▶ Play / ■ Stop
        if (isPlaying)
            ImGui.PushStyleColor(ImGuiCol.Button, PlayActive);
        if (ImGui.Button(isPlaying ? "\u25A0 Stop" : "\u25B6 Play", Sz(64, 24)))
            _playMode.TogglePlay();
        if (isPlaying)
            ImGui.PopStyleColor();

        ImGui.SameLine();

        // ❚❚ Pause
        bool pauseEnabled = isPlaying;
        if (!pauseEnabled) ImGui.BeginDisabled();
        if (isPaused)
            ImGui.PushStyleColor(ImGuiCol.Button, PauseActive);
        if (ImGui.Button("\u2016 Pause", Sz(64, 24)))
            _playMode.TogglePause();
        if (isPaused)
            ImGui.PopStyleColor();
        if (!pauseEnabled) ImGui.EndDisabled();

        ImGui.SameLine();

        // ⏭ Step
        bool stepEnabled = isPaused;
        if (!stepEnabled) ImGui.BeginDisabled();
        if (ImGui.Button("\u23ED Step", Sz(56, 24)))
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

        ImGui.End();
    }
}
