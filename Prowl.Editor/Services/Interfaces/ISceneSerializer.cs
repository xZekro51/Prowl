// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Resources;

namespace Prowl.Editor.Services;

/// <summary>
/// Abstracts scene serialization so the editor can save/load scenes
/// without being tied to a specific format.
/// </summary>
public interface ISceneSerializer
{
    /// <summary> File extension used by this serializer (e.g. ".scene"). </summary>
    string FileExtension { get; }

    /// <summary> Saves the given scene to the specified file path. </summary>
    void Save(Scene scene, string filePath);

    /// <summary> Loads a scene from the specified file path. Returns the deserialized scene. </summary>
    Scene? Load(string filePath);
}
