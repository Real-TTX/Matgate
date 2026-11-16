using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

// What NtlmClient does beyond the example of the specification: the messages a current server gets, the forms
// of a user name, sealing in both directions - and what happens when the server's challenge is not what it
// should be (NtlmChallengeTests).
public class NtlmClientTests
{
    private const string Target = "TERMSRV/server.example";

    [Fact]
    public void NegotiateMessage()
    {
        using var client = NewClient();

        // Signature, type 1, the flags (56, KEY_EXCH, 128, VERSION, EXTENDED_SESSIONSECURITY, ALWAYS_SIGN,
        // NTLM, SEAL, SIGN, REQUEST_TARGET, UNICODE), empty domain and workstation fields pointing behind
        // the header, and the version: Windows 10.0 build 26100, NTLM revision 15.
        Assert.Equal(
            "4e544c4d5353500001000000358208e2000000002800000000000000280000000a00f4650000000f",
            Hex.Text(client.CreateNegotiate()));
    }

    [Fact]
    public void AnswerToAServerThatSendsItsTime()
    {
        var server = new FakeNlaServer();
        using var client = new NtlmClient("tester", "CORP", "secret", "WORKSTATION", Target);

        // The fake server checks the proof of the password, and throws when the integrity code is wrong.
        var authenticate = client.CreateAuthenticate(server.CreateChallenge(client.CreateNegotiate()));
        Assert.True(server.Authenticate(authenticate));

        Assert.Equal(("CORP", "tester"), server.AuthenticatedAs);
        Assert.Equal("WORKSTATION", server.ClientWorkstation);
        Assert.True(server.ClientSentIntegrityCode);
        Assert.Equal(new byte[24], server.ClientLmResponse);
        Assert.Equal((NtlmFlags)0xE2888235, server.ClientFlags);

        // The server's pairs come back in its order, then the client's own.
        Assert.Equal(
            [NtlmAvId.NetBiosDomainName, NtlmAvId.NetBiosComputerName, NtlmAvId.DnsDomainName, NtlmAvId.DnsComputerName, NtlmAvId.Timestamp, NtlmAvId.Flags, NtlmAvId.ChannelBindings, NtlmAvId.TargetName],
            server.ClientAvPairs.Select(pair => pair.Id));
        Assert.Equal(new byte[16], server.ClientAvPair(NtlmAvId.ChannelBindings));
        Assert.Equal(Target, Encoding.Unicode.GetString(server.ClientAvPair(NtlmAvId.TargetName)!));
        Assert.Equal(new byte[] { 2, 0, 0, 0 }, server.ClientAvPair(NtlmAvId.Flags));

        // The time in the response is the server's, not the client's clock.
        var serverTime = FakeNlaServer.Time.ToFileTime();
        Assert.Equal(serverTime, BinaryPrimitives.ReadInt64LittleEndian(server.ClientAvPair(NtlmAvId.Timestamp)));
        Assert.Equal(serverTime, BinaryPrimitives.ReadInt64LittleEndian(Field(authenticate, 20).AsSpan(16 + 8)));

        // Version and integrity code sit between the fixed fields and the payload.
        Assert.Equal("0a00f4650000000f", Hex.Text(authenticate.AsSpan(64, 8)));
        Assert.Equal(88, BinaryPrimitives.ReadInt32LittleEndian(authenticate.AsSpan(32)));
        Assert.NotEqual(new byte[16], authenticate.AsSpan(72, 16).ToArray());
    }

    [Fact]
    public void AnswerToAServerThatSendsNoTime()
    {
        var server = new FakeNlaServer { SendsTime = false };
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using var client = new NtlmClient("tester", "", "secret", "WORKSTATION", Target, new NtlmClientOptions { TimeProvider = new FixedTime(now) });

        var authenticate = client.CreateAuthenticate(server.CreateChallenge(client.CreateNegotiate()));
        Assert.True(server.Authenticate(authenticate));

        // No integrity code, so no field for it: the payload starts right behind the version. The LM
        // response is the real LMv2 one, and the time is the client's own.
        Assert.False(server.ClientSentIntegrityCode);
        Assert.Null(server.ClientAvPair(NtlmAvId.Flags));
        Assert.Equal(72, BinaryPrimitives.ReadInt32LittleEndian(authenticate.AsSpan(32)));
        Assert.NotEqual(new byte[24], server.ClientLmResponse);
        Assert.Equal(now.ToFileTime(), BinaryPrimitives.ReadInt64LittleEndian(Field(authenticate, 20).AsSpan(16 + 8)));

        // The session works all the same.
        Assert.Equal("there"u8.ToArray(), server.Session.Decrypt(client.Encrypt("there"u8)));
        Assert.Equal("back"u8.ToArray(), client.Decrypt(server.Session.Encrypt("back"u8)));
    }

