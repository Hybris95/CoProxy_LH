/*
 File: ConquerClassicLordsHandler.cs
 Responsibility:
   - Provides a sample IConquerProtocolHandler implementation.
   - Currently behaves as a pure pass-through (no packet modification).
   - Packet routing helpers default to false (not used in this proxy variant).
*/

/// <summary>
/// A minimal, pass-through handler for Conquer Classic "Lords" flavor.
/// - Does not modify packets.
/// - Does not decide routing (both IsPacketFor* return false).
/// Use this as a template to implement actual parsing and rewriting.
/// </summary>
public class ConquerClassicLordsHandler : IConquerProtocolHandler
{
    /// <summary>
    /// Handles an outbound packet from client to server.
    /// Precondition:
    ///   - data is a non-null array containing the raw packet.
    /// Postcondition:
    ///   - modifiedPacket contains the packet to forward (unmodified in this handler).
    /// Thread-safety:
    ///   - Stateless; safe to call concurrently for different connections.
    /// </summary>
    /// <param name="data">Raw packet bytes from client.</param>
    /// <param name="modifiedPacket">Output bytes to forward to server.</param>
    /// <param name="context">Connection context for routing/version decisions.</param>
    public void HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context)
    {
        modifiedPacket = data;
    }

    /// <summary>
    /// Indicates whether a packet targets the Login server.
    /// Always returns false in this basic implementation.
    /// </summary>
    public bool IsPacketForLoginServer(byte[] data) => false;

    /// <summary>
    /// Indicates whether a packet targets the Game server.
    /// Always returns false in this basic implementation.
    /// </summary>
    public bool IsPacketForGameServer(byte[] data) => false;
}
