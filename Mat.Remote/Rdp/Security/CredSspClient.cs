using System.Security.Cryptography;
using System.Text;

namespace Matgate.Display.Rdp.Security;

// What a caller may want to say beyond the credentials. The defaults are right for a normal connection.
public sealed class CredSspClientOptions
{
    // The name this computer gives itself in the logon; servers write it into their security log.
    public string Workstation { get; init; } = Environment.MachineName;

    // The oldest version of the protocol to accept from a server. Up to version 4 the client seals the
    // server's public key itself, which lets a machine in the middle have it seal data of its own choosing
    // (CVE-2018-0886); from 5 on it is a hash. A patched Windows refuses anything below 5 unless a policy
    // says otherwise; servers that never got their update of May 2018 answer with 2 to 4.
    public int MinimumServerVersion { get; init; } = 2;

    // The 32 random bytes of the public key binding, for tests that need the exchange to repeat.
    public ReadOnlyMemory<byte>? ClientNonce { get; init; }

    // For tests as well: fixed values for the NTLM exchange underneath.
    public NtlmClientOptions? Ntlm { get; init; }
}

// The client side of CredSSP (MS-CSSP), which is what Network Level Authentication is: inside the TLS
// connection and before the RDP connection sequence goes on, the user is authenticated with NTLM, the server
// proves that the certificate of this very TLS connection is its own, and the client hands over the
// credentials for the logon - all of it in TSRequest messages.
//
//   client  TSRequest { NTLM NEGOTIATE }                                   Start()
//   server  TSRequest { NTLM CHALLENGE }
//   client  TSRequest { NTLM AUTHENTICATE, pubKeyAuth, clientNonce }       Next()
//   server  TSRequest { pubKeyAuth }            or { errorCode }, or it just closes the connection
//   client  TSRequest { authInfo = the credentials }                       Next(), IsComplete
//
// The NTLM messages go into negoTokens as they are, without a SPNEGO wrapper - the way FreeRDP 2 did it
// against every version of Windows. Kerberos is not in here.
//
// Each TSRequest has to reach the server in one piece - one Write on the SslStream per message - because
// Windows expects a whole message in a single TLS record (MS-CSSP, product behaviour note on 2.2.1). In the
// other direction MessageLength says how many bytes belong to the message that is arriving.
//
// Not safe for use from several threads at once.
public sealed class CredSspClient : IDisposable
{
    // The version this client speaks; it puts it into every message.
    public const int Version = 6;

    // No message of this exchange is longer, in either direction.
    public const int MaxMessageLength = TsRequest.MaxLength;

    private readonly NtlmClient _ntlm;
    private readonly byte[] _publicKey;
    private readonly byte[] _password;
    private readonly CredSspClientOptions _options;
    private byte[]? _nonce;
    private Step _step = Step.Start;

    // serverHost is the name the connection was made to; serverPublicKey is the SubjectPublicKey of the
    // certificate the server showed in the TLS handshake - what X509Certificate.GetPublicKey() returns.
    // For the forms a user name can have see NtlmClient.
    public CredSspClient(string user, string domain, string password, string serverHost, ReadOnlyMemory<byte> serverPublicKey, CredSspClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrEmpty(serverHost);
        if (serverPublicKey.IsEmpty)
        {
            throw new ArgumentException("The public key of the server's certificate is required.", nameof(serverPublicKey));
        }

        _options = options ?? new CredSspClientOptions();
        if (_options.ClientNonce is { Length: not 32 })
        {
            throw new ArgumentException("The client nonce has 32 bytes.", nameof(options));
        }

        _ntlm = new NtlmClient(user, domain, password, _options.Workstation, "TERMSRV/" + serverHost, _options.Ntlm);
        _publicKey = serverPublicKey.ToArray();

        // Kept as bytes rather than as the string so that it can be wiped when it has been sent.
        _password = Encoding.Unicode.GetBytes(password);
    }

    private enum Step
    {
        Start,
        ChallengeExpected,
        PublicKeyExpected,
        Complete,
        Failed,
    }

    // True when Next has returned the last message. Once that is sent, the RDP connection sequence goes on
    // (after the four bytes of the Early User Authorization Result, if PROTOCOL_HYBRID_EX was negotiated).
    public bool IsComplete => _step == Step.Complete;

