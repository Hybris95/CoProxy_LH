/*
 File: ConquerClassicLordsHandler.cs
 Responsibility:
   - Implements a protocol handler for Conquer Classic (Lords) and generic CO packets with:
       1) Blowfish-based encryption/decryption hooks (payload; header stays clear-text).
       2) Basic Conquer packet parsing structure (Little Endian, [Length, Type] header, optional footer).
       3) Automatic reconnection logic (session-level resync/backoff on decrypt/parse failures).
       4) Packet tagging and field extraction for visualization in the GUI using ConquerPacket.
   - Provides a safe, pass-through fallback when Blowfish engine is not available.
   - Stateless across processes, but maintains per-connection session state via ConditionalWeakTable.

 Encryption:
   - Blowfish ECB mode on the payload (bytes after the 4-byte header).
   - Header [Length(ushort), Type(ushort)] is not encrypted.
   - 8-byte block processing; last partial block left as-is (no padding).
   - Real Blowfish provided via BouncyCastle (Org.BouncyCastle). If not present, a XOR fallback is used (NOT SECURE).
   - By default, client->server path attempts encryption for "Game" server type. Server->client path is inspected without decryption by default.

 Parsing:
   - Little Endian ordering.
   - Packet header: [0..1]=Length (ushort), [2..3]=Type (ushort). Payload is [4..Length).
   - Optional ASCII footer beyond length for patches > 5017: "TQServer" or "TQClient" (ignored).
   - Basic tag dictionary maps common Type ids to friendly names (extend as needed).

 Auto-Reconnect Logic:
   - On decrypt/parse failures, session triggers a backoff window and resets cipher state to allow re-handshake.
   - During backoff, outbound packets are dropped to facilitate client retry.

 Notes:
   - This handler processes both directions via ProcessClientToServer and ProcessServerToClient.
   - For real Blowfish, install BouncyCastle and see README.md.
*/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

public class ConquerClassicLordsHandler : IConquerProtocolHandler
{
    // Per-connection session state storage keyed by ConnectionContext (reference identity)
    private readonly ConditionalWeakTable<ConnectionContext, SessionState> _sessions = new();

    // Default Blowfish keys (commonly used indicators; adapt per server patch if needed)
    private static readonly byte[] DefaultClientKey = Encoding.ASCII.GetBytes("TQClient");
    private static readonly byte[] DefaultServerKey = Encoding.ASCII.GetBytes("TQServer");

    // Footer markers (not included in header length)
    private static readonly byte[] FooterTQClient = Encoding.ASCII.GetBytes("TQClient");
    private static readonly byte[] FooterTQServer = Encoding.ASCII.GetBytes("TQServer");

    // Known packet tags (extend as needed; examples shown)
    private static readonly Dictionary<ushort, string> PacketTags = new()
    {
        { 0x2715, "MsgWalk" },
        { 0x3F05, "MsgAction" }, // Example placeholder
        { 0x3F2A, "MsgTalk" },   // Example placeholder
        // Add more mappings here
    };

