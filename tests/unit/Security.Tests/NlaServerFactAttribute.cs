using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

// A test that needs a real NLA server: skipped unless the environment variable names one.
public sealed class NlaServerFactAttribute : FactAttribute
{
    public NlaServerFactAttribute(string hostVariable)
    {
        Skip = NlaServer.ReasonToSkip(hostVariable);
    }

    // For tests that sign in with the accounts of nla-server/Dockerfile, which another server does not have.
    public bool OwnAccountsOnly
    {
        get;
        set
        {
            field = value;
            if (value && Skip is null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MATGATE_NLA_TEST_USER")))
            {
                Skip = "Needs the accounts of nla-server/Dockerfile, and MATGATE_NLA_TEST_USER names another account.";
            }
        }
    }
}

// The same for a test with several sets of data.
public sealed class NlaServerTheoryAttribute : TheoryAttribute
{
    public NlaServerTheoryAttribute(string hostVariable)
    {
        Skip = NlaServer.ReasonToSkip(hostVariable);
    }
}

internal static class NlaServer
{
    public static string? ReasonToSkip(string hostVariable) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(hostVariable))
            ? hostVariable + " is not set; the Dockerfiles next to the tests build the servers."
            : null;
}
