namespace Matgate.Display.Vnc;

// The ways a server may pack a rectangle of pixels, by their number in the protocol.
public enum VncEncoding
{
    Raw = 0,
    CopyRect = 1,
    Rre = 2,
    Hextile = 5,
    Tight = 7,
    Zrle = 16,
}

public sealed class VncOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 5900;

    public string? Username { get; init; }

    public string? Password { get; init; }

    // For reaching the server and for every step of the handshake; the time a person takes to type a password
    // does not count.
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // Encodings in order of preference; null = the default order. Lets a test force one decoder.
    public IReadOnlyList<VncEncoding>? Encodings { get; init; }

    // Where a server encrypts with a certificate (VeNCrypt's X509 types): whether that certificate has to be
    // one this machine trusts and made out to Host. Off, because VNC servers come with a certificate they
    // signed themselves; Matgate's connections decide about that on their own.
    public bool ValidateCertificate { get; init; }
}

public sealed record VncCredentials(string? Username, string Password);
