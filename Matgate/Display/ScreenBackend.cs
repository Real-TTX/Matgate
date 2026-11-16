using System.Text.Json.Nodes;

namespace Matgate.Display;

// What a screen session connects to, and how the viewer wants it shown at first.
public sealed class ScreenTarget
{
    // "rdp" or "vnc".
    public required string Protocol { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; }

    public string UserName { get; init; } = "";

    public string Password { get; init; } = "";

    public string Domain { get; init; } = "";

    public bool IgnoreCertificate { get; init; } = true;

    // Windows' keyboard layout id (RDP).
    public uint KeyboardLayout { get; init; } = 0x409;

    public string Quality { get; init; } = "auto";

    // "auto" (fit the viewer's window), "native" (as connected) or "WxH".
    public string Resolution { get; init; } = "auto";

    public int FitWidth { get; init; }

    public int FitHeight { get; init; }

    public string Session { get; init; } = "";
}

// Something went wrong that the viewer should read as it is.
public sealed class ScreenException(string message) : Exception(message);

public sealed record ScreenCredentials(string UserName, string Password, string Domain);

// A piece of a file from the remote clipboard: the file's whole size, its name, and the bytes from the offset asked for.
public sealed record ClipPiece(long Size, string Name, ReadOnlyMemory<byte> Data);

// Looks at the current picture: BGRX, 4 bytes a pixel, top-down. The span is only valid inside the call.
public delegate void FrameReader(ReadOnlySpan<byte> bgrx, int stride, int width, int height);

// What a session offers the protocol behind it.
public interface IScreenHost
{
    // A part of the picture changed.
    void Dirty(int x, int y, int width, int height);

    // The remote pointer's shape (RGBA, R first, straight alpha), that it is hidden, or that it is the plain arrow.
    void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba);

    void CursorHidden(bool hidden);

    void CursorDefault();

    // A message for the viewer's page (screen.clip, screen.clip.done ...).
    void Event(string op, JsonObject data, ReadOnlyMemory<byte> payload = default);

    void Status(string state, string? message = null);

    // Asks the viewer for what is missing to sign in. `need` names the fields ("username", "password", "domain").
    // Null: the viewer gave up or did not answer.
    Task<ScreenCredentials?> AskAsync(string[] need, string userName, string domain, bool failed, CancellationToken cancellationToken);

    // Sound from the remote desktop: interleaved 16-bit samples.
    void Audio(ReadOnlySpan<byte> pcm, int rate, int channels);

    // The size the viewer wants right now; (0, 0) when it leaves the desktop as it is.
    (int Width, int Height) WantedSize();

    void Log(string message);
}

// What a remote desktop protocol has to provide.
public interface IScreenBackend : IAsyncDisposable
{
    // Shown as the monitor's name.
    string Name { get; }

    // What this protocol's clipboard takes: pictures (as a Windows bitmap), files.
    bool ClipImages { get; }

    bool ClipFiles { get; }

    bool Sound { get; }

    // Connects and signs in, asking the host for credentials where needed. Throws ScreenException.
    Task ConnectAsync(CancellationToken cancellationToken);

    // Runs until the remote side ends the session or the token is cancelled. Returns why it ended, if known.
    Task<string?> RunAsync(CancellationToken cancellationToken);

    // Hands the current picture to the reader; false when there is none yet.
    bool WithFrame(FrameReader reader);

    void Input(JsonObject data);

    void Resize(int width, int height);

    // The viewer's clipboard: screen.clip.set, .begin, .chunk, .end.
    void Clip(string op, JsonObject data, ReadOnlyMemory<byte> payload);

    Task<ClipPiece> ClipReadAsync(string token, int index, long offset, CancellationToken cancellationToken);

    void SecureAttention();

    void SoundEnabled(bool on);
}

// Reading the page's messages: numbers arrive as whatever JavaScript made of them.
internal static class ScreenJson
{
    public static int Int(JsonObject? data, string name, int fallback = 0)
    {
        return (int)Math.Clamp(Long(data, name, fallback), int.MinValue, int.MaxValue);
    }

    public static long Long(JsonObject? data, string name, long fallback = 0)
    {
        if (data is null || !data.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return double.IsFinite(number) ? (long)Math.Round(number) : fallback;
        }

        return value.TryGetValue<bool>(out var flag) ? (flag ? 1 : 0) : fallback;
    }

    public static bool Flag(JsonObject? data, string name)
    {
        return Long(data, name) != 0;
    }

    public static string Text(JsonObject? data, string name, string fallback = "")
    {
        return data is not null && data.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : fallback;
    }

    public static bool Has(JsonObject? data, string name)
    {
        return data is not null && data.TryGetPropertyValue(name, out var node) && node is not null;
    }
}
