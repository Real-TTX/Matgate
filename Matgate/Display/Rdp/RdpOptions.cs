namespace Matgate.Display.Rdp;

public sealed class RdpOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 3389;

    // "user", "DOMAIN\user" or "user@domain"; a separate Domain wins over one written into the name.
    public string UserName { get; init; } = "";

    public string Domain { get; init; } = "";

    public string Password { get; init; } = "";

    // The size asked for. The server decides; RdpClient.Width and Height say what it became.
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 800;

    // Windows' keyboard layout id, e.g. 0x407 for German.
    public uint KeyboardLayout { get; init; } = 0x409;

    // RDP servers nearly always present a certificate they made themselves. When false, the certificate has to
    // be one this machine trusts and has to carry the host's name.
    public bool IgnoreCertificate { get; init; } = true;

    public string ClientName { get; init; } = "Matgate";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // Whether the remote desktop should send its sound at all (the sound channel has to be added as well).
    public bool Sound { get; init; }
}

// What the session tells whoever shows it. All of these are called on the session's read loop: they must
// return quickly and must not call back into the client in a way that waits for the server.
public interface IRdpHandler
{
    // A rectangle of the framebuffer changed (it is already in RdpClient.Framebuffer).
    void Updated(int x, int y, int width, int height);

    // The desktop has a new size; RdpClient.Framebuffer was replaced.
    void Resized(int width, int height);

    // The pointer's shape: RGBA, R first, straight alpha, top-down.
    void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba);

    void CursorHidden();

    void CursorDefault();
}
