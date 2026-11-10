using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// Frames over a pipe: [4 bytes length, little endian][one agent message as on the WebSocket].
internal sealed class FramePipe(Stream stream) : IDisposable
{
    private const int MaxFrame = 2 * AgentWire.MaxMessageBytes;
    private readonly SemaphoreSlim _write = new(1, 1);

    public async Task SendAsync(AgentFrame frame)
    {
        var body = AgentWire.Encode(frame);
        var length = BitConverter.GetBytes(body.Length);
        await _write.WaitAsync();
        try
        {
            await stream.WriteAsync(length);
            await stream.WriteAsync(body);
        }
        finally
        {
            _write.Release();
        }
    }

    // In order: whoever calls this waits until the frame is in the pipe (input must not overtake itself).
    public void Send(AgentFrame frame) => SendAsync(frame).GetAwaiter().GetResult();

    // Null when the other side is gone.
    public async Task<AgentFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken);
            var length = BitConverter.ToInt32(header);
            if (length is < 6 or > MaxFrame)
            {
                return null;
            }

            var body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return AgentWire.Decode(body);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ObjectDisposedException or InvalidDataException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        stream.Dispose();
        _write.Dispose();
    }
}

// The agent's end of a helper process (see SessionNative): starts it, talks to it through a named pipe only it
// and the helper may open, asks it things (requests with answers) and hears from it (events). When the helper
// ends - its session went away - whatever waited for an answer fails, and onExit says so.
internal sealed class HostLink : IDisposable
{
    private readonly string _role;
    private readonly Func<string, Process> _start;
    private readonly SecurityIdentifier? _peer;
    private readonly Action<AgentFrame> _onEvent;
    private readonly Action _onExit;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<AgentFrame>> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private FramePipe? _pipe;
    private Process? _process;
    private long _nextId;

    // `start` gets the helper's arguments and starts it; `peer` is the account the helper runs as, when it is
    // not this process's own.
    public HostLink(string role, Func<string, Process> start, SecurityIdentifier? peer, Action<AgentFrame> onEvent, Action onExit)
    {
        _role = role;
        _start = start;
        _peer = peer;
        _onEvent = onEvent;
        _onExit = onExit;
    }

    public bool IsRunning => _pipe is not null && _process is { HasExited: false };

