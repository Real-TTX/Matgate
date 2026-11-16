namespace Matgate.Display.Rdp.Security;

// Network Level Authentication did not get through. The message is a sentence a person can be shown.
public sealed class CredSspException : Exception
{
    public CredSspException(string message, bool isLogonFailure = false, uint? status = null, Exception? innerException = null)
        : base(message, innerException)
    {
        IsLogonFailure = isLogonFailure;
        Status = status;
    }

    // True when the server turned down the user name or the password, so that asking for them again makes
    // sense. False for everything another password would not change: an account that is locked or disabled,
    // a server that is not the one it claims to be, a message that does not parse.
    public bool IsLogonFailure { get; }

    // The NTSTATUS the server gave as its reason, when it gave one.
    public uint? Status { get; }
}