    // The version the server answered with, 0 before its first message.
    public int ServerVersion { get; private set; }

    // From version 5 on, what is sealed is SHA-256 over one of these two (with its terminating zero), the
    // client's nonce and the public key.
    private static ReadOnlySpan<byte> ClientToServerMagic => "CredSSP Client-To-Server Binding Hash\0"u8;

    private static ReadOnlySpan<byte> ServerToClientMagic => "CredSSP Server-To-Client Binding Hash\0"u8;

    // The server's version decides how the public key is bound; a version this client does not know yet
    // counts as its own.
    private bool UsesBindingHash => Math.Min(ServerVersion, Version) >= 5;

    // How many bytes the TSRequest starting with these bytes has in total, or 0 when more of its beginning is
    // needed to tell. Throws CredSspException when the bytes cannot be the start of a TSRequest.
    public static int MessageLength(ReadOnlySpan<byte> start) => TsRequest.MessageLength(start);

    // The first message to send.
    public byte[] Start()
    {
        if (_step != Step.Start)
        {
            throw new InvalidOperationException("The exchange has been started already.");
        }

        var request = new TsRequest { Version = Version, NegoToken = _ntlm.CreateNegotiate() };
        _step = Step.ChallengeExpected;
        return request.Encode();
    }

    // Takes one complete message of the server and returns the next one to send; after the last one
    // IsComplete is true. With NTLM the client always has the last word, so the result is never null - the
    // type leaves room for an exchange that ends with a message of the server.
    // Throws CredSspException when the server refuses the logon, when it cannot prove that it is the server
    // of this TLS connection, or when what it sent makes no sense.
    public byte[]? Next(ReadOnlySpan<byte> serverTsRequest)
    {
        if (_step is not (Step.ChallengeExpected or Step.PublicKeyExpected))
        {
            throw new InvalidOperationException("The exchange is not waiting for a message of the server.");
        }

        try
        {
            var request = TsRequest.Decode(serverTsRequest);
            if (request.ErrorCode is { } status && status != 0)
            {
                throw FromStatus(status, logonSent: _step == Step.PublicKeyExpected);
            }

            return _step == Step.ChallengeExpected ? AnswerChallenge(request) : SendCredentials(request);
        }
        catch (CredSspException)
        {
            Fail();
            throw;
        }
        catch (NtlmException exception)
        {
            Fail();
            throw new CredSspException(exception.Message, innerException: exception);
        }
    }

