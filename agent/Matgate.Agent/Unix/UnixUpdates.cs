using System.Diagnostics;
using System.Security.Cryptography;

namespace Matgate.Agent;

// A new version on Linux and macOS, brought by Matgate over its connection - the same three steps as on
// Windows (begin, the file in pieces, apply) and the same checks: size, SHA-256, and that the new program
// answers --version with the version it was announced as.
//
// Replacing is simpler here: a running program's file can be exchanged under it. The new file is moved
// over the old one (the old one kept beside it), the agent ends, and the service manager - systemd,
// launchd - starts it again, now the new version. An agent that was started by hand has nobody to start
// it again and does not update itself.
public sealed class UnixUpdates(string configPath) : IAgentUpdates
{
    private readonly object _gate = new();
    private FileStream? _incoming;
    private string _expectedVersion = "";
    private string _expectedSha256 = "";
    private long _expectedSize;

    private string UpdateFolder => Path.Combine(Path.GetDirectoryName(configPath)!, "update");

    // The installed single-file program, run by the service manager.
    public bool CanUpdate =>
        string.IsNullOrEmpty(typeof(UnixUpdates).Assembly.Location)
        && Environment.ProcessPath is { } program
        && string.Equals(Path.GetFullPath(program), UnixInstaller.InstalledProgram, StringComparison.Ordinal)
        && File.Exists(UnixInstaller.UnitPath);

    public AgentFrame Begin(AgentFrame request)
    {
        if (!CanUpdate)
        {
            throw new InvalidOperationException("This agent cannot update itself (it is not the installed service).");
        }

        lock (_gate)
        {
            _incoming?.Dispose();
            Directory.CreateDirectory(UpdateFolder);
            _expectedVersion = request.Text("version") ?? "";
            _expectedSha256 = (request.Text("sha256") ?? "").ToUpperInvariant();
            _expectedSize = request.Long("size");
            if (!Version.TryParse(_expectedVersion, out _) || _expectedSha256.Length != 64 || _expectedSize <= 0)
            {
                throw new InvalidOperationException("The update is not described completely.");
            }

            _incoming = new FileStream(Path.Combine(UpdateFolder, "matgate-agent.new"), FileMode.Create, FileAccess.Write);
        }

        AgentLog.Write($"Receiving update {_expectedVersion} ({_expectedSize / 1024} KB)");
        return Result(request);
    }

    public AgentFrame Write(AgentFrame request)
    {
        lock (_gate)
        {
            (_incoming ?? throw new InvalidOperationException("No update is being received.")).Write(request.Payload.Span);
            if (_incoming.Length > _expectedSize)
            {
                throw new InvalidOperationException("The update is larger than announced.");
            }
        }

        return Result(request);
    }

    public AgentFrame Apply(AgentFrame request, Action shutdown)
    {
        string incoming;
        lock (_gate)
        {
            if (_incoming is null)
            {
                throw new InvalidOperationException("No update is being received.");
            }

            incoming = _incoming.Name;
            _incoming.Dispose();
            _incoming = null;
        }

        var size = new FileInfo(incoming).Length;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(incoming)));
        if (size != _expectedSize || hash != _expectedSha256)
        {
            File.Delete(incoming);
            throw new InvalidOperationException("The update arrived damaged (size or SHA-256 differ) and was discarded.");
        }

        File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var answered = AskVersion(incoming);
        if (!Version.TryParse(answered, out var version) || version != Version.Parse(_expectedVersion))
        {
            File.Delete(incoming);
            throw new InvalidOperationException($"The new program says it is version \"{answered}\", not {_expectedVersion} - discarded.");
        }

        var target = Environment.ProcessPath!;
        // The version that runs now stays beside the new one: a new one that does not come up can be put back by hand.
        File.Copy(target, target + ".previous", overwrite: true);
        // On the same disk as the target, so the last step is one rename.
        var staged = target + ".new";
        File.Copy(incoming, staged, overwrite: true);
        File.SetUnixFileMode(staged, File.GetUnixFileMode(incoming));
        File.Move(staged, target, overwrite: true);
        File.Delete(incoming);
        AgentLog.Write($"Update {_expectedVersion} checked and in place - restarting with it");
        // Answer first, then go: Matgate is told before the connection drops. The service manager starts the agent again.
        _ = Task.Delay(1500).ContinueWith(_ => shutdown());
        return Result(request);
    }

    private static string AskVersion(string program)
    {
        var info = new ProcessStartInfo(program, "--version") { UseShellExecute = false, RedirectStandardOutput = true };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        if (!process.WaitForExit(20000))
        {
            process.Kill();
            return "";
        }

        return output;
    }

    private static AgentFrame Result(AgentFrame request) => new() { Id = request.Id, Kind = "res", Op = request.Op };
}