    [Fact]
    public void WithoutKeyExchangeTheSessionKeyIsTheOneFromThePassword()
    {
        var server = new FakeNlaServer { WithheldFlags = NtlmFlags.KeyExchange };
        using var client = NewClient();

        var authenticate = client.CreateAuthenticate(server.CreateChallenge(client.CreateNegotiate()));
        Assert.True(server.Authenticate(authenticate));

        // No encrypted session key in the message, the flag is gone, and both ends still agree on the keys.
        Assert.Empty(Field(authenticate, 52));
        Assert.False(server.ClientFlags.HasFlag(NtlmFlags.KeyExchange));
        Assert.Equal("there"u8.ToArray(), server.Session.Decrypt(client.Encrypt("there"u8)));
        Assert.Equal("back"u8.ToArray(), client.Decrypt(server.Session.Encrypt("back"u8)));
    }

    [Fact]
    public void AWrongPasswordDoesNotGetPastTheServer()
    {
        var server = new FakeNlaServer { Password = "another" };
        using var client = NewClient();
        Assert.False(server.Authenticate(client.CreateAuthenticate(server.CreateChallenge(client.CreateNegotiate()))));
    }

    [Fact]
    public void RandomValuesDifferFromRunToRun()
    {
        var server = new FakeNlaServer();
        using var probe = NewClient();
        var challenge = server.CreateChallenge(probe.CreateNegotiate());

        byte[] Authenticate()
        {
            using var client = NewClient();
            client.CreateNegotiate();
            return client.CreateAuthenticate(challenge);
        }

        var first = Authenticate();
        var second = Authenticate();

        // The client challenge (inside the NT response) and the encrypted session key.
        Assert.NotEqual(Hex.Text(Field(first, 20).AsSpan(32, 8)), Hex.Text(Field(second, 20).AsSpan(32, 8)));
        Assert.NotEqual(Hex.Text(Field(first, 52)), Hex.Text(Field(second, 52)));
    }

