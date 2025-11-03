using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

class ConquerProxy
{
    private List<IConquerProtocolHandler> handlers;
    private Dictionary<string, int> serverPorts = new Dictionary<string, int>() {
        { "Login", 9958 }, { "Game", 5816 }, { "Logging", 5817 }
    };

    public ConquerProxy() {
        // Register handlers for versions
        handlers = new List<IConquerProtocolHandler> {
            new ConquerV5517Handler(),
            new ConquerV5695Handler(),
            // ...additional version handlers
        };
    }

    public void Start() {
        foreach(var kvp in serverPorts) {
            var serverType = kvp.Key;
            var port = kvp.Value;
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listenForConnections(listener, serverType);
        }
    }

    private void listenForConnections(TcpListener listener, string serverType) {
        // Async handling for each server type
        new Thread(() => {
            while (true) {
                var client = listener.AcceptTcpClient();
                var context = new ConnectionContext { TargetServerType = serverType, /*detect version later*/ };
                // Determine the right handler based on initial packets
                IConquerProtocolHandler handler = DetectHandler(client);
                Thread relayThread = new Thread(() => ProxyConnection(client, handler, context));
                relayThread.Start();
            }
        }).Start();
    }

    private IConquerProtocolHandler DetectHandler(TcpClient client) {
        // Inspect initial packet to detect version, return handler
        // Simplified for illustration
        return handlers.First();
    }

    private void ProxyConnection(TcpClient client, IConquerProtocolHandler handler, ConnectionContext context) {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        while(true) {
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if(bytesRead == 0) break;
            handler.HandlePacket(buffer.Take(bytesRead).ToArray(), out var modifiedPacket, context);
            // Forward to appropriate server, handle as needed
        }
    }
}
