using System.Buffers.Binary;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Matgate.Display.Vnc;

// From the first byte to the server's ServerInit: the version, the way to sign in, the signing in itself.
public sealed partial class VncClient
{
    // How often a server may say no to a password before Matgate stops asking for another one. Kept low:
    // servers count failed logins by address and lock the address out after a handful (TigerVNC after five).
    private const int MaxLogins = 3;

    private const int MaxNameBytes = 1024 * 1024;

    private enum Outcome
    {
        Connected,

        // The server said no to the user name or password.
        Refused,

        // The server hung up while the handler was being asked for a password: it did not want to wait.
        GaveUpWaiting,

        // VeNCrypt was offered, but with nothing in it for Matgate: the same again, without it.
        WithoutVeNCrypt,
    }

    // What carries over from one attempt at signing in to the next.
    private sealed class Login
    {
        public VncCredentials? Credentials { get; set; }

        public bool WithoutVeNCrypt { get; set; }

        // The handler was asked while the connection of this attempt was open.
        public bool Asked { get; set; }

        // The way of signing in that was used; for VeNCrypt its sub-type.
        public int Type { get; set; }
    }

    // The line cannot be used any more; why is decided by whoever knows what was going on.
    private sealed class LineClosedException : Exception;

    // The line during the handshake. Reads and writes are exact - nothing is read ahead - because the stream
    // changes under them when TLS begins.
    private sealed class Wire(Stream stream, CancellationToken cancellationToken)
    {
        public Stream Stream { get; set; } = stream;

        public CancellationToken Token => cancellationToken;

        public async ValueTask<byte[]> ReadAsync(int count)
        {
            var bytes = new byte[count];
            try
            {
                await Stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or SocketException)
            {
                // An EndOfStreamException is an IOException: the server closed the connection.
                throw new LineClosedException();
            }

            return bytes;
        }

