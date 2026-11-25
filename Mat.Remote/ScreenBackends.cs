namespace Matgate.Display;

// The way into the engine for its users (Matgate's ScreenSession, ThixGate's session view): the backend of a
// target's protocol. "vnc" is Matgate's own VNC client, everything else its own RDP client.
public static class ScreenBackends
{
    public static IScreenBackend Create(ScreenTarget target, IScreenHost host)
    {
        return target.Protocol == "vnc" ? new VncBackend(target, host) : new RdpBackend(target, host);
    }
}
