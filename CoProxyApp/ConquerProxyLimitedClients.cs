using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

/*
 File: ConquerProxyLimitedClients.cs
 Responsibility:
   - Multi-port TCP proxy for "Login" and "Game" server types.
   - Enforces 1 concurrent client per server type with an exclusivity rule:
       * When a Game client is connected, Login connections are blocked.
   - Relays traffic between a local client and a remote server (by IP + per-type port).
   - Emits events for UI/monitoring when local/remote sides connect or disconnect.
   - Emits per-packet analysis events for visualization (PacketInfo).
   - Manages lifecycle (Start/Stop), listeners, and relay threads.

 Threading Model:
   - One TcpListener per server type runs on a dedicated listener thread.
   - Each accepted client spins two relay threads (client->server and server->client).
   - Access to active client counts and "game-connected" flag is protected by a lock.
*/

public class ConquerProxyLimitedClients
{
    private List<IConquerProtocolHandler> handlers;
    private Dictionary<string, int> serverPorts;
    private IConquerProtocolHandler selectedHandler;

    // Remote server address to forward traffic to (IP or hostname)
    private string remoteServerAddress;

    // Tracks the number of active clients per server type; enforced limit = 1
    private Dictionary<string, int> activeClientCounts;

    // Indicates whether a GameServer client is currently connected (locks Login)
    private bool isGameServerConnected = false;
    private object connectionLock = new object();

    // Listeners and threads used to manage server sockets
    private List<TcpListener> listeners = new();
    private volatile bool isRunning = false;

    private List<Thread> listenerThreads = new();

    // Events for UI
    public event Action<string, bool>? OnClientConnected;
    public event Action<string, bool>? OnRemoteConnected;

    /// <summary>
    /// Raised for each packet observed and analyzed, used by GUI to visualize flows.
    /// </summary>
    public event Action<PacketInfo>? OnPacketCaptured;

    public ConquerProxyLimitedClients(Dictionary<string, int> ports, IConquerProtocolHandler handler, string remoteAddress, Dictionary<string, int> clientCounts)
    {
        handlers = new List<IConquerProtocolHandler> { handler };
        serverPorts = ports;
        selectedHandler = handler;
        remoteServerAddress = remoteAddress;
        activeClientCounts = clientCounts;
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
        isRunning = false;

        foreach (var listener in listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // Ignore errors during shutdown
            }
        }

        foreach (var thread in listenerThreads)
        {
            if (thread.IsAlive)
            {
                thread.Join(1000);
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
                    break; // Listener stopped or socket error
                }

                lock (connectionLock)
                {
                    // Enforce limit: only 1 client per server type
                    if (activeClientCounts.TryGetValue(serverType, out int count) && count >= 1)
                    {
                        client.Close();
                        continue;
                    }

                    // Deny Login while Game is connected (exclusive lock)
                    if (serverType == "Login" && isGameServerConnected)
                    {
                        client.Close();
                        continue;
                    }

                    // If a Game client connects, lock Login
                    if (serverType == "Game")
                    {
                        isGameServerConnected = true;
                    }

                    activeClientCounts[serverType] = count + 1;
                }

                OnClientConnected?.Invoke(serverType, true);

                var context = new ConnectionContext
                {
                    TargetServerType = serverType,
                    Label = client.Client.RemoteEndPoint?.ToString()
                };

                Thread relayThread = new Thread(() =>
                {
                    try
                    {
                        ProxyConnection(client, selectedHandler, context);
                    }
                    finally
                    {
                        lock (connectionLock)
                        {
                            if (activeClientCounts.TryGetValue(serverType, out int c))
                            {
                                activeClientCounts[serverType] = Math.Max(0, c - 1);
                            }
                            if (serverType == "Game")
                            {
                                isGameServerConnected = false;
                            }
                        }
                        OnClientConnected?.Invoke(serverType, false);
                        OnRemoteConnected?.Invoke(serverType, false);
                    }
                });
                relayThread.Start();
            }
        }
        catch (Exception)
        {
            // Consider logging if needed
        }
    }

    private void ProxyConnection(TcpClient client, IConquerProtocolHandler handler, ConnectionContext context)
    {
        using NetworkStream clientStream = client.GetStream();

        if (string.IsNullOrEmpty(context.TargetServerType) ||
            !serverPorts.TryGetValue(context.TargetServerType, out int remotePort))
        {
            client.Close();
            return;
        }

        TcpClient remoteServerClient = new TcpClient();
        try
        {
            remoteServerClient.Connect(remoteServerAddress, remotePort);
            OnRemoteConnected?.Invoke(context.TargetServerType, true);
        }
        catch (Exception)
        {
            client.Close();
            OnRemoteConnected?.Invoke(context.TargetServerType, false);
            return;
        }

        using NetworkStream serverStream = remoteServerClient.GetStream();

        byte[] bufferClient = new byte[4096];
        byte[] bufferServer = new byte[4096];

        ManualResetEvent closingEvent = new ManualResetEvent(false);

        Thread clientToServerThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    int bytesRead = clientStream.Read(bufferClient, 0, bufferClient.Length);
                    if (bytesRead == 0) break;

                    var slice = bufferClient.Take(bytesRead).ToArray();
                    var outBytes = handler.ProcessClientToServer(slice, context, out var info);
                    // Notify UI about the packet
                    SafeRaisePacket(info);

                    if (outBytes != null && outBytes.Length > 0)
                    {
                        serverStream.Write(outBytes, 0, outBytes.Length);
                    }
                }
            }
            catch { }
            finally { closingEvent.Set(); }
        });

        Thread serverToClientThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    int bytesRead = serverStream.Read(bufferServer, 0, bufferServer.Length);
                    if (bytesRead == 0) break;

                    var slice = bufferServer.Take(bytesRead).ToArray();
                    var outBytes = handler.ProcessServerToClient(slice, context, out var info);
                    SafeRaisePacket(info);

                    if (outBytes != null && outBytes.Length > 0)
                    {
                        clientStream.Write(outBytes, 0, outBytes.Length);
                    }
                }
            }
            catch { }
            finally { closingEvent.Set(); }
        });

        clientToServerThread.Start();
        serverToClientThread.Start();

        closingEvent.WaitOne();

        client.Close();
        remoteServerClient.Close();

        OnRemoteConnected?.Invoke(context.TargetServerType, false);
    }

    private void SafeRaisePacket(PacketInfo info)
    {
        try
        {
            OnPacketCaptured?.Invoke(info);
        }
        catch
        {
            // Swallow UI errors to not break proxy flow
        }
    }
}
