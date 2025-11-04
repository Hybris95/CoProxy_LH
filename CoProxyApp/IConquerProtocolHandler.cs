/*
 File: IConquerProtocolHandler.cs
 Responsibility:
   - Defines the protocol handler abstraction used by the proxy.
   - Enables version- or flavor-specific packet manipulation and server routing logic.
   - Produces ConquerPacket models for visualization and analysis.
*/

using System;

/// <summary>
/// Direction of a packet relative to the proxy.
/// </summary>
public enum PacketDirection
{
    ClientToServer,
    ServerToClient
}

/// <summary>
/// Contract for protocol handlers that can inspect/modify packets
/// and optionally determine intended server destinations. Produces
/// a ConquerPacket model for GUI visualization.
/// </summary>
public interface IConquerProtocolHandler
{
    /// <summary>
    /// Intercepts and optionally modifies a client->server packet.
    /// Returns bytes to forward and a ConquerPacket model for visualization.
    /// To drop the packet, return an empty array.
    /// </summary>
    /// <param name="data">Raw input bytes from client (as read from socket).</param>
    /// <param name="context">Connection context (server type, version, etc.).</param>
    /// <param name="packet">Output parsed/annotated packet for GUI.</param>
    /// <returns>Bytes to forward to the remote server.</returns>
    byte[] ProcessClientToServer(byte[] data, ConnectionContext context, out ConquerPacket packet);

    /// <summary>
    /// Intercepts and optionally modifies a server->client packet.
    /// Returns bytes to forward and a ConquerPacket model for visualization.
    /// </summary>
    /// <param name="data">Raw input bytes from server.</param>
    /// <param name="context">Connection context (server type, version, etc.).</param>
    /// <param name="packet">Output parsed/annotated packet for GUI.</param>
    /// <returns>Bytes to forward to the client.</returns>
    byte[] ProcessServerToClient(byte[] data, ConnectionContext context, out ConquerPacket packet);

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
