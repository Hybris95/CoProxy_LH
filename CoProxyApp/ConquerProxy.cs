using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

public class ConquerProxy
{
    private List<IConquerProtocolHandler> handlers;
    private Dictionary<string, int> serverPorts;
    private IConquerProtocolHandler selectedHandler;

    // Constructor accepts ports and handler to use
    public ConquerProxy(Dictionary<string, int> ports, IConquerProtocolHandler handler)
    {
        handlers = new List<IConquerProtocolHandler> { handler };
        serverPorts = ports;
        selectedHandler = handler;
    }

    public void Start()
    {
        foreach(var kvp in serverPorts)
        {
            var serverType = kvp.Key;
            var port = kvp.Value;
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listenForConnections(listener, serverType);
        }
    }

    private void listenForConnections(TcpListener listener, string serverType)
    {
        new Thread(() => {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                var context = new ConnectionContext { TargetServerType = serverType };
                // Use the selected handler directly
                Thread relayThread = new Thread(() => ProxyConnection(client, selectedHandler, context));
                relayThread.Start();
            }
        }).Start();
    }

    private void ProxyConnection(TcpClient client, IConquerProtocolHandler handler, ConnectionContext context)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        while(true)
        {
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if(bytesRead == 0) break;
            handler.HandlePacket(buffer.Take(bytesRead).ToArray(), out var modifiedPacket, context);
            // Forward to appropriate server, handle as needed
        }
    }
}
