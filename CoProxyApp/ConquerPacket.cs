/*
 File: ConquerPacket.cs
 Responsibility:
   - Shared packet model used by parser and GUI to exchange structured information about a frame.
   - Encapsulates Conquer Online header (Length, Type), payload, optional footer, and contextual metadata.
   - Provides parsing helpers (including footer detection) and hex-dump utilities for visualization.

 Structure:
   - Little Endian byte order.
   - Header: [0..1] Length (ushort), [2..3] Type (ushort).
   - Payload: [4..Length).
   - Optional footer beyond 'Length' for patches > 5017: ASCII "TQServer" or "TQClient".
     Footer bytes are not counted in 'Length'.

 Notes:
   - This class is a neutral data container and does not perform encryption/decryption.
   - Handlers can attach tags, descriptions, and parsed fields for the GUI.
*/

using System;
using System.Collections.Generic;
using System.Text;

public sealed class ConquerPacket
{
    // Known optional footers (not included in header length)
    private static readonly byte[] FooterTQClient = Encoding.ASCII.GetBytes("TQClient");
    private static readonly byte[] FooterTQServer = Encoding.ASCII.GetBytes("TQServer");

    // ---- Contextual metadata (for GUI and correlation) ----

    /// <summary>Unique connection identifier this packet belongs to.</summary>
    public Guid ConnectionId { get; init; }

    /// <summary>Target server type label, e.g., "Login" or "Game".</summary>
    public string ServerType { get; init; } = "?";

    /// <summary>UTC timestamp when the proxy captured/created this packet model.</summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Packet flow direction relative to the proxy.</summary>
    public PacketDirection Direction { get; init; }

    // ---- Core header/payload/footer ----

    /// <summary>Declared length from header (header + payload, excluding optional footer).</summary>
    public ushort DeclaredLength { get; init; }

    /// <summary>Type id from header.</summary>
    public ushort Type { get; init; }

    /// <summary>4-byte header (Length, Type) as read from the frame.</summary>
    public byte[] Header { get; init; } = Array.Empty<byte>();

    /// <summary>Payload bytes after the 4-byte header (as captured; not decrypted by this class).</summary>
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    /// <summary>Total number of bytes present after DeclaredLength (optional footer size).</summary>
    public int FooterLength { get; init; }

    /// <summary>Footer textual marker if recognized, e.g., "TQClient" or "TQServer".</summary>
    public string? FooterText { get; init; }

    /// <summary>Raw frame = header + payload only (length = DeclaredLength).</summary>
    public byte[] RawFrame { get; init; } = Array.Empty<byte>();

    /// <summary>Raw frame including any bytes past DeclaredLength (e.g., footer if present).</summary>
    public byte[] RawWithFooter { get; init; } = Array.Empty<byte>();

    // ---- Analysis/visualization ----

    /// <summary>Human-friendly tag (e.g., "MsgWalk"). Handlers can set this.</summary>
    public string Tag { get; set; } = "Unknown";

    /// <summary>Short description for UI. Handlers can set this.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Parsed fields and values for visualization. Handlers can populate this.</summary>
    public Dictionary<string, object> Fields { get; } = new();

    // ---- Validation ----

    /// <summary>Indicates whether the frame structure is valid (header consistent with actual data).</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>Error message when IsValid is false.</summary>
    public string? Error { get; init; }

    // ---- Construction helpers ----

