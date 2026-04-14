// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Prowl.Editor.Scripting;

/// <summary>
/// Classifies script changes to determine whether an IL-safe hotload
/// (method-body-only) is possible, or a full structural hotload is required.
/// </summary>
public static class ChangeClassifier
{
    /// <summary>The type of hotload required for a set of changes.</summary>
    public enum HotloadType
    {
        /// <summary>No changes detected.</summary>
        None,

        /// <summary>
        /// Only method bodies, property accessors, or using directives changed.
        /// Can be applied without unloading the ALC (IL patching or fast recompile + swap).
        /// </summary>
        ILSafe,

        /// <summary>
        /// Structural changes detected: new/removed types, field changes, base class changes,
        /// new files, attribute changes. Requires full ALC unload + reload + instance migration.
        /// </summary>
        Full,
    }

    /// <summary>Result of classifying a set of file changes.</summary>
    public readonly struct ClassificationResult
    {
        public HotloadType Type { get; init; }
        public string[] ChangedFiles { get; init; }
        public string Reason { get; init; }
    }

    /// <summary>
    /// Per-file content hashes from the last successful compilation.
    /// Used to detect which files actually have content changes.
    /// </summary>
    private static readonly Dictionary<string, byte[]> s_fileHashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-file structural signatures from the last successful compilation.
    /// A structural signature is a hash of everything EXCEPT method bodies:
    /// type declarations, field declarations, base types, attributes, etc.
    /// </summary>
    private static readonly Dictionary<string, byte[]> s_structuralHashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot all script file hashes for the given project.
    /// Call this after a successful compilation to establish the baseline.
    /// </summary>
    public static void SnapshotBaseline(string assetsPath)
    {
        s_fileHashes.Clear();
        s_structuralHashes.Clear();

        if (!Directory.Exists(assetsPath)) return;

        foreach (var file in Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllBytes(file);
                s_fileHashes[file] = SHA256.HashData(content);
                s_structuralHashes[file] = ComputeStructuralHash(content);
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        HotloadLogger.LogDetail($"Baseline snapshot: {s_fileHashes.Count} files hashed.");
    }

    /// <summary>
    /// Classify a set of file changes to determine the required hotload type.
    /// </summary>
    public static ClassificationResult Classify(ScriptFileWatcher.ChangeSet changes)
    {
        if (changes.IsEmpty)
            return new ClassificationResult { Type = HotloadType.None, ChangedFiles = [], Reason = "No changes" };

        // New or deleted files always require a full hotload
        if (changes.Created.Count > 0)
        {
            return new ClassificationResult
            {
                Type = HotloadType.Full,
                ChangedFiles = changes.Created.ToArray(),
                Reason = $"New files added: {string.Join(", ", changes.Created.Select(Path.GetFileName))}"
            };
        }

        if (changes.Deleted.Count > 0)
        {
            return new ClassificationResult
            {
                Type = HotloadType.Full,
                ChangedFiles = changes.Deleted.ToArray(),
                Reason = $"Files deleted: {string.Join(", ", changes.Deleted.Select(Path.GetFileName))}"
            };
        }

        // Renamed files require full hotload (type names may have changed)
        if (changes.Renamed.Count > 0)
        {
            return new ClassificationResult
            {
                Type = HotloadType.Full,
                ChangedFiles = changes.Renamed.Select(r => r.NewPath).ToArray(),
                Reason = $"Files renamed: {string.Join(", ", changes.Renamed.Select(r => Path.GetFileName(r.NewPath)))}"
            };
        }

        // Modified files — check if changes are IL-safe (method bodies only)
        var actuallyChanged = new List<string>();
        bool hasStructuralChanges = false;
        var structuralReasons = new List<string>();

        foreach (var file in changes.Modified)
        {
            try
            {
                var content = File.ReadAllBytes(file);
                var newHash = SHA256.HashData(content);

                // Check if file actually changed (debounce might send false positives)
                if (s_fileHashes.TryGetValue(file, out var oldHash) && oldHash.AsSpan().SequenceEqual(newHash))
                    continue;

                actuallyChanged.Add(file);

                // Check structural hash
                var newStructural = ComputeStructuralHash(content);
                if (!s_structuralHashes.TryGetValue(file, out var oldStructural) ||
                    !oldStructural.AsSpan().SequenceEqual(newStructural))
                {
                    hasStructuralChanges = true;
                    structuralReasons.Add(Path.GetFileName(file));
                }
            }
            catch
            {
                // Can't read file — treat as structural change
                actuallyChanged.Add(file);
                hasStructuralChanges = true;
                structuralReasons.Add($"{Path.GetFileName(file)} (unreadable)");
            }
        }

        if (actuallyChanged.Count == 0)
            return new ClassificationResult { Type = HotloadType.None, ChangedFiles = [], Reason = "No actual content changes" };

        if (hasStructuralChanges)
        {
            return new ClassificationResult
            {
                Type = HotloadType.Full,
                ChangedFiles = actuallyChanged.ToArray(),
                Reason = $"Structural changes in: {string.Join(", ", structuralReasons)}"
            };
        }

        return new ClassificationResult
        {
            Type = HotloadType.ILSafe,
            ChangedFiles = actuallyChanged.ToArray(),
            Reason = $"Method-body-only changes in {actuallyChanged.Count} file(s)"
        };
    }

    /// <summary>
    /// Update the baseline hashes for specific files after a successful hotload.
    /// </summary>
    public static void UpdateBaseline(string[] files)
    {
        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file))
                {
                    s_fileHashes.Remove(file);
                    s_structuralHashes.Remove(file);
                    continue;
                }

