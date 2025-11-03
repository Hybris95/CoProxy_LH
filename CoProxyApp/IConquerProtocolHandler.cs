public interface IConquerProtocolHandler
{
    void HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context);
    bool IsPacketForLoginServer(byte[] data);
    bool IsPacketForGameServer(byte[] data);
    // Any version-specific logic goes here
}
