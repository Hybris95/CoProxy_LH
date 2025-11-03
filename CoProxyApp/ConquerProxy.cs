using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

public class ConquerProxy
{
    private List<IConquerProtocolHandler> handlers;
    private Dictionary<string, int> serverPorts = new Dictionary<string, int>() {
        { "Login", 9958 }, { "Game", 5816 }, { "Logging", 5817 }
    };

    // Constructeur : initialisation des handlers
    public ConquerProxy()
    {
        handlers = new List<IConquerProtocolHandler> {
            new ConquerClassicLordsHandler()
            // ...ajoute ici d'autres handlers si besoin
        };
    }

    // Point d'entrée du programme
    public static void Main(string[] args)
    {
        Console.WriteLine("Proxy lancé !");
        var proxy = new ConquerProxy();
        proxy.Start();
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
                IConquerProtocolHandler handler = DetectHandler(client);
                Thread relayThread = new Thread(() => ProxyConnection(client, handler, context));
                relayThread.Start();
            }
        }).Start();
    }

    private IConquerProtocolHandler DetectHandler(TcpClient client)
    {
        // Logique de détection simplifiée
        return handlers.First();
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
