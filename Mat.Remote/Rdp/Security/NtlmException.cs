namespace Matgate.Display.Rdp.Security;

// Something is wrong with what the server sent in the NTLM exchange: a message that does not parse, a feature
// this client insists on that the server did not grant, or a signature that does not verify.
public sealed class NtlmException : Exception
{
    public NtlmException(string message)
        : base(message)
    {
    }
}
