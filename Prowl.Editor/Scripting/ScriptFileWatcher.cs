// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Prowl.Runtime;

namespace Prowl.Editor.Scripting;

/// <summary>
/// Watches script directories for .cs file changes and notifies the hotload system.
/// Uses <see cref="FileSystemWatcher"/> with debouncing and batch coalescing.
/// </summary>
public sealed class ScriptFileWatcher : IDisposable
{
    /// <summary>
    /// Represents a batch of file changes detected since the last drain.
    /// </summary>
    public sealed class ChangeSet
    {
        public List<string> Created { get; } = [];
        public List<string> Modified { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<(string OldPath, string NewPath)> Renamed { get; } = [];

        public bool IsEmpty => Created.Count == 0 && Modified.Count == 0 &&
                               Deleted.Count == 0 && Renamed.Count == 0;

        public int TotalChanges => Created.Count + Modified.Count + Deleted.Count + Renamed.Count;

        /// <summary>All paths that were changed (created, modified, renamed-to).</summary>
        public IEnumerable<string> AllChangedPaths =>
            Created.Concat(Modified).Concat(Renamed.Select(r => r.NewPath));
    }

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
    private readonly Dictionary<string, ScriptFileEventType> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _renameOldPaths = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastEventTime = DateTime.MinValue;
    private bool _disposed;

    /// <summary>Debounce delay in milliseconds before changes are reported.</summary>
    public double DebounceMs { get; set; } = 300;

    private enum ScriptFileEventType { Created, Modified, Deleted, Renamed }

    /// <summary>
    /// Start watching the given directories for .cs file changes.
    /// Typically called with the project's Assets/ path.
    /// </summary>
    public void Start(params string[] directories)
    {
        Stop();

        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir)) continue;

            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                             | NotifyFilters.CreationTime | NotifyFilters.Size,
                Filter = "*.cs",
                InternalBufferSize = 32 * 1024,
            };

            watcher.Created += OnFileCreated;
            watcher.Changed += OnFileModified;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watchers.Add(watcher);
            HotloadLogger.LogDetail($"Watching for script changes: {dir}");
        }
    }

    /// <summary>Stop all file watchers.</summary>
    public void Stop()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();

        lock (_lock)
        {
            _pendingEvents.Clear();
            _renameOldPaths.Clear();
        }
    }

    /// <summary>
    /// Drain pending changes (called on main thread each frame).
    /// Returns null if no changes are ready (still debouncing or no events).
    /// </summary>
    public ChangeSet? DrainChanges()
    {
        lock (_lock)
        {
            if (_pendingEvents.Count == 0) return null;
            if ((DateTime.UtcNow - _lastEventTime).TotalMilliseconds < DebounceMs) return null;

            var result = new ChangeSet();

            foreach (var (path, evtType) in _pendingEvents)
            {
                switch (evtType)
                {
                    case ScriptFileEventType.Created:
                        result.Created.Add(path);
                        break;
                    case ScriptFileEventType.Modified:
                        result.Modified.Add(path);
                        break;
                    case ScriptFileEventType.Deleted:
                        result.Deleted.Add(path);
                        break;
                    case ScriptFileEventType.Renamed:
                        var oldPath = _renameOldPaths.GetValueOrDefault(path, path);
                        result.Renamed.Add((oldPath, path));
                        break;
                }
            }

            _pendingEvents.Clear();
            _renameOldPaths.Clear();

            return result.IsEmpty ? null : result;
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!IsScriptFile(e.FullPath)) return;
        QueueEvent(e.FullPath, ScriptFileEventType.Created);
    }

    private void OnFileModified(object sender, FileSystemEventArgs e)
    {
        if (!IsScriptFile(e.FullPath)) return;
        QueueEvent(e.FullPath, ScriptFileEventType.Modified);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsScriptFile(e.FullPath)) return;
        QueueEvent(e.FullPath, ScriptFileEventType.Deleted);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsScriptFile(e.FullPath) && !IsScriptFile(e.OldFullPath)) return;

        lock (_lock)
        {
            // Remove any pending event for the old path
            _pendingEvents.Remove(e.OldFullPath);
            _renameOldPaths.Remove(e.OldFullPath);

            if (IsScriptFile(e.FullPath))
            {
                _pendingEvents[e.FullPath] = ScriptFileEventType.Renamed;
                _renameOldPaths[e.FullPath] = e.OldFullPath;
            }
            else
            {
                // Renamed away from .cs — treat as deletion of old
                _pendingEvents[e.OldFullPath] = ScriptFileEventType.Deleted;
            }

            _lastEventTime = DateTime.UtcNow;
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        HotloadLogger.LogWarning($"ScriptFileWatcher buffer overflow: {e.GetException().Message}");
    }

    private void QueueEvent(string path, ScriptFileEventType type)
    {
        lock (_lock)
        {
            if (_pendingEvents.TryGetValue(path, out var existing))
            {
                // Coalesce: Created + Deleted = cancel; others = latest wins
                if (existing == ScriptFileEventType.Created && type == ScriptFileEventType.Deleted)
                {
                    _pendingEvents.Remove(path);
                    _lastEventTime = DateTime.UtcNow;
                    return;
                }

                if (existing == ScriptFileEventType.Created && type == ScriptFileEventType.Modified)
                {
                    _lastEventTime = DateTime.UtcNow;
                    return; // Keep as Created
                }
            }

            _pendingEvents[path] = type;
            _lastEventTime = DateTime.UtcNow;
        }
    }

    private static bool IsScriptFile(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
