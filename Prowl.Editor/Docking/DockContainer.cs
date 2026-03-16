// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.UI;
using Prowl.Vector;

namespace Prowl.Editor.Docking;

/// <summary>
/// Manages and renders the docking layout tree.
/// Recursively draws split nodes and leaf panels.
/// </summary>
public sealed class DockContainer
{
    private static readonly Color DividerColor = new(20, 20, 20);
    private static readonly Color PanelBg = new(35, 35, 35);
    private static readonly Color TitleBarBg = new(48, 48, 48);
    private static readonly Color TitleText = new(180, 180, 180);
    private const float TitleBarHeight = 28;
    private const float DividerSize = 2;

    public DockNode Root { get; set; }

    public DockContainer(DockNode root)
    {
        Root = root;
    }

    public void Draw(IUIRenderer ui)
    {
        DrawNode(ui, Root, "dock");
    }

    private void DrawNode(IUIRenderer ui, DockNode node, string id)
    {
        if (node.IsLeaf)
            DrawLeaf(ui, node, id);
        else
            DrawSplit(ui, node, id);
    }

    private void DrawLeaf(IUIRenderer ui, DockNode node, string id)
    {
        if (node.Panel == null || !node.Panel.IsOpen)
            return;

        // Panel frame: title bar + content
        using (ui.Column($"P_{id}")
            .Width(UIValue.Stretch(1))
            .Height(UIValue.Stretch(1))
            .BackgroundColor(PanelBg)
            .Enter())
        {
            // Title bar
            var titleBuilder = ui.Row($"TB_{id}")
                .Width(UIValue.Stretch(1))
                .Height(TitleBarHeight)
                .BackgroundColor(TitleBarBg)
                .ChildLeft(8);

            using (titleBuilder.Enter()) { }

            // Divider below title
            using (ui.Box($"TD_{id}")
                .Width(UIValue.Stretch(1))
                .Height(1)
                .BackgroundColor(DividerColor)
                .Enter()) { }

            // Content area — panels now use ImGui directly via Draw()
            using (ui.Column($"PC_{id}")
                .Width(UIValue.Stretch(1))
                .Height(UIValue.Stretch(1))
                .ChildLeft(4).ChildRight(4).ChildTop(4).ChildBottom(4)
                .Enter())
            {
                node.Panel.Draw();
            }
        }
    }

    private void DrawSplit(IUIRenderer ui, DockNode node, string id)
    {
        bool isHorizontal = node.Direction == SplitDirection.Horizontal;
        float r = node.SplitRatio;

        if (isHorizontal)
        {
            using (ui.Row($"S_{id}")
                .Width(UIValue.Stretch(1))
                .Height(UIValue.Stretch(1))
                .Enter())
            {
                using (ui.Box($"SF_{id}")
                    .Width(UIValue.Stretch(r))
                    .Height(UIValue.Stretch(1))
                    .Enter())
                {
                    DrawNode(ui, node.First!, $"{id}a");
                }

                using (ui.Box($"SD_{id}")
                    .Width(DividerSize)
                    .Height(UIValue.Stretch(1))
                    .BackgroundColor(DividerColor)
                    .Enter()) { }

                using (ui.Box($"SS_{id}")
                    .Width(UIValue.Stretch(1f - r))
                    .Height(UIValue.Stretch(1))
                    .Enter())
                {
                    DrawNode(ui, node.Second!, $"{id}b");
                }
            }
        }
        else
        {
            using (ui.Column($"S_{id}")
                .Width(UIValue.Stretch(1))
                .Height(UIValue.Stretch(1))
                .Enter())
            {
                using (ui.Box($"SF_{id}")
                    .Width(UIValue.Stretch(1))
                    .Height(UIValue.Stretch(r))
                    .Enter())
                {
                    DrawNode(ui, node.First!, $"{id}a");
                }

                using (ui.Box($"SD_{id}")
                    .Width(UIValue.Stretch(1))
                    .Height(DividerSize)
                    .BackgroundColor(DividerColor)
                    .Enter()) { }

                using (ui.Box($"SS_{id}")
                    .Width(UIValue.Stretch(1))
                    .Height(UIValue.Stretch(1f - r))
                    .Enter())
                {
                    DrawNode(ui, node.Second!, $"{id}b");
                }
            }
        }
    }
}
