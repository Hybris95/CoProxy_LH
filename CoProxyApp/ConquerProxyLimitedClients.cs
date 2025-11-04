/*
 File: ConquerProxyLimitedClients.cs
 Responsibility:
   - Multi-port TCP proxy for "Login" and "Game" server types.
   - Enforces 1 concurrent client per server type with an exclusivity rule:
       * When a Game client is connected, Login connections are blocked.
   - Relays traffic between a local client and a remote server (by IP + per-type port).
   - Emits events for UI/monitoring when local/remote sides connect or disconnect.
   - Manages lifecycle (Start/Stop), listeners, and relay threads.

 Threading Model:
   - One TcpListener per server type runs on a dedicated listener thread.
   - Each accepted client spins two relay threads (client->server and server->client).
   - Access to active client counts and "game-connected" flag is protected by a lock.

 Error Handling:
   - Socket/IO exceptions are caught and connection is closed gracefully.
   - Non-fatal listener errors are ignored during shutdown.

 Limitations:
   - No TLS or encryption; no packet-level parsing beyond handler interception.
   - No automatic reconnection or retry policies.
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// TCP proxy that accepts client connections on configured ports (e.g., Login/Game),
/// forwards traffic to a remote server, and enforces a single-client-per-server-type policy.
/// Additionally, when the Game server is connected, the Login server is locked (no new clients).
/// </summary>
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

    /// <summary>
    /// Event fired when a local client connects/disconnects to a given server type.
    /// Args:
    ///   string serverType ("Login"|"Game"), bool connected (true on connect, false on disconnect).
    /// Threading:
    ///   - Raised from worker threads; subscribers should marshal to UI thread if needed.
    /// </summary>
    public event Action<string, bool>? OnClientConnected;

    /// <summary>
    /// Event fired when the proxy (on remote side) connects/disconnects to the upstream server
    /// for a given server type.
    /// Args:
    ///   string serverType ("Login"|"Game"), bool connected (true on connect, false on disconnect).
    /// Threading:
    ///   - Raised from worker threads; subscribers should marshal to UI thread if needed.
    /// </summary>
    public event Action<string, bool>? OnRemoteConnected;

    /// <summary>
    /// Creates a new proxy instance.
    /// Precondition:
    ///   - ports contains at least keys "Login" and/or "Game".
    ///   - handler is not null.
    ///   - remoteAddress is a resolvable address for the upstream servers.
    ///   - clientCounts is a shared dictionary initialized with server types and 0 counts.
    /// </summary>
    /// <param name="ports">Mapping of server type to local listening port.</param>
    /// <param name="handler">Packet handler used for client->server packet interception.</param>
    /// <param name="remoteAddress">Remote server IP/hostname.</param>
    /// <param name="clientCounts">Shared client counters per server type (limit = 1).</param>
    public ConquerProxyLimitedClients(Dictionary<string, int> ports, IConquerProtocolHandler handler, string remoteAddress, Dictionary<string, int> clientCounts)
    {
        handlers = new List<IConquerProtocolHandler> { handler };
        serverPorts = ports;
        selectedHandler = handler;
        remoteServerAddress = remoteAddress;
        activeClientCounts = clientCounts;
    }

    /// <summary>
    /// Starts the proxy:
    ///   - Creates and starts listeners for each configured server type.
    ///   - Spawns one listener thread per server type.
    /// Postcondition:
    ///   - isRunning == true
    ///   - listeners and listenerThreads populated and active.
    /// </summary>
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

    /// <summary>
    /// Stops the proxy:
    ///   - Signals listeners to stop, joins listener threads.
    ///   - Does not forcibly terminate existing relay threads (they will exit naturally).
    /// Postcondition:
    ///   - isRunning == false
    ///   - listeners and listenerThreads cleared.
    /// </summary>
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

    /// <summary>
    /// Accept loop for a given server type listener.
    /// Enforces:
    ///   - Max 1 client per server type.
    ///   - While a Game client is connected, Login connections are rejected.
    /// Side effects:
    ///   - Emits OnClientConnected and OnRemoteConnected upon state changes.
    /// Threading:
    ///   - Runs on its own listener thread.
    /// </summary>
    /// <param name="listener">The TcpListener to accept from.</param>
    /// <param name="serverType">"Login" or "Game".</param>
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

                var context = new ConnectionContext { TargetServerType = serverType };

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

    /// <summary>
    /// Bridges a single client with the remote server:
    ///   - Connects to the remote endpoint matching the connection's server type.
    ///   - Spawns two relay threads:
    ///       client->server (with handler interception) and server->client (raw).
    ///   - Closes both sides when either direction ends.
    /// Precondition:
    ///   - context.TargetServerType is set and present in serverPorts.
    /// Postcondition:
    ///   - Both sockets are closed; OnRemoteConnected raised with false.
    /// </summary>
    /// <param name="client">Accepted TcpClient from local side.</param>
    /// <param name="handler">Packet handler for client->server path.</param>
    /// <param name="context">Connection context containing target server type.</param>
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

                    handler.HandlePacket(bufferClient.Take(bytesRead).ToArray(), out var modifiedPacket, context);
                    if (modifiedPacket != null && modifiedPacket.Length > 0)
                    {
                        serverStream.Write(modifiedPacket, 0, modifiedPacket.Length);
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

                    clientStream.Write(bufferServer, 0, bytesRead);
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
}
