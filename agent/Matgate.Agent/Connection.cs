using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// One connection from Matgate. Requests are answered concurrently (a file copy must not hold up a
// terminal); every send goes through one lock, because a WebSocket takes one message at a time.
public sealed class Connection(WebSocket socket, AgentConfig config, string version, FilesPlugin files, TerminalPlugin terminals)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(Hello());
            while (socket.State == WebSocketState.Open)
            {
                var frame = await AgentWire.ReceiveAsync(socket, cancellationToken);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Kind)
                {
                    case "req":
                        _ = Task.Run(() => AnswerAsync(frame, cancellationToken), cancellationToken);
                        break;
                    case "evt" when frame.Op == "pty.input":
                        terminals.Input(frame);
                        break;
                    case "evt" when frame.Op == "pty.resize":
                        terminals.Resize(frame);
                        break;
                    case "evt" when frame.Op == "pty.detach":
                        terminals.Detach(frame);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidDataException)
        {
            // Gone - the sessions keep running for the next connection.
        }
        finally
        {
            terminals.DetachAll(SendAsync);
            files.DropAllUploads();
        }
    }

    private async Task AnswerAsync(AgentFrame request, CancellationToken cancellationToken)
    {
        AgentFrame response;
        try
        {
            response = request.Op switch
            {
                "hello" => Hello(request.Id),
                "pty.open" => terminals.Open(request, SendAsync),
                "pty.kill" => terminals.Kill(request),
                "pty.list" => terminals.List(request),
                _ when request.Op.StartsWith("files.", StringComparison.Ordinal) && files.IsAvailable
                    => await files.HandleAsync(request, cancellationToken),
                _ => throw new InvalidOperationException("The agent does not offer this."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Error = Explain(exception) };
        }

        try
        {
            await SendAsync(response);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
        {
        }
    }

    private AgentFrame Hello(long id = 0)
    {
        var plugins = new JsonArray();
        if (files.IsAvailable)
        {
            plugins.Add(new JsonObject { ["id"] = "files", ["kind"] = "files", ["title"] = "Dateien" });
        }

        foreach (var terminal in terminals.Available)
        {
            plugins.Add(new JsonObject { ["id"] = terminal.Id, ["kind"] = "terminal", ["title"] = string.IsNullOrWhiteSpace(terminal.Title) ? terminal.Id : terminal.Title });
        }

        return new AgentFrame
        {
            Id = id,
            Kind = id == 0 ? "evt" : "res",
            Op = "hello",
            Data = new JsonObject
            {
                ["protocol"] = AgentWire.ProtocolVersion,
                ["version"] = version,
                ["name"] = string.IsNullOrWhiteSpace(config.Name) ? Environment.MachineName : config.Name,
                ["machine"] = Environment.MachineName,
                ["user"] = Environment.UserName,
                ["os"] = RuntimeInformation.OSDescription,
                ["plugins"] = plugins,
            },
        };
    }

    private async Task SendAsync(AgentFrame frame)
    {
        await _sendLock.WaitAsync();
        try
        {
            await AgentWire.SendAsync(socket, frame, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // The sentence a person sees in Matgate. The machine's own messages may name local paths, so only
    // the kinds of failure that are the user's to fix are passed on as they are.
    private static string Explain(Exception exception) => exception switch
    {
        InvalidOperationException => exception.Message,
        FileNotFoundException or DirectoryNotFoundException => "This path does not exist.",
        UnauthorizedAccessException => "No permission for this path on the machine.",
        IOException => "The file is in use or could not be written.",
        _ => "The agent could not do this.",
    };
}
