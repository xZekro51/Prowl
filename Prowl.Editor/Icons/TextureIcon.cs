// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Numerics;
using ImGuiNET;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Icons;

/// <summary>
/// An <see cref="IIcon"/> backed by a GPU texture (e.g. rasterised SVG or loaded PNG).
/// Renders via <c>ImDrawList.AddImage</c>.
/// </summary>
public sealed class TextureIcon : IIcon, IDisposable
{
    private readonly Texture2D _texture;
    private bool _disposed;

    /// <summary>The raw OpenGL texture handle usable with <c>ImGui.Image</c>.</summary>
    public nint TextureId => _disposed ? 0 : (nint)_texture.Handle.Handle;

    public TextureIcon(Texture2D texture)
    {
        _texture = texture ?? throw new ArgumentNullException(nameof(texture));
    }

    /// <inheritdoc />
    public void Draw(Vector2 position, float size, Vector4? tint = null)
    {
        nint texId = TextureId;
        if (texId == 0) return;

        var color = tint ?? new Vector4(1f, 1f, 1f, 1f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddImage(
            texId,
            position,
            new Vector2(position.X + size, position.Y + size),
            new Vector2(0, 1), new Vector2(1, 0),
            ImGui.GetColorU32(color));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _texture?.Dispose();
    }
}
