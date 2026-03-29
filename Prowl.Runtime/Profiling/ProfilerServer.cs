// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Prowl.Runtime.Profiling;

/// <summary>
/// TCP server that runs inside a debug player build and streams
/// <see cref="ProfilerFrame"/> data to a connected editor (the
/// <c>ProfilerClient</c>). Only one client connection is accepted at a time.
/// <para>
/// Usage (auto-generated in <c>Program.cs</c> for debug builds):
/// <code>
/// ProfilerServer.Start();          // before Game.Run()
/// // ... game runs ...
/// ProfilerServer.Stop();           // on exit
/// </code>
/// </para>
/// <para>
/// The server hooks into <see cref="Profiler.OnFrameCompleted"/> to receive
/// frames and queues them for the sending thread. If the queue exceeds a
/// threshold, oldest frames are dropped to prevent memory growth.
/// </para>
/// </summary>
public static class ProfilerServer
{
    private static TcpListener? s_listener;
    private static Thread? s_acceptThread;
    private static Thread? s_sendThread;
    private static volatile bool s_running;
    private static TcpClient? s_client;

    // Bounded queue so the game thread never blocks.
    private static readonly ConcurrentQueue<ProfilerFrame> s_queue = new();
    private static readonly AutoResetEvent s_signal = new(false);

    private const int MaxQueuedFrames = 120;

    /// <summary> Whether the profiler server is actively listening. </summary>
    public static bool IsRunning => s_running;

    /// <summary> Whether an editor client is currently connected. </summary>
    public static bool IsClientConnected => s_client?.Connected == true;

    /// <summary>
    /// Starts listening for editor connections on <see cref="ProfilerProtocol.DefaultPort"/>.
    /// Also enables the <see cref="Profiler"/> and hooks into frame completion.
    /// </summary>
    public static void Start(int port = ProfilerProtocol.DefaultPort)
    {
        if (s_running) return;
        s_running = true;

        Profiler.Enabled = true;
        Profiler.OnFrameCompleted += OnFrameCompleted;

        try
        {
            s_listener = new TcpListener(IPAddress.Any, port);
            s_listener.Start();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ProfilerServer] Failed to start on port {port}: {ex.Message}");
            s_running = false;
            return;
        }

        s_acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ProfilerServer.Accept" };
        s_acceptThread.Start();

        s_sendThread = new Thread(SendLoop) { IsBackground = true, Name = "ProfilerServer.Send" };
        s_sendThread.Start();

        Debug.Log($"[ProfilerServer] Listening on port {port}");
    }

    /// <summary>
    /// Stops the server, disconnects any client, and unhooks from the profiler.
    /// </summary>
    public static void Stop()
    {
        if (!s_running) return;
        s_running = false;

        Profiler.OnFrameCompleted -= OnFrameCompleted;

        s_signal.Set(); // wake the send thread
        try { s_listener?.Stop(); } catch { }
        try { s_client?.Close(); } catch { }

        s_acceptThread?.Join(2000);
        s_sendThread?.Join(2000);

        s_listener = null;
        s_client = null;

        // Drain the queue
        while (s_queue.TryDequeue(out _)) { }
    }

    // ── Profiler callback ────────────────────────────────────────────

    private static void OnFrameCompleted(ProfilerFrame frame)
    {
        // Only queue if a client is actually connected.
        if (s_client is not { Connected: true })
            return;

        s_queue.Enqueue(frame);

        // Drop oldest frames if the queue grows too large.
        while (s_queue.Count > MaxQueuedFrames)
            s_queue.TryDequeue(out _);

        s_signal.Set();
    }

    // ── Background threads ───────────────────────────────────────────

    private static void AcceptLoop()
    {
        while (s_running)
        {
            try
            {
                var listener = s_listener;
                if (listener == null) break;

                var client = listener.AcceptTcpClient();
                client.NoDelay = true;

                // Only one client at a time — replace any existing.
                try { s_client?.Close(); } catch { }
                s_client = client;

                Debug.Log("[ProfilerServer] Editor connected.");
            }
            catch (SocketException) when (!s_running)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (s_running)
                    Debug.LogWarning($"[ProfilerServer] Accept error: {ex.Message}");
            }
        }
    }

    private static void SendLoop()
    {
        while (s_running)
        {
            s_signal.WaitOne(100); // wake on signal or periodic check

            while (s_queue.TryDequeue(out var frame))
            {
                var client = s_client;
                if (client is not { Connected: true })
                {
                    // No client — drain the queue silently
                    while (s_queue.TryDequeue(out _)) { }
                    break;
                }

                try
                {
                    byte[] data = ProfilerProtocol.Serialize(frame);
                    client.GetStream().Write(data, 0, data.Length);
                }
                catch (Exception)
                {
                    // Client disconnected — close and drain
                    try { client.Close(); } catch { }
                    s_client = null;
                    while (s_queue.TryDequeue(out _)) { }
                    Debug.Log("[ProfilerServer] Editor disconnected.");
                    break;
                }
            }
        }
    }
}
