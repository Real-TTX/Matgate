using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// Terminals: one plugin per configured program (Claude Code, PowerShell, ...). A session outlives the
// browser that started it - a phone that loses the connection attaches again and sees what happened
// in the meantime (the last part of the output is kept for that).
public sealed class TerminalPlugin
{
    private const int BacklogBytes = 256 * 1024;

    private readonly AgentConfig _config;
    private readonly FilesPlugin _files;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Timer _reaper;

    public TerminalPlugin(AgentConfig config, FilesPlugin files)
    {
        _config = config;
        _files = files;
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public IEnumerable<AgentTerminal> Available => OperatingSystem.IsWindows()
        ? _config.Terminals.Where(terminal => terminal.Enabled && !string.IsNullOrWhiteSpace(terminal.Id) && !string.IsNullOrWhiteSpace(terminal.Command))
        : [];

    // Opens a session, or attaches to the one with this id. Output goes to `send` from then on.
    public AgentFrame Open(AgentFrame request, Func<AgentFrame, Task> send)
    {
        var plugin = request.Text("plugin") ?? "";
        var id = request.Text("session");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            id = Guid.NewGuid().ToString("N");
        }

        var columns = request.Int("cols", 120);
        var rows = request.Int("rows", 32);

        if (_sessions.TryGetValue(id, out var existing))
        {
            if (!string.Equals(existing.Plugin, plugin, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This session belongs to another program.");
            }

            existing.Attach(send);
            if (OperatingSystem.IsWindows())
            {
                existing.Pty.Resize(columns, rows);
            }
            return Result(request, id, attached: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Terminals are only available on Windows so far.");
        }

        var terminal = Available.FirstOrDefault(candidate => string.Equals(candidate.Id, plugin, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("This program is not offered by the agent.");
        var directory = _files.DirectoryFor(request.Text("cwd"))
            ?? _files.FirstRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var pty = new ConPty(terminal.Command, directory, columns, rows);
        var session = new Session(id, plugin, pty);
        _sessions[id] = session;
        pty.Output += session.OnOutput;
        pty.Exited += code =>
        {
            _sessions.TryRemove(id, out _);
            session.OnExit(code);
        };
        session.Attach(send);
        return Result(request, id, attached: false);
    }

    public void Input(AgentFrame frame)
    {
        if (OperatingSystem.IsWindows() && _sessions.TryGetValue(frame.Text("session") ?? "", out var session))
        {
            session.Pty.Write(frame.Payload.Span);
        }
    }

    public void Resize(AgentFrame frame)
    {
        if (OperatingSystem.IsWindows() && _sessions.TryGetValue(frame.Text("session") ?? "", out var session))
        {
            session.Pty.Resize(frame.Int("cols", 120), frame.Int("rows", 32));
        }
    }

    public void Detach(AgentFrame frame)
    {
        if (_sessions.TryGetValue(frame.Text("session") ?? "", out var session))
        {
            session.Detach();
        }
    }

    // A connection from Matgate went away: whatever it was showing keeps running, detached.
    public void DetachAll(Func<AgentFrame, Task> send)
    {
        foreach (var session in _sessions.Values)
        {
            session.Detach(send);
        }
    }

    public AgentFrame Kill(AgentFrame request)
    {
        if (OperatingSystem.IsWindows() && _sessions.TryGetValue(request.Text("session") ?? "", out var session))
        {
            session.Pty.Kill();
        }

        return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op };
    }

    public AgentFrame List(AgentFrame request)
    {
        var list = new JsonArray();
        foreach (var session in _sessions.Values)
        {
            list.Add(new JsonObject
            {
                ["session"] = session.Id,
                ["plugin"] = session.Plugin,
                ["started"] = session.Started.ToUnixTimeMilliseconds(),
                ["attached"] = session.IsAttached,
            });
        }

        return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Data = new JsonObject { ["sessions"] = list } };
    }

    private void Reap()
    {
        if (_config.DetachedSessionMinutes <= 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        var limit = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(_config.DetachedSessionMinutes);
        foreach (var session in _sessions.Values.Where(session => !session.IsAttached && session.DetachedSince < limit))
        {
            session.Pty.Kill();
        }
    }

    private static AgentFrame Result(AgentFrame request, string id, bool attached)
    {
        return new AgentFrame
        {
            Id = request.Id,
            Kind = "res",
            Op = request.Op,
            Data = new JsonObject { ["session"] = id, ["attached"] = attached },
        };
    }

    private sealed class Session(string id, string plugin, ConPty pty)
    {
        private readonly object _gate = new();
        private readonly Queue<byte[]> _backlog = new();
        private int _backlogSize;
        private Func<AgentFrame, Task>? _send;

        public string Id { get; } = id;

        public string Plugin { get; } = plugin;

        public ConPty Pty { get; } = pty;

        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset DetachedSince { get; private set; } = DateTimeOffset.UtcNow;

        public bool IsAttached
        {
            get
            {
                lock (_gate)
                {
                    return _send is not null;
                }
            }
        }

        public void Attach(Func<AgentFrame, Task> send)
        {
            byte[] replay;
            lock (_gate)
            {
                _send = send;
                replay = _backlog.SelectMany(chunk => chunk).ToArray();
            }

            // What happened while nobody was looking. The program redraws on the resize that follows.
            if (replay.Length > 0)
            {
                _ = send(Data(replay));
            }
        }

        public void Detach(Func<AgentFrame, Task>? onlyFor = null)
        {
            lock (_gate)
            {
                // Delegates compare by target and method, so a fresh delegate for the same connection matches.
                if (onlyFor is null || Equals(_send, onlyFor))
                {
                    _send = null;
                    DetachedSince = DateTimeOffset.UtcNow;
                }
            }
        }

        public void OnOutput(byte[] buffer, int count)
        {
            var chunk = buffer.AsSpan(0, count).ToArray();
            Func<AgentFrame, Task>? send;
            lock (_gate)
            {
                _backlog.Enqueue(chunk);
                _backlogSize += chunk.Length;
                while (_backlogSize > BacklogBytes && _backlog.Count > 1)
                {
                    _backlogSize -= _backlog.Dequeue().Length;
                }

                send = _send;
            }

            if (send is not null)
            {
                // The read loop waits for the send, so output can never overtake itself.
                try
                {
                    send(Data(chunk)).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Matgate went away mid-send: keep running detached, the backlog has the output.
                    Detach(send);
                }
            }
        }

        public void OnExit(int code)
        {
            Func<AgentFrame, Task>? send;
            lock (_gate)
            {
                send = _send;
                _send = null;
            }

            send?.Invoke(new AgentFrame
            {
                Kind = "evt",
                Op = "pty.exit",
                Data = new JsonObject { ["session"] = Id, ["code"] = code },
            });
        }

        private AgentFrame Data(byte[] bytes) => new()
        {
            Kind = "evt",
            Op = "pty.data",
            Data = new JsonObject { ["session"] = Id },
            Payload = bytes,
        };
    }
}
