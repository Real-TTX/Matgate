using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Matgate.Display.Rdp.Security;

// What the examples of MS-NLMP 4.2 have to pin down. Nobody else needs to set any of it.
public sealed class NtlmClientOptions
{
    // The client's half of the challenge in the NTLMv2 response, 8 bytes. Random unless given.
    public ReadOnlyMemory<byte>? ClientChallenge { get; init; }

    // The session key the client makes up for the key exchange, 16 bytes. Random unless given.
    public ReadOnlyMemory<byte>? ExportedSessionKey { get; init; }

    // Where the time in the response comes from when the server's challenge carries none of its own.
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    // The VERSION structure: the Windows this client says it is (here 11 24H2). Servers use it for their logs only.
    public (byte Major, byte Minor, ushort Build) OperatingSystemVersion { get; init; } = (10, 0, 26100);

    // Whether the response names the service the client wants to reach (MsvAvTargetName) and carries channel
    // bindings (MsvAvChannelBindings, all zero: none), as every Windows since 7 does. The example in the
    // specification is from a client older than that, which is the one reason to switch it off.
    public bool ExtendedProtection { get; init; } = true;
}

// The client side of NTLMv2 (MS-NLMP, connection-oriented) with signing and sealing: NEGOTIATE, then the answer
// to the server's CHALLENGE, then messages protected with the session key both sides now have. NTLMv1 and LM
// are left out on purpose: every Windows that can do Network Level Authentication does NTLMv2.
//
// How a user name goes into the messages, the way Windows does it:
//   user          with the domain that was given separately, or with none, which leaves it to the server
//                 where it looks for the account.
//   DOMAIN\user   the part before the backslash is the domain, the rest the user - unless a domain was given
//                 separately, which wins, and the name stays as it is.
//   user@domain   a principal name. It is sent whole as the user with an empty domain; splitting it would
//                 turn the DNS name behind the @ into a NetBIOS domain, which it is not.
// The key that proves the password is HMAC_MD5(MD4(password), UPPERCASE(user) + domain): only the user is
// folded to upper case, the domain goes in as it was typed, and both go into the message the way they were
// typed too. The server computes the same from the names in the message, so these must be the same strings.
//
// Not safe for use from several threads at once.
public sealed class NtlmClient : IDisposable
{
    private const int MaxNameLength = 1024;

    // What a current Windows offers for CredSSP, minus the two flags that only matter when Unicode or extended
    // session security are missing (OEM, LM_KEY).
    private const NtlmFlags Requested =
        NtlmFlags.Key56 | NtlmFlags.KeyExchange | NtlmFlags.Key128 | NtlmFlags.Version |
        NtlmFlags.ExtendedSessionSecurity | NtlmFlags.AlwaysSign | NtlmFlags.Ntlm |
        NtlmFlags.Seal | NtlmFlags.Sign | NtlmFlags.RequestTarget | NtlmFlags.Unicode;

    // Without these there is no NTLMv2 session worth having. Windows has refused anything below 128 bits
    // since 7, and a server has to grant signing and sealing when the client asks.
    private const NtlmFlags Required =
        NtlmFlags.Unicode | NtlmFlags.ExtendedSessionSecurity | NtlmFlags.Key128 | NtlmFlags.Sign | NtlmFlags.Seal;

    private readonly string _workstation;
    private readonly string _targetName;
    private readonly NtlmClientOptions _options;
    private byte[]? _responseKey;
    private byte[]? _negotiate;
    private NtlmSession? _session;
    private bool _disposed;