        public async ValueTask<uint> ReadUInt32Async() => BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4).ConfigureAwait(false));

        public async ValueTask WriteAsync(byte[] bytes)
        {
            try
            {
                await Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or SocketException)
            {
                throw new LineClosedException();
            }
        }
    }

    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        var login = new Login();
        if (!string.IsNullOrEmpty(_options.Password))
        {
            login.Credentials = new VncCredentials(string.IsNullOrEmpty(_options.Username) ? null : _options.Username, _options.Password);
        }

        var refusals = 0;
        var gaveUp = 0;
        while (true)
        {
            login.Asked = false;
            var outcome = await AttemptAsync(login, cancellationToken).ConfigureAwait(false);
            if (outcome == Outcome.Connected)
            {
                return;
            }

            // A server that refused a login closes the connection; the next attempt needs a new one anyway.
            CloseLine();
            switch (outcome)
            {
                case Outcome.WithoutVeNCrypt:
                    login.WithoutVeNCrypt = true;
                    break;
                case Outcome.GaveUpWaiting:
                    // The password is there now: once more, this time without making the server wait.
                    if (++gaveUp > 1)
                    {
                        throw new VncException($"The VNC server at {Target} closed the connection while signing in.");
                    }

                    break;
                default:
                    if (++refusals >= MaxLogins)
                    {
                        throw new VncException("The password was refused.");
                    }

                    login.Credentials = await _handler.CredentialsAsync(VncSecurity.NeedsUsername(login.Type), true, cancellationToken).ConfigureAwait(false)
                        ?? throw new VncException("The password was refused.");
                    break;
            }
        }
    }

    private async Task<Outcome> AttemptAsync(Login login, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        deadline.CancelAfter(_options.ConnectTimeout);
        try
        {
            var wire = await OpenAsync(deadline.Token).ConfigureAwait(false);
            var version = await ExchangeVersionsAsync(wire).ConfigureAwait(false);
            var offered = await ReadSecurityTypesAsync(wire, version).ConfigureAwait(false);
            var type = PickType(offered, login);
            if (version >= 7)
            {
                await wire.WriteAsync([(byte)type]).ConfigureAwait(false);
            }

            if (type == VncSecurity.VeNCrypt)
            {
                type = await EnterVeNCryptAsync(wire, offered, login).ConfigureAwait(false);
                if (type == 0)
                {
                    return Outcome.WithoutVeNCrypt;
                }
            }

            login.Type = type;
            if (VncSecurity.UsesTls(type))
            {
                await StartTlsAsync(wire).ConfigureAwait(false);
            }

            var credentials = login.Credentials;
            var named = VncSecurity.NeedsUsername(type);
            if (VncSecurity.NeedsPassword(type) && (credentials is null || (named && string.IsNullOrEmpty(credentials.Username))))
            {
                // A person is typing now, for as long as that takes; the clock starts again afterwards.
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                login.Asked = true;
                credentials = await _handler.CredentialsAsync(named, false, cancellationToken).ConfigureAwait(false)
                    ?? throw new VncException(named
                        ? "The VNC server asks for a user name and a password, and none were given."
                        : "The VNC server asks for a password, and none was given.");
                login.Credentials = credentials;
                deadline.CancelAfter(_options.ConnectTimeout);
            }

            switch (type)
            {
                case VncSecurity.VncAuthentication or VncSecurity.X509Vnc:
                    var challenge = await wire.ReadAsync(16).ConfigureAwait(false);
                    var response = new byte[16];
                    VncDes.Respond(Encoding.UTF8.GetBytes(credentials!.Password), challenge, response);
                    await wire.WriteAsync(response).ConfigureAwait(false);
                    break;
                case VncSecurity.Plain or VncSecurity.X509Plain:
                    await wire.WriteAsync(VncSecurity.PlainLogin(credentials!.Username ?? "", credentials.Password)).ConfigureAwait(false);
                    break;
                case VncSecurity.AppleRemoteDesktop:
                    await AppleLoginAsync(wire, credentials!).ConfigureAwait(false);
                    break;
            }

            // The server says whether that was right - except for "none" before version 3.8, where there is
            // nothing to be right about.
            if (version >= 8 || type != VncSecurity.None)
            {
                var result = await wire.ReadUInt32Async().ConfigureAwait(false);
                if (result != 0)
                {
                    return await RefusedAsync(wire, version, type, result).ConfigureAwait(false);
                }
            }

            await InitialiseAsync(wire, version, type).ConfigureAwait(false);
            return Outcome.Connected;
        }
        catch (LineClosedException) when (login.Asked)
        {
            return Outcome.GaveUpWaiting;
        }
        catch (LineClosedException)
        {
            throw new VncException($"The VNC server at {Target} closed the connection during the handshake.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_closing.IsCancellationRequested)
        {
            throw new VncException($"The VNC server at {Target} did not answer in time.");
        }
    }

    private async Task<Wire> OpenAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            client.Dispose();
            throw new VncException($"The VNC server at {Target} cannot be reached: {Describe(exception.SocketErrorCode)}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var socket = client.Client;

        // A session can be silent for hours; without this, a server that vanished without a word is never
        // noticed.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

        var stream = new NetworkStream(socket, ownsSocket: true);
        Volatile.Write(ref _socket, socket);
        Volatile.Write(ref _stream, stream);
        return new Wire(stream, cancellationToken);
    }

    private static string Describe(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "nothing answers on that port",
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "the name is not known",
        SocketError.TimedOut => "it does not answer",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.HostDown => "there is no way to that address",
        _ => $"the connection failed ({error})",
    };

    // "RFB 003.008\n" from the server, Matgate's answer in the same form. Returns the minor version both speak.
    private async Task<int> ExchangeVersionsAsync(Wire wire)
    {
        var text = Encoding.ASCII.GetString(await wire.ReadAsync(12).ConfigureAwait(false));
        if (!text.StartsWith("RFB ", StringComparison.Ordinal) || text[7] != '.' || text[11] != '\n'
            || !int.TryParse(text.AsSpan(4, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(text.AsSpan(8, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            throw new VncException($"{Target} does not answer like a VNC server.");
        }

        // 3.3, 3.7 and 3.8 are all there is. Whatever a server calls itself beyond that (4.x, 5.x, Apple's
        // 3.889) speaks 3.8; anything in between is 3.3 under another name.
        int version;
        if (major > 3 || (major == 3 && minor >= 8))
        {
            version = 8;
        }
        else if (major == 3 && minor == 7)
        {
            version = 7;
        }
        else if (major == 3 && minor >= 3)
        {
            version = 3;
        }
        else
        {
            throw new VncException($"The VNC server at {Target} speaks a version of the protocol that is too old ({text.Trim()}).");
        }

        await wire.WriteAsync(Encoding.ASCII.GetBytes($"RFB 003.00{version}\n")).ConfigureAwait(false);
        return version;
    }

    private async Task<byte[]> ReadSecurityTypesAsync(Wire wire, int version)
    {
        if (version == 3)
        {
            // The oldest version does not offer: the server decides.
            var decided = await wire.ReadUInt32Async().ConfigureAwait(false);
            if (decided == 0)
            {
                throw new VncException($"The VNC server at {Target} refused the connection: {await ReadReasonAsync(wire).ConfigureAwait(false)}");
            }

            return decided <= 255
                ? [(byte)decided]
                : throw new VncException($"The VNC server at {Target} wants a way of signing in that Matgate does not know ({decided}).");
        }

        var count = (await wire.ReadAsync(1).ConfigureAwait(false))[0];
        if (count == 0)
        {
            throw new VncException($"The VNC server at {Target} refused the connection: {await ReadReasonAsync(wire).ConfigureAwait(false)}");
        }

        return await wire.ReadAsync(count).ConfigureAwait(false);
    }

    // Why the server says no, in its own words - made a sentence.
    private static async Task<string> ReadReasonAsync(Wire wire)
    {
        try
        {
            var length = await wire.ReadUInt32Async().ConfigureAwait(false);
            var reason = Encoding.UTF8.GetString(await wire.ReadAsync((int)Math.Min(length, 1024)).ConfigureAwait(false)).Trim('\0', ' ', '\r', '\n');
            return reason.Length == 0 ? "no reason given." : reason.EndsWith('.') ? reason : reason + ".";
        }
        catch (LineClosedException)
        {
            return "no reason given.";
        }
    }

    private int PickType(byte[] offered, Login login)
    {
        var best = 0;
        var bestRank = 0;
        foreach (var type in offered)
        {
            // VeNCrypt first where it is offered: what is in it only shows after it has been chosen.
            var rank = type == VncSecurity.VeNCrypt
                ? (login.WithoutVeNCrypt ? 0 : int.MaxValue)
                : Rank(type, false, login);
            if (rank > bestRank)
            {
                best = type;
                bestRank = rank;
            }
        }

        if (best == 0)
        {
            throw new VncException($"The VNC server at {Target} offers no way of signing in that Matgate knows (it offers: {VncSecurity.Describe(offered.Select(type => (int)type))}).");
        }

        return best;
    }

    private static int Rank(int type, bool inVeNCrypt, Login login)
    {
        return VncSecurity.Rank(type, inVeNCrypt, !string.IsNullOrEmpty(login.Credentials?.Username), login.Credentials is not null);
    }

    // VeNCrypt (19) is a second list inside the first: the encrypted ways of signing in, and often the plain
    // ones once more. Returns the one to go on with - or 0 when nothing in it is as good as what the first
    // list has besides, and the connection has to be made again for that.
    private async Task<int> EnterVeNCryptAsync(Wire wire, byte[] offered, Login login)
    {
        var best = 0;
        var bestRank = 0;
        var inside = new List<int>();
        var serverVersion = await wire.ReadAsync(2).ConfigureAwait(false);

        // 0.2 is the version there is; a server that only has 0.1 speaks something else.
        if (serverVersion[0] > 0 || serverVersion[1] >= 2)
        {
            await wire.WriteAsync([0, 2]).ConfigureAwait(false);
            if ((await wire.ReadAsync(1).ConfigureAwait(false))[0] == 0)
            {
                var count = (await wire.ReadAsync(1).ConfigureAwait(false))[0];
                var types = await wire.ReadAsync(count * 4).ConfigureAwait(false);
                for (var index = 0; index < count; index++)
                {
                    var type = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(types.AsSpan(index * 4)), int.MaxValue);
                    inside.Add(type);
                    var rank = Rank(type, true, login);
                    if (rank > bestRank)
                    {
                        best = type;
                        bestRank = rank;
                    }
                }
            }
        }

        var otherRank = 0;
        foreach (var type in offered)
        {
            if (type != VncSecurity.VeNCrypt)
            {
                otherRank = Math.Max(otherRank, Rank(type, false, login));
            }
        }

        if (bestRank > 0 && bestRank >= otherRank)
        {
            var chosen = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(chosen, (uint)best);
            await wire.WriteAsync(chosen).ConfigureAwait(false);
            return best;
        }

        if (otherRank == 0)
        {
            throw new VncException($"The VNC server at {Target} offers no way of signing in that Matgate knows (it offers: {VncSecurity.Describe(offered.Select(type => (int)type))}; within VeNCrypt: {VncSecurity.Describe(inside)}).");
        }

        Log($"VNC: VeNCrypt has nothing better to offer at {Target} ({VncSecurity.Describe(inside)}); connecting again without it.");
        return 0;
    }

    private async Task StartTlsAsync(Wire wire)
    {
        if ((await wire.ReadAsync(1).ConfigureAwait(false))[0] != 1)
        {
            throw new VncException($"The VNC server at {Target} could not start the encryption.");
        }

        var problems = SslPolicyErrors.None;
        var tls = new SslStream(wire.Stream, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = _options.Host,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    {
                        problems = errors;
                        return !_options.ValidateCertificate || errors == SslPolicyErrors.None;
                    },
                },
                wire.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException exception)
        {
            throw new VncException(
                problems != SslPolicyErrors.None && _options.ValidateCertificate
                    ? $"The certificate of the VNC server at {Target} is not trusted ({problems})."
                    : $"The encryption with the VNC server at {Target} could not be set up: {exception.Message}",
                exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SocketException)
        {
            throw new LineClosedException();
        }

        wire.Stream = tls;
        Volatile.Write(ref _stream, tls);
    }

    private static async Task AppleLoginAsync(Wire wire, VncCredentials credentials)
    {
        var header = await wire.ReadAsync(4).ConfigureAwait(false);
        int generator = BinaryPrimitives.ReadUInt16BigEndian(header);
        int size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));

        // Apple uses 1024 bits. The limit keeps a server from handing out sums that take minutes.
        if (size is < 8 or > 512)
        {
            throw new VncException("The VNC server sent a key of a size nothing can be done with (Apple Remote Desktop).");
        }

        var numbers = await wire.ReadAsync(size * 2).ConfigureAwait(false);
        var answer = VncSecurity.AppleLogin(generator, numbers.AsSpan(0, size), numbers.AsSpan(size), credentials.Username ?? "", credentials.Password);
        await wire.WriteAsync(answer).ConfigureAwait(false);
    }

    private async Task<Outcome> RefusedAsync(Wire wire, int version, int type, uint result)
    {
        // Only version 3.8 says why.
        var reason = version >= 8 ? await ReadReasonAsync(wire).ConfigureAwait(false) : "no reason given.";
        if (result == 2)
        {
            throw new VncException($"The VNC server at {Target} does not take any more logins for now: there were too many wrong ones.");
        }

        if (!VncSecurity.NeedsPassword(type))
        {
            throw new VncException($"The VNC server at {Target} refused the connection: {reason}");
        }

        Log($"VNC: {Target} refused the login ({VncSecurity.Name(type)}): {reason}");
        return Outcome.Refused;
    }

    // ClientInit - shared, so that whoever else looks at this desktop stays connected - and ServerInit: the
    // desktop's size and name. The pixel format the server would like is of no interest: Matgate names its own.
    private async Task InitialiseAsync(Wire wire, int version, int type)
    {
        await wire.WriteAsync([1]).ConfigureAwait(false);
        var init = await wire.ReadAsync(24).ConfigureAwait(false);
        int width = BinaryPrimitives.ReadUInt16BigEndian(init);
        int height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        if (nameLength > MaxNameBytes)
        {
            throw new VncException($"{Target} does not answer like a VNC server (the name of its desktop would be {nameLength} bytes long).");
        }

        var name = DecodeName(await wire.ReadAsync((int)nameLength).ConfigureAwait(false));
        var back = new VncFramebuffer(width, height);
        var front = back.Snapshot();
        _back = back;
        _name = name;
        lock (_frameLock)
        {
            _front = front;
            _width = width;
            _height = height;
        }

        _reader = new VncReader(wire.Stream);
        Log($"VNC: connected to {Target} (protocol 3.{version}, signed in with: {VncSecurity.Name(type)}), \"{name}\", {width} x {height}.");
    }

    // UTF-8 from today's servers, Latin-1 from old ones - which is what bytes that are no UTF-8 are read as.
    private static string DecodeName(byte[] name)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(name).TrimEnd('\0');
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(name).TrimEnd('\0');
        }
    }
}
