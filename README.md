# CoProxyApp

A WinForms-based TCP proxy and packet workbench for Conquer Online style servers.

- Listens on two local ports (Login and Game).
- Forwards traffic to a specified remote server hosted at a single address with distinct ports per type.
- Enforces a single concurrent client per server type and locks the Login endpoint while a Game client is connected.
- Visualizes live packets to assist reverse-engineering and protocol analysis.
- Supports Blowfish encryption hooks on payloads for the client-to-server path.
- Provides automatic session reconnection and backoff on decrypt or parse failures.

## What’s included

- Proxy Core: Multi-port relay with simple concurrency limits and UI events.
- Packet Workbench UI: Live list, details view with hex dump and parsed fields, and simple filtering options.
- Default Handler: A handler that parses the common header, tags known message types, and extracts basic fields for select packets. Includes Blowfish payload processing and a reconnection/backoff strategy.

## What’s new compared to a basic proxy

- Packet visualization tab to help reverse-engineering:
  - Live packet stream with columns for time, direction, server, type, declared length, tag, and a short description.
  - Details pane with a hex dump (header plus payload) and a list of parsed fields when available.
  - Filters by direction, server type, and a case-insensitive tag or text search.
  - Export to CSV, JSON Lines, or plain text without requiring external tooling.
- Enhanced handler interface:
  - Separate methods for client-to-server and server-to-client paths, both returning a structured PacketInfo for visualization.
  - Tagging and simple field extraction to make it easier to understand the traffic while iterating on parsing logic.
- Backend event for packet capture:
  - Emitted for every observed packet so the GUI can update in real time.

## Prerequisites

- Windows 10 or Windows 11.
- Visual Studio 2022 or later.
- .NET 6.0 or later.

Optional for real Blowfish encryption:
- BouncyCastle.Cryptography package available to the application. Install it using the NuGet package manager in your IDE. If the package is not available at runtime, a development-only XOR fallback is used to keep traffic flowing, which is not secure and intended only for development.

## Build and run

- Open the solution file in Visual Studio.
- Choose Debug or Release configuration.
- Start the application from the IDE. The main window provides configuration fields for ports, remote server address, and handler selection, along with connection status indicators.

## How it works

- UI (MainForm):
  - Proxy tab: set local ports for Login and Game, provide the remote server address, select the handler, and observe green or red status indicators for both client and server sides.
  - Packets tab: monitor all packets in a live list. Select any packet to view the hex dump and parsed key fields. Apply filters by direction, server type, and tag or text to narrow the view. Export the visible set to CSV, JSON Lines, or plain text.

- Proxy core:
  - Creates one listener per configured server type.
  - For each connected client, opens two relay flows: client to server and server to client.
  - Enforces a single client per server type and blocks Login connections while a Game connection is active.
  - Emits events for client and remote connection state, and one event per captured packet.

- Handler (IConquerProtocolHandler and default implementation):
  - Parses the Conquer header using little-endian order with length and type fields.
  - Identifies and ignores the optional footer strings that may be attached beyond the declared length in newer patches.
  - Attempts Blowfish for the client-to-server payload while keeping the header clear-text. Uses an eight-byte block approach and leaves a final partial block as-is.
  - Tags recognized packet types and extracts a few commonly useful fields for select messages to help you iterate on your parsing.
  - Applies a per-connection reconnection and backoff policy when decryption or parsing fails so that the client can re-handshake cleanly.

## Packet inspection model

Each packet observed by the handler is summarized in a structured model that the UI uses to render rows and details:

- Connection identifier and server type label.
- UTC timestamp and direction.
- Declared length and packet type from the header.
- A human-friendly tag and a short description.
- Parsed fields as a key and value list when available.
- Raw frame (header plus payload) bytes and payload bytes.

This model is designed to be extended as your parsing improves and more Conquer message types are recognized.

## Customization

- Extend packet tagging by adding more mappings from numeric message types to human-friendly names and by expanding the field extraction logic as you discover packet formats.
- Adjust encryption behavior by enabling or disabling decryption in the server-to-client path if your environment requires it, and adapt key selection or handshake logic per patch or server variant.
- Improve export formats by adding or modifying export options to suit your analysis workflow.

## Limitations and notes

- No TLS or end-to-end encryption beyond Blowfish payload handling in the handler.
- Blowfish is used in a simple way for compatibility and ease of inspection; server variants may need different modes or padding.
- Server-to-client decryption is disabled by default to avoid interfering with gameplay while you observe traffic; enable if you need symmetric inspection.
- The XOR fallback is a development-only convenience and provides no security guarantees.
- No persistent logging is included; use the export feature from the Packets tab to capture sessions.

## Troubleshooting

- If the application cannot bind to the specified ports, verify that they are not already in use and that any firewall rules allow local listening.
- If the remote connection fails, confirm the remote address and that the remote server is reachable from your machine.
- If you see frequent decryption or parsing errors, verify that the expected keys and patch assumptions match your client and server. The handler’s reconnection and backoff logic will try to recover by allowing a new handshake.
- If the UI stops updating, check for unhandled exceptions in the IDE’s output window and ensure long operations are not performed on the UI thread.
