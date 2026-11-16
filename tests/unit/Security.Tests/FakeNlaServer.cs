using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace Matgate.Display.Rdp.Security.Tests;

// The other end of the exchange, for tests without a network: an NTLM server that knows one account, and the
// CredSSP server around it. It takes the client's messages apart and builds its own with code written for
// this purpose, following the server's rules of MS-NLMP 3.2.5 and MS-CSSP 3.1.5; what it shares with the
// client are the primitives (MD4, RC4, the key derivation), which the examples of the specification pin down.
//
// It proves that the client is consistent with a second reading of the specification by the same author.
// That a real server agrees is what NlaServerTests is for.
internal sealed class FakeNlaServer
{
    // The time the server puts into its challenge - not the time of the test run, so that a client which
    // takes its own clock instead is caught.
    public static readonly DateTimeOffset Time = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] ServerChallenge = Hex.Bytes("0123456789abcdef");
    private readonly List<byte[]> _ntlmMessages = [];
    private NtlmSession? _session;
    private int _step;

    public string User { get; init; } = "tester";

    public string Password { get; init; } = "secret";

    // The SubjectPublicKey of the certificate the server showed. Any bytes do for the exchange.
    public byte[] PublicKey { get; init; } = Hex.Bytes("3082010a0282010100c1d2e3f4a5b60718293a4b5c6d7e8f900203010001");

    public int Version { get; init; } = 6;

    // Servers before Vista sent no time; then the client must not send an integrity code either.
    public bool SendsTime { get; init; } = true;

    public NtlmFlags WithheldFlags { get; init; }

    // Instead of confirming the public key, answer the logon with this status.
    public uint? RefuseWith { get; init; }

    // Gets what a correct server seals as its confirmation of the public key and returns what this one seals
    // instead - a server that is not the one the certificate belongs to cannot produce the right answer.
    public Func<byte[], byte[]>? ForgeConfirmation { get; init; }

    public byte[]? ClientNonce { get; private set; }

    public bool ClientSentIntegrityCode { get; private set; }

    public byte[]? ClientLmResponse { get; private set; }

    public NtlmFlags ClientFlags { get; private set; }

    public string? ClientWorkstation { get; private set; }

    // The AV pairs of the client's response, in the order it sent them.
    public List<(NtlmAvId Id, byte[] Value)> ClientAvPairs { get; } = [];

    // What the client sent in the NTLM message: the names the response key was computed from.
    public (string Domain, string User)? AuthenticatedAs { get; private set; }

    // What arrived in TSCredentials at the end.
    public (string Domain, string User, string Password)? DelegatedCredentials { get; private set; }

    public byte[] Challenge { get; private set; } = [];

    // Takes a message of the client and returns the server's answer, or null after the last one.
    public byte[]? Answer(byte[] clientMessage)
    {
        var request = TsRequest.Decode(clientMessage);
        if (request.Version != 6)
        {
            throw new InvalidOperationException($"The client sent version {request.Version}.");
        }

        switch (_step++)
        {
            case 0:
                return new TsRequest { Version = Version, NegoToken = CreateChallenge(request.NegoToken ?? throw new InvalidOperationException("No NEGOTIATE message.")) }.Encode();

            case 1:
                if (!Authenticate(request.NegoToken ?? throw new InvalidOperationException("No AUTHENTICATE message.")))
                {
                    return new TsRequest { Version = Version, ErrorCode = 0xC000006D }.Encode();
                }

                if (RefuseWith is { } status)
                {
                    return new TsRequest { Version = Version, ErrorCode = status }.Encode();
                }

                ClientNonce = request.ClientNonce;
                return new TsRequest { Version = Version, PubKeyAuth = ConfirmPublicKey(request) }.Encode();

            case 2:
                if (request.NegoToken is not null || request.PubKeyAuth is not null)
                {
                    throw new InvalidOperationException("The last message carries more than the credentials.");
                }

                DelegatedCredentials = ReadCredentials(_session!.Decrypt(request.AuthInfo ?? throw new InvalidOperationException("No authInfo.")));
                return null;

            default:
                throw new InvalidOperationException("The exchange is over.");
        }
    }

    // The server's half of the session, for tests that want to play further tricks.
    public NtlmSession Session => _session ?? throw new InvalidOperationException("No session yet.");

    public byte[]? ClientAvPair(NtlmAvId id) => ClientAvPairs.Where(pair => pair.Id == id).Select(pair => pair.Value).FirstOrDefault();

    // The NTLM server on its own: the CHALLENGE in answer to the client's NEGOTIATE.
    public byte[] CreateChallenge(byte[] negotiate)
    {
        _ntlmMessages.Add(negotiate);
        Challenge = BuildChallenge(negotiate);
        _ntlmMessages.Add(Challenge);
        return Challenge;
    }

    private byte[] BuildChallenge(byte[] negotiate)
    {
        if (!negotiate.AsSpan(0, 8).SequenceEqual("NTLMSSP\0"u8) || BinaryPrimitives.ReadUInt32LittleEndian(negotiate.AsSpan(8)) != 1)
        {
            throw new InvalidOperationException("Not a NEGOTIATE message.");
        }

        // A server grants what it was asked for and adds what it has to say about itself.
        var requested = (NtlmFlags)BinaryPrimitives.ReadUInt32LittleEndian(negotiate.AsSpan(12));
        var flags = (requested | NtlmFlags.TargetInfo | NtlmFlags.TargetTypeServer) & ~WithheldFlags;

        var targetName = Encoding.Unicode.GetBytes("FAKESERVER");
        var targetInfo = new MemoryStream();
        WritePair(targetInfo, NtlmAvId.NetBiosDomainName, Encoding.Unicode.GetBytes("FAKESERVER"));
        WritePair(targetInfo, NtlmAvId.NetBiosComputerName, Encoding.Unicode.GetBytes("FAKESERVER"));
        WritePair(targetInfo, NtlmAvId.DnsDomainName, Encoding.Unicode.GetBytes("fakeserver.test"));
        WritePair(targetInfo, NtlmAvId.DnsComputerName, Encoding.Unicode.GetBytes("fakeserver.test"));
        if (SendsTime)
        {
            var time = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(time, Time.ToFileTime());
            WritePair(targetInfo, NtlmAvId.Timestamp, time);
        }

        WritePair(targetInfo, NtlmAvId.EndOfList, []);
        var info = targetInfo.ToArray();

        var message = new byte[56 + targetName.Length + info.Length];
        "NTLMSSP\0"u8.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 2);
        WriteField(message, 12, targetName, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), (uint)flags);
        ServerChallenge.CopyTo(message, 24);
        WriteField(message, 40, info, 56 + targetName.Length);
        Hex.Bytes("0a00f4650000000f").CopyTo(message, 48);
        return message;
    }

    // The NTLM server on its own: checks the client's AUTHENTICATE message. False when the user is unknown or
    // the password wrong; an exception when the message is not what a correct client sends.
    public bool Authenticate(byte[] message)
    {
        if (!message.AsSpan(0, 8).SequenceEqual("NTLMSSP\0"u8) || BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8)) != 3)
        {
            throw new InvalidOperationException("Not an AUTHENTICATE message.");
        }

        ClientLmResponse = ReadField(message, 12);
        var ntResponse = ReadField(message, 20);
        var domain = Encoding.Unicode.GetString(ReadField(message, 28));
        var user = Encoding.Unicode.GetString(ReadField(message, 36));
        ClientWorkstation = Encoding.Unicode.GetString(ReadField(message, 44));
        var encryptedSessionKey = ReadField(message, 52);
        ClientFlags = (NtlmFlags)BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(60));
        AuthenticatedAs = (domain, user);

        // An unknown user: the server has no key to check anything with.
        if (!string.Equals(user, User, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // NTLMv2_RESPONSE: the proof, then the client's challenge structure with the AV pairs from offset 28 on.
        var proof = ntResponse.AsSpan(0, 16);
        var temp = ntResponse.AsSpan(16);
        var responseKey = NtlmClient.NtOwfV2(Password, user, domain);
        if (!NtlmClient.NtProof(responseKey, ServerChallenge, temp).AsSpan().SequenceEqual(proof))
        {
            return false;
        }

        ReadPairs(temp[28..^4]);
        var sessionBaseKey = NtlmClient.SessionBaseKey(responseKey, proof);
        byte[] exportedSessionKey;
        if (ClientFlags.HasFlag(NtlmFlags.KeyExchange))
        {
            using var cipher = new Rc4(sessionBaseKey);
            exportedSessionKey = cipher.Process(encryptedSessionKey);
        }
        else
        {
            exportedSessionKey = sessionBaseKey;
        }

        ClientSentIntegrityCode = ClientAvPair(NtlmAvId.Flags) is { } avFlags && (BinaryPrimitives.ReadUInt32LittleEndian(avFlags) & 2) != 0;
        if (ClientSentIntegrityCode)
        {
            // Over all three messages, the last one with the field of the code itself set to zero.
            var sent = message.AsSpan(72, 16).ToArray();
            var cleared = (byte[])message.Clone();
            cleared.AsSpan(72, 16).Clear();
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, exportedSessionKey);
            hmac.AppendData(_ntlmMessages[0]);
            hmac.AppendData(_ntlmMessages[1]);
            hmac.AppendData(cleared);
            if (!hmac.GetHashAndReset().AsSpan().SequenceEqual(sent))
            {
                throw new InvalidOperationException("The integrity code of the AUTHENTICATE message is wrong.");
            }
        }

        _session = new NtlmSession(exportedSessionKey, ClientFlags, asServer: true);
        return true;
    }

    // Step 4 of MS-CSSP 3.1.5: check that the client bound itself to the key of this server's certificate,
    // and prove in return that this server holds the session key.
    private byte[] ConfirmPublicKey(TsRequest request)
    {
        var sent = _session!.Decrypt(request.PubKeyAuth ?? throw new InvalidOperationException("No pubKeyAuth."));
        byte[] confirmation;
        if (Version >= 5)
        {
            var nonce = request.ClientNonce ?? throw new InvalidOperationException("No client nonce.");
            if (nonce.Length != 32)
            {
                throw new InvalidOperationException("The client nonce has not 32 bytes.");
            }

            if (!sent.AsSpan().SequenceEqual(BindingHash("CredSSP Client-To-Server Binding Hash\0"u8, nonce, PublicKey)))
            {
                throw new InvalidOperationException("The client did not seal the hash over this server's public key.");
            }

            confirmation = BindingHash("CredSSP Server-To-Client Binding Hash\0"u8, nonce, PublicKey);
        }
        else
        {
            if (request.ClientNonce is not null)
            {
                throw new InvalidOperationException("A client nonce in the old version of the exchange.");
            }

            if (!sent.AsSpan().SequenceEqual(PublicKey))
            {
                throw new InvalidOperationException("The client did not seal this server's public key.");
            }

            confirmation = (byte[])PublicKey.Clone();
            confirmation[0]++;
        }

        return _session.Encrypt(ForgeConfirmation?.Invoke(confirmation) ?? confirmation);
    }

    // SHA-256 over the magic string with its terminating zero, the client's nonce and the public key.
    private static byte[] BindingHash(ReadOnlySpan<byte> magic, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> publicKey) =>
        SHA256.HashData([.. magic, .. nonce, .. publicKey]);

    private static (string Domain, string User, string Password) ReadCredentials(byte[] credentials)
    {
        var reader = new AsnReader(credentials, AsnEncodingRules.DER);
        var outer = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        var type = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadInteger();
        if (type != 1)
        {
            throw new InvalidOperationException($"Credentials of type {type}.");
        }

        var inner = new AsnReader(outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadOctetString(), AsnEncodingRules.DER);
        outer.ThrowIfNotEmpty();
        var password = inner.ReadSequence();
        inner.ThrowIfNotEmpty();
        var domain = Encoding.Unicode.GetString(password.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString());
        var user = Encoding.Unicode.GetString(password.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadOctetString());
        var secret = Encoding.Unicode.GetString(password.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2)).ReadOctetString());
        password.ThrowIfNotEmpty();
        return (domain, user, secret);
    }

    private void ReadPairs(ReadOnlySpan<byte> pairs)
    {
        while (true)
        {
            var id = (NtlmAvId)BinaryPrimitives.ReadUInt16LittleEndian(pairs);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(pairs[2..]);
            if (id == NtlmAvId.EndOfList)
            {
                if (pairs.Length != 4)
                {
                    throw new InvalidOperationException("Bytes behind the end of the AV pairs.");
                }

                return;
            }

            ClientAvPairs.Add((id, pairs.Slice(4, length).ToArray()));
            pairs = pairs[(4 + length)..];
        }
    }

    private static byte[] ReadField(byte[] message, int field)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(field));
        var maximum = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(field + 2));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(field + 4));
        if (maximum != length)
        {
            throw new InvalidOperationException("A field whose two lengths differ.");
        }

        return message.AsSpan(offset, length).ToArray();
    }

    private static void WriteField(byte[] message, int field, byte[] data, int offset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(field), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(field + 2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(field + 4), (uint)offset);
        data.CopyTo(message, offset);
    }

    private static void WritePair(Stream stream, NtlmAvId id, byte[] value)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)id);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)value.Length);
        stream.Write(header);
        stream.Write(value);
    }
}
