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

    // Store listeners and threads so we can stop/cleanup
    private List<TcpListener> listeners = new();
    private volatile bool isRunning = false;

    // Store listener threads to optionally join later
    private List<Thread> listenerThreads = new();

    // Constructor accepts ports and handler to use
    public ConquerProxy(Dictionary<string, int> ports, IConquerProtocolHandler handler)
    {
        handlers = new List<IConquerProtocolHandler> { handler };
        serverPorts = ports;
        selectedHandler = handler;
    }

    public void Start()
    {
        isRunning = true;

        foreach (var kvp in serverPorts)
        {
            var serverType = kvp.Key;
            var port = kvp.Value;
            var listener = new TcpListener(IPAddress.Any, port);
            listeners.Add(listener);
            listener.Start();
            var thread = new Thread(() => listenForConnections(listener, serverType));
            listenerThreads.Add(thread);
            thread.Start();
        }
    }

    public void Stop()
    {
        // Signal stop
        isRunning = false;

        // Stop all listeners to unblock AcceptTcpClient
        foreach (var listener in listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // Log or ignore exceptions when stopping
            }
        }

        // Optionally wait for listener threads to finish
        foreach (var thread in listenerThreads)
        {
            if (thread.IsAlive)
            {
                thread.Join(1000); // wait 1 second max
            }
        }

        listeners.Clear();
        listenerThreads.Clear();
    }

    private void listenForConnections(TcpListener listener, string serverType)
    {
        try
        {
            while (isRunning)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    // Listener stopped or socket closed: exit loop
                    break;
                }

                var context = new ConnectionContext { TargetServerType = serverType };
                Thread relayThread = new Thread(() => ProxyConnection(client, selectedHandler, context));
                relayThread.Start();
            }
        }
        catch (Exception)
        {
            // Log unexpected errors or ignore
        }
    }

    private void ProxyConnection(TcpClient client, IConquerProtocolHandler handler, ConnectionContext context)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = stream.Read(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                break; // Connection error, terminate thread
            }
            if (bytesRead == 0) break;

            handler.HandlePacket(buffer.Take(bytesRead).ToArray(), out var modifiedPacket, context);
            // Forward to appropriate server, handle as needed
        }

        client.Close();
    }
}
