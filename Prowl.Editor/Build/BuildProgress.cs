// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Build;

/// <summary>
/// A single log entry produced during a build, carrying severity metadata
/// so the UI can render it with the same style as the console panel.
/// </summary>
public sealed class BuildLogEntry
{
    public string Message { get; init; } = string.Empty;
    public LogSeverity Severity { get; init; } = LogSeverity.Normal;
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

/// <summary>
/// Thread-safe container for build progress information.
/// The build pipeline writes log lines from a background thread,
/// while the editor UI reads them from the main thread.
/// </summary>
public sealed class BuildProgress
{
    private readonly object _lock = new();
    private readonly List<BuildLogEntry> _entries = [];

    /// <summary>
    /// Whether the build has finished (success or failure).
    /// </summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// The final result, available once <see cref="IsComplete"/> is true.
    /// </summary>
    public BuildResult? Result { get; private set; }

    /// <summary>
    /// Appends a log line with default (Normal) severity. Thread-safe.
    /// </summary>
    public void Log(string message)
    {
        Log(message, LogSeverity.Normal);
    }

    /// <summary>
    /// Appends a log line with the given severity. Thread-safe.
    /// </summary>
    public void Log(string message, LogSeverity severity)
    {
        lock (_lock)
        {
            _entries.Add(new BuildLogEntry
            {
                Message = message,
                Severity = severity,
                Timestamp = DateTime.Now,
            });
        }
    }

    /// <summary>
    /// Marks the build as complete with the given result. Thread-safe.
    /// </summary>
    public void Complete(BuildResult result)
    {
        lock (_lock)
        {
            Result = result;
            IsComplete = true;
        }
    }

    /// <summary>
    /// Returns a snapshot of all log entries accumulated so far. Thread-safe.
    /// </summary>
    public List<BuildLogEntry> GetEntries()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    /// <summary>
    /// Returns the number of log entries. Thread-safe.
    /// </summary>
    public int EntryCount
    {
        get { lock (_lock) { return _entries.Count; } }
    }
}
