using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Matgate.Display.Rdp.Security;

namespace Matgate.Display.Rdp;

// Getting to the server: the X.224 negotiation that settles how the connection is secured, TLS, the sign-in
// before the session (Network Level Authentication), and the MCS layer that carries the channels.
public sealed partial class RdpClient
{
    private const uint ProtocolSsl = 0x1;
    private const uint ProtocolHybrid = 0x2;
    private const uint ProtocolHybridEx = 0x8;

    // The MCS channel every session has for its own messages.
    private const ushort DefaultIoChannel = 1003;

    private uint _selectedProtocol;
    private bool _certificateRefused;
    private ushort _userChannel;
    private ushort _ioChannel = DefaultIoChannel;
    private IPAddress _localAddress = IPAddress.Any;

    // True when the options carry what a sign-in before the session needs.
    private bool HasCredentials => _options.UserName.Trim().Length > 0 && _options.Password.Length > 0;

    private (string Domain, string User) Account()
    {
        var user = _options.UserName.Trim();
        var domain = _options.Domain.Trim();
        if (domain.Length == 0)
        {
            // "DOMAIN\user" in the name field; "user@domain" stays whole - that is a name of its own kind.
            var slash = user.IndexOf('\\');
            if (slash > 0)
            {
                domain = user[..slash];
                user = user[(slash + 1)..];
            }
        }

        return (domain, user);
    }

    private async Task OpenTransportAsync(CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        _tcp = tcp;
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(_options.Host, _options.Port, connecting.Token);
        }

        if (tcp.Client.LocalEndPoint is IPEndPoint local)
        {
            _localAddress = local.Address;
        }

        var network = tcp.GetStream();
        await NegotiateAsync(network, cancellationToken);