    // For the caller whose connection ended instead of delivering the server's next message: the exception
    // that says what that means at this point. Servers too old to send a status (before Windows 8.1) simply
    // close the connection when they refuse the user name or the password.
    public CredSspException ConnectionClosed(Exception? cause = null)
    {
        if (_step == Step.Complete)
        {
            return new CredSspException("The server closed the connection after Network Level Authentication had succeeded.", innerException: cause);
        }

        var waitingForVerdict = _step == Step.PublicKeyExpected;
        Fail();
        return waitingForVerdict
            ? new CredSspException("The server closed the connection when it received the logon. It does that when the user name or the password is wrong.", isLogonFailure: true, innerException: cause)
            : new CredSspException("The server closed the connection during Network Level Authentication.", innerException: cause);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_password);
        _ntlm.Dispose();
    }

    private byte[] AnswerChallenge(TsRequest request)
    {
        if (request.NegoToken is null)
        {
            throw new CredSspException("The server did not answer with an NTLM challenge.");
        }

        // Versions below 2 never left Microsoft.
        if (request.Version < Math.Max(2, _options.MinimumServerVersion))
        {
            throw new CredSspException($"The server only speaks version {request.Version} of CredSSP, which is older than this connection accepts.");
        }

        ServerVersion = request.Version;
        var authenticate = _ntlm.CreateAuthenticate(request.NegoToken);

        byte[] publicKeyAuth;
        if (UsesBindingHash)
        {
            _nonce = _options.ClientNonce?.ToArray() ?? RandomNumberGenerator.GetBytes(32);
            publicKeyAuth = _ntlm.Encrypt(BindingHash(ClientToServerMagic));
        }
        else
        {
            publicKeyAuth = _ntlm.Encrypt(_publicKey);
        }

        _step = Step.PublicKeyExpected;
        return new TsRequest { Version = Version, NegoToken = authenticate, PubKeyAuth = publicKeyAuth, ClientNonce = _nonce }.Encode();
    }

    private byte[] SendCredentials(TsRequest request)
    {
        if (request.PubKeyAuth is null)
        {
            // Neither the confirmation nor a status: the only thing a server can mean by that is no.
            throw new CredSspException("The server did not accept the logon. It gave no reason; usually the user name or the password is wrong.", isLogonFailure: true);
        }

        byte[] answer;
        try
        {
            answer = _ntlm.Decrypt(request.PubKeyAuth);
        }
        catch (NtlmException exception)
        {
            // Only somebody who knows the password can produce this signature - that is what makes it a proof.
            throw new CredSspException("The server's answer is not signed with the key that follows from the password. This is not the server that knows this account, or somebody sits in between.", innerException: exception);
        }

        var expected = UsesBindingHash ? BindingHash(ServerToClientMagic) : IncrementedPublicKey();
        if (!CryptographicOperations.FixedTimeEquals(answer, expected))
        {
            throw new CredSspException("The server could not confirm that the certificate of this connection is its own. Somebody may sit in between.");
        }

        // Only now, with the server proven, do the credentials leave.
        var credentials = TsRequest.EncodePasswordCredentials(_ntlm.Domain, _ntlm.User, _password);
        try
        {
            var message = new TsRequest { Version = Version, AuthInfo = _ntlm.Encrypt(credentials) }.Encode();
            _step = Step.Complete;
            return message;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentials);
            CryptographicOperations.ZeroMemory(_password);
        }
    }

    private byte[] BindingHash(ReadOnlySpan<byte> magic)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(magic);
        hash.AppendData(_nonce!);
        hash.AppendData(_publicKey);
        return hash.GetHashAndReset();
    }

    // What a server up to version 4 sends back: the public key with one added to its first byte, so that the
    // client's own message cannot simply be played back to it. Taken as a little-endian number, the way other
    // implementations do; an RSA or elliptic-curve key never starts with 0xFF, so nothing is carried over.
    private byte[] IncrementedPublicKey()
    {
        var key = (byte[])_publicKey.Clone();
        for (var i = 0; i < key.Length; i++)
        {
            if (++key[i] != 0)
            {
                break;
            }
        }

        return key;
    }

    private void Fail()
    {
        _step = Step.Failed;
        CryptographicOperations.ZeroMemory(_password);
    }

    // The NTSTATUS values a server gives for a logon it refuses (MS-ERREF 2.3.1), as sentences. Asking for
    // the password again is worth it for the first three, and for a status this table does not know: the
    // server has looked at the logon and said no, and servers that are not Windows say it with codes of
    // their own. Before the logon was sent, nothing is a logon failure.
    private static CredSspException FromStatus(uint status, bool logonSent)
    {
        var (message, isLogonFailure) = status switch
        {
            0xC000006D => ("The user name or the password is wrong.", true),
            0xC000006A => ("The password is wrong.", true),
            0xC0000064 => ("The server does not know this user.", true),
            0xC0000071 => ("The password has expired. It has to be changed before this user can log on.", false),
            0xC0000224 => ("The password has to be changed before this user can log on.", false),
            0xC0000234 => ("The account is locked out.", false),
            0xC0000072 => ("The account is disabled.", false),
            0xC0000193 => ("The account has expired.", false),
            0xC000015B => ("This user has not been granted the right to log on to this computer this way.", false),
            0xC000006E => ("A restriction on the account prevents the logon - an empty password, for example.", false),
            0xC000006F => ("The account is not allowed to log on at this time of day.", false),
            0xC0000070 => ("The account is not allowed to log on from this computer.", false),
            0xC000005E => ("The server cannot reach a domain controller to check the logon.", false),
            0xC0000022 => ("The server denied access.", false),
            0xC00000BB or 0xC0000418 => ("The server does not accept this logon: it rejects NTLM or this version of CredSSP.", false),
            _ when logonSent => ($"The server refused the logon (status 0x{status:X8}). Usually the user name or the password is wrong.", true),
            _ => ($"The server refused Network Level Authentication (status 0x{status:X8}).", false),
        };

        return new CredSspException(message, isLogonFailure && logonSent, status);
    }
}
