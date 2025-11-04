using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

public class ConquerProxyLimitedClients
{
    private List<IConquerProtocolHandler> handlers;
    private Dictionary<string, int> serverPorts;
    private IConquerProtocolHandler selectedHandler;

    // Adresse serveur distant pour relayer le trafic
    private string remoteServerAddress;

    // Suivi du nombre de clients connectés par serveur (limite à 1)
    private Dictionary<string, int> activeClientCounts;

    // Indicateur si GameServer est connecté (verrou exclusif sur LoginServer)
    private bool isGameServerConnected = false;
    private object connectionLock = new object();

    // Stockage des listeners et threads pour gestion propre
    private List<TcpListener> listeners = new();
    private volatile bool isRunning = false;

    private List<Thread> listenerThreads = new();

    // Événements pour informer sur états clients et serveurs distants
    public event Action<string, bool>? OnClientConnected;
    public event Action<string, bool>? OnRemoteConnected;

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
                // Ignorer erreurs à l'arrêt
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
                    break; // Listener arrêté ou erreur socket
                }

                lock (connectionLock)
                {
                    // Limitation à 1 client par serveur
                    if (activeClientCounts.TryGetValue(serverType, out int count) && count >= 1)
                    {
                        client.Close();
                        continue;
                    }

                    // Interdire login tant que game est connecté
                    if (serverType == "Login" && isGameServerConnected)
                    {
                        client.Close();
                        continue;
                    }

                    // Si connexion game acceptée, verrouiller login
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
            // Ignorer ou logger
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
