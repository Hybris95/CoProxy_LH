# CoProxyApp

A minimal WinForms-based TCP proxy for Conquer Online style servers that:

- Listens on two local ports (Login and Game).
- Forwards traffic to a specified remote server (same host, distinct ports per type).
- Enforces a single concurrent client per server type (limit = 1).
- Locks the Login server while a Game client is connected.
- Exposes connection events used by the UI to show green/red status indicators.

This project includes an extended protocol handler (`ConquerClassicLordsHandler`) implementing `IConquerProtocolHandler` with:
- Blowfish-based encryption hooks for client→server payloads (header stays clear-text).
- Basic Conquer packet parsing (Little Endian header [Length, Type], optional `"TQServer"`/`"TQClient"` footer).
- Automatic session reconnection logic (backoff and cipher resync on decrypt/parse failures).

## Table of contents

- Prerequisites
- Build and run
- How it works
- Encryption (Blowfish)
- Automatic reconnection
- Adding a new protocol handler
- Configuration and UX
- Limitations and notes
- Troubleshooting

## Prerequisites

- OS: Windows 10/11 (required for WinForms).
- IDE: Visual Studio 2022 (v17.5+) recommended.
- .NET SDK: .NET 6.0 or later.

Optional (for real Blowfish encryption):
- NuGet package: `BouncyCastle.Cryptography` (a.k.a. `BouncyCastle.Crypto`).
  - Install with: `dotnet add .\CoProxyApp.csproj package BouncyCastle.Cryptography`
  - Or via Visual Studio NuGet UI.

Notes:
- Administrator privileges may be required if binding to ports < 1024.
- Ensure the chosen ports are not in use by other processes.

## Build and run

1. Open `CoProxyApp.sln` in Visual Studio 2022+.
2. Select build configuration (Debug or Release).
3. Press F5 (or Start Debugging) to run.

CLI alternative:

```
dotnet build dotnet run --project .\CoProxyApp.csproj
```

## How it works

- UI (`MainForm`):
  - Inputs: local Login/Game ports, remote server IP/hostname, handler selection.
  - Status indicators:
    - Login Client and Login Server
    - Game Client and Game Server
  - Green = connected, Red = disconnected.

- Proxy core (`ConquerProxyLimitedClients`):
  - Starts one `TcpListener` per server type.
  - Accepts at most one client per server type.
  - Locks Login while a Game client is connected.
  - For each client:
    - Connects to the remote endpoint for that server type.
    - Spawns two relay threads:
      - Client → Server: passes data through `IConquerProtocolHandler.HandlePacket`.
      - Server → Client: raw relay to client.
  - Emits:
    - `OnClientConnected(serverType, bool)`
    - `OnRemoteConnected(serverType, bool)`

- Protocol handler (`ConquerClassicLordsHandler`):
  - Parses Conquer header: `[Length(ushort), Type(ushort)]` (Little Endian).
  - Skips optional footers (`"TQServer"`/`"TQClient"`) outside `Length`.
  - Encrypts/decrypts payload (bytes after header) with Blowfish ECB when enabled.
  - Applies session-level auto-reconnect/backoff when decryption or parsing fails.

## Encryption (Blowfish)

- The handler supports Blowfish for payload encryption/decryption. The header remains clear-text.
- It uses an adapter that attempts to load BouncyCastle’s `BlowfishEngine` at runtime:
  - If found, real Blowfish ECB is used in 8-byte blocks (partial last block is left plain; no padding).
  - If not found, a non-secure XOR fallback is used to maintain traffic flow. Do NOT use this fallback in production.

To enable real Blowfish:
- Add the package:
```
dotnet add .\CoProxyApp.csproj package BouncyCastle.Cryptography
```

- Rebuild and run. The handler will automatically detect and use `BlowfishEngine`.

Keying:
- The handler heuristically enables encryption for the Game server and uses a default key (`"TQClient"` for client→server).
- Adjust keying or handshake logic per your server patch/version by modifying:
- `DefaultClientKey` / `DefaultServerKey`
- `EnsureCipherInitialized(...)`

## Automatic reconnection

- The handler maintains per-connection session state and tracks consecutive decryption/parse failures.
- On failures:
- It resets cipher state (to allow re-handshake) and applies exponential backoff (250ms → up to 8s).
- During backoff, outbound packets are dropped (empty byte array), allowing the client to naturally retry.
- When backoff expires, the next packet reinitializes the cipher and resumes normal processing.

Note:
- This is a session-level “reconnect” (cipher resync) implemented inside the handler.
- Socket-level reconnection (to the upstream server) is already handled by the proxy’s lifecycle.

## Adding a new protocol handler

1. Create a class implementing `IConquerProtocolHandler`.
2. In `HandlePacket`, parse the header and payload and modify or drop as needed.
3. Optionally enable your own crypto or routing logic.

Example:
```csharp
public class MyConquerHandler : IConquerProtocolHandler
{
  public void HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context)
  {
      // Parse header
      ushort length = BitConverter.ToUInt16(data, 0);
      ushort type = BitConverter.ToUInt16(data, 2);
      // ...inspect/modify payload...
      modifiedPacket = data;
  }

  public bool IsPacketForLoginServer(byte[] data) => false;
  public bool IsPacketForGameServer(byte[] data) => false;
}
```

## Configuration and UX

- Login Port: Local port for Login server traffic (e.g., 9958).
- Game Port: Local port for Game server traffic (e.g., 5816).
- Remote Server IP: Remote host to forward to.
- Handler: Choose the protocol handler (e.g., ConquerClassicLordsHandler).

Buttons:

- Start Proxy: Starts listeners and accepting clients.
- Stop Proxy: Stops listeners and resets UI indicators.

## Limitations and notes

- No TLS/SSL.
- No packet parsing beyond what the handler implements.
- No persistent logging included.
- Single-client-per-type limit per running instance.
- Blowfish ECB is used for simplicity. Some patches may require different modes or header encryption.
- The XOR fallback is only for development when BouncyCastle is not available.

## Troubleshooting

- Invalid ports:
  - Ensure both fields contain integers in range 1–65535.
- Connections fail:
  - Check Windows Firewall rules and port availability (netstat -ano).
- Encryption errors:
  - Ensure BouncyCastle is installed if real Blowfish is required.
  - Verify keys (DefaultClientKey/DefaultServerKey) and patch-specific requirements.
- UI status not updating:
  - The app marshals events via Invoke; check for unhandled exceptions in the Output window.
