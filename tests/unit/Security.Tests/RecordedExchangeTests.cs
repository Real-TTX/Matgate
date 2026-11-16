using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

// Two exchanges as they really happened, recorded from the test servers and replayed here without them: the
// server's public key and all five messages. With the few values a client makes up pinned to what they
// were in the recording, the client has to produce its three messages again byte for byte, and it has to
// accept what the real server sealed. That keeps what those servers agreed to from changing unnoticed.
//
//   exchange-freerdp.txt       FreeRDP 3.15 shadow server (nla-server), CredSSP 6: the hash of the public key
//   exchange-pyspnego-v2.txt   pyspnego 0.12.4 (nla-server-pyspnego) announcing CredSSP 2: the key itself
//
// Both were recorded with user "tester", domain "NLATEST", the host given as 127.0.0.1.
public class RecordedExchangeTests
{
    // What is random in a real client.
    internal static CredSspClientOptions FixedClient => new()
    {
        Workstation = "MATGATE",
        ClientNonce = Hex.Bytes("202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"),
        Ntlm = new NtlmClientOptions
        {
            ClientChallenge = Hex.Bytes("1122334455667788"),
            ExportedSessionKey = Hex.Bytes("000102030405060708090a0b0c0d0e0f"),
        },
    };

    [Theory]
    [InlineData("exchange-freerdp.txt", 6)]
    [InlineData("exchange-pyspnego-v2.txt", 2)]
    public void TheClientRepeatsTheExchangeByteForByte(string recording, int serverVersion)
    {
        var (key, fromClient, fromServer) = Read(recording);
        using var client = new CredSspClient("tester", "NLATEST", "Matgate-NLA-2026", "127.0.0.1", key, FixedClient);

        Assert.Equal(Hex.Text(fromClient[0]), Hex.Text(client.Start()));
        Assert.Equal(Hex.Text(fromClient[1]), Hex.Text(client.Next(fromServer[0])));
        Assert.Equal(serverVersion, client.ServerVersion);
        Assert.Equal(Hex.Text(fromClient[2]), Hex.Text(client.Next(fromServer[1])));
        Assert.True(client.IsComplete);
    }

    [Theory]
    [InlineData("exchange-freerdp.txt")]
    [InlineData("exchange-pyspnego-v2.txt")]
    public void WithAnotherSessionKeyTheServersAnswerIsWorthless(string recording)
    {
        // The server sealed its confirmation with the session key the client had made up and sent to it
        // under the key from the password - which only a server that knows the password can unpack. For a
        // client with any other session key, which is every other run, the recorded answer is noise.
        var (key, _, fromServer) = Read(recording);
        var options = new CredSspClientOptions
        {
            Workstation = FixedClient.Workstation,
            ClientNonce = FixedClient.ClientNonce,
            Ntlm = new NtlmClientOptions { ClientChallenge = FixedClient.Ntlm!.ClientChallenge, ExportedSessionKey = Hex.Bytes("0f0e0d0c0b0a09080706050403020100") },
        };

        using var client = new CredSspClient("tester", "NLATEST", "Matgate-NLA-2026", "127.0.0.1", key, options);
        client.Start();
        client.Next(fromServer[0]);

        var exception = Assert.Throws<CredSspException>(() => client.Next(fromServer[1]));
        Assert.IsType<NtlmException>(exception.InnerException);
        Assert.False(exception.IsLogonFailure);
    }

    [Theory]
    [InlineData("exchange-freerdp.txt")]
    [InlineData("exchange-pyspnego-v2.txt")]
    public void WithAnotherCertificateTheServersAnswerIsRefused(string recording)
    {
        // The same server, the same password - but the client saw a different key in its TLS handshake than
        // the one the server confirmed: somebody sits in between.
        var (key, _, fromServer) = Read(recording);
        key[^1] ^= 0x01;
        using var client = new CredSspClient("tester", "NLATEST", "Matgate-NLA-2026", "127.0.0.1", key, FixedClient);
        client.Start();
        client.Next(fromServer[0]);

        var exception = Assert.Throws<CredSspException>(() => client.Next(fromServer[1]));
        Assert.Null(exception.InnerException);
        Assert.Contains("certificate", exception.Message);
    }

    private static (byte[] Key, List<byte[]> FromClient, List<byte[]> FromServer) Read(string recording)
    {
        using var resource = typeof(RecordedExchangeTests).Assembly.GetManifestResourceStream(recording)
            ?? throw new InvalidOperationException($"The recording {recording} is not embedded in the test assembly.");
        using var reader = new StreamReader(resource);
        byte[]? key = null;
        var fromClient = new List<byte[]>();
        var fromServer = new List<byte[]>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "key":
                    key = Hex.Bytes(parts[1]);
                    break;
                case "client":
                    fromClient.Add(Hex.Bytes(parts[1]));
                    break;
                case "server":
                    fromServer.Add(Hex.Bytes(parts[1]));
                    break;
            }
        }

        Assert.NotNull(key);
        Assert.Equal(3, fromClient.Count);
        Assert.Equal(2, fromServer.Count);
        return (key, fromClient, fromServer);
    }
}
