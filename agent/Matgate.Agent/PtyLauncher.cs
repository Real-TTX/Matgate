using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// A program in a pseudo console, wherever it runs.
public interface IPty
{
    event Action<byte[], int>? Output;

    event Action<int>? Exited;

    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);

    void Kill();
}

// Where programs start: in this process (user mode, or a service under an account - they run as that
// account), or, for a LocalSystem service, through a helper in the session of the person who is signed in, with
// that person's token: Claude Code then runs as them, with their login, exactly as in user mode - not as SYSTEM.
// MATGATE_AGENT_PTY_HOST=1 makes a test agent take the helper's way with a helper of its own user.
internal sealed class PtyLauncher
{
    private readonly bool _helper = OperatingSystem.IsWindows()
        && (SessionNative.IsSystemService || Environment.GetEnvironmentVariable("MATGATE_AGENT_PTY_HOST") == "1");

    private readonly object _gate = new();
    private readonly Dictionary<int, HostLink> _links = [];
    private readonly ConcurrentDictionary<string, RemotePty> _programs = new();

    public IPty Start(string id, string command, string? directory, int columns, int rows)
    {
        if (!_helper)
        {
            return new ConPty(command, directory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), columns, rows);
        }

        var (session, link) = LinkForSignedInUser();
        link.EnsureStartedAsync().GetAwaiter().GetResult();
        var program = new RemotePty(id, session, link);
        _programs[id] = program;
        try
        {
            link.RequestAsync("pty.spawn", new JsonObject
            {
                ["session"] = id,
                ["command"] = command,
                ["directory"] = directory,
                ["cols"] = columns,
                ["rows"] = rows,
            }).GetAwaiter().GetResult();
        }
        catch
        {
            _programs.TryRemove(id, out _);
            throw;
        }

        return program;
    }

    private (int Session, HostLink Link) LinkForSignedInUser()
    {
        if (!SessionNative.IsSystemService)
        {
            // The tests: one helper of this very user.
            lock (_gate)
            {
                if (!_links.TryGetValue(0, out var own))
                {
                    own = new HostLink("terminals", SessionNative.StartChild, null, OnEvent, () => OnExit(0));
                    _links[0] = own;
                }

                return (0, own);
            }
        }

        if (SessionNative.SignedInUser() is not { } user)
        {
            throw new InvalidOperationException(NobodySignedIn);
        }

        try
        {
            lock (_gate)
            {
                var session = user.Session;
                if (!_links.TryGetValue(session, out var link))
                {
                    link = new HostLink("terminals", arguments =>
                    {
                        // The token afresh at every start: the person may have signed out and in again meanwhile.
                        var token = SessionNative.UserToken(session) ?? throw new InvalidOperationException(NobodySignedIn);
                        try
                        {
                            return SessionNative.StartAsUser(token, arguments);
                        }
                        finally
                        {
                            CloseHandle(token);
                        }
                    }, SessionNative.UserOf(user.Token), OnEvent, () => OnExit(session));
                    _links[session] = link;
                }

                return (session, link);
            }
        }
        finally
        {
            CloseHandle(user.Token);
        }
    }

    private const string NobodySignedIn = "Nobody is signed in on this machine - programs start as the signed-in user, once someone is.";

    private void OnEvent(AgentFrame frame)
    {
        if (!_programs.TryGetValue(frame.Text("session") ?? "", out var program))
        {
            return;
        }

        if (frame.Op == "pty.data")
        {
            var bytes = frame.Payload.ToArray();
            program.RaiseOutput(bytes, bytes.Length);
        }
        else if (frame.Op == "pty.exit")
        {
            _programs.TryRemove(program.Id, out _);
            program.RaiseExit(frame.Int("code"));
        }
    }

    // The helper is gone (the person signed out): its programs went with it.
    private void OnExit(int session)
    {
        foreach (var program in _programs.Values.Where(program => program.Session == session).ToList())
        {
            _programs.TryRemove(program.Id, out _);
            program.RaiseExit(-1);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class RemotePty(string id, int session, HostLink link) : IPty
    {
        private int _exited;

        public event Action<byte[], int>? Output;

        public event Action<int>? Exited;

        public string Id { get; } = id;

        public int Session { get; } = session;

        public void Write(ReadOnlySpan<byte> data) => link.Post("pty.input", new JsonObject { ["session"] = Id }, data.ToArray());

        public void Resize(int columns, int rows) => link.Post("pty.resize", new JsonObject { ["session"] = Id, ["cols"] = columns, ["rows"] = rows });

        public void Kill()
        {
            if (link.IsRunning)
            {
                link.Post("pty.kill", new JsonObject { ["session"] = Id });
            }
            else
            {
                RaiseExit(-1);
            }
        }

        public void RaiseOutput(byte[] buffer, int count) => Output?.Invoke(buffer, count);

        public void RaiseExit(int code)
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0)
            {
                Exited?.Invoke(code);
            }
        }
    }
}
