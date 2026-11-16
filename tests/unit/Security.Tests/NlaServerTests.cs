using Xunit;
using Xunit.Abstractions;

namespace Matgate.Display.Rdp.Security.Tests;

// CredSspClient against a real server that requires Network Level Authentication. Skipped unless
// MATGATE_NLA_TEST_HOST names one ("host" or "host:port"); nla-server/Dockerfile builds the server these
// tests were written against - FreeRDP's shadow server - with the accounts used below.
//
// For another server: MATGATE_NLA_TEST_USER, MATGATE_NLA_TEST_PASSWORD and, if it has one,
// MATGATE_NLA_TEST_DOMAIN name an account there. The tests for the further accounts of the Dockerfile are
// then skipped.
//
// FreeRDP's server always chooses PROTOCOL_HYBRID, also when the client offers the extended protocol, and
// then waits for the connection sequence. "Accepted" therefore means here: the server took the credentials
// and keeps the connection open - and DamagedCredentialsAreNotAccepted shows that it does look at them.
public class NlaServerTests(ITestOutputHelper output)
{
    public const string HostVariable = "MATGATE_NLA_TEST_HOST";

    private static string Server => Environment.GetEnvironmentVariable(HostVariable)!;

    private static string User => Environment.GetEnvironmentVariable("MATGATE_NLA_TEST_USER") ?? "tester";

    private static string Password => Environment.GetEnvironmentVariable("MATGATE_NLA_TEST_PASSWORD") ?? "Matgate-NLA-2026";

    private static string Domain => Environment.GetEnvironmentVariable("MATGATE_NLA_TEST_DOMAIN") ?? "";

    [NlaServerFact(HostVariable)]
    public async Task TheAccountIsAccepted()
    {
        Accepted(await NlaTestConnection.SignInAsync(Server, User, Domain, Password));
    }

    [NlaServerFact(HostVariable)]
    public async Task TheAccountIsAcceptedWithoutTheExtendedProtocol()
    {
        var outcome = await NlaTestConnection.SignInAsync(Server, User, Domain, Password, new NlaTestOptions { RequestedProtocols = NlaTestConnection.ProtocolHybrid | NlaTestConnection.ProtocolSsl });
        Assert.Equal(NlaTestConnection.ProtocolHybrid, outcome.SelectedProtocol);
        Accepted(outcome);
    }

    [NlaServerFact(HostVariable, OwnAccountsOnly = true)]
    public async Task TheAccountIsAcceptedWithADomain()
    {
        Accepted(await NlaTestConnection.SignInAsync(Server, "tester", "NLATEST", "Matgate-NLA-2026"));
    }

    [NlaServerFact(HostVariable, OwnAccountsOnly = true)]
    public async Task TheAccountIsAcceptedWithTheDomainInTheName()
    {
        Accepted(await NlaTestConnection.SignInAsync(Server, @"NLATEST\tester", "", "Matgate-NLA-2026"));
    }

    [NlaServerFact(HostVariable, OwnAccountsOnly = true)]
    public async Task APrincipalNameIsAccepted()
    {
        Accepted(await NlaTestConnection.SignInAsync(Server, "tester@nla.test", "", "Matgate-NLA-2026"));
    }

    [NlaServerFact(HostVariable, OwnAccountsOnly = true)]
    public async Task NamesAndPasswordsBeyondAsciiAreAccepted()
    {
        Accepted(await NlaTestConnection.SignInAsync(Server, "jürgen", "", "Grüße-€-2026"));
    }

    // The proof that "accepted" means something: the same exchange with one bit of the sealed credentials
    // flipped on the way. A server that reads what it is handed cannot take that.
    [NlaServerFact(HostVariable)]
    public async Task DamagedCredentialsAreNotAccepted()
    {
        try
        {
            var outcome = await NlaTestConnection.SignInAsync(Server, User, Domain, Password, new NlaTestOptions { DamageCredentials = true });
            output.WriteLine(outcome.Evidence);
            Assert.False(outcome.Accepted, outcome.Evidence);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            output.WriteLine("the server hung up: " + exception.GetType().Name);
        }
    }

    [NlaServerFact(HostVariable)]
    public async Task AWrongPasswordIsALogonFailure()
    {
        var exception = await Assert.ThrowsAsync<CredSspException>(() => NlaTestConnection.SignInAsync(Server, User, Domain, Password + "x"));
        output.WriteLine($"{exception.Message} [status {exception.Status:X8}]");
        Assert.True(exception.IsLogonFailure, exception.Message);
    }

    [NlaServerFact(HostVariable)]
    public async Task AnUnknownUserIsALogonFailure()
    {
        var exception = await Assert.ThrowsAsync<CredSspException>(() => NlaTestConnection.SignInAsync(Server, "nobody-" + Guid.NewGuid().ToString("N")[..8], Domain, Password));
        output.WriteLine($"{exception.Message} [status {exception.Status:X8}]");
        Assert.True(exception.IsLogonFailure, exception.Message);
    }

    private void Accepted(NlaOutcome outcome)
    {
        output.WriteLine(outcome.Evidence);
        Assert.True(outcome.Accepted, outcome.Evidence);
    }
}
