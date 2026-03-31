// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

using Prowl.Editor.Core;
using Prowl.Runtime;
using Prowl.Runtime.EventSystem;

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
    public long FrameNumber { get; init; }
    public DebugStackTrace? StackFrames { get; init; }
}

/// <summary>
/// Static log sink that subscribes to <see cref="Debug.DebugEventManager"/> and stores
/// entries in memory for the Console panel and Status bar to read.
/// Call <see cref="Initialize"/> once during editor startup.
/// </summary>
public static class EditorConsoleLogger
{
    private static readonly List<LogEntry> _entries = new();
    private static readonly object _lock = new();
    private static bool _initialized;
    private static IDisposable? _logSubscription;

    /// <summary> Maximum entries kept in memory before oldest are discarded. </summary>
    public static int MaxEntries { get; set; } = 4096;

    /// <summary> Whether identical consecutive messages should be collapsed. </summary>
    public static bool Collapse { get; set; }

    // Filter flags
    public static bool ShowInfo { get; set; } = true;
    public static bool ShowWarning { get; set; } = true;
    public static bool ShowError { get; set; } = true;

    /// <summary> Automatically clear the log when entering play mode. </summary>
    public static bool ClearOnPlay { get; set; } = true;

    /// <summary> Pause play mode when an error or exception is logged. </summary>
    public static bool ErrorPause { get; set; }

    /// <summary> Subscribes to the engine's Debug event system. Safe to call multiple times. </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        _logSubscription = DebugEvents.SubscribeOnLog(
            args => OnLogReceived(args.Message, args.StackTrace, args.Severity));
    }

    /// <summary> Unsubscribes from the engine log event. </summary>
    public static void Shutdown()
    {
        _logSubscription?.Dispose();
        _logSubscription = null;
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

    /// <summary>
    /// Exports all current log entries to a text file.
    /// </summary>
    public static void ExportToFile(string filePath)
    {
        List<LogEntry> snapshot;
        lock (_lock)
            snapshot = new List<LogEntry>(_entries);

        var sb = new StringBuilder();
        sb.AppendLine($"Prowl Engine \u2014 Console Log Export ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();

        foreach (var entry in snapshot)
        {
            string sev = entry.Severity switch
            {
                LogSeverity.Success   => "SUCCESS",
                LogSeverity.Warning   => "WARN   ",
                LogSeverity.Error     => "ERROR  ",
                LogSeverity.Exception => "EXCEPT ",
                _                     => "INFO   ",
            };
            sb.AppendLine($"[{entry.Timestamp:HH:mm:ss.fff}] [Frame {entry.FrameNumber,6}] [{sev}] {entry.Message}");
            if (!string.IsNullOrEmpty(entry.StackTrace))
                sb.AppendLine(entry.StackTrace);
        }

        File.WriteAllText(filePath, sb.ToString());
    }

    /// <summary>
    /// Formats all entries as plain text, suitable for copying to the clipboard.
    /// </summary>
    public static string FormatAllAsText()
    {
        List<LogEntry> snapshot;
        lock (_lock)
            snapshot = new List<LogEntry>(_entries);

        var sb = new StringBuilder();
        foreach (var entry in snapshot)
        {
            string sev = entry.Severity switch
            {
                LogSeverity.Success   => "[SUCCESS]",
                LogSeverity.Warning   => "[WARN]",
                LogSeverity.Error     => "[ERROR]",
                LogSeverity.Exception => "[EXCEPTION]",
                _                     => "[INFO]",
            };
            sb.AppendLine($"[{entry.Timestamp:HH:mm:ss.fff}] {sev} {entry.Message}");
        }
        return sb.ToString();
    }

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
                }
                else
                {
                    AppendEntry(message, severity, stackTrace);
                }
            }
            else
            {
                AppendEntry(message, severity, stackTrace);
            }
        }

        // Fire outside the lock to avoid deadlocks from re-entrant logging
        if (severity is LogSeverity.Error or LogSeverity.Exception)
            EditorEvents.InvokeOnErrorLogged();
    }

    private static void AppendEntry(string message, LogSeverity severity, DebugStackTrace? stackTrace)
    {
        _entries.Add(new LogEntry
        {
            Message = message,
            Severity = severity,
            StackTrace = stackTrace?.ToString(),
            StackFrames = stackTrace,
            Timestamp = DateTime.Now,
            FrameNumber = Time.FrameCount,
        });

        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    internal static void ResetCounts()
    {
        InfoCount = 0;
        WarningCount = 0;
        ErrorCount = 0;
    }
}
