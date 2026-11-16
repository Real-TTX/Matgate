using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace Matgate.Display.Vnc.Tests;

// The whole client against a scripted server: from the first byte to the three messages every session
// begins with.
public class ClientHandshakeTests
{
    private static readonly int[] DefaultEncodings =
    [
        7, 16, 1, 5, 2, 0, // Tight, ZRLE, CopyRect, Hextile, RRE, Raw
        -254, // compression level 2
        -239, -223, -308, -224, -313, -312, // cursor, desktop size, extended desktop size, last rectangle, continuous updates, fence
        unchecked((int)0xC0A1E5CE), // extended clipboard
    ];

    [Fact]
    public async Task Connects_without_a_password_and_sets_the_session_up()
    {
        int[]? encodings = null;
        await using var server = new ScriptedServer(async side =>
        {
            encodings = await side.AcceptAsync(640, 480, "Schreibtisch – täglich");
        });

        var handler = new RecordingHandler();
        await using var client = new VncClient(server.Options(), handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();

        Assert.Equal(640, client.Width);
        Assert.Equal(480, client.Height);
        Assert.Equal("Schreibtisch – täglich", client.Name);
        Assert.Equal(DefaultEncodings, encodings);
        Assert.False(client.CanResize);
        Assert.Empty(handler.Asked);

        // Black, opaque, and of the size the server named.
        lock (client.FrameLock)
        {
            new Picture(640, 480).AssertShownBy(client.Framebuffer, client.Width, client.Height);
        }
    }

    [Fact]
    public async Task Names_only_the_encodings_it_was_told_to()
    {
        int[]? encodings = null;
        await using var server = new ScriptedServer(async side =>
        {
            encodings = await side.AcceptAsync(64, 48);
        });

        await using var client = new VncClient(server.Options(encodings: [VncEncoding.Hextile, VncEncoding.Raw, VncEncoding.Hextile]), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();

        int[] expected = [5, 0, .. DefaultEncodings[6..]];
        Assert.Equal(expected, encodings);
    }

    [Theory]
    [InlineData("RFB 003.008\n", "RFB 003.008\n")]
    [InlineData("RFB 003.889\n", "RFB 003.008\n")] // Apple
    [InlineData("RFB 004.001\n", "RFB 003.008\n")] // RealVNC
    [InlineData("RFB 005.000\n", "RFB 003.008\n")]
    [InlineData("RFB 003.007\n", "RFB 003.007\n")]
    [InlineData("RFB 003.003\n", "RFB 003.003\n")]
    [InlineData("RFB 003.005\n", "RFB 003.003\n")] // a version that does not exist is the oldest one
    [InlineData("RFB 003.006\n", "RFB 003.003\n")]
    public async Task Speaks_the_version_the_server_has(string greeting, string expected)
    {
        string? answer = null;
        await using var server = new ScriptedServer(async side =>
        {
            answer = await side.GreetAsync(greeting);
            if (expected.EndsWith("3\n", StringComparison.Ordinal))
            {
                // 3.3: the server names the type, and that is that.
                await side.SendAsync(new Packet().U32(1));
            }
            else
            {
                Assert.Equal(1, await side.OfferAsync(1));
                if (expected.EndsWith("8\n", StringComparison.Ordinal))
                {
                    // Only 3.8 has a result where there was nothing to check.
                    await side.ResultAsync(0);
                }
            }

            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        await using var client = new VncClient(server.Options(), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
        Assert.Equal(expected, answer);
        Assert.Equal(32, client.Width);
    }

    [Theory]
    [InlineData("HTTP/1.1 400 ")]
    [InlineData("RFB 003.008 ")]
    [InlineData("RFB 0o3.008\n")]
    [InlineData("RFB 002.002\n")]
    [InlineData("RFB 003.002\n")]
    [InlineData("SSH-2.0-Open")]
    public async Task Something_that_is_no_VNC_server_is_told_apart(string greeting)
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.SendAsync(Encoding.ASCII.GetBytes(greeting));
            await side.ClosedAsync();
        });

        await using var client = new VncClient(server.Options(), new RecordingHandler());
        await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
    }

    [Fact]
    public async Task Signs_in_with_the_password_of_the_options()
    {
        var accepted = false;
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();

            // Both are offered: the password is taken, not the open door.
            Assert.Equal(2, await side.OfferAsync(1, 2));
            accepted = await side.ChallengeAsync("geheim12");
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        var handler = new RecordingHandler();
        await using var client = new VncClient(server.Options(password: "geheim12-und-mehr"), handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
        Assert.True(accepted);
        Assert.Empty(handler.Asked);
    }

    [Fact]
    public async Task Signs_in_with_a_password_at_a_server_of_the_oldest_version()
    {
        var accepted = false;
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync("RFB 003.003\n");
            await side.SendAsync(new Packet().U32(2));
            accepted = await side.ChallengeAsync("alt");
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        await using var client = new VncClient(server.Options(password: "alt"), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
        Assert.True(accepted);
    }

    [Fact]
    public async Task Asks_for_a_password_the_options_do_not_carry()
    {
        var accepted = false;
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            Assert.Equal(2, await side.OfferAsync(2));
            accepted = await side.ChallengeAsync("gefragt");
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        var handler = new RecordingHandler { Credentials = (_, _) => new VncCredentials(null, "gefragt") };
        await using var client = new VncClient(server.Options(), handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
        Assert.True(accepted);
        Assert.Equal(new[] { (false, false) }, handler.Asked);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task Gives_up_when_nobody_has_a_password()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.OfferAsync(2);
            await side.SendAsync(new byte[16]);
            Assert.True(await side.ClosedAsync());
        });

        var handler = new RecordingHandler();
        await using var client = new VncClient(server.Options(), handler);
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Equal("The VNC server asks for a password, and none was given.", exception.Message);
        Assert.Equal(new[] { (false, false) }, handler.Asked);
        await server.FinishAsync();
    }

    [Fact]
    public async Task A_refused_password_is_asked_for_again_on_a_new_connection()
    {
        await using var server = new ScriptedServer(async (side, connection) =>
        {
            await side.GreetAsync();
            Assert.Equal(2, await side.OfferAsync(2));
            if (await side.ChallengeAsync("richtig"))
            {
                await side.ResultAsync(0);
                await side.InitAsync(32, 16);
                await side.ReadSetupAsync(32, 16);
                Assert.Equal(1, connection);
            }
            else
            {
                // A server says why (3.8) and hangs up.
                await side.ResultAsync(1, "Authentication failed");
                Assert.Equal(0, connection);
            }
        });

        var handler = new RecordingHandler { Credentials = (_, _) => new VncCredentials(null, "richtig") };
        await using var client = new VncClient(server.Options(password: "falsch"), handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
        Assert.Equal(new[] { (false, true) }, handler.Asked);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task After_three_refusals_the_password_was_refused()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.OfferAsync(2);
            await side.ChallengeAsync("nie");
            await side.ResultAsync(1, "Authentication failed");
        });

        var handler = new RecordingHandler { Credentials = (_, _) => new VncCredentials(null, "wieder falsch") };
        await using var client = new VncClient(server.Options(password: "falsch"), handler);
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Equal("The password was refused.", exception.Message);

        // Three attempts: the password of the options, and two more the handler was asked for.
        Assert.Equal(new[] { (false, true), (false, true) }, handler.Asked);
        Assert.Equal(3, server.Connections);
        await server.FinishAsync();
    }

    [Theory]
    [InlineData("RFB 003.008\n")]
    [InlineData("RFB 003.007\n")] // gives no reason
    public async Task The_password_was_refused_when_the_handler_has_no_other(string version)
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync(version);
            await side.OfferAsync(2);
            await side.ChallengeAsync("nie");
            await side.ResultAsync(1, version.Contains("008") ? "Authentication failed" : null);
        });

