using System.Runtime.InteropServices;

namespace Matgate.Agent;

// What the agent's common part asks of the system it runs on. Everything that talks to Matgate - the
// connection, files, terminals, the logo - is the same on Windows, Linux and macOS; what differs is how a
// program gets a terminal, how the screen is shown, and how the agent replaces itself with a new version.

// A program in a pseudo console, wherever it runs.
public interface IPty
{
    event Action<byte[], int>? Output;

    event Action<int>? Exited;

    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);

    void Kill();
}

// Starts programs in terminals: ConPTY on Windows (in this process, or in the session of whoever is signed
// in), a pseudo terminal on Linux and macOS.
public interface IPtyStarter
{
    IPty Start(string id, string command, string? directory, int columns, int rows);
}

// The machine's screen for a viewer in Matgate - or the lack of one.
public interface IAgentScreen
{
    bool IsAvailable { get; }

    // "ok", or why the browser's microphone and controllers cannot be this machine's.
    string MicrophoneState { get; }

    string GamepadState { get; }

    Task<AgentFrame> OpenAsync(AgentFrame request, Func<AgentFrame, Task> send);

    Task<AgentFrame> ReadClipAsync(AgentFrame request);

    void Event(AgentFrame frame);

    void DetachAll(Func<AgentFrame, Task> send);
}

// A system the agent does not show the screen of (yet).
public sealed class NoScreen : IAgentScreen
{
    public bool IsAvailable => false;

    public string MicrophoneState => "off";

    public string GamepadState => "off";

    public Task<AgentFrame> OpenAsync(AgentFrame request, Func<AgentFrame, Task> send) =>
        throw new InvalidOperationException("This agent does not share its screen.");

    public Task<AgentFrame> ReadClipAsync(AgentFrame request) =>
        throw new InvalidOperationException("This agent does not share its screen.");

    public void Event(AgentFrame frame)
    {
    }

    public void DetachAll(Func<AgentFrame, Task> send)
    {
    }
}

// A new version, brought by Matgate over the connection it already has.
public interface IAgentUpdates
{
    bool CanUpdate { get; }

    AgentFrame Begin(AgentFrame request);

    AgentFrame Write(AgentFrame request);

    AgentFrame Apply(AgentFrame request, Action shutdown);
}

public static class AgentPlatform
{
    // Which build this is, as Matgate needs it to bring the right update: win-x64, linux-x64, linux-arm64,
    // osx-arm64, osx-x64.
    public static string Id
    {
        get
        {
            var system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => "x64",
            };
            return system + "-" + architecture;
        }
    }
}