    [Fact]
    public void MessagesAreSealedInOrderInBothDirections()
    {
        var (client, server) = Session();
        using (client)
        {
            for (var i = 0; i < 5; i++)
            {
                var fromClient = Encoding.UTF8.GetBytes($"client {i}");
                var fromServer = Encoding.UTF8.GetBytes($"server {i}");
                var sealedByClient = client.Encrypt(fromClient);
                var sealedByServer = server.Encrypt(fromServer);

                // 16 bytes of signature: version 1, the checksum, the sequence number of this direction.
                Assert.Equal(16 + fromClient.Length, sealedByClient.Length);
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(sealedByClient));
                Assert.Equal((uint)i, BinaryPrimitives.ReadUInt32LittleEndian(sealedByClient.AsSpan(12)));
                Assert.Equal((uint)i, BinaryPrimitives.ReadUInt32LittleEndian(sealedByServer.AsSpan(12)));
                Assert.Equal(fromClient, server.Decrypt(sealedByClient));
                Assert.Equal(fromServer, client.Decrypt(sealedByServer));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    public void ATamperedMessageIsRefused(int position)
    {
        var (client, server) = Session();
        using (client)
        {
            var message = server.Encrypt("the server's public key"u8);
            message[position] ^= 0x01;
            Assert.Throws<NtlmException>(() => client.Decrypt(message));
        }
    }

    [Fact]
    public void AMessageOfTheWrongDirectionOrOutOfOrderIsRefused()
    {
        var (client, server) = Session();
        using (client)
        {
            // What the client sealed itself must not pass as the server's.
            Assert.Throws<NtlmException>(() => client.Decrypt(client.Encrypt("echo"u8)));
        }

        (client, server) = Session();
        using (client)
        {
            server.Encrypt("first"u8);
            var second = server.Encrypt("second"u8);
            Assert.Throws<NtlmException>(() => client.Decrypt(second));
        }

        (client, _) = Session();
        using (client)
        {
            Assert.Throws<NtlmException>(() => client.Decrypt(new byte[15]));
        }
    }

    [Theory]
    [InlineData("tester", "", "tester", "")]
    [InlineData("tester", "CORP", "tester", "CORP")]
    [InlineData(@"CORP\tester", "", "tester", "CORP")]
    [InlineData(@"CORP\tester", null, "tester", "CORP")]
    [InlineData(@".\tester", "", "tester", ".")]
    [InlineData("tester@corp.example", "", "tester@corp.example", "")]
    [InlineData(@"AzureAD\tester@corp.example", "", "tester@corp.example", "AzureAD")]
    // A domain given separately wins, and the name stays as it was typed.
    [InlineData(@"OTHER\tester", "CORP", @"OTHER\tester", "CORP")]
    [InlineData("tester@corp.example", "CORP", "tester@corp.example", "CORP")]
    [InlineData(@"\tester", "", @"\tester", "")]
    public void UserNameForms(string user, string? domain, string expectedUser, string expectedDomain)
    {
        Assert.Equal((expectedUser, expectedDomain), NtlmClient.SplitUserName(user, domain));

        using var client = new NtlmClient(user, domain!, "secret", "WORKSTATION", Target);
        Assert.Equal(expectedUser, client.User);
        Assert.Equal(expectedDomain, client.Domain);
    }

    [Fact]
    public void TheNamesInTheMessageAreTheOnesTheKeyWasMadeFrom()
    {
        // The server computes the key from the names it reads in the message. A principal name goes in whole,
        // with no domain at all.
        var server = new FakeNlaServer { User = "tester@corp.example" };
        using var client = new NtlmClient("tester@corp.example", "", "secret", "WORKSTATION", Target);
        var authenticate = client.CreateAuthenticate(server.CreateChallenge(client.CreateNegotiate()));

        Assert.True(server.Authenticate(authenticate));
        Assert.Equal(("", "tester@corp.example"), server.AuthenticatedAs);
        Assert.Empty(Field(authenticate, 28));
    }

    [Fact]
    public void TheResponseKeyFoldsTheUserToUpperCaseButNotTheDomain()
    {
        var key = NtlmClient.NtOwfV2("secret", "tester", "Corp");
        Assert.Equal(key, NtlmClient.NtOwfV2("secret", "TESTER", "Corp"));
        Assert.Equal(key, NtlmClient.NtOwfV2("secret", "TeStEr", "Corp"));
        Assert.NotEqual(key, NtlmClient.NtOwfV2("secret", "tester", "CORP"));
        Assert.NotEqual(key, NtlmClient.NtOwfV2("Secret", "tester", "Corp"));

        // Beyond ASCII too.
        Assert.Equal(NtlmClient.NtOwfV2("secret", "JÜRGEN", ""), NtlmClient.NtOwfV2("secret", "jürgen", ""));

        // Spelled out once: HMAC-MD5 with the MD4 of the password over the upper-cased user and the domain.
        var expected = HMACMD5.HashData(Md4.HashData(Encoding.Unicode.GetBytes("secret")), Encoding.Unicode.GetBytes("TESTERCorp"));
        Assert.Equal(expected, key);
    }

    // The hashes winpr-hash writes into the SAM file of nla-server/Dockerfile are MD4 over the UTF-16
    // password: a second implementation (FreeRDP's, on top of OpenSSL) that agrees with Md4, umlauts and
    // euro sign included.
    [Theory]
    [InlineData("Matgate-NLA-2026", "c2be9453a7197203c9d0457324321b33")]
    [InlineData("Grüße-€-2026", "45972d32e210c68efec62c2da76e5d63")]
    public void PasswordHashesMatchTheSamFileOfTheTestServer(string password, string expected)
    {
        Assert.Equal(expected, Hex.Text(Md4.HashData(Encoding.Unicode.GetBytes(password))));
    }

    [Fact]
    public void RefusesMisuse()
    {
        Assert.Throws<ArgumentException>(() => new NtlmClient("", "", "secret", "W", Target));
        Assert.Throws<ArgumentException>(() => new NtlmClient(@"CORP\", "", "secret", "W", Target));
        Assert.Throws<ArgumentException>(() => new NtlmClient(new string('u', 1025), "", "secret", "W", Target));
        Assert.Throws<ArgumentException>(() => new NtlmClient("tester", "", "secret", "W", Target, new NtlmClientOptions { ClientChallenge = new byte[7] }));
        Assert.Throws<ArgumentException>(() => new NtlmClient("tester", "", "secret", "W", Target, new NtlmClientOptions { ExportedSessionKey = new byte[8] }));

        var server = new FakeNlaServer();
        var client = NewClient();
        Assert.Throws<InvalidOperationException>(() => client.CreateAuthenticate(new byte[64]));
        Assert.Throws<InvalidOperationException>(() => client.Encrypt("x"u8));
        Assert.False(client.HasSession);

        var challenge = server.CreateChallenge(client.CreateNegotiate());
        Assert.Throws<InvalidOperationException>(() => client.CreateNegotiate());
        client.CreateAuthenticate(challenge);
        Assert.True(client.HasSession);
        Assert.Throws<InvalidOperationException>(() => client.CreateAuthenticate(challenge));

        client.Dispose();
        Assert.Throws<ObjectDisposedException>(() => client.Encrypt("x"u8));
        client.Dispose();
    }

    private static NtlmClient NewClient() => new("tester", "", "secret", "WORKSTATION", Target);

    // A client that has completed the handshake with the fake server, and the server's end of the session.
    private static (NtlmClient Client, NtlmSession Server) Session()
    {
        var server = new FakeNlaServer();
        var client = NewClient();
        Assert.True(server.Authenticate(client.CreateAuthenticate(server.CreateChallenge(client.CreateNegotiate()))));
        return (client, server.Session);
    }

    // The bytes one of the length/offset fields of a message points to.
    private static byte[] Field(byte[] message, int field)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(field));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(field + 4));
        return message.AsSpan(offset, length).ToArray();
    }
}
