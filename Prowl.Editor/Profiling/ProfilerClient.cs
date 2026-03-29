// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

using Prowl.Runtime;
using Prowl.Runtime.Profiling;

namespace Prowl.Editor.Profiling;

/// <summary>
/// Editor-side TCP client that connects to a <see cref="ProfilerServer"/>
/// running inside a debug player build. Received <see cref="ProfilerFrame"/>
/// objects are stored in a local ring buffer that the <c>ProfilerPanel</c>
/// can query via <see cref="GetFrame"/> / <see cref="FrameCount"/>.
/// </summary>
public sealed class ProfilerClient : IDisposable
{
    private TcpClient? _tcp;
    private Thread? _receiveThread;
    private volatile bool _running;

    // Ring buffer mirroring the one in Profiler but fed by remote data.
    private const int MaxHistoryFrames = Profiler.MaxHistoryFrames;
    private readonly ProfilerFrame?[] _history = new ProfilerFrame?[MaxHistoryFrames];
    private int _head;
    private int _count;
    private readonly object _lock = new();

    /// <summary> Whether the client is currently connected to a remote player. </summary>
    public bool IsConnected => _tcp?.Connected == true;

    /// <summary> Number of completed frames available in the remote history. </summary>
    public int FrameCount { get { lock (_lock) return _count; } }

    /// <summary> The address we connected (or last attempted) to. </summary>
    public string? Address { get; private set; }

    /// <summary> The port we connected (or last attempted) to. </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Attempts to connect to a profiler server at the given address.
    /// Returns <c>true</c> if the connection succeeded.
    /// </summary>
    public bool Connect(string address = "127.0.0.1", int port = ProfilerProtocol.DefaultPort)
    {
        Disconnect();

        Address = address;
        Port = port;

        try
        {
            _tcp = new TcpClient();
            _tcp.Connect(address, port);
            _tcp.NoDelay = true;
            _running = true;

            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "ProfilerClient.Receive" };
            _receiveThread.Start();

            Debug.Log($"[ProfilerClient] Connected to {address}:{port}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ProfilerClient] Failed to connect to {address}:{port}: {ex.Message}");
            _tcp?.Dispose();
            _tcp = null;
            _running = false;
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the remote player.
    /// </summary>
    public void Disconnect()
    {
        _running = false;
        try { _tcp?.Close(); } catch { }
        _receiveThread?.Join(2000);
        _tcp?.Dispose();
        _tcp = null;
        _receiveThread = null;
    }

    /// <summary>
    /// Returns the profiler frame at the given age (0 = most recent).
    /// Returns null if the index is out of range.
    /// </summary>
    public ProfilerFrame? GetFrame(int age)
    {
        lock (_lock)
        {
            if (age < 0 || age >= _count) return null;
            int idx = (_head - 1 - age + MaxHistoryFrames * 2) % MaxHistoryFrames;
            return _history[idx];
        }
    }

    /// <summary> Clears all received history. </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _head = 0;
            Array.Clear(_history, 0, _history.Length);
        }
    }

    public void Dispose()
    {
        Disconnect();
    }

    // ── Background receive loop ──────────────────────────────────────

    private void ReceiveLoop()
    {
        try
        {
            var stream = _tcp?.GetStream();
            if (stream == null) return;

            while (_running)
            {
                var frame = ProfilerProtocol.Deserialize(stream);
                if (frame == null)
                    break; // stream closed or bad data

                lock (_lock)
                {
                    _history[_head] = frame;
                    _head = (_head + 1) % MaxHistoryFrames;
                    if (_count < MaxHistoryFrames)
                        _count++;
                }
            }
        }
        catch (Exception) when (!_running)
        {
            // Expected on disconnect
        }
        catch (Exception ex)
        {
            if (_running)
                Debug.LogWarning($"[ProfilerClient] Connection lost: {ex.Message}");
        }

        _running = false;
    }
}