    public byte[] ProcessClientToServer(byte[] data, ConnectionContext context, out ConquerPacket packet)
    {
        var session = _sessions.GetValue(context, _ => new SessionState());

        if (session.IsInBackoff())
        {
            packet = BuildPacketSafe(data, context, PacketDirection.ClientToServer, valid: false, "Dropped during backoff");
            return Array.Empty<byte>();
        }

        try
        {
            if (data == null || data.Length < 4)
                throw new ArgumentException("Insufficient data for header.");

            ushort declaredLength = BitConverter.ToUInt16(data, 0);
            ushort type = BitConverter.ToUInt16(data, 2);

            if (declaredLength < 4 || declaredLength > data.Length)
                throw new ArgumentException("Malformed declared length.");

            int packetSize = declaredLength;
            _ = DetectFooterSize(data, packetSize, data.Length); // Footer ignored here

            int payloadLength = packetSize - 4;
            var payloadSpan = payloadLength > 0 ? new ReadOnlySpan<byte>(data, 4, payloadLength) : ReadOnlySpan<byte>.Empty;

            EnsureCipherInitialized(context, session, isClientToServer: true);

            byte[] analyzedPayload = session.EncryptionEnabled && payloadLength > 0
                ? ProcessPayloadDecrypt(session, payloadSpan)
                : payloadSpan.ToArray();

            // Build ConquerPacket model from the raw data (header+payload only)
            packet = ConquerPacket.Parse(new ReadOnlySpan<byte>(data, 0, packetSize), context.ConnectionId, context.TargetServerType ?? "?", PacketDirection.ClientToServer);

            // Tag and annotate using the analyzed (potentially decrypted) payload
            TagAndDescribe(packet, analyzedPayload);

            // Re-encrypt payload for forwarding if enabled (header remains clear-text)
            byte[] outPayload = session.EncryptionEnabled && analyzedPayload.Length > 0
                ? ProcessPayloadEncrypt(session, analyzedPayload)
                : analyzedPayload;

            byte[] outFrame = new byte[4 + outPayload.Length];
            BitConverter.TryWriteBytes(new Span<byte>(outFrame, 0, 2), declaredLength);
            BitConverter.TryWriteBytes(new Span<byte>(outFrame, 2, 2), type);
            Buffer.BlockCopy(outPayload, 0, outFrame, 4, outPayload.Length);

            session.ResetFailures();
            return outFrame;
        }
        catch (Exception ex)
        {
            session.RegisterFailure();
            session.TriggerBackoffAndReset();
            packet = BuildPacketSafe(data, context, PacketDirection.ClientToServer, valid: false, $"Error: {ex.Message}");
            return Array.Empty<byte>(); // Drop to let client re-sync/reconnect
        }
    }

    public byte[] ProcessServerToClient(byte[] data, ConnectionContext context, out ConquerPacket packet)
    {
        try
        {
            if (data == null || data.Length < 4)
                throw new ArgumentException("Insufficient data for header.");

            ushort declaredLength = BitConverter.ToUInt16(data, 0);
            ushort type = BitConverter.ToUInt16(data, 2);

            if (declaredLength < 4 || declaredLength > data.Length)
                throw new ArgumentException("Malformed declared length.");

            int packetSize = declaredLength;
            _ = DetectFooterSize(data, packetSize, data.Length); // Footer ignored

            int payloadLength = packetSize - 4;
            var payloadSpan = payloadLength > 0 ? new ReadOnlySpan<byte>(data, 4, payloadLength) : ReadOnlySpan<byte>.Empty;

            // We do not decrypt server->client by default
            byte[] analyzedPayload = payloadSpan.ToArray();

            packet = ConquerPacket.Parse(new ReadOnlySpan<byte>(data, 0, packetSize), context.ConnectionId, context.TargetServerType ?? "?", PacketDirection.ServerToClient);
            TagAndDescribe(packet, analyzedPayload);

            return data;
        }
        catch (Exception ex)
        {
            packet = BuildPacketSafe(data ?? Array.Empty<byte>(), context, PacketDirection.ServerToClient, valid: false, $"Parse error (forwarded raw): {ex.Message}");
            return data ?? Array.Empty<byte>();
        }
    }

    public bool IsPacketForLoginServer(byte[] data) => false;
    public bool IsPacketForGameServer(byte[] data) => false;

    // ---- Internal helpers / tagging ----

    private static ConquerPacket BuildPacketSafe(byte[] raw, ConnectionContext ctx, PacketDirection dir, bool valid, string message)
    {
        if (!ConquerPacket.TryParse(raw, ctx.ConnectionId, ctx.TargetServerType ?? "?", dir, out var pkt))
        {
            // When even TryParse fails, we still emit a minimal packet model
            pkt = new ConquerPacket
            {
                ConnectionId = ctx.ConnectionId,
                ServerType = ctx.TargetServerType ?? "?",
                Direction = dir,
                TimestampUtc = DateTime.UtcNow,
                DeclaredLength = 0,
                Type = 0,
                RawFrame = raw,
                RawWithFooter = raw,
                Description = message,
                Tag = "Unknown"
            };
        }
        pkt.Description = message;
        return pkt;
    }

