# CoProxyApp

A minimal WinForms-based TCP proxy for Conquer Online style servers that:

- Listens on two local ports (Login and Game).
- Forwards traffic to a specified remote server (same host, distinct ports per type).
- Enforces a single concurrent client per server type (limit = 1).
- Locks the Login server while a Game client is connected.
- Exposes connection events used by the UI to show green/red status indicators.

This project includes a basic, pass-through protocol handler (`ConquerClassicLordsHandler`) implementing `IConquerProtocolHandler`. You can extend it to parse and modify packets for specific versions or flavors.

## Table of contents

- Prerequisites
- Build and run
- How it works
- Adding a new protocol handler
- Configuration and UX
- Limitations and notes
- Troubleshooting

## Prerequisites

- OS: Windows 10/11 (required for WinForms).
- IDE: Visual Studio 2022 (v17.5+) recommended.
- .NET SDK: .NET 6.0 or later.

Optional:
- Administrator privileges may be required if binding to ports < 1024.
- Ensure the chosen ports are not in use by other processes.

## Build and run

1. Open `CoProxyApp.sln` in Visual Studio 2022+.
2. Select build configuration (Debug or Release).
3. Press F5 (or Start Debugging) to run.

Alternatively, if you have the project file:

- Open a Developer Command Prompt:
  - `dotnet build`
  - `dotnet run --project .\CoProxyApp.csproj`

## How it works

- UI (`MainForm`):
  - Lets you input local Login and Game ports, remote server IP/hostname, and select a protocol handler.
  - Displays real-time status:
    - Login Client and Login Server
    - Game Client and Game Server
  - Status icons:
    - Green = connected
    - Red = disconnected

- Proxy core (`ConquerProxyLimitedClients`):
  - Starts one `TcpListener` per configured server type.
  - Accepts at most one client per server type.
  - When a Game client connects, Login is locked (new Login connections are refused) until Game disconnects.
  - For each client:
    - Connects to the appropriate remote port (by server type).
    - Spawns two relay threads:
      - Client → Server: passes data through the selected `IConquerProtocolHandler`.
      - Server → Client: forwards raw data back to the client.
  - Emits:
    - `OnClientConnected(serverType, bool)`
    - `OnRemoteConnected(serverType, bool)`

- Protocol handler (`IConquerProtocolHandler`):
  - `HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context)`: modify or drop packets.
  - `IsPacketForLoginServer`, `IsPacketForGameServer`: optional helpers (not used by the current proxy which binds per type already).
  - `ConquerClassicLordsHandler`: simple pass-through example (no modifications).

## Adding a new protocol handler

1. Create a new class implementing `IConquerProtocolHandler`.
2. Implement `HandlePacket` to parse and optionally modify outbound packets (client → server).
3. Optionally implement routing helpers if you later design a single-port multiplexer.
4. Register your handler in the UI:
   - Add it to the `handlers` list in `MainForm` constructor.
   - It will appear in the handler dropdown automatically.

Example skeleton:
```csharp
public class MyConquerHandler : IConquerProtocolHandler
{
    public void HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context)
    {
        // TODO: parse, inspect, modify or drop
        modifiedPacket = data; // pass-through example
    }

    public bool IsPacketForLoginServer(byte[] data) => false;
    public bool IsPacketForGameServer(byte[] data) => false;
}
