using System.Text;
using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

// The CredSSP exchange from its first message to the credentials, against the fake server: both ways of
// binding the public key, every way a server says no, and a server that is not who it claims to be.
public class CredSspClientTests
{
    private const string Host = "server.example";

    [Fact]
    public void ACurrentServerGetsTheHashAndThenTheCredentials()
    {
        var server = new FakeNlaServer();
        var (client, sent) = Run(server);

        Assert.True(client.IsComplete);
        Assert.Equal(6, client.ServerVersion);
        Assert.Equal(("", "tester", "secret"), server.DelegatedCredentials);
        Assert.Equal(3, sent.Count);

        // First the NTLM NEGOTIATE alone.
        Assert.Equal(6, sent[0].Version);
        Assert.Equal("4e544c4d5353500001000000", Hex.Text(sent[0].NegoToken.AsSpan(0, 12)));
        Assert.True(sent[0] is { AuthInfo: null, PubKeyAuth: null, ErrorCode: null, ClientNonce: null });

        // Then the AUTHENTICATE message together with the binding to the public key and the nonce for it.
        // That the sealed value is the right hash is the fake server's check.
        Assert.Equal("4e544c4d5353500003000000", Hex.Text(sent[1].NegoToken.AsSpan(0, 12)));
        Assert.Equal(16 + 32, sent[1].PubKeyAuth!.Length);
        Assert.Equal(32, sent[1].ClientNonce!.Length);
        Assert.True(sent[1] is { AuthInfo: null, ErrorCode: null });

        // At last the credentials and nothing else.
        Assert.True(sent[2] is { Version: 6, AuthInfo: not null, NegoToken: null, PubKeyAuth: null, ErrorCode: null, ClientNonce: null });

        // The service the client asked for is the terminal server of the host it connected to.
        Assert.Equal("TERMSRV/" + Host, Encoding.Unicode.GetString(server.ClientAvPair(NtlmAvId.TargetName)!));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AnOldServerGetsThePublicKeyItself(int version)
    {
        var server = new FakeNlaServer { Version = version };
        var (client, sent) = Run(server);

        Assert.True(client.IsComplete);
        Assert.Equal(version, client.ServerVersion);
        Assert.Equal(("", "tester", "secret"), server.DelegatedCredentials);

        // The client still says 6, seals the key as it is, and sends no nonce.
        Assert.All(sent, message => Assert.Equal(6, message.Version));
        Assert.Equal(16 + server.PublicKey.Length, sent[1].PubKeyAuth!.Length);
        Assert.Null(sent[1].ClientNonce);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(1000)]
    public void FromVersionFiveOnItIsTheHashAlsoForVersionsYetToCome(int version)
    {
        var server = new FakeNlaServer { Version = version };
        var (client, sent) = Run(server);

        Assert.True(client.IsComplete);
        Assert.Equal(version, client.ServerVersion);
        Assert.Equal(32, sent[1].ClientNonce!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void AVersionThatNeverExistedIsRefused(int version)
    {
        var server = new FakeNlaServer { Version = version };
        var exception = Assert.Throws<CredSspException>(() => Run(server));
        Assert.False(exception.IsLogonFailure);

        // The logon was never sent.
        Assert.Null(server.AuthenticatedAs);
    }

    [Fact]
    public void TheCallerCanRefuseServersThatWantThePublicKeyItself()
    {
        var options = new CredSspClientOptions { MinimumServerVersion = 5 };
        var old = new FakeNlaServer { Version = 4 };
        var exception = Assert.Throws<CredSspException>(() => Run(old, options: options));
        Assert.False(exception.IsLogonFailure);
        Assert.Contains("version 4", exception.Message);
        Assert.Null(old.AuthenticatedAs);

        var current = new FakeNlaServer { Version = 5 };
        Assert.True(Run(current, options: options).Client.IsComplete);
    }

    [Theory]
    [InlineData(@"CORP\tester", "", "CORP", "tester")]
    [InlineData("tester", "CORP", "CORP", "tester")]
    [InlineData("tester", "", "", "tester")]
    [InlineData("TESTER", "corp", "corp", "TESTER")]
    public void TheCredentialsCarryTheNamesOfTheLogon(string user, string domain, string expectedDomain, string expectedUser)
    {
        var server = new FakeNlaServer();
        Run(server, user, domain);

        Assert.Equal((expectedDomain, expectedUser), server.AuthenticatedAs);
        Assert.Equal((expectedDomain, expectedUser, "secret"), server.DelegatedCredentials);
    }

    [Fact]
    public void APrincipalNameStaysWholeAllTheWay()
    {
        var server = new FakeNlaServer { User = "tester@corp.example" };
        Run(server, "tester@corp.example");

        Assert.Equal(("", "tester@corp.example"), server.AuthenticatedAs);
        Assert.Equal(("", "tester@corp.example", "secret"), server.DelegatedCredentials);
    }

    [Fact]
    public void APasswordBeyondAsciiArrivesAsItWasTyped()
    {
        var server = new FakeNlaServer { User = "jürgen", Password = "Grüße-€-2026 \U0001F511" };
        Run(server, "jürgen", password: "Grüße-€-2026 \U0001F511");
        Assert.Equal(("", "jürgen", "Grüße-€-2026 \U0001F511"), server.DelegatedCredentials);
    }

    [Fact]
    public void TheWorkstationIsThisMachineUnlessTheCallerSaysOtherwise()
    {
        var server = new FakeNlaServer();
        Run(server);
        Assert.Equal(Environment.MachineName, server.ClientWorkstation);

        server = new FakeNlaServer();
        Run(server, options: new CredSspClientOptions { Workstation = "MATGATE" });
        Assert.Equal("MATGATE", server.ClientWorkstation);
    }

    [Fact]
    public void AGivenNonceIsTheOneThatIsSent()
    {
        var nonce = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var server = new FakeNlaServer();
        var (_, sent) = Run(server, options: new CredSspClientOptions { ClientNonce = nonce });
        Assert.Equal(nonce, sent[1].ClientNonce);
        Assert.NotEqual(nonce, Run(new FakeNlaServer()).Sent[1].ClientNonce);
    }

    [Fact]
    public void AWrongPasswordAndAnUnknownUserAreLogonFailures()
    {
        var wrongPassword = Assert.Throws<CredSspException>(() => Run(new FakeNlaServer(), password: "guess"));
        Assert.True(wrongPassword.IsLogonFailure);
        Assert.Equal(0xC000006Du, wrongPassword.Status);

        var unknownUser = Assert.Throws<CredSspException>(() => Run(new FakeNlaServer(), "somebody"));
        Assert.True(unknownUser.IsLogonFailure);
    }

    // What the server says when it refuses a logon, and whether another password could change it.
    [Theory]
    [InlineData(0xC000006Du, true, "user name or the password is wrong")]
    [InlineData(0xC000006Au, true, "password is wrong")]
    [InlineData(0xC0000064u, true, "does not know this user")]
    [InlineData(0xC0000071u, false, "password has expired")]
    [InlineData(0xC0000224u, false, "password has to be changed")]
    [InlineData(0xC0000234u, false, "locked out")]
    [InlineData(0xC0000072u, false, "disabled")]
    [InlineData(0xC0000193u, false, "account has expired")]
    [InlineData(0xC000015Bu, false, "not been granted the right")]
    [InlineData(0xC000006Eu, false, "restriction on the account")]
    [InlineData(0xC000006Fu, false, "time of day")]
    [InlineData(0xC0000070u, false, "from this computer")]
    [InlineData(0xC000005Eu, false, "domain controller")]
    [InlineData(0xC0000022u, false, "denied access")]
    [InlineData(0xC00000BBu, false, "does not accept this logon")]
    [InlineData(0xC0000418u, false, "does not accept this logon")]
    // FreeRDP's server wraps whatever Win32 error is lying around; this is what a wrong password gives there.
    [InlineData(0xC00700EAu, true, "0xC00700EA")]
    [InlineData(0x8009030Cu, true, "0x8009030C")]
    public void TheStatusOfTheServerBecomesASentence(uint status, bool isLogonFailure, string fragment)
    {
        var exception = Assert.Throws<CredSspException>(() => Run(new FakeNlaServer { RefuseWith = status }));
        Assert.Equal(status, exception.Status);
        Assert.Equal(isLogonFailure, exception.IsLogonFailure);
        Assert.Contains(fragment, exception.Message);
    }

    [Fact]
    public void AStatusBeforeTheLogonWasSentIsNoLogonFailure()
    {
        using var client = NewClient(new FakeNlaServer());
        client.Start();
        var exception = Assert.Throws<CredSspException>(() => client.Next(new TsRequest { Version = 6, ErrorCode = 0xC000006D }.Encode()));
        Assert.False(exception.IsLogonFailure);
        Assert.Equal(0xC000006Du, exception.Status);
    }

    [Fact]
    public void AStatusOfZeroIsNoError()
    {
        // "Success" next to the confirmation of the public key changes nothing.
        var server = new FakeNlaServer();
        using var client = NewClient(server);
        var logon = client.Next(server.Answer(client.Start())!)!;
        var confirmation = TsRequest.Decode(server.Answer(logon)!);
        var withStatus = new TsRequest { Version = 6, PubKeyAuth = confirmation.PubKeyAuth, ErrorCode = 0 }.Encode();

        Assert.NotNull(client.Next(withStatus));
        Assert.True(client.IsComplete);
    }

    [Fact]
    public void AnAnswerWithNothingInItIsALogonFailure()
    {
        // FreeRDP's server does this when its own bookkeeping has no status for the refusal.
        var server = new FakeNlaServer();
        using var client = NewClient(server);
        client.Next(server.Answer(client.Start())!);

        var exception = Assert.Throws<CredSspException>(() => client.Next(new TsRequest { Version = 6 }.Encode()));
        Assert.True(exception.IsLogonFailure);
        Assert.Null(exception.Status);
    }

    [Fact]
    public void AConnectionThatClosesAfterTheLogonIsALogonFailure()
    {
        var server = new FakeNlaServer();
        using var client = NewClient(server);

        // While the challenge is still due, a closed connection says nothing about the logon.
        using (var early = NewClient(server))
        {
            early.Start();
            Assert.False(early.ConnectionClosed().IsLogonFailure);
            Assert.Throws<InvalidOperationException>(() => early.Next(new TsRequest { Version = 6 }.Encode()));
        }

        // Once the logon is out and the verdict is due, it is how old servers say no.
        client.Next(server.Answer(client.Start())!);
        var cause = new IOException("connection reset");
        var exception = client.ConnectionClosed(cause);
        Assert.True(exception.IsLogonFailure);
        Assert.Null(exception.Status);
        Assert.Same(cause, exception.InnerException);
        Assert.False(client.IsComplete);
    }

    [Fact]
    public void AConnectionThatClosesAfterTheExchangeIsNotTheExchangesFault()
    {
        var (client, _) = Run(new FakeNlaServer());
        Assert.False(client.ConnectionClosed().IsLogonFailure);
        Assert.True(client.IsComplete);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(2)]
    public void AServerThatCannotConfirmThePublicKeyIsRefused(int version)
    {
        // The right keys, the wrong answer: a server that relays the logon but showed a certificate of its own.
        var server = new FakeNlaServer
        {
            Version = version,
            ForgeConfirmation = correct =>
            {
                correct[^1] ^= 0x01;
                return correct;
            },
        };

        var exception = Assert.Throws<CredSspException>(() => Run(server));
        Assert.False(exception.IsLogonFailure);
        Assert.Contains("certificate", exception.Message);

        // No credentials left the client.
        Assert.Null(server.DelegatedCredentials);
    }

    [Fact]
    public void AnOldServerThatSendsTheKeyBackUnchangedIsRefused()
    {
        // The one thing the increment is there for: the client's own value must not pass as the answer.
        var key = new FakeNlaServer().PublicKey;
        var server = new FakeNlaServer { Version = 3, ForgeConfirmation = _ => key };
        var exception = Assert.Throws<CredSspException>(() => Run(server));
        Assert.False(exception.IsLogonFailure);
        Assert.Null(server.DelegatedCredentials);
    }

    [Fact]
    public void AnAnswerThatIsNotSealedWithTheSessionKeyIsRefused()
    {
        // Whoever does not know the password cannot produce the signature - nor repair one after a change.
        var server = new FakeNlaServer();
        using var client = NewClient(server);
        var logon = client.Next(server.Answer(client.Start())!)!;
        var confirmation = TsRequest.Decode(server.Answer(logon)!);
        confirmation.PubKeyAuth![20] ^= 0x80;

        var exception = Assert.Throws<CredSspException>(() => client.Next(new TsRequest { Version = 6, PubKeyAuth = confirmation.PubKeyAuth }.Encode()));
        Assert.False(exception.IsLogonFailure);
        Assert.IsType<NtlmException>(exception.InnerException);
        Assert.Contains("somebody sits in between", exception.Message);
    }

    [Fact]
    public void ACertificateThatIsNotTheServersIsNoticedOnTheOtherSideToo()
    {
        // The client binds itself to the key it saw in the TLS handshake. When that is not the key of the
        // server the logon ends up at, that server finds out from the client's message.
        var server = new FakeNlaServer();
        var seen = (byte[])server.PublicKey.Clone();
        seen[5] ^= 0xFF;
        using var client = new CredSspClient("tester", "", "secret", Host, seen);
        var logon = client.Next(server.Answer(client.Start())!)!;
        Assert.Throws<InvalidOperationException>(() => server.Answer(logon));
    }

    [Fact]
    public void AChallengeTheNtlmClientRefusesEndsTheExchange()
    {
        var server = new FakeNlaServer { WithheldFlags = NtlmFlags.Seal };
        var exception = Assert.Throws<CredSspException>(() => Run(server));
        Assert.False(exception.IsLogonFailure);
        Assert.IsType<NtlmException>(exception.InnerException);
    }

    [Fact]
    public void MessagesThatMakeNoSenseEndTheExchange()
    {
        var server = new FakeNlaServer();

        // No token where the challenge should be.
        using (var client = NewClient(server))
        {
            client.Start();
            Assert.Throws<CredSspException>(() => client.Next(new TsRequest { Version = 6 }.Encode()));
            Assert.Throws<InvalidOperationException>(() => client.Next(new TsRequest { Version = 6 }.Encode()));
        }

        // Something that is no NTLM message.
        using (var client = NewClient(server))
        {
            client.Start();
            var exception = Assert.Throws<CredSspException>(() => client.Next(new TsRequest { Version = 6, NegoToken = new byte[80] }.Encode()));
            Assert.IsType<NtlmException>(exception.InnerException);
        }

        // Something that is no TSRequest, at either point where the client listens.
        using (var client = NewClient(server))
        {
            client.Start();
            Assert.Throws<CredSspException>(() => client.Next([0x16, 0x03, 0x01, 0x00, 0x05]));
        }

        server = new FakeNlaServer();
        using (var client = NewClient(server))
        {
            client.Next(server.Answer(client.Start())!);
            var exception = Assert.Throws<CredSspException>(() => client.Next([0x30, 0x03, 0x02, 0x01]));
            Assert.False(exception.IsLogonFailure);
        }
    }

    // Both messages a server gets to send, with random damage, ten thousand times: the client goes on or stops
    // with a CredSspException. Anything else would be a hole a hostile server could reach.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomDamageToTheServersMessagesNeverGetsPastTheClient(bool secondMessage)
    {
        var random = new Random(20261116);
        var accepted = 0;
        var refused = 0;
        for (var round = 0; round < 5_000; round++)
        {
            var server = new FakeNlaServer();
            using var client = NewClient(server);
            var message = server.Answer(client.Start())!;
            if (secondMessage)
            {
                message = server.Answer(client.Next(message)!)!;
            }

            if (random.Next(5) == 0)
            {
                message = message[..random.Next(message.Length)];
            }

            for (var flips = random.Next(1, 4); flips > 0 && message.Length > 0; flips--)
            {
                message[random.Next(message.Length)] = (byte)random.Next(256);
            }

            try
            {
                client.Next(message);
                accepted++;
            }
            catch (CredSspException)
            {
                refused++;
            }
        }

        Assert.True(refused > 1_000, $"only {refused} damaged messages were refused");
        Assert.True(secondMessage || accepted > 500, $"only {accepted} damaged challenges were still answered");
    }

    [Fact]
    public void RefusesMisuse()
    {
        var key = new FakeNlaServer().PublicKey;
        Assert.Throws<ArgumentException>(() => new CredSspClient("tester", "", "secret", "", key));
        Assert.Throws<ArgumentException>(() => new CredSspClient("tester", "", "secret", Host, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => new CredSspClient("", "", "secret", Host, key));
        Assert.Throws<ArgumentNullException>(() => new CredSspClient("tester", "", null!, Host, key));
        Assert.Throws<ArgumentException>(() => new CredSspClient("tester", "", "secret", Host, key, new CredSspClientOptions { ClientNonce = new byte[16] }));

        var server = new FakeNlaServer();
        using var client = NewClient(server);
        Assert.False(client.IsComplete);
        Assert.Equal(0, client.ServerVersion);
        Assert.Throws<InvalidOperationException>(() => client.Next(new TsRequest { Version = 6 }.Encode()));

        var first = client.Start();
        Assert.Throws<InvalidOperationException>(() => client.Start());
        var last = client.Next(server.Answer(client.Next(server.Answer(first)!)!)!);
        Assert.NotNull(last);
        Assert.True(client.IsComplete);
        Assert.Throws<InvalidOperationException>(() => client.Next(new TsRequest { Version = 6 }.Encode()));
        Assert.Throws<InvalidOperationException>(() => client.Start());
    }

    private static CredSspClient NewClient(FakeNlaServer server, string user = "tester", string domain = "", string password = "secret", CredSspClientOptions? options = null) =>
        new(user, domain, password, Host, server.PublicKey, options);

    // Lets client and server talk until the server has nothing more to say, and keeps what the client sent.
    private static (CredSspClient Client, List<TsRequest> Sent) Run(FakeNlaServer server, string user = "tester", string domain = "", string password = "secret", CredSspClientOptions? options = null)
    {
        var client = NewClient(server, user, domain, password, options);
        var sent = new List<TsRequest>();
        var message = client.Start();
        while (true)
        {
            sent.Add(TsRequest.Decode(message));
            var answer = server.Answer(message);
            if (answer is null)
            {
                Assert.True(client.IsComplete, "The server is done and the client is not.");
                return (client, sent);
            }

            Assert.False(client.IsComplete, "The client is done and the server is not.");
            message = client.Next(answer) ?? throw new InvalidOperationException("The client has nothing to send.");
        }
    }
}