    /// <summary>
    /// Parses a raw frame into a ConquerPacket model. Throws on invalid header or truncated data.
    /// Footer bytes (if present) are detected but excluded from RawFrame and DeclaredLength.
    /// </summary>
    /// <param name="raw">Raw bytes as captured on the wire.</param>
    /// <param name="connectionId">Connection id for correlation.</param>
    /// <param name="serverType">Server type label ("Login"/"Game").</param>
    /// <param name="direction">Packet direction.</param>
    public static ConquerPacket Parse(ReadOnlySpan<byte> raw, Guid connectionId, string serverType, PacketDirection direction)
    {
        if (raw.Length < 4)
            throw new ArgumentException("Insufficient data for header (need at least 4 bytes).", nameof(raw));

        ushort length = BitConverter.ToUInt16(raw.Slice(0, 2));
        ushort type = BitConverter.ToUInt16(raw.Slice(2, 2));

        if (length < 4)
            throw new ArgumentException("Declared length must be >= 4.", nameof(raw));

        if (length > raw.Length)
            throw new ArgumentException("Declared length exceeds available data (truncated frame).", nameof(raw));

        int footerLen = DetectFooterSize(raw, length);
        string? footerText = null;
        if (footerLen == FooterTQClient.Length && EndsWith(raw, FooterTQClient))
            footerText = "TQClient";
        else if (footerLen == FooterTQServer.Length && EndsWith(raw, FooterTQServer))
            footerText = "TQServer";

        var header = raw.Slice(0, 4).ToArray();
        var payload = raw.Slice(4, length - 4).ToArray();
        var rawFrame = raw.Slice(0, length).ToArray();
        var rawWithFooter = raw.ToArray();

        return new ConquerPacket
        {
            ConnectionId = connectionId,
            ServerType = serverType ?? "?",
            Direction = direction,
            TimestampUtc = DateTime.UtcNow,
            DeclaredLength = length,
            Type = type,
            Header = header,
            Payload = payload,
            FooterLength = footerLen,
            FooterText = footerText,
            RawFrame = rawFrame,
            RawWithFooter = rawWithFooter,
            IsValid = true,
            Error = null
        };
    }

    /// <summary>
    /// Tries to parse a raw frame into a ConquerPacket without throwing.
    /// Returns false and sets error on failure.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> raw, Guid connectionId, string serverType, PacketDirection direction, out ConquerPacket packet)
    {
        try
        {
            packet = Parse(raw, connectionId, serverType, direction);
            return true;
        }
        catch (Exception ex)
        {
            packet = new ConquerPacket
            {
                ConnectionId = connectionId,
                ServerType = serverType ?? "?",
                Direction = direction,
                TimestampUtc = DateTime.UtcNow,
                DeclaredLength = 0,
                Type = 0,
                Header = Array.Empty<byte>(),
                Payload = Array.Empty<byte>(),
                FooterLength = 0,
                FooterText = null,
                RawFrame = raw.ToArray(),
                RawWithFooter = raw.ToArray(),
                IsValid = false,
                Error = ex.Message
            };
            return false;
        }
    }

    // ---- Utilities ----

    /// <summary>
    /// Builds a hex dump string for display. By default dumps RawFrame (header + payload only).
    /// Set includeFooter to true to dump RawWithFooter.
    /// </summary>
    public string ToHexDump(bool includeFooter = false, int bytesPerLine = 16)
    {
        var data = includeFooter ? RawWithFooter : RawFrame;
        if (data == null || data.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += bytesPerLine)
        {
            int count = Math.Min(bytesPerLine, data.Length - i);
            sb.Append(i.ToString("X4"));
            sb.Append("  ");

            for (int j = 0; j < bytesPerLine; j++)
            {
                if (j < count) sb.Append(data[i + j].ToString("X2"));
                else sb.Append("  ");
                sb.Append(' ');
                if (j == 7) sb.Append(' ');
            }

            sb.Append(" | ");
            for (int j = 0; j < count; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public override string ToString()
    {
        string dir = Direction.ToString();
        string tag = string.IsNullOrEmpty(Tag) ? "Unknown" : Tag;
        return $"{TimestampUtc:o} {dir} {ServerType} Type=0x{Type:X4} Len={DeclaredLength} Tag={tag} {Description}";
    }

    // ---- Internal helpers ----

    private static int DetectFooterSize(ReadOnlySpan<byte> raw, int declaredLength)
    {
        int tail = raw.Length - declaredLength;
        if (tail <= 0) return 0;

        // Check if known footers are at the very end of the raw buffer
        if (tail >= FooterTQClient.Length && EndsWith(raw, FooterTQClient))
            return FooterTQClient.Length;

        if (tail >= FooterTQServer.Length && EndsWith(raw, FooterTQServer))
            return FooterTQServer.Length;

        return 0;
    }

    private static bool EndsWith(ReadOnlySpan<byte> span, byte[] pattern)
    {
        if (span.Length < pattern.Length) return false;
        var tail = span.Slice(span.Length - pattern.Length, pattern.Length);
        return tail.SequenceEqual(pattern);
    }
}