    // targetName is the service principal name of the server, for a remote desktop "TERMSRV/<host>".
    public NtlmClient(string user, string domain, string password, string workstation, string targetName, NtlmClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(workstation);
        ArgumentNullException.ThrowIfNull(targetName);

        (User, Domain) = SplitUserName(user, domain);
        if (User.Length == 0)
        {
            throw new ArgumentException("A user name is required.", nameof(user));
        }

        // Every one of them is written with a 16-bit length; nothing sensible comes near this limit.
        if (User.Length > MaxNameLength || Domain.Length > MaxNameLength || workstation.Length > MaxNameLength || targetName.Length > MaxNameLength)
        {
            throw new ArgumentException("User, domain, workstation and target name are limited to 1024 characters each.");
        }

        _options = options ?? new NtlmClientOptions();
        if (_options.ClientChallenge is { Length: not 8 })
        {
            throw new ArgumentException("The client challenge has 8 bytes.", nameof(options));
        }

        if (_options.ExportedSessionKey is { Length: not 16 })
        {
            throw new ArgumentException("The exported session key has 16 bytes.", nameof(options));
        }

        _workstation = workstation;
        _targetName = targetName;

        // The password itself is not kept: this key is all the exchange needs from it.
        _responseKey = NtOwfV2(password, User, Domain);
    }

    // User and domain as they are sent to the server.
    public string User { get; }

    public string Domain { get; }

    // True once the answer to the challenge exists: from then on Encrypt and Decrypt work.
    public bool HasSession => _session is not null;

    internal static ReadOnlySpan<byte> Signature => "NTLMSSP\0"u8;

