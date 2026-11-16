using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Matgate.Display.Rdp.Security.Tests;

// What a sign-in against a real server came to.
internal sealed record NlaOutcome(uint SelectedProtocol, int ServerVersion, bool Accepted, string Evidence, byte[]? CredentialsDigest);

internal sealed class NlaTestOptions
{
    public uint RequestedProtocols { get; init; } = NlaTestConnection.ProtocolHybridEx | NlaTestConnection.ProtocolHybrid | NlaTestConnection.ProtocolSsl;

    // The "Cookie: mstshash=" of the connection request. Real servers ignore it; the pyspnego server takes
    // its variant from it.
    public string? Cookie { get; init; }

    // Flip one bit of the sealed credentials on their way to the server.
    public bool DamageCredentials { get; init; }

    // The pyspnego server answers a completed exchange with SHA-256 over the credentials it was handed.
    public bool ServerReportsCredentials { get; init; }

    public CredSspClientOptions? Client { get; init; }

    // Receives the server's public key and every message of the exchange, in order: "key", "client", "server".
    public Action<string, byte[]>? Trace { get; init; }
}

// CredSspClient on a real connection. Around it there is only what an RDP connection has before and after
// Network Level Authentication: the X.224 connection request that asks for CredSSP, TLS that accepts any
// certificate, and a look at what the server does when the exchange is over.
internal static class NlaTestConnection
{
    public const uint ProtocolSsl = 0x1;
    public const uint ProtocolHybrid = 0x2;
    public const uint ProtocolHybridEx = 0x8;

    // The machine this runs on is shared with dozens of containers; a slow answer is not a failure.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    // server is "host" or "host:port".
    public static async Task<NlaOutcome> SignInAsync(string server, string user, string domain, string password, NlaTestOptions? options = null)
    {
        options ??= new NlaTestOptions();
        using var timeout = new CancellationTokenSource(Patience);
        var cancellationToken = timeout.Token;
        var (host, port) = HostAndPort(server);

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancellationToken);
        var network = tcp.GetStream();

        // X.224 Connection Request with an RDP Negotiation Request (MS-RDPBCGR 2.2.1.1): the TPKT header,
        // seven bytes of X.224, the optional cookie line, and type 1 with the protocols the client offers.
        var cookie = options.Cookie is null ? [] : Encoding.ASCII.GetBytes($"Cookie: mstshash={options.Cookie}\r\n");
        var request = new byte[4 + 7 + cookie.Length + 8];
        request[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)request.Length);
        request[4] = (byte)(request.Length - 5);
        request[5] = 0xe0;
        cookie.CopyTo(request, 11);
        request[11 + cookie.Length] = 0x01;
        request[13 + cookie.Length] = 0x08;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(15 + cookie.Length), options.RequestedProtocols);
        await network.WriteAsync(request, cancellationToken);

        // X.224 Connection Confirm with the RDP Negotiation Response: type 2 and the protocol the server chose.
        var confirm = new byte[19];
        await network.ReadExactlyAsync(confirm, cancellationToken);
        if (confirm[0] != 0x03 || confirm[5] != 0xd0 || confirm[11] != 0x02)
        {
            throw new InvalidOperationException("The server did not confirm the connection request: " + Hex.Text(confirm));
        }

        var selected = BinaryPrimitives.ReadUInt32LittleEndian(confirm.AsSpan(15));
        if ((selected & (ProtocolHybrid | ProtocolHybridEx)) == 0)
        {
            throw new InvalidOperationException($"The server did not choose CredSSP but protocol {selected}.");
        }

        await using var ssl = new SslStream(network, false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken);
        var publicKey = ssl.RemoteCertificate!.GetPublicKey();
        options.Trace?.Invoke("key", publicKey);

        using var client = new CredSspClient(user, domain, password, host, publicKey, options.Client);
        var message = client.Start();
        while (true)
        {
            options.Trace?.Invoke("client", message);
            if (options.DamageCredentials && client.IsComplete)
            {
                // Somewhere in the sealed TSCredentials, which are the end of the last message.
                message[^8] ^= 0x01;
            }

            // One write per message: a Windows server wants each TSRequest in a single TLS record.
            await ssl.WriteAsync(message, cancellationToken);
            await ssl.FlushAsync(cancellationToken);
            if (client.IsComplete)
            {
                break;
            }

            byte[] answer;
            try
            {
                answer = await ReadTsRequestAsync(ssl, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or AuthenticationException)
            {
                throw client.ConnectionClosed(exception);
            }

            options.Trace?.Invoke("server", answer);
            message = client.Next(answer) ?? throw new InvalidOperationException("The exchange stopped without its last message.");
        }

        var evidence = $"{ssl.SslProtocol}, protocol {selected}, CredSSP {client.ServerVersion}";
        if ((selected & ProtocolHybridEx) != 0)
        {
            // Early User Authorization Result (MS-RDPBCGR 2.2.10.2): 0 means the user may log on.
            var result = new byte[4];
            await ssl.ReadExactlyAsync(result, cancellationToken);
            var authorization = BinaryPrimitives.ReadUInt32LittleEndian(result);
            evidence += $", early user authorization result {authorization}";
            if (authorization != 0)
            {
                return new NlaOutcome(selected, client.ServerVersion, false, evidence, null);
            }
        }

        if (options.ServerReportsCredentials)
        {
            var digest = new byte[32];
            await ssl.ReadExactlyAsync(digest, cancellationToken);
            return new NlaOutcome(selected, client.ServerVersion, true, evidence + ", the server reported the credentials", digest);
        }

        // Otherwise the server says nothing more: it waits for the connection sequence to go on. A server
        // that rejects the credentials it was handed hangs up instead.
        var reaction = await ReactionAsync(ssl);
        return new NlaOutcome(selected, client.ServerVersion, reaction is null, evidence + ", " + (reaction ?? "the server waits for the connection sequence"), null);
    }

    public static (string Host, int Port) HostAndPort(string server)
    {
        var address = server.Trim();
        var colon = address.LastIndexOf(':');
        return colon > 0 && int.TryParse(address[(colon + 1)..], out var port)
            ? (address[..colon], port)
            : (address, 3389);
    }

    // Reads one TSRequest, however the server spreads it over TLS records.
    private static async Task<byte[]> ReadTsRequestAsync(SslStream ssl, CancellationToken cancellationToken)
    {
        // A sequence tag and a length of at most five bytes: enough to know how long the message is.
        const int header = 6;
        var buffer = new byte[CredSspClient.MaxMessageLength];
        var filled = 0;
        while (true)
        {
            var length = CredSspClient.MessageLength(buffer.AsSpan(0, filled));
            if (length != 0 && filled >= length)
            {
                return buffer[..length];
            }

            var wanted = (length == 0 ? header : length) - filled;
            var read = await ssl.ReadAsync(buffer.AsMemory(filled, wanted), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            filled += read;
        }
    }

    // What the server does within three seconds of the last message, or null when it does nothing - which is
    // what a server does that accepted the credentials and now waits for the client.
    private static async Task<string?> ReactionAsync(SslStream ssl)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var read = await ssl.ReadAsync(new byte[1], grace.Token);
            return read == 0 ? "the server closed the connection after the credentials" : "the server sent something after the credentials";
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException exception)
        {
            return "the server broke the connection off after the credentials: " + exception.Message;
        }
    }
}
