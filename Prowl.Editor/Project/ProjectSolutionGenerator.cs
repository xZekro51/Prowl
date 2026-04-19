// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;

using Prowl.Runtime;

namespace Prowl.Editor;

/// <summary>
/// Generates an IDE-friendly <c>.csproj</c> and <c>.sln</c> at the root of a
/// Prowl game project so that external editors (Visual Studio, Rider, VS Code)
/// provide IntelliSense, code navigation, and error highlighting for user
/// scripts under <c>Assets/</c>.
/// <para>
/// This mirrors the Unity workflow where a <c>.sln</c> is placed beside the
/// <c>Assets/</c> folder and can be opened directly from the editor.
/// </para>
/// </summary>
public static class ProjectSolutionGenerator
{
    /// <summary>Standard C# project type GUID used in .sln files.</summary>
    private static readonly Guid CSharpProjectTypeGuid = new("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC");

    /// <summary>File name of the event-system source generator assembly.</summary>
    internal const string GeneratorDllName = "Prowl.EventSystem.Generators.dll";

    /// <summary>
    /// Attempts to locate the <c>Prowl.EventSystem.Generators.dll</c> analyzer.
    /// Searches the <c>analyzers/</c> subfolder of the editor's base directory
    /// first, then falls back to the generator project's build output (useful
    /// during development when running from the build tree).
    /// Returns <c>null</c> if not found.
    /// </summary>
    internal static string? FindGeneratorDllPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Deployed location: {editor}/analyzers/
        string deployed = Path.Combine(baseDir, "analyzers", GeneratorDllName);
        if (File.Exists(deployed))
            return Path.GetFullPath(deployed);

        // 2. Development location: walk up from the editor output to the solution
        //    root, then into the generator project's build output.
        //    Editor output is typically: <sln>/Build/Editor/<config>/net9.0/
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName,
                "Prowl.EventSystem.Generators", "bin");
            if (Directory.Exists(candidate))
            {
                // Search all configuration/TFM combos under bin/
                foreach (string dll in Directory.GetFiles(candidate, GeneratorDllName, SearchOption.AllDirectories))
                    return Path.GetFullPath(dll);
            }
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Generates (or regenerates) the <c>Assembly-Project.csproj</c> and
    /// <c>{ProjectName}.sln</c> files at the root of <paramref name="projectPath"/>.
    /// Existing files are overwritten.
    /// </summary>
    public static void GenerateSolution(string projectPath)
    {
        string projectName = Path.GetFileName(
            projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string csprojFileName = $"{ProjectScriptCompiler.AssemblyName}.csproj";
        string csprojPath = Path.Combine(projectPath, csprojFileName);
        string slnPath = Path.Combine(projectPath, $"{projectName}.sln");

        // Deterministic GUID so it stays stable across regenerations.
        Guid projectGuid = GenerateDeterministicGuid(ProjectScriptCompiler.AssemblyName);

        // Collect engine DLL references from the editor's output directory.
        var references = CollectEngineReferences();

        // Locate the source generator so it can be emitted as an <Analyzer>.
        string? generatorDll = FindGeneratorDllPath();

        WriteCsProj(csprojPath, references, generatorDll);
        WriteSln(slnPath, csprojFileName, ProjectScriptCompiler.AssemblyName, projectGuid);

        Debug.Log($"[Scripts] Generated IDE solution: {slnPath}");
    }

    /// <summary>
    /// Opens the solution file in the user's default IDE.
    /// If the solution does not exist yet it is generated first.
    /// </summary>
    public static void OpenSolution(string projectPath)
    {
        string projectName = Path.GetFileName(
            projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string slnPath = Path.Combine(projectPath, $"{projectName}.sln");

        if (!File.Exists(slnPath))
            GenerateSolution(projectPath);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = slnPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Scripts] Failed to open solution: {ex.Message}");
        }
    }

    // ── .csproj Generation ──────────────────────────────────────────

    private static void WriteCsProj(
        string path,
        List<(string name, string dllPath)> references,
        string? generatorDllPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("""<Project Sdk="Microsoft.NET.Sdk">""");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net9.0</TargetFramework>");
        sb.AppendLine("    <LangVersion>13</LangVersion>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
        sb.AppendLine("    <!-- This project is for IDE IntelliSense; the Prowl Editor compiles scripts via Roslyn. -->");
        sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine("""    <Compile Include="Assets\**\*.cs" />""");
        sb.AppendLine("  </ItemGroup>");

        if (references.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var (name, dllPath) in references)
            {
                string escapedPath = SecurityElement.Escape(dllPath) ?? dllPath;
                sb.AppendLine($"    <Reference Include=\"{SecurityElement.Escape(name) ?? name}\">");
                sb.AppendLine($"      <HintPath>{escapedPath}</HintPath>");
                sb.AppendLine("      <Private>false</Private>");
                sb.AppendLine("    </Reference>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        // Source generator so that [EventDomain] classes get IntelliSense
        if (generatorDllPath != null)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            string escaped = SecurityElement.Escape(generatorDllPath) ?? generatorDllPath;
            sb.AppendLine($"    <Analyzer Include=\"{escaped}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine();
        sb.AppendLine("</Project>");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ── .sln Generation ─────────────────────────────────────────────

    private static void WriteSln(
        string path,
        string csprojFileName,
        string csprojDisplayName,
        Guid projectGuid)
    {
        string typeGuid = CSharpProjectTypeGuid.ToString("B").ToUpperInvariant();
        string projGuid = projectGuid.ToString("B").ToUpperInvariant();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
        sb.AppendLine($"Project(\"{typeGuid}\") = \"{csprojDisplayName}\", \"{csprojFileName}\", \"{projGuid}\"");
        sb.AppendLine("EndProject");
        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        sb.AppendLine($"\t\t{projGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
        sb.AppendLine($"\t\t{projGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ── Reference Collection ────────────────────────────────────────

    /// <summary>
    /// Collects managed DLL references from the editor's base directory.
    /// These are the engine assemblies (Prowl.Runtime, Echo, Paper, Silk.NET, …)
    /// that user scripts may depend on. BCL references are provided
    /// automatically by the <c>net9.0</c> target framework.
    /// </summary>
    private static List<(string name, string dllPath)> CollectEngineReferences()
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!Directory.Exists(baseDir))
            return result;

        foreach (string dll in Directory.GetFiles(baseDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileNameWithoutExtension(dll);

            // Skip Roslyn compiler assemblies — users don't need those for scripting.
            if (fileName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(Path.GetFullPath(dll)))
                continue;

            if (!IsManagedAssembly(dll))
                continue;

            string assemblyName = AssemblyName.GetAssemblyName(dll).Name ?? fileName;
            result.Add((assemblyName, Path.GetFullPath(dll)));
        }

        return result;
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Guid GenerateDeterministicGuid(string input)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