    private static int DetectFooterSize(byte[] buffer, int contentLength, int realLength)
    {
        int tail = realLength - contentLength;
        if (tail <= 0) return 0;

        if (tail >= FooterTQClient.Length)
        {
            var span = new ReadOnlySpan<byte>(buffer, contentLength, tail);
            if (EndsWith(span, FooterTQClient)) return FooterTQClient.Length;
        }
        if (tail >= FooterTQServer.Length)
        {
            var span = new ReadOnlySpan<byte>(buffer, contentLength, tail);
            if (EndsWith(span, FooterTQServer)) return FooterTQServer.Length;
        }
        return 0;
    }

    private static bool EndsWith(ReadOnlySpan<byte> span, byte[] pattern)
    {
        if (span.Length < pattern.Length) return false;
        return span.Slice(span.Length - pattern.Length).SequenceEqual(pattern);
    }

    private static void EnsureCipherInitialized(ConnectionContext ctx, SessionState session, bool isClientToServer)
    {
        if (session.CipherInitialized) return;

        // Heuristic: enable encryption for Game server; optional for Login.
        session.EncryptionEnabled = string.Equals(ctx.TargetServerType, "Game", StringComparison.OrdinalIgnoreCase);
        var key = isClientToServer ? DefaultClientKey : DefaultServerKey;
        session.InitCipher(key);
    }

    private static byte[] ProcessPayloadDecrypt(SessionState session, ReadOnlySpan<byte> input)
    {
        try
        {
            return session.CipherDecrypt(input);
        }
        catch
        {
            session.TriggerBackoffAndReset();
            throw;
        }
    }

    private static byte[] ProcessPayloadEncrypt(SessionState session, ReadOnlySpan<byte> input)
    {
        try
        {
            return session.CipherEncrypt(input);
        }
        catch
        {
            session.TriggerBackoffAndReset();
            throw;
        }
    }

    private static void TagAndDescribe(ConquerPacket pkt, byte[] analyzedPayload)
    {
        string tag = PacketTags.TryGetValue(pkt.Type, out var name) ? name : "Unknown";
        pkt.Tag = tag;

        // Example field extraction for MsgWalk (0x2715)
        if (pkt.Type == 0x2715 && analyzedPayload.Length >= 24)
        {
            uint directionRaw = BitConverter.ToUInt32(analyzedPayload, 0);
            uint characterId = BitConverter.ToUInt32(analyzedPayload, 4);
            uint moveType = BitConverter.ToUInt32(analyzedPayload, 8);
            uint timestamp = BitConverter.ToUInt32(analyzedPayload, 12);
            uint mapId = BitConverter.ToUInt32(analyzedPayload, 16);

            pkt.Fields["DirectionRaw"] = directionRaw;
            pkt.Fields["DirectionMod8"] = directionRaw % 8;
            pkt.Fields["CharacterId"] = characterId;
            pkt.Fields["MoveType"] = moveType;
            pkt.Fields["Timestamp"] = timestamp;
            pkt.Fields["MapId"] = mapId;

            pkt.Description = $"Walk dir={directionRaw % 8}, Char={characterId}, Type={moveType}, Map={mapId}";
        }
        else
        {
            pkt.Description = $"Type=0x{pkt.Type:X4}, Payload={Math.Max(0, pkt.DeclaredLength - 4)} bytes";
        }
    }

    // ---- Internal models and session/cipher management ----

    private class SessionState
    {
        private ICipherAdapter? _cipher;

        private int _consecutiveFailures = 0;
        private DateTime _nextAllowed = DateTime.MinValue;

        public bool EncryptionEnabled { get; set; } = false;
        public bool CipherInitialized { get; private set; } = false;

        public void InitCipher(byte[] key)
        {
            var bf = BlowfishAdapter.TryCreate(key);
            if (bf != null)
                _cipher = bf;
            else
                _cipher = new XorFallbackCipher(key);

            CipherInitialized = true;
        }

        public bool IsInBackoff() => DateTime.UtcNow < _nextAllowed;

