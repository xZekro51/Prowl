// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Prowl.Runtime.Profiling;

/// <summary>
/// Binary wire protocol for streaming <see cref="ProfilerFrame"/> data
/// between a player (server) and the editor (client) over TCP.
/// <para>
/// Wire format (little-endian):
/// <code>
/// [4 bytes] magic    = 0x50524F46 ("PROF")
/// [4 bytes] payload length (excluding this 8-byte header)
/// [8 bytes] TotalMs  (double)
/// [4 bytes] sample count
/// per sample:
///   [4+N bytes] Name        (length-prefixed UTF-8)
///   [4+N bytes] Category    (length-prefixed UTF-8)
///   [4+N bytes] Description (length-prefixed UTF-8)
///   [4 bytes]   Depth       (int32)
///   [8 bytes]   StartMs     (double)
///   [8 bytes]   DurationMs  (double)
/// </code>
/// </para>
/// </summary>
public static class ProfilerProtocol
{
    /// <summary> Default TCP port used by the profiler server. </summary>
    public const int DefaultPort = 34120;

    /// <summary> Magic header identifying a profiler frame packet. </summary>
    public const uint Magic = 0x50524F46; // "PROF"

    /// <summary>
    /// Serializes a <see cref="ProfilerFrame"/> into the wire format.
    /// </summary>
    public static byte[] Serialize(ProfilerFrame frame)
    {
        using var ms = new MemoryStream(1024);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Placeholder for header (magic + payload length)
        bw.Write(Magic);
        bw.Write(0); // payload length placeholder

        long payloadStart = ms.Position;

        bw.Write(frame.TotalMs);
        bw.Write(frame.Samples.Length);

        foreach (var s in frame.Samples)
        {
            WriteString(bw, s.Name);
            WriteString(bw, s.Category);
            WriteString(bw, s.Description);
            bw.Write(s.Depth);
            bw.Write(s.StartMs);
            bw.Write(s.DurationMs);
        }

        long payloadLength = ms.Position - payloadStart;

        // Go back and write the actual payload length
        ms.Position = 4; // after magic
        bw.Write((int)payloadLength);

        return ms.ToArray();
    }

    /// <summary>
    /// Attempts to deserialize a <see cref="ProfilerFrame"/> from the given
    /// stream. Returns <c>null</c> if the stream is closed or the data is
    /// malformed.
    /// </summary>
    public static ProfilerFrame? Deserialize(Stream stream)
    {
        // Read header
        byte[] header = new byte[8];
        if (!ReadExact(stream, header, 0, 8))
            return null;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != Magic)
            return null;

        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (payloadLen < 0 || payloadLen > 16 * 1024 * 1024) // 16 MB sanity cap
            return null;

        byte[] payload = new byte[payloadLen];
        if (!ReadExact(stream, payload, 0, payloadLen))
            return null;

        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms, Encoding.UTF8);

        double totalMs = br.ReadDouble();
        int count = br.ReadInt32();
        if (count < 0 || count > 100_000)
            return null;

        var samples = new ProfilerSample[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = new ProfilerSample
            {
                Name = ReadString(br),
                Category = ReadString(br),
                Description = ReadString(br),
                Depth = br.ReadInt32(),
                StartMs = br.ReadDouble(),
                DurationMs = br.ReadDouble(),
            };
        }

        return new ProfilerFrame
        {
            Samples = samples,
            TotalMs = totalMs,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void WriteString(BinaryWriter bw, string? value)
    {
        value ??= string.Empty;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadString(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len <= 0) return string.Empty;
        if (len > 1024 * 1024) return string.Empty; // sanity
        byte[] bytes = br.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) return false; // stream closed
            totalRead += read;
        }
        return true;
    }
}
