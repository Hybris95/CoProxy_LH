/*
 File: ConquerClassicLordsHandler.cs
 Responsibility:
   - Implements a protocol handler for Conquer Classic (Lords) with:
       1) Blowfish-based encryption/decryption hooks for client->server payloads.
       2) Basic Conquer packet parsing structure (Little Endian, [Length, Type] header, optional footer).
       3) Automatic reconnection logic (session-level resync/backoff on decrypt/parse failures).
   - Provides a safe, pass-through fallback when Blowfish engine is not available.
   - Stateless across processes, but maintains per-connection session state via ConditionalWeakTable.

 Encryption:
   - The handler supports Blowfish ECB mode on the payload (bytes after the 4-byte header).
   - Header [Length(ushort), Type(ushort)] is not encrypted.
   - For block processing, the payload is processed in 8-byte blocks; the last partial block (if any)
     is left as-is (no padding). This matches a pragmatic approach when packet sizes are not aligned.
   - Real Blowfish is provided via an optional adapter using BouncyCastle (Org.BouncyCastle).
     If the library is not present, a XOR fallback is used (NOT SECURE) to keep flows functional.
     See README.md for enabling real Blowfish.

 Parsing:
   - Conquer Online uses Little Endian ordering.
   - Packet structure:
        [0..1]  Length (ushort) (bytes of the packet header+body, excluding optional ASCII footer)
        [2..3]  Type   (ushort)
        [4..N)  Payload (N = Length)
     Optional ASCII footer (post-Length) for patches > 5017:
        - "TQServer" for server-sourced packets, "TQClient" for client-sourced.
     The handler identifies and ignores any footer beyond the declared header length when parsing.

 Auto-Reconnect Logic:
   - A per-session backoff policy resets cipher state upon consistent decryption or parsing errors.
   - On failure:
       * The session is marked "desynced" and a reconnect delay is scheduled using exponential backoff.
       * During backoff, outbound packets are dropped (empty byte[]), allowing the client to naturally
         re-attempt its connection/handshake and reinitialize cipher state.
   - When a new packet arrives after backoff, the session resets and resumes normal processing.

 Notes:
   - This handler only processes client->server direction (as invoked by the proxy).
   - IsPacketForLoginServer/GameServer remain false in this version (ports are pre-bound by proxy).
   - For real Blowfish, add BouncyCastle.Crypto and see README.md.

*/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// A protocol handler for Conquer Classic (Lords) that supports:
/// - Blowfish encryption hooks on payloads (header is clear-text),
/// - Basic Conquer packet parsing (Little Endian),
/// - Session auto-reconnect/backoff on decrypt/parse failures.
/// </summary>
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

    /// <summary>
    /// Handles a client->server packet:
    /// - Parses Conquer header and ignores optional footer.
    /// - Decrypts payload (if encryption is enabled and Blowfish is available).
    /// - On failures, triggers session auto-reconnect (resets cipher with backoff) and drops packet.
    /// - Re-serializes packet (re-encrypts payload if enabled) for forwarding.
    /// </summary>
    /// <param name="data">Raw packet bytes from client.</param>
    /// <param name="modifiedPacket">Bytes to forward to server (possibly modified/encrypted).</param>
    /// <param name="context">Connection context holding server type/version.</param>
    public void HandlePacket(byte[] data, out byte[] modifiedPacket, ConnectionContext context)
    {
        var session = _sessions.GetValue(context, _ => new SessionState());

        // If we are in backoff window due to previous failures, drop packet to let client retry
        if (session.IsInBackoff())
        {
            modifiedPacket = Array.Empty<byte>();
            return;
        }

        try
        {
            // 1) Basic validation and detection of optional footer beyond declared length
            if (data == null || data.Length < 4)
            {
                // Insufficient data for header; count as a failure
                session.RegisterFailure();
                modifiedPacket = Array.Empty<byte>();
                return;
            }

            ushort declaredLength = BitConverter.ToUInt16(data, 0); // Little Endian
            ushort type = BitConverter.ToUInt16(data, 2);

            if (declaredLength < 4 || declaredLength > data.Length)
            {
                // Malformed declared length
                session.RegisterFailure();
                modifiedPacket = Array.Empty<byte>();
                return;
            }

            // Identify and skip optional footer (outside declared length)
            int packetSize = declaredLength; // Header + payload size according to header
            int footerSize = DetectFooterSize(data, packetSize, data.Length);

            int payloadLength = packetSize - 4;
            var payloadSpan = payloadLength > 0 ? new ReadOnlySpan<byte>(data, 4, payloadLength) : ReadOnlySpan<byte>.Empty;

            // 2) Decrypt payload if session encryption is enabled (or decide to enable it heuristically)
            EnsureCipherInitialized(context, session, isClientToServer: true);

            byte[] decryptedPayload;
            if (session.EncryptionEnabled && payloadLength > 0)
            {
                // Try decrypt; if fails, trigger reconnection/backoff
                decryptedPayload = ProcessPayloadDecrypt(session, payloadSpan);
            }
            else
            {
                decryptedPayload = payloadSpan.ToArray();
            }

            // 3) Parse the packet for validation and optional inspection
            var parsed = ConquerPacket.Parse((ushort)packetSize, type, decryptedPayload);

            // Example minimal usage: we could inspect parsed.Type to branch logic here.

            // 4) Re-serialize: encrypt payload if enabled; header remains clear-text
            byte[] outPayload;
            if (session.EncryptionEnabled && parsed.Payload.Length > 0)
            {
                outPayload = ProcessPayloadEncrypt(session, parsed.Payload);
            }
            else
            {
                outPayload = parsed.Payload;
            }

            // Recompose final packet without footer (proxy does not add or require it)
            modifiedPacket = new byte[4 + outPayload.Length];
            BitConverter.TryWriteBytes(new Span<byte>(modifiedPacket, 0, 2), parsed.Length);
            BitConverter.TryWriteBytes(new Span<byte>(modifiedPacket, 2, 2), parsed.Type);
            Buffer.BlockCopy(outPayload, 0, modifiedPacket, 4, outPayload.Length);

            // Success path: reset failures and keep going
            session.ResetFailures();
        }
        catch
        {
            // Any exception: register failure and apply backoff policy
            session.RegisterFailure();
            modifiedPacket = Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Determines if packet is for Login server. Not used in this proxy variant.
    /// </summary>
    public bool IsPacketForLoginServer(byte[] data) => false;

    /// <summary>
    /// Determines if packet is for Game server. Not used in this proxy variant.
    /// </summary>
    public bool IsPacketForGameServer(byte[] data) => false;

    // ---- Internal helpers ----

    private static int DetectFooterSize(byte[] buffer, int contentLength, int realLength)
    {
        // Footer is outside declared 'Length' at offset 'contentLength'
        int tail = realLength - contentLength;
        if (tail <= 0) return 0;

        // Check for known footers at the tail end
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

        // Heuristic: enable encryption for Game server; keep optional for Login.
        // Adjust as needed per patch/version in ctx.Version.
        session.EncryptionEnabled = string.Equals(ctx.TargetServerType, "Game", StringComparison.OrdinalIgnoreCase);

        // Choose default key depending on direction/endpoint; can be refined after handshake.
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
            // Decrypt failure indicates desync; trigger reconnection/backoff
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

    // ---- Internal models and session/cipher management ----

    private class SessionState
    {
        // Cipher adapter (real Blowfish via BouncyCastle if present; else XOR fallback)
        private ICipherAdapter? _cipher;

        // Failure/backoff management
        private int _consecutiveFailures = 0;
        private DateTime _nextAllowed = DateTime.MinValue;

        public bool EncryptionEnabled { get; set; } = false;
        public bool CipherInitialized { get; private set; } = false;

        public void InitCipher(byte[] key)
        {
            // Attempt to create Blowfish via reflection (BouncyCastle) and fall back if unavailable
            var bf = BlowfishAdapter.TryCreate(key);
            if (bf != null)
            {
                _cipher = bf; // BlowfishAdapter implements ICipherAdapter
            }
            else
            {
                _cipher = new XorFallbackCipher(key);
            }
            CipherInitialized = true;
        }

        public bool IsInBackoff()
        {
            return DateTime.UtcNow < _nextAllowed;
        }

        public void RegisterFailure()
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 2)
            {
                // Apply exponential backoff starting at 250ms
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
            // Reset cipher so that next valid handshake can reinitialize it
            _cipher = null;
            CipherInitialized = false;
            EncryptionEnabled = false; // Will re-enable based on heuristics on next packet
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

    private sealed class ConquerPacket
    {
        public ushort Length { get; }
        public ushort Type { get; }
        public byte[] Payload { get; }

        private ConquerPacket(ushort length, ushort type, byte[] payload)
        {
            Length = length;
            Type = type;
            Payload = payload;
        }

        public static ConquerPacket Parse(ushort declaredLength, ushort type, byte[] payloadBytes)
        {
            if (declaredLength < 4) throw new ArgumentException("Declared length must be >= 4.");
            if (payloadBytes == null) throw new ArgumentNullException(nameof(payloadBytes));
            if (declaredLength != (4 + payloadBytes.Length))
                throw new ArgumentException("Declared length/header does not match payload length.");

            // Example of extracting fields for a known type (e.g., MsgWalk 0x2715) could go here.

            return new ConquerPacket(declaredLength, type, payloadBytes);
        }
    }

    // ---- Cipher abstraction and implementations ----

    private interface ICipherAdapter
    {
        byte[] Encrypt(ReadOnlySpan<byte> input);
        byte[] Decrypt(ReadOnlySpan<byte> input);
    }

    /// <summary>
    /// Blowfish adapter that uses BouncyCastle if available at runtime.
    /// Processes payload in 8-byte blocks (ECB), leaves the final partial block as-is (no padding).
    /// </summary>
    private sealed class BlowfishAdapter : ICipherAdapter
    {
        private readonly object _engine;        // Org.BouncyCastle.Crypto.Engines.BlowfishEngine
        private readonly object _encParams;     // Org.BouncyCastle.Crypto.Parameters.KeyParameter
        private readonly System.Reflection.MethodInfo _initMethod;    // void Init(bool forEncryption, ICipherParameters)
        private readonly System.Reflection.MethodInfo _processBlock;  // int ProcessBlock(byte[] in, int inOff, byte[] out, int outOff)
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

        public byte[] Encrypt(ReadOnlySpan<byte> input)
        {
            return ProcessBlocks(input, encrypt: true);
        }

        public byte[] Decrypt(ReadOnlySpan<byte> input)
        {
            return ProcessBlocks(input, encrypt: false);
        }

        private byte[] ProcessBlocks(ReadOnlySpan<byte> input, bool encrypt)
        {
            int blockSize = 8;
            int fullBlocks = input.Length / blockSize;
            int remainder = input.Length % blockSize;

            byte[] output = new byte[input.Length];

            // Initialize engine
            _initMethod.Invoke(_engine, new object[] { encrypt, _encParams });

            // Process full 8-byte blocks
            for (int i = 0; i < fullBlocks; i++)
            {
                int inOff = i * blockSize;
                int outOff = i * blockSize;

                byte[] inBuf = input.Slice(inOff, blockSize).ToArray();
                // BouncyCastle requires arrays; write to output array
                _processBlock.Invoke(_engine, new object[] { inBuf, 0, output, outOff });
            }

            // Copy remainder as-is (no padding)
            if (remainder > 0)
            {
                input.Slice(fullBlocks * blockSize, remainder).CopyTo(new Span<byte>(output, fullBlocks * blockSize, remainder));
            }

            return output;
        }
    }

    /// <summary>
    /// Non-secure XOR fallback used when Blowfish is not available.
    /// Maintains flow but provides no real confidentiality. DO NOT use in production.
    /// </summary>
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
