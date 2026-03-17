// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;

using ImGuiNET;

namespace Prowl.Editor.Docking;

/// <summary>
/// Base class for all dockable editor panels (Hierarchy, Inspector, etc.).
/// Each panel is rendered as its own ImGui window that participates in the
/// editor's dockspace — it can be docked, tabbed, undocked, or rearranged
/// freely by the user.
/// </summary>
public abstract class EditorPanel
{
    /// <summary> Display title shown in the window tab/title bar. </summary>
    public string Title { get; }

    /// <summary> Whether this panel is currently visible. </summary>
    public bool IsOpen { get; set; } = true;

    protected EditorPanel(string title) => Title = title;

    public virtual Vector2 GetPanelPadding()
    {
        return ImGui.GetStyle().WindowPadding;
    }

    /// <summary>
    /// Draws the panel as an ImGui window. Handles Begin/End and the close
    /// button. Subclasses implement <see cref="DrawContent"/> for the body.
    /// </summary>
    public void Draw()
    {
        if (!IsOpen) return;

        bool open = IsOpen;

        // Setting manual padding for each panel
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, GetPanelPadding());

        if (ImGui.Begin(Title, ref open))
        {
            DrawContent();
        }
        ImGui.End();

        ImGui.PopStyleVar((int)ImGuiStyleVar.WindowPadding);

        IsOpen = open;
    }

    /// <summary>
    /// Override to draw the panel's content using Dear ImGui calls.
    /// Called each frame while the panel is open and the window is visible.
    /// </summary>
    protected abstract void DrawContent();
}
