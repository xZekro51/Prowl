// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json.Serialization;

namespace Prowl.Launcher;

/// <summary>
/// Lightweight model representing a known project in the launcher's list.
/// </summary>
public sealed class ProjectInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; }
}
