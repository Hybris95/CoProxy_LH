/*
 File: IConquerProtocolHandler.cs
 Responsibility:
   - Defines the protocol handler abstraction used by the proxy.
   - Enables version- or flavor-specific packet manipulation and server routing logic.
   - Provides packet inspection/annotation for visualization and reverse-engineering.
*/

using System;
using System.Collections.Generic;

/// <summary>
/// Direction of a packet relative to the proxy.
/// </summary>
public enum PacketDirection
{
    ClientToServer,
    ServerToClient
}

/// <summary>
/// A structured representation of a parsed/analyzed packet for visualization.
/// </summary>
public class PacketInfo
{
    /// <summary>Connection identifier.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>Server type label ("Login" or "Game").</summary>
    public string ServerType { get; set; } = "?";

    /// <summary>UTC timestamp of capture.</summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Direction: ClientToServer or ServerToClient.</summary>
    public PacketDirection Direction { get; set; }

    /// <summary>Total declared length from header (including header, excluding optional footer).</summary>
    public ushort DeclaredLength { get; set; }

    /// <summary>Packet type id from header.</summary>
    public ushort Type { get; set; }

    /// <summary>Human-friendly tag, e.g., "MsgWalk".</summary>
    public string Tag { get; set; } = "Unknown";

    /// <summary>Short description or summary for UI.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Parsed fields as key/value pairs.</summary>
    public Dictionary<string, object> Fields { get; set; } = new();

    /// <summary>Raw bytes of the full frame as seen by the handler after its processing decision (header + payload, no footer).</summary>
    public byte[] RawFrame { get; set; } = Array.Empty<byte>();

    /// <summary>Raw payload bytes (after header), possibly decrypted for analysis.</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Contract for protocol handlers that can inspect/modify packets
/// and optionally determine intended server destinations.
/// </summary>
public interface IConquerProtocolHandler
{
    /// <summary>
    /// Intercepts and optionally modifies a client->server packet. Also returns PacketInfo for visualization.
    /// Precondition:
    ///   - data is not null and contains at least the header if any data is present.
    /// Postcondition:
    ///   - modifiedPacket contains the bytes to forward (could be same or different).
    ///   - To drop the packet, return an empty array.
    ///   - info contains a best-effort parse and tag for UI visualization.
    /// </summary>
    byte[] ProcessClientToServer(byte[] data, ConnectionContext context, out PacketInfo info);

    /// <summary>
    /// Intercepts and optionally modifies a server->client packet. Also returns PacketInfo for visualization.
    /// </summary>
    byte[] ProcessServerToClient(byte[] data, ConnectionContext context, out PacketInfo info);

    /// <summary>
    /// Indicates whether a given packet targets the Login server.
    /// Not used by the current proxy implementation (ports are pre-bound).
    /// </summary>
    bool IsPacketForLoginServer(byte[] data);

    /// <summary>
    /// Indicates whether a given packet targets the Game server.
    /// Not used by the current proxy implementation (ports are pre-bound).
    /// </summary>
    bool IsPacketForGameServer(byte[] data);

    // Any version-specific logic can be modeled through additional methods or via context fields.
}
