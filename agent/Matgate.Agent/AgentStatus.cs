using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Matgate.Agent;

// What the tray icon shows: whether the agent runs, on which port, whether Matgate is connected and how
// many terminals are open. Handed out over a local named pipe, like MatMon's agent - the tray runs in the
// user's session, the agent may run as a service in session 0. The pipe carries no secret.
public static class AgentStatus
{
    public const string PipeName = "MatgateAgentStatus";

    private static int _connections;
    private static string _lastRemote = "";
    private static DateTimeOffset? _lastConnected;

    public static int Port { get; set; }

    public static string Mode { get; set; } = "";

    public static Func<int> OpenTerminals { get; set; } = () => 0;

    public static void Connected(string remote)
    {
        Interlocked.Increment(ref _connections);
        _lastRemote = remote;
        _lastConnected = DateTimeOffset.Now;
    }

    public static void Disconnected() => Interlocked.Decrement(ref _connections);

    public sealed record Snapshot(string Version, int Port, string Mode, int Connections, string LastRemote, DateTimeOffset? LastConnected, int Terminals, DateTimeOffset Started);

    private static readonly DateTimeOffset Started = DateTimeOffset.Now;

    public static Snapshot Current() => new(
        typeof(AgentStatus).Assembly.GetName().Version?.ToString(3) ?? "0",
        Port, Mode, Volatile.Read(ref _connections), _lastRemote, _lastConnected, OpenTerminals(), Started);

    public static void StartServer(CancellationToken stopping)
    {
        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    // Every signed-in user may read it - the tray runs as the user, the agent perhaps as a service.
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                    await using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.Out, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                    await server.WaitForConnectionAsync(stopping);
                    var json = JsonSerializer.SerializeToUtf8Bytes(Current());
                    await server.WriteAsync(json, stopping);
                    server.WaitForPipeDrain();
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

    // For the tray: null when no agent answers.
    public static Snapshot? Query()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In);
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
