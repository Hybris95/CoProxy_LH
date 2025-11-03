using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

class ConquerProxy
{
    static void Main(string[] args)
    {
        int localPort = 9958; // The port Conquer uses for login
        string serverIp = "YOUR_SERVER_IP";
        int serverPort = 9958;

        TcpListener listener = new TcpListener(IPAddress.Any, localPort);
        listener.Start();
        Console.WriteLine("Proxy started. Waiting for client connection...");

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            Console.WriteLine("Client connected!");

            TcpClient server = new TcpClient(serverIp, serverPort);

            Thread clientToServer = new Thread(() => RelayPackets(client.GetStream(), server.GetStream(), "Client->Server"));
            Thread serverToClient = new Thread(() => RelayPackets(server.GetStream(), client.GetStream(), "Server->Client"));

            clientToServer.Start();
            serverToClient.Start();
        }
    }

    static void RelayPackets(NetworkStream from, NetworkStream to, string direction)
    {
        try
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                int bytesRead = from.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                // Manipulate packet here if needed (for bots, logging, filtering, etc.)
                to.Write(buffer, 0, bytesRead);
            }
        }
        catch { }
    }
}