    // The session the helper runs in (-1 when there is none).
    public int Session
    {
        get
        {
            try
            {
                return _process is { HasExited: false } process ? process.SessionId : -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }

    public async Task EnsureStartedAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (IsRunning)
            {
                return;
            }

            Stop();
            var name = $"matgate-agent-{_role}-{Guid.NewGuid():N}";
            var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            Process process;
            try
            {
                process = _start($"session-host --role {_role} --pipe {name}");
            }
            catch
            {
                server.Dispose();
                throw;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var connected = server.WaitForConnectionAsync(timeout.Token);
            var ended = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, ended) != connected || !connected.IsCompletedSuccessfully)
            {
                server.Dispose();
                TryKill(process);
                throw new InvalidOperationException($"The {_role} helper did not start on this machine.");
            }

            // The other end must be the process just started, not anyone else who found the name.
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var client) || client != process.Id)
            {
                server.Dispose();
                TryKill(process);
                throw new InvalidOperationException($"The {_role} helper could not be verified.");
            }

            _process = process;
            _pipe = new FramePipe(server);
            var pipe = _pipe;
            _ = Task.Run(() => ReadAsync(pipe));
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<AgentFrame> RequestAsync(string op, JsonObject data, ReadOnlyMemory<byte> payload = default, TimeSpan? timeout = null)
    {
        var pipe = _pipe ?? throw new InvalidOperationException($"The {_role} helper is not running.");
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<AgentFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await pipe.SendAsync(new AgentFrame { Id = id, Kind = "req", Op = op, Data = data, Payload = payload });
            var response = await answer.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(20));
            return response.Error is { } error ? throw new InvalidOperationException(error) : response;
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The {_role} helper did not answer.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException($"The {_role} helper has ended.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // An event to the helper, in order with every other one.
    public void Post(string op, JsonObject data, ReadOnlyMemory<byte> payload = default)
    {
        try
        {
            _pipe?.Send(new AgentFrame { Kind = "evt", Op = op, Data = data, Payload = payload });
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Gone; ReadAsync notices and reports it.
        }
    }

    private async Task ReadAsync(FramePipe pipe)
    {
        try
        {
            while (await pipe.ReceiveAsync(CancellationToken.None) is { } frame)
            {
                if (frame.Kind == "res")
                {
                    if (_pending.TryRemove(frame.Id, out var waiting))
                    {
                        waiting.TrySetResult(frame);
                    }
                }
                else if (frame.Op == "host.log")
                {
                    AgentLog.Write($"{_role} helper: {frame.Text("text")}");
                }
                else
                {
                    try
                    {
                        _onEvent(frame);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        AgentLog.Write($"{_role} helper event: {exception.Message}");
                    }
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_pipe, pipe))
            {
                _pipe = null;
            }

            pipe.Dispose();
            foreach (var waiting in _pending.Values)
            {
                waiting.TrySetException(new IOException("gone"));
            }

            _onExit();
        }
    }

    private PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (_peer is not null)
        {
            security.AddAccessRule(new PipeAccessRule(_peer, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }

        return security;
    }

    private void Stop()
    {
        _pipe?.Dispose();
        _pipe = null;
        if (_process is { } process)
        {
            TryKill(process);
            process.Dispose();
            _process = null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose() => Stop();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out int processId);
}

// The helper's side: `matgate-agent session-host --role screen|terminals --pipe <name>`, started by the agent
// (never by hand). It connects back, does what it is asked and ends when the agent's end of the pipe closes.
internal static class SessionHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index > 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var role = Option("--role");
        var name = Option("--pipe");
        if (name is null || role is not ("screen" or "terminals"))
        {
            return 2;
        }

        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10000);
        using var pipe = new FramePipe(client);
        // Nobody reads a helper's console: its log lines go into the agent's log.
        AgentLog.Sink = line => _ = pipe.SendAsync(new AgentFrame { Kind = "evt", Op = "host.log", Data = new JsonObject { ["text"] = line } });

        return role == "screen" ? await RunScreenAsync(pipe) : await RunTerminalsAsync(pipe);
    }

    private static async Task<int> RunScreenAsync(FramePipe pipe)
    {
        var plugin = new Screen.ScreenPlugin(new AgentConfig { Screen = true }, inThisProcess: true);
        Task Send(AgentFrame frame) => pipe.SendAsync(frame);

        // A helper belongs to the session it was started in: when the monitor shows another one (somebody
        // signed out, or took the console over remotely), it ends and the agent starts a new one there.
        using var watch = new System.Threading.Timer(_ =>
        {
            if (SessionNative.IsLocalSystem && SessionNative.ConsoleSession() is var console and >= 0 && console != SessionNative.CurrentSession)
            {
                Environment.Exit(0);
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        try
        {
            while (await pipe.ReceiveAsync(CancellationToken.None) is { } frame)
            {
                if (frame.Kind == "req")
                {
                    _ = Task.Run(async () =>
                    {
                        AgentFrame response;
                        try
                        {
                            response = frame.Op switch
                            {
                                "screen.open" => await plugin.OpenAsync(frame, Send),
                                "screen.clip.read" => await plugin.ReadClipAsync(frame),
                                _ => throw new InvalidOperationException("The screen helper does not know this."),
                            };
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            response = new AgentFrame { Id = frame.Id, Kind = "res", Op = frame.Op, Error = exception is InvalidOperationException ? exception.Message : "The screen could not be shared." };
                        }

                        await pipe.SendAsync(response);
                    });
                }
                else if (frame.Kind == "evt")
                {
                    plugin.Event(frame);
                }
            }
        }
        finally
        {
            plugin.DetachAll(Send);
        }

        return 0;
    }

    private static async Task<int> RunTerminalsAsync(FramePipe pipe)
    {
        var programs = new ConcurrentDictionary<string, ConPty>();
        while (await pipe.ReceiveAsync(CancellationToken.None) is { } frame)
        {
            var id = frame.Text("session") ?? "";
            switch (frame.Op)
            {
                case "pty.spawn":
                    AgentFrame response;
                    try
                    {
                        var directory = frame.Text("directory");
                        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                        {
                            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        }

                        var pty = new ConPty(frame.Text("command") ?? "", directory, frame.Int("cols", 120), frame.Int("rows", 32));
                        programs[id] = pty;
                        pty.Output += (buffer, count) => pipe.Send(new AgentFrame
                        {
                            Kind = "evt",
                            Op = "pty.data",
                            Data = new JsonObject { ["session"] = id },
                            Payload = buffer.AsSpan(0, count).ToArray(),
                        });
                        pty.Exited += code =>
                        {
                            programs.TryRemove(id, out _);
                            pipe.Send(new AgentFrame { Kind = "evt", Op = "pty.exit", Data = new JsonObject { ["session"] = id, ["code"] = code } });
                        };
                        response = new AgentFrame { Id = frame.Id, Kind = "res", Op = frame.Op };
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        response = new AgentFrame { Id = frame.Id, Kind = "res", Op = frame.Op, Error = "The program could not be started: " + exception.Message };
                    }

                    await pipe.SendAsync(response);
                    break;
                case "pty.input" when programs.TryGetValue(id, out var target):
                    target.Write(frame.Payload.Span);
                    break;
                case "pty.resize" when programs.TryGetValue(id, out var target):
                    target.Resize(frame.Int("cols", 120), frame.Int("rows", 32));
                    break;
                case "pty.kill" when programs.TryGetValue(id, out var target):
                    target.Kill();
                    break;
            }
        }

        // The agent is gone (restart, update): the programs end with it, as they would in user mode.
        foreach (var pty in programs.Values)
        {
            pty.Kill();
        }

        return 0;
    }
}
