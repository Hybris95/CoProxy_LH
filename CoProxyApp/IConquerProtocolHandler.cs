/*
 File: IConquerProtocolHandler.cs
 Responsibility:
   - Defines the protocol handler abstraction used by the proxy.
   - Enables version- or flavor-specific packet manipulation and server routing logic.
*/

/// <summary>
/// Contract for protocol handlers that can inspect/modify packets
/// and optionally determine intended server destinations.
/// </summary>
public interface IConquerProtocolHandler
{
    /// <summary>
    /// Intercepts a packet from client to server and can emit a modified packet.
    /// Precondition:
    ///   - data is not null.
    /// Postcondition:
    ///   - modifiedPacket contains the bytes to forward (could be same or different).
    ///   - If dropping the packet, set modifiedPacket to an empty array.
    /// </summary>
    /// <param name="data">Raw input bytes from client.</param>
    /// <param name="modifiedPacket">Output bytes to forward to the server.</param>
    /// <param name="context">Connection context (server type, version, etc.).</param>
    void HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context);

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
