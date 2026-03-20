// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Build;

/// <summary>
/// Describes the outcome of a build pipeline execution.
/// </summary>
public sealed class BuildResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// A build pipeline knows how to compile and package a Prowl project
/// for a specific family of platforms.  Implement this interface to add
/// support for new platform backends (consoles, mobile, web, etc.).
/// </summary>
public interface IBuildPipeline
{
    /// <summary>
    /// Human-readable display name shown in the editor UI.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// The set of <see cref="BuildTarget"/> values this pipeline supports.
    /// </summary>
    IReadOnlyList<BuildTarget> SupportedTargets { get; }

    /// <summary>
    /// Executes the full build: compile scripts → package assets → publish
    /// an executable.
    /// </summary>
    /// <param name="projectPath">Absolute path to the Prowl project root.</param>
    /// <param name="target">The platform to build for.</param>
    /// <param name="settings">Build settings (configuration, defines, etc.).</param>
    /// <param name="outputDirectory">
    /// Where to place the final output. If <c>null</c>, the pipeline picks
    /// a sensible default (e.g. <c>Builds/{Target}/</c>).
    /// </param>
    /// <returns>A <see cref="BuildResult"/> with success/failure details.</returns>
    BuildResult Build(
        string projectPath,
        BuildTarget target,
        BuildSettings settings,
        string? outputDirectory = null);
}