        var handler = new RecordingHandler();
        await using var client = new VncClient(server.Options(password: "falsch"), handler);
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Equal("The password was refused.", exception.Message);
        Assert.Equal(new[] { (false, true) }, handler.Asked);
        Assert.Equal(1, server.Connections);
        await server.FinishAsync();
    }

    [Fact]
    public async Task A_server_that_did_not_wait_for_the_password_is_called_again()
    {
        await using var server = new ScriptedServer(async (side, connection) =>
        {
            await side.GreetAsync();
            await side.OfferAsync(2);
            if (connection == 0)
            {
                // Hangs up while the person is still typing.
                return;
            }

            Assert.True(await side.ChallengeAsync("getippt"));
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        var handler = new RecordingHandler();
        handler.Credentials = (_, _) =>
        {
            Thread.Sleep(300);
            return new VncCredentials(null, "getippt");
        };

        await using var client = new VncClient(server.Options(), handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();

        // Asked once - the second connection is made with what is known by then.
        Assert.Equal(new[] { (false, false) }, handler.Asked);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task A_server_that_refuses_everybody_says_why()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.SendAsync(new Packet().U8(0).U32(26).Text("Too many security failures"));
        });

        await using var client = new VncClient(server.Options(password: "x"), new RecordingHandler());
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("Too many security failures.", exception.Message);
        await server.FinishAsync();
    }

    [Fact]
    public async Task A_refusal_without_a_password_is_no_wrong_password()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.OfferAsync(1);
            await side.ResultAsync(1, "The desktop is in use");
        });

        var handler = new RecordingHandler();
        await using var client = new VncClient(server.Options(), handler);
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("The desktop is in use.", exception.Message);
        Assert.Empty(handler.Asked);
        await server.FinishAsync();
    }

    [Fact]
    public async Task Nothing_usable_among_the_ways_in_is_said_so()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.SendAsync(new Packet().U8(3).U8(5).U8(129).U8(16));
            Assert.True(await side.ClosedAsync());
        });

        await using var client = new VncClient(server.Options(password: "x"), new RecordingHandler());
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("offers no way of signing in that Matgate knows", exception.Message);
        Assert.Contains("(129)", exception.Message);
        await server.FinishAsync();
    }

    [Fact]
    public async Task Signs_in_with_user_name_and_password_through_VeNCrypt_Plain()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            Assert.Equal(19, await side.OfferAsync(19, 2));
            await VeNCryptAsync(side, 256, 256, 258, 2);
            var lengths = await side.ReadAsync(8);
            Assert.Equal("jörg", Encoding.UTF8.GetString(await side.ReadAsync(BinaryPrimitives.ReadInt32BigEndian(lengths))));
            Assert.Equal("pass wört", Encoding.UTF8.GetString(await side.ReadAsync(BinaryPrimitives.ReadInt32BigEndian(lengths.AsSpan(4)))));
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        await using var client = new VncClient(server.Options("jörg", "pass wört"), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
    }

    [Fact]
    public async Task Takes_the_VNC_password_inside_VeNCrypt_where_the_encrypted_ways_need_no_certificate()
    {
        // What a TigerVNC server offers when nothing was configured: TLSVnc and VncAuth.
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            Assert.Equal(19, await side.OfferAsync(19, 2));
            await VeNCryptAsync(side, 2, 258, 2);
            Assert.True(await side.ChallengeAsync("kennwort"));
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        await using var client = new VncClient(server.Options(password: "kennwort"), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task Connects_again_without_VeNCrypt_when_it_has_nothing_to_offer()
    {
        // A server that keeps the plain ways out of its VeNCrypt list (Vino does).
        await using var server = new ScriptedServer(async (side, connection) =>
        {
            await side.GreetAsync();
            if (connection == 0)
            {
                Assert.Equal(19, await side.OfferAsync(19, 2));
                await side.SendAsync(new Packet().U8(0).U8(2));
                Assert.Equal(new byte[] { 0, 2 }, await side.ReadAsync(2));
                await side.SendAsync(new Packet().U8(0).U8(2).U32(257).U32(258));
                Assert.True(await side.ClosedAsync());
                return;
            }

            Assert.Equal(2, await side.OfferAsync(19, 2));
            Assert.True(await side.ChallengeAsync("kennwort"));
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        await using var client = new VncClient(server.Options(password: "kennwort"), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task VeNCrypt_with_nothing_usable_and_nothing_besides_is_said_so()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            Assert.Equal(19, await side.OfferAsync(19, 5));
            await side.SendAsync(new Packet().U8(0).U8(2));
            await side.ReadAsync(2);
            await side.SendAsync(new Packet().U8(0).U8(3).U32(257).U32(258).U32(263));
            Assert.True(await side.ClosedAsync());
        });

        await using var client = new VncClient(server.Options(password: "kennwort"), new RecordingHandler());
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("within VeNCrypt", exception.Message);
        Assert.Contains("(258)", exception.Message);
        Assert.Equal(1, server.Connections);
        await server.FinishAsync();
    }

    [Theory]
    [InlineData(260, null, null)] // X509None
    [InlineData(261, null, "kennwort")] // X509Vnc
    [InlineData(262, "anna", "kennwort")] // X509Plain
    public async Task Signs_in_over_TLS_with_a_certificate(int subtype, string? username, string? password)
    {
        using var certificate = SelfSigned("some.other.name");
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            Assert.Equal(19, await side.OfferAsync(2, 19, 1));
            await VeNCryptAsync(side, subtype, 258, subtype, 2);

            // "Go ahead", and TLS from here on.
            await side.SendAsync([1]);
            var tls = new SslStream(side.Stream);
            await tls.AuthenticateAsServerAsync(certificate).WaitAsync(ScriptedServer.Patience);
            side.Stream = tls;

            if (subtype == 261)
            {
                Assert.True(await side.ChallengeAsync(password!));
            }
            else if (subtype == 262)
            {
                var lengths = await side.ReadAsync(8);
                Assert.Equal(username, Encoding.UTF8.GetString(await side.ReadAsync(BinaryPrimitives.ReadInt32BigEndian(lengths))));
                Assert.Equal(password, Encoding.UTF8.GetString(await side.ReadAsync(BinaryPrimitives.ReadInt32BigEndian(lengths.AsSpan(4)))));
            }

            await side.ResultAsync(0);
            await side.InitAsync(48, 32);
            await side.ReadSetupAsync(48, 32);

            // The session itself runs inside TLS as well.
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 1, 1, 0).Pixel(0x123456));
            await side.ReadMessageAsync();
        });

        var handler = new RecordingHandler();
        await using var client = new VncClient(server.Options(username, password), handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        var running = client.RunAsync(CancellationToken.None);
        await handler.UntilAsync(seen => seen.Updates.Count == 1, "the update sent through TLS");
        await server.FinishAsync();
        Assert.Empty(handler.Asked);
        lock (client.FrameLock)
        {
            Assert.Equal(new byte[] { 0x56, 0x34, 0x12, 0xFF }, client.Framebuffer[..4]);
        }

        await client.DisposeAsync();
        await running.WaitAsync(ScriptedServer.Patience);
    }

    [Fact]
    public async Task A_certificate_nobody_vouches_for_is_refused_when_that_is_asked_for()
    {
        using var certificate = SelfSigned("some.other.name");
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.OfferAsync(19);
            await VeNCryptAsync(side, 260, 260);
            await side.SendAsync([1]);
            var tls = new SslStream(side.Stream);
            try
            {
                await tls.AuthenticateAsServerAsync(certificate).WaitAsync(ScriptedServer.Patience);
                await tls.ReadAsync(new byte[1]).AsTask().WaitAsync(ScriptedServer.Patience);
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException)
            {
                // The client breaks the handshake off, or hangs up right after it.
            }
        });

        var options = new VncOptions { Host = "127.0.0.1", Port = server.Port, ValidateCertificate = true };
        await using var client = new VncClient(options, new RecordingHandler());
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("certificate", exception.Message);
        Assert.Contains("not trusted", exception.Message);
        await server.FinishAsync();
    }

    // Apple Remote Desktop, with the server's side of the key agreement worked out here.
    [Theory]
    [InlineData("anna", "kennwort", false)]
    [InlineData(null, null, true)]
    public async Task Signs_in_the_way_a_Mac_wants_it(string? username, string? password, bool asked)
    {
        const int KeySize = 128;
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync("RFB 003.889\n");
            Assert.Equal(30, await side.OfferAsync(30, 33, 36, 35, 2));

            var modulus = new byte[KeySize];
            new Random(1).NextBytes(modulus);
            modulus[0] |= 0x80;
            modulus[^1] |= 1;
            var prime = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
            var secret = new BigInteger(RandomNumberGenerator.GetBytes(KeySize), isUnsigned: true, isBigEndian: true);
            var publicKey = BigInteger.ModPow(5, secret, prime).ToByteArray(isUnsigned: true, isBigEndian: true);
            await side.SendAsync(new Packet().U16(5).U16(KeySize).Bytes(modulus).Zeros(KeySize - publicKey.Length).Bytes(publicKey));

            var encrypted = await side.ReadAsync(128);
            var clientKey = new BigInteger(await side.ReadAsync(KeySize), isUnsigned: true, isBigEndian: true);
            var shared = BigInteger.ModPow(clientKey, secret, prime).ToByteArray(isUnsigned: true, isBigEndian: true);
            using var aes = Aes.Create();
            aes.Key = MD5.HashData([.. new byte[KeySize - shared.Length], .. shared]);
            var block = aes.DecryptEcb(encrypted, PaddingMode.None);
            Assert.Equal("anna\0", Encoding.UTF8.GetString(block, 0, 5));
            Assert.Equal("kennwort\0", Encoding.UTF8.GetString(block, 64, 9));

            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        var handler = new RecordingHandler { Credentials = (_, _) => new VncCredentials("anna", "kennwort") };
        await using var client = new VncClient(server.Options(username, password), handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();

        // Without anything in the options the handler is asked - for a user name as well.
        Assert.Equal(asked ? new[] { (true, false) } : Array.Empty<(bool, bool)>(), handler.Asked);
    }

    [Fact]
    public async Task A_password_alone_is_the_VNC_password_on_a_Mac()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync("RFB 003.889\n");
            Assert.Equal(2, await side.OfferAsync(30, 33, 36, 35, 2));
            Assert.True(await side.ChallengeAsync("vncpw"));
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        await using var client = new VncClient(server.Options(password: "vncpw"), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
    }

    [Fact]
    public async Task A_server_that_says_nothing_is_given_up_after_the_timeout()
    {
        await using var server = new ScriptedServer(side => side.ClosedAsync());
        var options = new VncOptions { Host = "127.0.0.1", Port = server.Port, ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        await using var client = new VncClient(options, new RecordingHandler());
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("did not answer in time", exception.Message);
    }

    [Fact]
    public async Task The_time_a_person_takes_to_type_is_not_the_servers()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.OfferAsync(2);
            Assert.True(await side.ChallengeAsync("langsam"));
            await side.ResultAsync(0);
            await side.InitAsync(32, 16);
            await side.ReadSetupAsync(32, 16);
        });

        var handler = new RecordingHandler();
        handler.Credentials = (_, _) =>
        {
            // Longer than the whole timeout.
            Thread.Sleep(4500);
            return new VncCredentials(null, "langsam");
        };

        var options = new VncOptions { Host = "127.0.0.1", Port = server.Port, ConnectTimeout = TimeSpan.FromSeconds(2) };
        await using var client = new VncClient(options, handler);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await server.FinishAsync();
    }

    [Fact]
    public async Task A_port_nobody_listens_on_is_said_so()
    {
        // A port that was free a moment ago.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var client = new VncClient(new VncOptions { Host = "127.0.0.1", Port = port }, new RecordingHandler());
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("cannot be reached", exception.Message);
    }

    [Fact]
    public async Task A_name_nobody_knows_is_said_so()
    {
        await using var client = new VncClient(new VncOptions { Host = "no-such-host.invalid" }, new RecordingHandler());

        // Whatever the resolver makes of it and however long it takes: it ends as a VncException.
        await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
    }

    [Fact]
    public async Task Cancelling_ends_the_handshake()
    {
        await using var server = new ScriptedServer(side => side.ClosedAsync());
        await using var client = new VncClient(server.Options(), new RecordingHandler());
        using var cancel = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cancel.Token).WaitAsync(ScriptedServer.Patience));
    }

    [Theory]
    [InlineData(20000, 100)]
    [InlineData(100, 16385)]
    public async Task A_desktop_larger_than_any_is_refused(int width, int height)
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.OfferAsync(1);
            await side.ResultAsync(0);
            await side.InitAsync(width, height);
            Assert.True(await side.ClosedAsync());
        });

        await using var client = new VncClient(server.Options(), new RecordingHandler());
        var exception = await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        Assert.Contains("16384", exception.Message);
        await server.FinishAsync();
    }

    [Fact]
    public async Task A_name_longer_than_any_is_refused()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.GreetAsync();
            await side.OfferAsync(1);
            await side.ResultAsync(0);
            await side.ReadAsync(1);
            await side.SendAsync(new Packet().U16(32).U16(16).Zeros(16).U32(0x7FFFFFFF));
            Assert.True(await side.ClosedAsync());
        });

        await using var client = new VncClient(server.Options(), new RecordingHandler());
        await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        await server.FinishAsync();
    }

    [Fact]
    public async Task A_handshake_that_breaks_off_anywhere_ends_in_a_VncException()
    {
        // A whole handshake with a password, cut off after every possible number of bytes.
        var whole = new Packet().Text("RFB 003.008\n").U8(1).U8(2).Zeros(16).U32(0)
            .U16(32).U16(16).Zeros(16).U32(4).Text("name").ToArray();
        for (var length = 0; length < whole.Length; length++)
        {
            var sent = whole.AsSpan(0, length).ToArray();
            await using var server = new ScriptedServer(async side =>
            {
                await side.SendAsync(sent);
                side.Stream.Close();
            });

            await using var client = new VncClient(server.Options(password: "x"), new RecordingHandler());
            await Assert.ThrowsAsync<VncException>(() => client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience));
        }
    }

    // The server's side of VeNCrypt up to the sub-type: its version, the client's, "fine", the list.
    private static async Task VeNCryptAsync(ServerSide side, int expected, params int[] subtypes)
    {
        await side.SendAsync(new Packet().U8(0).U8(2));
        Assert.Equal(new byte[] { 0, 2 }, await side.ReadAsync(2));
        var list = new Packet().U8(0).U8(subtypes.Length);
        foreach (var subtype in subtypes)
        {
            list.U32((uint)subtype);
        }

        await side.SendAsync(list);
        Assert.Equal((uint)expected, BinaryPrimitives.ReadUInt32BigEndian(await side.ReadAsync(4)));
    }

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Windows only serves TLS with a key it holds in a store of its own: exported and read back, it does.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}
