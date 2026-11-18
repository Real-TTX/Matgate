using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Matgate.Agent;

// What the tray icon shows: whether the agent runs, on which port and how many terminals are open. Whether
// Matgate is connected says little (Matgate keeps its connection open while idle) - that is in the log. Handed out over a local named pipe, like MatMon's agent - the tray runs in the
// user's session, the agent may run as a service in session 0. The pipe carries a snapshot and never a secret: no token, no password,
// no certificate (AGENT-03) - every signed-in user may read it.
public static class AgentStatus
{
    // The status channel of the installed agent, named as the Mat family names it (AGENT-01): matgate-agent-status.
    public static readonly string InstalledPipeName = OperatingSystem.IsWindows() ? "matgate-agent-status" : "/tmp/matgate-agent-status";

    // An agent started by hand from a folder (a test beside an installed one) takes a pipe of its own per port, so that two
    // agents on one machine do not answer for each other.
    public static string PipeName(int port) => InstalledPipeName + "-" + port;

    // The name up to version 0.7: the installed agent answers on it too, so that the tray of an older version - which starts
    // again from the new program file once it sees the agent run - finds the updated agent.
    private static string LegacyPipeName(int port) => "MatgateAgentStatus-" + port;

    public static int Port { get; set; }

    // True for the installed agent (its settings in %ProgramData%\Matgate Agent).
    public static bool Installed { get; set; }

    public static string Mode { get; set; } = "";

    public static Func<int> OpenTerminals { get; set; } = () => 0;

    public sealed record Snapshot(string Version, int Port, string Mode, int Terminals, DateTimeOffset Started);

    private static readonly DateTimeOffset Started = DateTimeOffset.Now;

    public static Snapshot Current() => new(
        typeof(AgentStatus).Assembly.GetName().Version?.ToString() ?? "0",
        Port, Mode, OpenTerminals(), Started);

    public static void StartServer(CancellationToken stopping)
    {
        foreach (var name in !Installed ? [PipeName(Port)] : OperatingSystem.IsWindows() ? [InstalledPipeName, LegacyPipeName(Port)] : new[] { InstalledPipeName })
        {
            Serve(name, stopping);
        }
    }

    private static void Serve(string name, CancellationToken stopping)
    {
        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await using var server = Open(name);
                    await server.WaitForConnectionAsync(stopping);
                    var json = JsonSerializer.SerializeToUtf8Bytes(Current());
                    await server.WriteAsync(json, stopping);
                    if (OperatingSystem.IsWindows())
                    {
                        server.WaitForPipeDrain();
                    }
                    else
                    {
                        await server.FlushAsync(stopping);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(1000, stopping).ContinueWith(_ => { });
                }
            }
        }, stopping);
    }

    // Every signed-in user may read it - the tray runs as the user, the agent perhaps as a service. (On
    // Linux and macOS a named pipe is a socket file; who may read it is the file system's to say.)
    private static NamedPipeServerStream Open(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            // A full path is the socket file itself. Whoever is signed in may ask (the status carries no secret).
            if (File.Exists(name))
            {
                try
                {
                    File.Delete(name);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                File.SetUnixFileMode(name, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            return pipe;
        }

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.Out, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    // For the tray: the installed agent, or null when it does not answer.
    public static Snapshot? QueryInstalled() => Read(InstalledPipeName);

    // The agent on a port: the installed one when it listens there, else one started by hand. Null when none answers.
    public static Snapshot? Query(int port) =>
        QueryInstalled() is { } installed && installed.Port == port ? installed : Read(PipeName(port));

    private static Snapshot? Read(string name)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.In);
            client.Connect(500);
            using var reader = new StreamReader(client, Encoding.UTF8);
            return JsonSerializer.Deserialize<Snapshot>(reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
