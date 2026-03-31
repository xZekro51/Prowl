// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using Prowl.Runtime.EventSystem;

namespace Prowl.Runtime;

/// <summary>
/// A file-based log sink that subscribes to <see cref="Debug.DebugEventManager"/> and writes
/// every message to a <c>Player.log</c> file beside the executable.
/// Designed for standalone player builds — call <see cref="Initialize"/> at startup
/// (before <see cref="Game.Run"/>) and <see cref="Shutdown"/> at exit.
/// </summary>
public static class PlayerFileLogger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    private static bool _initialized;
    private static IDisposable? _logSubscription;

    /// <summary>
    /// Initializes the file logger, creating or overwriting the log file at
    /// <paramref name="logPath"/>. If null, defaults to <c>Player.log</c> in
    /// <see cref="AppDomain.CurrentDomain.BaseDirectory"/>.
    /// </summary>
    public static void Initialize(string? logPath = null)
    {
        if (_initialized) return;

        logPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Player.log");

        try
        {
            // StreamWriter creates the file if it doesn't exist — no need for File.Create.
            _writer = new StreamWriter(logPath, append: false)
            {
                AutoFlush = true,
            };

            _writer.WriteLine($"Prowl Engine — Player Log ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            _writer.WriteLine(new string('=', 72));
            _writer.WriteLine();

            _logSubscription = DebugEvents.SubscribeOnLog(
                args => OnLogReceived(args.Message, args.StackTrace, args.Severity));
            _initialized = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PlayerFileLogger] Failed to initialize: {ex.Message}");
        }
    }

    /// <summary>
    /// Flushes and closes the log file and unsubscribes from <see cref="Debug.DebugEventManager"/>.
    /// </summary>
    public static void Shutdown()
    {
        if (!_initialized) return;

        _logSubscription?.Dispose();
        _logSubscription = null;
        _initialized = false;

        lock (_lock)
        {
            _writer?.WriteLine();
            _writer?.WriteLine($"--- Log closed at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void OnLogReceived(string message, DebugStackTrace? stackTrace, LogSeverity severity)
    {
        lock (_lock)
        {
            if (_writer == null) return;

            string sev = severity switch
            {
                LogSeverity.Success   => "SUCCESS",
                LogSeverity.Normal    => "INFO",
                LogSeverity.Warning   => "WARN",
                LogSeverity.Error     => "ERROR",
                LogSeverity.Exception => "EXCEPTION",
                _                     => "LOG",
            };

            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{sev}] {message}");

            if (stackTrace != null && severity >= LogSeverity.Warning)
            {
                _writer.Write(stackTrace.ToString());
            }
        }
    }
}
