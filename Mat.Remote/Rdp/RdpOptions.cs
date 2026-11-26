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

    // Bits a pixel asked for: 32 is full colour, as every session had it; 16 (or 15, 24) asks for fewer
    // colours - less for the server to draw and to send. The server decides; RdpClient.ColorDepth says what
    // it became.
    public int ColorDepth { get; init; } = 32;

    // What the desktop may spend on looks (the Client Info's performanceFlags): 0x180 has smoothed fonts and
    // desktop composition, and leaves wallpaper, animations and visual styles on. 0x01 no wallpaper, 0x02 no
    // window contents while dragging, 0x04 no menu animations, 0x08 no visual styles, 0x20 no cursor shadow,
    // 0x40 no blinking cursor, 0x80 smoothed fonts, 0x100 desktop composition.
    public uint PerformanceFlags { get; init; } = 0x180;

    // Two or more: the desktop spans these monitors (Client Monitor Data, MS-RDPBCGR 2.2.1.3.6), at most 16; the
    // desktop is then the rectangle around them. Fewer: one monitor of Width by Height.
    public IReadOnlyList<RdpMonitor> Monitors { get; init; } = [];
}

// One monitor of this computer in its pixels; Left and Top place it among the others.
public sealed record RdpMonitor(int Left, int Top, int Width, int Height, bool Primary);

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