        var ssl = new SslStream(network, false, (_, _, _, errors) =>
        {
            if (_options.IgnoreCertificate || errors == SslPolicyErrors.None)
            {
                return true;
            }

            _certificateRefused = true;
            return false;
        });
        _stream = ssl;
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = _options.Host,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            },
            cancellationToken);

        if ((_selectedProtocol & (ProtocolHybrid | ProtocolHybridEx)) != 0)
        {
            await SignInBeforeSessionAsync(ssl, cancellationToken);
        }
    }

    // The X.224 connection request with what Matgate can do, and the server's choice.
    private async Task NegotiateAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        // Without a user name and password there is nothing to sign in with beforehand: ask for TLS alone, and
        // the server shows its own sign-in screen - unless it insists (see the failure code below).
        var wanted = HasCredentials ? ProtocolSsl | ProtocolHybrid | ProtocolHybridEx : ProtocolSsl;

        // Load balancers in front of terminal servers route by this line; servers themselves ignore it.
        var (_, user) = Account();
        var cookie = Array.Empty<byte>();
        var name = new string(user.Where(c => c is > ' ' and < (char)127).Take(9).ToArray());
        if (name.Length > 0)
        {
            cookie = Encoding.ASCII.GetBytes("Cookie: mstshash=" + name + "\r\n");
        }

        var total = 4 + 7 + cookie.Length + 8;
        var request = new WireWriter(total);
        request.U8(0x03).U8(0).U16Be(total);
        request.U8(total - 5).U8(0xE0).U16(0).U16(0).U8(0);
        request.Bytes(cookie);
        request.U8(0x01).U8(0).U16(8).U32(wanted);
        await network.WriteAsync(request.ToArray(), cancellationToken);

        var header = new byte[4];
        await network.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (header[0] != 0x03 || length < 11 || length > 1024)
        {
            throw new RdpException("The server did not answer like an RDP server.");
        }

        var body = new byte[length - 4];
        await network.ReadExactlyAsync(body, cancellationToken);
        var reader = new WireReader(body);
        reader.U8();
        if ((reader.U8() & 0xF0) != 0xD0)
        {
            throw new RdpException("The server did not answer like an RDP server.");
        }

        reader.Skip(5);
        if (reader.Remaining < 8)
        {
            // No negotiation at all: a server that only knows RDP's own, old encryption.
            throw new RdpException("The RDP server only offers the old RDP encryption, which Matgate's own engine does not support.");
        }

        var type = reader.U8();
        reader.U8();
        reader.U16();
        var value = reader.U32();
        if (type == 0x03)
        {
            throw value switch
            {
                2 => new RdpException("The RDP server only allows the old RDP encryption, which Matgate's own engine does not support."),
                3 => new RdpException("The RDP server has no certificate for a secure connection."),
                // Network Level Authentication is required and Matgate did not offer it for want of a password.
                5 when !HasCredentials => new RdpException("The RDP server wants a user name and password before it shows anything.", true),
                5 => new RdpException("The RDP server wants a kind of sign-in Matgate's own engine does not support."),
                6 => new RdpException("The RDP server wants a client certificate, which Matgate's own engine does not support."),
                _ => new RdpException("The RDP server and Matgate found no common way to secure the connection."),
            };
        }

        if (type != 0x02 || (value & (ProtocolSsl | ProtocolHybrid | ProtocolHybridEx)) == 0)
        {
            throw new RdpException("The RDP server and Matgate found no common way to secure the connection.");
        }

        _selectedProtocol = value;
    }

    // Network Level Authentication (CredSSP): the user name and password are proven before the server spends
    // anything on a session. The messages are bare DER structures on the TLS connection.
    private async Task SignInBeforeSessionAsync(SslStream ssl, CancellationToken cancellationToken)
    {
        var certificate = ssl.RemoteCertificate ?? throw new RdpException("The RDP server showed no certificate.");
        var (domain, user) = Account();
        // Servers that only know the scheme from before 2018 are refused, as a current Windows client refuses
        // them: with it, whoever sits between Matgate and the server could have Matgate seal data of his choosing
        // (CVE-2018-0886) - and could claim the old scheme to get there.
        using var signIn = new CredSspClient(
            user, domain, _options.Password, _options.Host, certificate.GetPublicKey(),
            new CredSspClientOptions { MinimumServerVersion = 5 });
        try
        {
            await ssl.WriteAsync(signIn.Start(), cancellationToken);
            await ssl.FlushAsync(cancellationToken);
            while (!signIn.IsComplete)
            {
                var message = await ReadDerAsync(ssl, cancellationToken);
                var next = signIn.Next(message);
                if (next is not null)
                {
                    await ssl.WriteAsync(next, cancellationToken);
                    await ssl.FlushAsync(cancellationToken);
                }
            }

            if ((_selectedProtocol & ProtocolHybridEx) != 0)
            {
                // The server says at once whether this user may sign in over Remote Desktop at all.
                var result = new byte[4];
                await ssl.ReadExactlyAsync(result, cancellationToken);
                if (BinaryPrimitives.ReadUInt32LittleEndian(result) != 0)
                {
                    throw new RdpException("This user is not allowed to sign in over Remote Desktop on this server.");
                }
            }
        }
        catch (CredSspException exception)
        {
            throw new RdpException(exception.Message, exception.IsLogonFailure, exception);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException)
        {
            // Servers hang up on a wrong password instead of saying so - but a connection that drops before the
            // password was even sent is something else. The sign-in knows where it stood.
            var closed = signIn.ConnectionClosed(exception);
            throw new RdpException(closed.Message, closed.IsLogonFailure, closed);
        }
    }

    // One DER structure (a sequence): the tag, a length of one to three bytes, the content.
    private static async Task<byte[]> ReadDerAsync(Stream stream, CancellationToken cancellationToken)
    {
        var start = new byte[4];
        await stream.ReadExactlyAsync(start.AsMemory(0, 2), cancellationToken);
        var header = 2;
        int length = start[1];
        if ((length & 0x80) != 0)
        {
            var extra = length & 0x7F;
            if (extra is < 1 or > 2)
            {
                throw new RdpProtocolException("a sign-in message of impossible length");
            }

            await stream.ReadExactlyAsync(start.AsMemory(2, extra), cancellationToken);
            length = extra == 1 ? start[2] : BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(2));
            header += extra;
        }

        var message = new byte[header + length];
        start.AsSpan(0, header).CopyTo(message);
        await stream.ReadExactlyAsync(message.AsMemory(header), cancellationToken);
        return message;
    }

    private async Task<int> ReadTpktAsync(CancellationToken cancellationToken)
    {
        var stream = _stream!;
        await stream.ReadExactlyAsync(_pdu.AsMemory(0, 4), cancellationToken);
        var length = BinaryPrimitives.ReadUInt16BigEndian(_pdu.AsSpan(2));
        if (_pdu[0] != 0x03 || length < 7)
        {
            throw new RdpProtocolException("an answer that is not an RDP message");
        }

        await stream.ReadExactlyAsync(_pdu.AsMemory(4, length - 4), cancellationToken);
        if (_pdu[5] != 0xF0)
        {
            throw new SessionEndedException();
        }

        return length;
    }

    // The MCS layer: one "connect" that carries what client and server are (GCC conference data), then the
    // user and the channels - the session's own and one per virtual channel.
    private async Task McsConnectAsync(CancellationToken cancellationToken)
    {
        Send(ConnectInitial());
        var length = await ReadTpktAsync(cancellationToken);
        ConnectResponse(_pdu.AsSpan(7, length - 7));

        // Erect Domain Request and Attach User Request.
        Send([0x03, 0x00, 0x00, 0x0C, 0x02, 0xF0, 0x80, 0x04, 0x01, 0x00, 0x01, 0x00]);
        Send([0x03, 0x00, 0x00, 0x08, 0x02, 0xF0, 0x80, 0x28]);
        length = await ReadTpktAsync(cancellationToken);
        var confirm = new WireReader(_pdu.AsSpan(7, length - 7));
        if (confirm.U8() >> 2 != 11 || confirm.U8() != 0)
        {
            throw new RdpProtocolException("the server refused the session's user");
        }

        _userChannel = (ushort)(confirm.U16Be() + 1001);

        var joins = new List<ushort> { _userChannel, _ioChannel };
        joins.AddRange(_channelsById.Keys);
        foreach (var channel in joins)
        {
            var join = new WireWriter(12);
            join.U8(0x03).U8(0).U16Be(12).U8(0x02).U8(0xF0).U8(0x80);
            join.U8(0x38).U16Be(_userChannel - 1001).U16Be(channel);
            Send(join.ToArray());
            length = await ReadTpktAsync(cancellationToken);
            var joined = new WireReader(_pdu.AsSpan(7, length - 7));
            if (joined.U8() >> 2 != 15 || joined.U8() != 0)
            {
                throw new RdpProtocolException("the server refused a channel");
            }
        }

        foreach (var (id, joined) in _channelsById)
        {
            var channel = joined.Channel;
            channel.Log = _log;
            joined.Reassembler.Log = _log;
            channel.Send = message => SendChannel(id, channel, message);
        }
    }

    private byte[] ConnectInitial()
    {
        var blocks = ClientData();

        // GCC Conference Create Request, as every RDP client sends it: only the two lengths vary.
        var conference = new WireWriter(blocks.Length + 32);
        conference.Bytes([0x00, 0x05, 0x00, 0x14, 0x7C, 0x00, 0x01]);
        conference.U16Be((blocks.Length + 14) | 0x8000);
        conference.Bytes([0x00, 0x08, 0x00, 0x10, 0x00, 0x01, 0xC0, 0x00, 0x44, 0x75, 0x63, 0x61]);
        conference.U16Be(blocks.Length | 0x8000);
        conference.Bytes(blocks);

        // MCS Connect Initial (BER). The three sets of domain parameters are the ones Microsoft's client sends.
        var body = new WireWriter(conference.Length + 128);
        body.Bytes([0x04, 0x01, 0x01, 0x04, 0x01, 0x01, 0x01, 0x01, 0xFF]);
        body.Bytes([0x30, 0x19, 0x02, 0x01, 0x22, 0x02, 0x01, 0x02, 0x02, 0x01, 0x00, 0x02, 0x01, 0x01, 0x02, 0x01, 0x00, 0x02, 0x01, 0x01, 0x02, 0x02, 0xFF, 0xFF, 0x02, 0x01, 0x02]);
        body.Bytes([0x30, 0x19, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, 0x02, 0x01, 0x00, 0x02, 0x01, 0x01, 0x02, 0x02, 0x04, 0x20, 0x02, 0x01, 0x02]);
        body.Bytes([0x30, 0x1C, 0x02, 0x02, 0xFF, 0xFF, 0x02, 0x02, 0xFC, 0x17, 0x02, 0x02, 0xFF, 0xFF, 0x02, 0x01, 0x01, 0x02, 0x01, 0x00, 0x02, 0x01, 0x01, 0x02, 0x02, 0xFF, 0xFF, 0x02, 0x01, 0x02]);
        body.U8(0x04).U8(0x82).U16Be(conference.Length);
        body.Bytes(conference.Span);

        var total = 4 + 3 + 5 + body.Length;
        var message = new WireWriter(total);
        message.U8(0x03).U8(0).U16Be(total).U8(0x02).U8(0xF0).U8(0x80);
        message.U8(0x7F).U8(0x65).U8(0x82).U16Be(body.Length);
        message.Bytes(body.Span);
        return message.ToArray();
    }

    // What this client is: the screen it wants, its keyboard, and the virtual channels it brings.
    private byte[] ClientData()
    {
        var data = new WireWriter(512);

        // Core data.
        data.U16(0xC001).U16(216);
        data.U32(0x00080004);
        data.U16(_options.Width).U16(_options.Height);
        data.U16(0xCA01).U16(0xAA03);
        data.U32(_options.KeyboardLayout);
        data.U32(19041);
        data.Utf16Field(_options.ClientName, 32);
        // An enhanced keyboard with twelve function keys.
        data.U32(4).U32(0).U32(12);
        data.Zero(64);
        data.U16(0xCA01).U16(1).U32(0);
        // 24 bits here and "32 bits wanted" in the flags: that is how a client asks for full colour.
        data.U16(24).U16(0x000F);
        // Error details, 32 bits, status messages, strong keys, a connection type that counts, monitor layouts.
        data.U16(0x006F);
        data.Zero(64);
        // The connection type "LAN": the server spends bandwidth on quality.
        data.U8(6).U8(0);
        data.U32(_selectedProtocol);

        // Cluster data: no redirection to another server of a farm (Matgate cannot follow one yet).
        data.U16(0xC004).U16(12).U32(0).U32(0);

        // Security data: nothing of RDP's own encryption - TLS secures the connection.
        data.U16(0xC002).U16(12).U32(0).U32(0);

        // Network data: the virtual channels.
        data.U16(0xC003).U16(8 + _channels.Count * 12).U32((uint)_channels.Count);
        foreach (var channel in _channels)
        {
            var name = new byte[8];
            Encoding.ASCII.GetBytes(channel.Name.AsSpan(0, Math.Min(7, channel.Name.Length)), name);
            data.Bytes(name).U32(channel.Options);
        }

        return data.ToArray();
    }

    private void ConnectResponse(ReadOnlySpan<byte> response)
    {
        var reader = new WireReader(response);
        if (reader.U8() != 0x7F || reader.U8() != 0x66)
        {
            throw new RdpProtocolException("an unexpected answer to the connect request");
        }

        reader.BerLength();
        // The result (an enumerated value); zero is success.
        if (reader.U8() != 0x0A || reader.BerLength() != 1 || reader.U8() != 0)
        {
            throw new RdpException("The RDP server refused the connection.");
        }

        // The connect id and the domain parameters are of no use here.
        reader.U8();
        reader.Skip(reader.BerLength());
        reader.U8();
        reader.Skip(reader.BerLength());
        if (reader.U8() != 0x04)
        {
            throw new RdpProtocolException("the connect answer carries no conference data");
        }

        reader.BerLength();

        // GCC Conference Create Response (PER), up to the server's data blocks.
        reader.U8();
        reader.Skip(reader.U8());
        reader.PerLength();
        reader.U8();
        reader.U16Be();
        reader.Skip(reader.PerLength());
        reader.U8();
        reader.U8();
        reader.U8();
        reader.Skip(reader.PerLength() + 4);
        reader.PerLength();

        while (reader.Remaining >= 4)
        {
            var type = reader.U16();
            var length = reader.U16();
            if (length < 4)
            {
                throw new RdpProtocolException("a data block of impossible length");
            }

            var block = new WireReader(reader.Bytes(Math.Min(length - 4, reader.Remaining)));
            switch (type)
            {
                case 0x0C02:
                    if (block.Remaining >= 8 && (block.U32() != 0 || block.U32() != 0))
                    {
                        throw new RdpException("The RDP server insists on the old RDP encryption, which Matgate's own engine does not support.");
                    }

                    break;
                case 0x0C03:
                    _ioChannel = block.U16();
                    var count = block.U16();
                    for (var i = 0; i < count && i < _channels.Count; i++)
                    {
                        var id = block.U16();
                        // Zero: the server did not make this channel.
                        if (id != 0)
                        {
                            _channelsById[id] = new JoinedChannel(_channels[i]);
                        }
                    }

                    break;
            }
        }
    }

    // The Client Info message: who signs in, and how the session should look and behave.
    private void SendClientInfo()
    {
        var (domain, user) = Account();
        var password = _options.Password;
        const uint Mouse = 0x1, DisableCtrlAltDel = 0x2, AutoLogon = 0x8, Unicode = 0x10, MaximizeShell = 0x20, LogonNotify = 0x40;
        const uint EnableWindowsKey = 0x100, LogonErrors = 0x10000, MouseHasWheel = 0x20000, NoAudioPlayback = 0x80000;
        var flags = Mouse | DisableCtrlAltDel | Unicode | MaximizeShell | LogonNotify | EnableWindowsKey | LogonErrors | MouseHasWheel;
        if (user.Length > 0 && password.Length > 0)
        {
            flags |= AutoLogon;
        }

        if (!_options.Sound)
        {
            flags |= NoAudioPlayback;
        }

        var info = new WireWriter(512);
        // The security header that says "this is the Client Info message".
        info.U16(0x0040).U16(0);
        info.U32(0).U32(flags);
        info.U16(domain.Length * 2).U16(user.Length * 2).U16(password.Length * 2).U16(0).U16(0);
        info.Utf16(domain, true).Utf16(user, true).Utf16(password, true).Utf16("", true).Utf16("", true);

        // The extended part: where the client is, its time zone, and what the desktop may spend on looks.
        var address = _localAddress.ToString();
        const string Directory = "C:\\Windows\\System32\\mstscax.dll";
        info.U16(_localAddress.AddressFamily == AddressFamily.InterNetworkV6 ? 0x0017 : 0x0002);
        info.U16((address.Length + 1) * 2).Utf16(address, true);
        info.U16((Directory.Length + 1) * 2).Utf16(Directory, true);
        // The time zone: UTC, no daylight saving time.
        info.I32(0).Utf16Field("UTC", 64).Zero(16).I32(0).Utf16Field("UTC", 64).Zero(16).I32(0);
        info.U32(0);
        // Smooth fonts and desktop composition: the network between Matgate and the server can take it.
        info.U32(0x00000180);
        info.U16(0);
        Send(McsData(_ioChannel, info.Span));
    }
}
