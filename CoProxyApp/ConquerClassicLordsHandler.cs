public class ConquerClassicLordsHandler : IConquerProtocolHandler
{
    public void HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context)
    {
        modifiedPacket = data;
    }
    public bool IsPacketForLoginServer(byte[] data) => false;
    public bool IsPacketForGameServer(byte[] data) => false;
}