        public void RegisterFailure()
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 2)
            {
                var delayMs = Math.Min(8000, 250 * (1 << Math.Min(5, _consecutiveFailures - 2)));
                _nextAllowed = DateTime.UtcNow.AddMilliseconds(delayMs);
            }
        }

        public void ResetFailures()
        {
            _consecutiveFailures = 0;
            _nextAllowed = DateTime.MinValue;
        }

        public void TriggerBackoffAndReset()
        {
            RegisterFailure();
            _cipher = null;
            CipherInitialized = false;
            EncryptionEnabled = false;
        }

        public byte[] CipherDecrypt(ReadOnlySpan<byte> input)
        {
            if (!EncryptionEnabled || _cipher == null) return input.ToArray();
            return _cipher.Decrypt(input);
        }

        public byte[] CipherEncrypt(ReadOnlySpan<byte> input)
        {
            if (!EncryptionEnabled || _cipher == null) return input.ToArray();
            return _cipher.Encrypt(input);
        }
    }

    private interface ICipherAdapter
    {
        byte[] Encrypt(ReadOnlySpan<byte> input);
        byte[] Decrypt(ReadOnlySpan<byte> input);
    }

    private sealed class BlowfishAdapter : ICipherAdapter
    {
        private readonly object _engine;        // Org.BouncyCastle.Crypto.Engines.BlowfishEngine
        private readonly object _encParams;     // Org.BouncyCastle.Crypto.Parameters.KeyParameter
        private readonly System.Reflection.MethodInfo _initMethod;
        private readonly System.Reflection.MethodInfo _processBlock;
        private readonly byte[] _key;

        private BlowfishAdapter(object engine, object keyParams, System.Reflection.MethodInfo init, System.Reflection.MethodInfo process, byte[] key)
        {
            _engine = engine;
            _encParams = keyParams;
            _initMethod = init;
            _processBlock = process;
            _key = key;
        }

        public static BlowfishAdapter? TryCreate(byte[] key)
        {
            try
            {
                var engineType = Type.GetType("Org.BouncyCastle.Crypto.Engines.BlowfishEngine, BouncyCastle.Crypto");
                var keyParamType = Type.GetType("Org.BouncyCastle.Crypto.Parameters.KeyParameter, BouncyCastle.Crypto");
                if (engineType == null || keyParamType == null)
                    return null;

                var engine = Activator.CreateInstance(engineType)!;
                var keyParam = Activator.CreateInstance(keyParamType, new object[] { key })!;
                var initMethod = engineType.GetMethod("Init", new[] { typeof(bool), keyParamType })!;
                var processBlock = engineType.GetMethod("ProcessBlock", new[] { typeof(byte[]), typeof(int), typeof(byte[]), typeof(int) })!;

                return new BlowfishAdapter(engine, keyParam, initMethod, processBlock, key);
            }
            catch
            {
                return null;
            }
        }

        public byte[] Encrypt(ReadOnlySpan<byte> input) => ProcessBlocks(input, encrypt: true);
        public byte[] Decrypt(ReadOnlySpan<byte> input) => ProcessBlocks(input, encrypt: false);

        private byte[] ProcessBlocks(ReadOnlySpan<byte> input, bool encrypt)
        {
            int blockSize = 8;
            int fullBlocks = input.Length / blockSize;
            int remainder = input.Length % blockSize;

            byte[] output = new byte[input.Length];

            _initMethod.Invoke(_engine, new object[] { encrypt, _encParams });

            for (int i = 0; i < fullBlocks; i++)
            {
                int inOff = i * blockSize;
                int outOff = i * blockSize;

                byte[] inBuf = input.Slice(inOff, blockSize).ToArray();
                _processBlock.Invoke(_engine, new object[] { inBuf, 0, output, outOff });
            }

            if (remainder > 0)
            {
                input.Slice(fullBlocks * blockSize, remainder).CopyTo(new Span<byte>(output, fullBlocks * blockSize, remainder));
            }

            return output;
        }
    }

    private sealed class XorFallbackCipher : ICipherAdapter
    {
        private readonly byte[] _key;
        public XorFallbackCipher(byte[] key) => _key = key.Length > 0 ? key : new byte[] { 0 };

        public byte[] Encrypt(ReadOnlySpan<byte> input) => Xor(input);
        public byte[] Decrypt(ReadOnlySpan<byte> input) => Xor(input);

        private byte[] Xor(ReadOnlySpan<byte> input)
        {
            byte[] output = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
                output[i] = (byte)(input[i] ^ _key[i % _key.Length]);
            return output;
        }
    }
}
