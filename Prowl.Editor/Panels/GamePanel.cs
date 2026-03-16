// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Editor.Core;
using Prowl.Editor.Docking;
using Prowl.Editor.Services;

namespace Prowl.Editor.Panels;

/// <summary>
/// Game View panel — displays the game camera's render texture via
/// ImGui.Image() so it renders within the window's clipping rectangle
/// and respects z-order with other panels and popups.
/// </summary>
public sealed class GamePanel : EditorPanel
{
    /// <summary> Viewport bounds in screen pixels (set from ImGui window position). </summary>
    public Rect ViewportRect { get; private set; }

    public GamePanel() : base("Game") { }

    protected override void DrawContent()
    {
        // Capture viewport rect from ImGui window content area
        Vector2 regionAvail = ImGui.GetContentRegionAvail();
        if (regionAvail.X < 1 || regionAvail.Y < 1)
        {
            ViewportRect = default;
            return;
        }

        Vector2 cursorScreen = ImGui.GetCursorScreenPos();

        bool isPlaying = EditorServices.TryGet<IEditorTime>(out var time) && time!.IsPlaying;

        // Draw game RT as an ImGui image when playing
        bool drewImage = false;
        if (isPlaying && EditorServices.TryGet<IEditorRendering>(out var rendering))
        {
            var rt = rendering!.GameViewRT;
            if (rt != null && rt.MainTexture != null)
            {
                nint texId = (nint)rt.MainTexture.Handle.Handle;
                ImGui.Image(texId, regionAvail, new Vector2(0, 1), new Vector2(1, 0));
                drewImage = true;
            }
        }

        if (!drewImage)
        {
            // Claim the area with an invisible button
            ImGui.InvisibleButton("##GameViewport", regionAvail);
        }

        ViewportRect = new Rect(
            cursorScreen.X, cursorScreen.Y,
            cursorScreen.X + regionAvail.X,
            cursorScreen.Y + regionAvail.Y);

        // Overlay text
        var drawList = ImGui.GetWindowDrawList();

        if (!isPlaying)
        {
            // Stopped overlay — centered hint text
            string stopped = "Press \u25B6 Play to start the game";
            Vector2 textSize = ImGui.CalcTextSize(stopped);
            float cx = cursorScreen.X + (regionAvail.X - textSize.X) * 0.5f;
            float cy = cursorScreen.Y + (regionAvail.Y - textSize.Y) * 0.5f;
            drawList.AddText(new Vector2(cx, cy),
                ImGui.GetColorU32(new Vector4(0.40f, 0.40f, 0.40f, 1f)), stopped);
        }
        else
        {
            // Playing overlay — top-left status
            string status = time!.IsPaused
                ? $"PAUSED \u2014 Frame {time.FrameCount}"
                : $"Playing \u2014 T:{time.SimulationTime:F1}s  Frame:{time.FrameCount}";

            drawList.AddText(new Vector2(cursorScreen.X + 6 * Game.DpiScale, cursorScreen.Y + 4 * Game.DpiScale),
                ImGui.GetColorU32(new Vector4(0.70f, 0.86f, 0.70f, 0.78f)), status);
        }
    }
}
