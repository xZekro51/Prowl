// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Services;

/// <summary>
/// Represents a single entry captured from the engine's <see cref="Debug"/> logging.
/// </summary>
public sealed class LogEntry
{
    public string Message { get; init; } = string.Empty;
    public LogSeverity Severity { get; init; }
    public string? StackTrace { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public int RepeatCount { get; set; } = 1;
}

/// <summary>
/// Static log sink that subscribes to <see cref="Debug.OnLog"/> and stores
/// entries in memory for the Console panel and Status bar to read.
/// Call <see cref="Initialize"/> once during editor startup.
/// </summary>
public static class EditorConsoleLogger
{
    private static readonly List<LogEntry> _entries = new();
    private static readonly object _lock = new();
    private static bool _initialized;

    /// <summary> Maximum entries kept in memory before oldest are discarded. </summary>
    public static int MaxEntries { get; set; } = 4096;

    /// <summary> Whether identical consecutive messages should be collapsed. </summary>
    public static bool Collapse { get; set; }

    // Filter flags
    public static bool ShowInfo { get; set; } = true;
    public static bool ShowWarning { get; set; } = true;
    public static bool ShowError { get; set; } = true;

    /// <summary> Subscribes to the engine's Debug.OnLog event. Safe to call multiple times. </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Debug.OnLog += OnLogReceived;
    }

    /// <summary> Unsubscribes from the engine log event. </summary>
    public static void Shutdown()
    {
        Debug.OnLog -= OnLogReceived;
        _initialized = false;
    }

    /// <summary> Returns a snapshot of all current entries (thread-safe copy). </summary>
    public static List<LogEntry> GetEntries()
    {
        lock (_lock)
            return new List<LogEntry>(_entries);
    }

    /// <summary> Returns the most recent entry, or null. </summary>
    public static LogEntry? GetLastEntry()
    {
        lock (_lock)
            return _entries.Count > 0 ? _entries[^1] : null;
    }

    /// <summary> Number of entries currently stored. </summary>
    public static int EntryCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary> Clears all stored entries. </summary>
    public static void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }

    public static int InfoCount { get; private set; }
    public static int WarningCount { get; private set; }
    public static int ErrorCount { get; private set; }

    private static void OnLogReceived(string message, DebugStackTrace? stackTrace, LogSeverity severity)
    {
        lock (_lock)
        {
            // Update counters
            switch (severity)
            {
                case LogSeverity.Normal or LogSeverity.Success:
                    InfoCount++;
                    break;
                case LogSeverity.Warning:
                    WarningCount++;
                    break;
                case LogSeverity.Error or LogSeverity.Exception:
                    ErrorCount++;
                    break;
            }

            // Collapse identical consecutive messages
            if (Collapse && _entries.Count > 0)
            {
                var last = _entries[^1];
                if (last.Message == message && last.Severity == severity)
                {
                    last.RepeatCount++;
                    return;
                }
            }

            _entries.Add(new LogEntry
            {
                Message = message,
                Severity = severity,
                StackTrace = stackTrace?.ToString(),
                Timestamp = DateTime.Now,
            });

            // Trim to max size
            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }
    }

    internal static void ResetCounts()
    {
        InfoCount = 0;
        WarningCount = 0;
        ErrorCount = 0;
    }
}
