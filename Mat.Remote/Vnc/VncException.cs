namespace Matgate.Display.Vnc;

// Whatever goes wrong between Matgate and a VNC server ends here. The message is a sentence for the person in
// front of the screen: it is shown as it is.
public sealed class VncException : Exception
{
    public VncException(string message)
        : base(message)
    {
    }

    public VncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
