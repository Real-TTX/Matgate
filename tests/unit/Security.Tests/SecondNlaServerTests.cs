using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Matgate.Display.Rdp.Security.Tests;

// CredSspClient against a second server that shares no code with FreeRDP: the CredSSP acceptor and the NTLM
// implementation of pyspnego (nla-server-pyspnego). Skipped unless MATGATE_NLA_TEST_SECOND_HOST names it.
//
// This server does three things FreeRDP's does not: it can announce the old versions of CredSSP, so the
// public key itself is sealed instead of its hash; in versions 2 and 5 it has no way to give a reason and
// just closes the connection when it refuses a logon; and it reports back what credentials it was handed.
public class SecondNlaServerTests(ITestOutputHelper output)
{
    public const string HostVariable = "MATGATE_NLA_TEST_SECOND_HOST";

    private const string Password = "Matgate-NLA-2026";

    private static string Server => Environment.GetEnvironmentVariable(HostVariable)!;

    [NlaServerTheory(HostVariable)]
    [InlineData("v6", 6)]
    [InlineData("v5", 5)]
    [InlineData("v4", 4)]
    [InlineData("v3", 3)]
    [InlineData("v2", 2)]
    public async Task EveryVersionOfTheExchangeHandsOverTheCredentials(string variant, int version)
    {
        var outcome = await SignInAsync(variant, "tester", "", Password);
        Assert.Equal(version, outcome.ServerVersion);
        Assert.Equal(NlaTestConnection.ProtocolHybrid, outcome.SelectedProtocol);
        Assert.Equal(Digest("", "tester", Password), outcome.CredentialsDigest);
    }

    [NlaServerFact(HostVariable)]
    public async Task TheExtendedProtocolEndsWithTheAuthorizationResult()
    {
        var outcome = await SignInAsync("v6ex", "tester", "", Password);
        Assert.Equal(NlaTestConnection.ProtocolHybridEx, outcome.SelectedProtocol);
        Assert.True(outcome.Accepted, outcome.Evidence);
        Assert.Equal(Digest("", "tester", Password), outcome.CredentialsDigest);
    }

    [NlaServerTheory(HostVariable)]
    [InlineData("tester", "NLATEST", "NLATEST", "tester")]
    [InlineData(@"NLATEST\tester", "", "NLATEST", "tester")]
    [InlineData("tester@nla.test", "", "", "tester@nla.test")]
    [InlineData("TeSTer", "", "", "TeSTer")]
    [InlineData("TESTER", "nlatest", "nlatest", "TESTER")]
    public async Task TheFormsOfAUserNameArriveAsTheSameAccount(string user, string domain, string expectedDomain, string expectedUser)
    {
        var outcome = await SignInAsync("v6", user, domain, Password);
        Assert.Equal(Digest(expectedDomain, expectedUser, Password), outcome.CredentialsDigest);
    }

    [NlaServerTheory(HostVariable)]
    [InlineData("v6")]
    [InlineData("v2")]
    public async Task NamesAndPasswordsBeyondAsciiArriveUnharmed(string variant)
    {
        var outcome = await SignInAsync(variant, "jürgen", "", "Grüße-€-2026");
        Assert.Equal(Digest("", "jürgen", "Grüße-€-2026"), outcome.CredentialsDigest);
    }

    // Versions 3, 4 and 6 answer with a status, versions 2 and 5 by closing the connection.
    [NlaServerTheory(HostVariable)]
    [InlineData("v6", true)]
    [InlineData("v5", false)]
    [InlineData("v4", true)]
    [InlineData("v3", true)]
    [InlineData("v2", false)]
    public async Task AWrongPasswordIsALogonFailureHoweverTheServerSaysIt(string variant, bool withStatus)
    {
        var exception = await Assert.ThrowsAsync<CredSspException>(() => SignInAsync(variant, "tester", "", Password + "x"));
        output.WriteLine($"{variant}: {exception.Message} [status {exception.Status:X8}]");
        Assert.True(exception.IsLogonFailure, exception.Message);
        Assert.Equal(withStatus, exception.Status is not null);
    }

    [NlaServerTheory(HostVariable)]
    [InlineData("v6")]
    [InlineData("v2")]
    public async Task AnUnknownUserIsALogonFailure(string variant)
    {
        var exception = await Assert.ThrowsAsync<CredSspException>(() => SignInAsync(variant, "nobody", "", Password));
        output.WriteLine($"{variant}: {exception.Message} [status {exception.Status:X8}]");
        Assert.True(exception.IsLogonFailure, exception.Message);
    }

    [NlaServerFact(HostVariable)]
    public async Task AnOldServerCanBeRefused()
    {
        var options = new NlaTestOptions { Cookie = "v4", ServerReportsCredentials = true, Client = new CredSspClientOptions { MinimumServerVersion = 5 } };
        var exception = await Assert.ThrowsAsync<CredSspException>(() => NlaTestConnection.SignInAsync(Server, "tester", "", Password, options));
        Assert.False(exception.IsLogonFailure);
    }

    private async Task<NlaOutcome> SignInAsync(string variant, string user, string domain, string password)
    {
        var outcome = await NlaTestConnection.SignInAsync(Server, user, domain, password, new NlaTestOptions { Cookie = variant, ServerReportsCredentials = true });
        output.WriteLine($"{variant}: {outcome.Evidence}");
        return outcome;
    }

    // What the server sends back: SHA-256 over the three parts of the credentials it decrypted.
    private static byte[] Digest(string domain, string user, string password) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{domain}\n{user}\n{password}"));
}