                var content = File.ReadAllBytes(file);
                s_fileHashes[file] = SHA256.HashData(content);
                s_structuralHashes[file] = ComputeStructuralHash(content);
            }
            catch { }
        }
    }

    /// <summary>
    /// Compute a "structural hash" of a C# file that captures everything EXCEPT
    /// method body contents. Changes to method bodies won't change this hash.
    /// 
    /// This is a heuristic approach — we strip content between { } inside method/property bodies
    /// and hash the rest. For production, this could use Roslyn SyntaxTree comparison.
    /// </summary>
    private static byte[] ComputeStructuralHash(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        var structural = ExtractStructuralSignature(text);
        return SHA256.HashData(Encoding.UTF8.GetBytes(structural));
    }

    /// <summary>
    /// Extract a structural signature from C# source code.
    /// This strips method body contents while preserving type declarations, field declarations,
    /// property declarations (signature only), attributes, using directives, and namespace declarations.
    /// </summary>
    public static string ExtractStructuralSignature(string source)
    {
        var sb = new StringBuilder(source.Length);
        int depth = 0;
        bool inMethodBody = false;
        int methodBodyStartDepth = 0;
        bool inString = false;
        bool inVerbatimString = false;
        bool inChar = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool prevWasSlash = false;
        bool prevWasStar = false;
        bool prevWasAt = false;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            // Handle line comments
            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }

            // Handle block comments
            if (inBlockComment)
            {
                if (prevWasStar && c == '/')
                {
                    inBlockComment = false;
                    prevWasStar = false;
                }
                else
                {
                    prevWasStar = c == '*';
                }
                continue;
            }

            // Handle string literals
            if (inString)
            {
                if (c == '\\' && !inVerbatimString) { i++; continue; } // skip escaped char
                if (c == '"')
                {
                    if (inVerbatimString && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        i++; continue; // escaped quote in verbatim string
                    }
                    inString = false;
                    inVerbatimString = false;
                }
                continue;
            }

            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }

            // Detect comment starts
            if (prevWasSlash)
            {
                prevWasSlash = false;
                if (c == '/') { inLineComment = true; continue; }
                if (c == '*') { inBlockComment = true; continue; }
            }
            if (c == '/') { prevWasSlash = true; continue; }

            // Detect string starts
            if (c == '"')
            {
                inString = true;
                inVerbatimString = prevWasAt;
                prevWasAt = false;
                continue;
            }
            if (c == '\'') { inChar = true; prevWasAt = false; continue; }

            prevWasAt = c == '@';

            // Track brace depth
            if (c == '{')
            {
                depth++;

                // Heuristic: if we're at depth >= 2, we're likely inside a method body.
                // Depth 1 = namespace/class, Depth 2+ = method body
                // This is a simplified heuristic — for structural changes,
                // field/property/type declarations happen at depth 1.
                if (depth >= 3 && !inMethodBody)
                {
                    inMethodBody = true;
                    methodBodyStartDepth = depth;
                    sb.Append('{');
                    continue;
                }
            }

            if (c == '}')
            {
                depth--;
                if (inMethodBody && depth < methodBodyStartDepth)
                {
                    inMethodBody = false;
                    sb.Append('}');
                    continue;
                }
            }

            // Only include non-method-body content in the structural hash
            if (!inMethodBody)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Clear all baseline data (e.g., when closing a project).</summary>
    public static void ClearBaseline()
    {
        s_fileHashes.Clear();
        s_structuralHashes.Clear();
    }
}