    // See the comment on the class for the three forms of a user name.
    public static (string User, string Domain) SplitUserName(string user, string? domain)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!string.IsNullOrEmpty(domain))
        {
            return (user, domain);
        }

        var backslash = user.IndexOf('\\');
        return backslash > 0
            ? (user[(backslash + 1)..], user[..backslash])
            : (user, "");
    }

    // The first message: what this client can do.
    public byte[] CreateNegotiate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_negotiate is not null)
        {
            throw new InvalidOperationException("The NEGOTIATE message has been created already.");
        }

        // No domain and no workstation name here (a client that sends a version leaves both empty); the two
        // fields then point to where the names would start.
        var message = new byte[40];
        Signature.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), (uint)Requested);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), (uint)message.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(28), (uint)message.Length);
        WriteVersion(message.AsSpan(32));

        // The integrity code of the last message covers this one, so what was sent must stay as it is.
        _negotiate = message;
        return (byte[])message.Clone();
    }

    // The third message, in answer to the server's CHALLENGE. Afterwards the session keys exist.
    public byte[] CreateAuthenticate(ReadOnlySpan<byte> challengeMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_negotiate is null || _responseKey is null)
        {
            throw new InvalidOperationException("The NEGOTIATE message comes first, and the challenge is answered once.");
        }

        var challenge = NtlmChallenge.Parse(challengeMessage);
        var missing = Required & ~challenge.Flags;
        if (missing != NtlmFlags.None)
        {
            throw new NtlmException($"The server does not grant what this client needs from NTLM ({missing}).");
        }

        // MS-NLMP 3.1.5.1.2: an NTLMv2 client that signs or seals refuses a challenge that does not name its server.
        if (challenge.Find(NtlmAvId.NetBiosComputerName) is null || challenge.Find(NtlmAvId.NetBiosDomainName) is null)
        {
            throw new NtlmException("The server's NTLM challenge does not name the server.");
        }

        var serverTime = challenge.Find(NtlmAvId.Timestamp);
        if (serverTime is { Length: not 8 })
        {
            throw new NtlmException("The time in the server's NTLM challenge is malformed.");
        }

        // A server that sends its time gets it back, an integrity code over all three messages, and no LM
        // response. One that does not (before Vista) gets the client's time and the LMv2 response instead.
        var withIntegrityCode = serverTime is not null;
        var targetInfo = BuildTargetInfo(challenge, withIntegrityCode);

        // "temp" of MS-NLMP 3.3.2, the NTLMv2_CLIENT_CHALLENGE: versions, time, client challenge, AV pairs.
        var temp = new byte[28 + targetInfo.Length + 4];
        if (16 + temp.Length > ushort.MaxValue)
        {
            throw new NtlmException("The server's NTLM challenge carries more target information than a response can hold.");
        }

        var clientChallenge = _options.ClientChallenge?.ToArray() ?? RandomNumberGenerator.GetBytes(8);
        temp[0] = 1;
        temp[1] = 1;
        if (serverTime is not null)
        {
            serverTime.CopyTo(temp, 8);
        }
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(temp.AsSpan(8), _options.TimeProvider.GetUtcNow().ToFileTime());
        }

        clientChallenge.CopyTo(temp, 16);
        targetInfo.CopyTo(temp, 28);

        var proof = NtProof(_responseKey, challenge.ServerChallenge, temp);
        byte[] ntResponse = [.. proof, .. temp];
        var lmResponse = withIntegrityCode ? new byte[24] : LmV2Response(_responseKey, challenge.ServerChallenge, clientChallenge);
        var sessionBaseKey = SessionBaseKey(_responseKey, proof);

        var negotiated = NegotiatedFlags(challenge.Flags);
        byte[] exportedSessionKey;
        byte[] encryptedSessionKey;
        if (negotiated.HasFlag(NtlmFlags.KeyExchange))
        {
            exportedSessionKey = _options.ExportedSessionKey?.ToArray() ?? RandomNumberGenerator.GetBytes(16);
            using var cipher = new Rc4(sessionBaseKey);
            encryptedSessionKey = cipher.Process(exportedSessionKey);
        }
        else
        {
            exportedSessionKey = (byte[])sessionBaseKey.Clone();
            encryptedSessionKey = [];
        }

        var domain = Encoding.Unicode.GetBytes(Domain);
        var user = Encoding.Unicode.GetBytes(User);
        var workstation = Encoding.Unicode.GetBytes(_workstation);

        // The fixed part ends after the version; the integrity code follows it only when there is one.
        var headerLength = 72 + (withIntegrityCode ? 16 : 0);
        var message = new byte[headerLength + domain.Length + user.Length + workstation.Length + lmResponse.Length + ntResponse.Length + encryptedSessionKey.Length];
        Signature.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 3);
        var position = headerLength;
        position = WritePayload(message, 28, domain, position);
        position = WritePayload(message, 36, user, position);
        position = WritePayload(message, 44, workstation, position);
        position = WritePayload(message, 12, lmResponse, position);
        position = WritePayload(message, 20, ntResponse, position);
        WritePayload(message, 52, encryptedSessionKey, position);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(60), (uint)negotiated);
        WriteVersion(message.AsSpan(64));

        if (withIntegrityCode)
        {
            // Over the three messages as they go over the wire, this one with the code's own field still zero.
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, exportedSessionKey);
            hmac.AppendData(_negotiate);
            hmac.AppendData(challengeMessage);
            hmac.AppendData(message);
            hmac.GetHashAndReset(message.AsSpan(72, 16));
        }

        _session = new NtlmSession(exportedSessionKey, negotiated);

        CryptographicOperations.ZeroMemory(_responseKey);
        CryptographicOperations.ZeroMemory(sessionBaseKey);
        CryptographicOperations.ZeroMemory(exportedSessionKey);
        _responseKey = null;
        return message;
    }

    // Like SSPI's EncryptMessage for NTLM: the 16 bytes of signature, then the sealed message.
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => Session().Encrypt(plaintext);

    // Like SSPI's DecryptMessage: takes signature plus sealed message of the server, checks the signature and
    // returns the plain message. Throws NtlmException when the signature does not match.
    public byte[] Decrypt(ReadOnlySpan<byte> message) => Session().Decrypt(message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_responseKey is not null)
        {
            CryptographicOperations.ZeroMemory(_responseKey);
            _responseKey = null;
        }

        _session?.Dispose();
    }

    // NTOWFv2 of MS-NLMP 3.3.2; LMOWFv2 is the same value.
    internal static byte[] NtOwfV2(string password, string user, string domain)
    {
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        Span<byte> passwordHash = stackalloc byte[Md4.HashSize];
        try
        {
            Md4.HashData(passwordBytes, passwordHash);
            return HMACMD5.HashData(passwordHash, Encoding.Unicode.GetBytes(user.ToUpperInvariant() + domain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(passwordHash);
        }
    }

    // NTProofStr: what proves the password, over the server's challenge and everything the client adds.
    internal static byte[] NtProof(ReadOnlySpan<byte> responseKey, ReadOnlySpan<byte> serverChallenge, ReadOnlySpan<byte> temp) =>
        Hmac(responseKey, serverChallenge, temp);

    internal static byte[] LmV2Response(ReadOnlySpan<byte> responseKey, ReadOnlySpan<byte> serverChallenge, ReadOnlySpan<byte> clientChallenge) =>
        [.. Hmac(responseKey, serverChallenge, clientChallenge), .. clientChallenge];

    // With NTLMv2 this is the key exchange key as well.
    internal static byte[] SessionBaseKey(ReadOnlySpan<byte> responseKey, ReadOnlySpan<byte> proof) =>
        HMACMD5.HashData(responseKey, proof);

    private static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, key);
        hmac.AppendData(first);
        hmac.AppendData(second);
        return hmac.GetHashAndReset();
    }

    // What goes into the flags of the last message and decides the session keys: of the abilities both sides
    // have to share, those the server granted; what only the client says about itself stays as it was asked.
    private static NtlmFlags NegotiatedFlags(NtlmFlags granted)
    {
        const NtlmFlags shared =
            NtlmFlags.Key56 | NtlmFlags.KeyExchange | NtlmFlags.Key128 | NtlmFlags.ExtendedSessionSecurity |
            NtlmFlags.AlwaysSign | NtlmFlags.Ntlm | NtlmFlags.Seal | NtlmFlags.Sign | NtlmFlags.Unicode;
        return (Requested & granted & shared)
            | (Requested & (NtlmFlags.RequestTarget | NtlmFlags.Version))
            | (granted & NtlmFlags.TargetInfo);
    }

    // The AV pairs of the response: the server's own, in its order, and behind them what the client adds -
    // that an integrity code is there, the channel bindings and the name of the service it means to reach.
    private byte[] BuildTargetInfo(NtlmChallenge challenge, bool withIntegrityCode)
    {
        const uint integrityCodePresent = 0x00000002;
        var pairs = new List<(NtlmAvId Id, byte[] Value)>();
        var flagsSeen = false;
        foreach (var (id, value) in challenge.TargetInfo)
        {
            if (_options.ExtendedProtection && id is NtlmAvId.ChannelBindings or NtlmAvId.TargetName)
            {
                // These two are the client's to say; a server that sent them would only make them ambiguous.
                continue;
            }

            if (id == NtlmAvId.Flags && withIntegrityCode)
            {
                if (value.Length != 4)
                {
                    throw new NtlmException("The flags in the server's NTLM challenge are malformed.");
                }

                var flags = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(flags, BinaryPrimitives.ReadUInt32LittleEndian(value) | integrityCodePresent);
                pairs.Add((id, flags));
                flagsSeen = true;
                continue;
            }

            pairs.Add((id, value));
        }

        if (withIntegrityCode && !flagsSeen)
        {
            var flags = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(flags, integrityCodePresent);
            pairs.Add((NtlmAvId.Flags, flags));
        }

        if (_options.ExtendedProtection)
        {
            pairs.Add((NtlmAvId.ChannelBindings, new byte[16]));
            pairs.Add((NtlmAvId.TargetName, Encoding.Unicode.GetBytes(_targetName)));
        }

        var length = 4;
        foreach (var (_, value) in pairs)
        {
            length += 4 + value.Length;
        }

        var result = new byte[length];
        var position = 0;
        foreach (var (id, value) in pairs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(position), (ushort)id);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(position + 2), (ushort)value.Length);
            value.CopyTo(result, position + 4);
            position += 4 + value.Length;
        }

        // The four bytes left over are MsvAvEOL, which is all zero.
        return result;
    }

    // One of the length/offset fields of a message, and the bytes it points to.
    private static int WritePayload(byte[] message, int field, byte[] data, int position)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(field), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(field + 2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(field + 4), (uint)position);
        data.CopyTo(message, position);
        return position + data.Length;
    }

    private void WriteVersion(Span<byte> destination)
    {
        const byte ntlmRevision = 0x0F;
        var (major, minor, build) = _options.OperatingSystemVersion;
        destination[0] = major;
        destination[1] = minor;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], build);
        destination[7] = ntlmRevision;
    }

    private NtlmSession Session()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _session ?? throw new InvalidOperationException("There is no NTLM session before the challenge has been answered.");
    }
}
