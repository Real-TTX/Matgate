using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Matgate.Agent.Setup;

namespace Matgate.Agent;

// Updates come from Matgate, over the connection it already has: the new exe in chunks, then "apply".
// The agent trusts its Matgate that far anyway - Matgate can start programs on it - so an update through
// the same authenticated channel adds no new door. What it checks itself: size, SHA-256, that the new exe
// answers --version with the version it was announced as, and - after the swap - that it really comes up;
// otherwise the old one is put back (see Run below, the part that runs as a separate process).
public sealed class UpdatePlugin(AgentConfig config, string configPath)
{
    private readonly object _gate = new();
    private FileStream? _incoming;
    private string _expectedVersion = "";
    private string _expectedSha256 = "";
    private long _expectedSize;

    private static string UpdateFolder(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "update");

    // Only the installed, single-file agent can replace itself; a development build is a folder of files.
    public static bool CanUpdate()
    {
        if (!string.IsNullOrEmpty(typeof(UpdatePlugin).Assembly.Location) || Environment.ProcessPath is not { } exe)
        {
            return false;
        }

        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exe)!, ".matgate-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public AgentFrame Begin(AgentFrame request)
    {
        if (!CanUpdate())
        {
            throw new InvalidOperationException("This agent cannot update itself (not the installed single-file program, or its folder is not writable).");
        }

        lock (_gate)
        {
            _incoming?.Dispose();
            Directory.CreateDirectory(UpdateFolder(configPath));
            _expectedVersion = request.Text("version") ?? "";
            _expectedSha256 = (request.Text("sha256") ?? "").ToUpperInvariant();
            _expectedSize = request.Long("size");
            if (!Version.TryParse(_expectedVersion, out _) || _expectedSha256.Length != 64 || _expectedSize <= 0)
            {
                throw new InvalidOperationException("The update is not described completely.");
            }

            _incoming = new FileStream(Path.Combine(UpdateFolder(configPath), "matgate-agent.new.exe"), FileMode.Create, FileAccess.Write);
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

    // Checks the new exe and hands over to the updater. The agent then ends; the updater swaps the files
    // and starts it again.
    public AgentFrame Apply(AgentFrame request, Action shutdown)
    {
        string newExe;
        lock (_gate)
        {
            if (_incoming is null)
            {
                throw new InvalidOperationException("No update is being received.");
            }

            newExe = _incoming.Name;
            _incoming.Dispose();
            _incoming = null;
        }

        var size = new FileInfo(newExe).Length;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(newExe)));
        if (size != _expectedSize || hash != _expectedSha256)
        {
            File.Delete(newExe);
            throw new InvalidOperationException("The update arrived damaged (size or SHA-256 differ) and was discarded.");
        }

        var answered = AskVersion(newExe);
        if (!Version.TryParse(answered, out var newVersion) || newVersion != Version.Parse(_expectedVersion))
        {
            File.Delete(newExe);
            throw new InvalidOperationException($"The new program says it is version \"{answered}\", not {_expectedVersion} - discarded.");
        }

        // The updater is a copy of the NEW exe: whatever it does, it does with the new code.
        var updater = Path.Combine(UpdateFolder(configPath), "matgate-agent-updater.exe");
        File.Copy(newExe, updater, overwrite: true);
        var info = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[]
        {
            "apply-update",
            "--target", Environment.ProcessPath!,
            "--new", newExe,
            "--pid", Environment.ProcessId.ToString(),
            "--port", config.Port.ToString(),
            "--version", _expectedVersion,
            "--mode", AgentStatus.Mode,
            "--config", configPath,
        })
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info);
        AgentLog.Write($"Update {_expectedVersion} checked - restarting with it");
        // Answer first, then go: Matgate is told before the connection drops.
        _ = Task.Delay(1500).ContinueWith(_ => shutdown());
        return Result(request);
    }

    private static string AskVersion(string exe)
    {
        var info = new ProcessStartInfo(exe, "--version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
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

    // matgate-agent apply-update ...: the separate process that swaps the program and watches the new one
    // come up. Without that, the old program is put back.
    public static int Run(string[] args)
    {
        string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? "";
        var target = Arg("--target");
        var newExe = Arg("--new");
        var port = int.TryParse(Arg("--port"), out var p) ? p : 7488;
        var version = Version.Parse(Arg("--version"));
        var mode = Arg("--mode");
        var configPath = Arg("--config");
        AgentLog.UseFile(Path.GetDirectoryName(configPath)!);
        var backup = target + ".bak";

        try
        {
            if (int.TryParse(Arg("--pid"), out var pid))
            {
                try
                {
                    Process.GetProcessById(pid).WaitForExit(30000);
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }

            Retry(() => File.Copy(target, backup, overwrite: true));
            Retry(() => File.Copy(newExe, target, overwrite: true));
            AgentLog.Write($"Updater: program replaced with {version}");
            Start(target, mode, configPath);
            if (WaitForVersion(port, version, TimeSpan.FromSeconds(120)))
            {
                AgentLog.Write($"Updater: {version} is running");
                File.Delete(newExe);
                return 0;
            }

            AgentLog.Write($"Updater: {version} did not come up - putting the previous version back");
            StopFrom(target);
            Retry(() => File.Copy(backup, target, overwrite: true));
            Start(target, mode, configPath);
            return 1;
        }
        catch (Exception exception)
        {
            AgentLog.Write("Updater failed: " + exception.Message);
            if (File.Exists(backup) && !File.Exists(target))
            {
                File.Copy(backup, target);
            }

            Start(target, mode, configPath);
            return 1;
        }
    }

    private static void Start(string exe, string mode, string configPath)
    {
        if (mode == "service")
        {
            // Windows restarts the service on its own (failure actions); asking it as well does no harm.
            try
            {
                Process.Start(new ProcessStartInfo("sc.exe", "start MatgateAgent") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(10000);
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            return;
        }

        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(configPath);
        Process.Start(info);
    }

    private static bool WaitForVersion(int port, Version version, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (AgentStatus.Query(port) is { } status && Version.TryParse(status.Version, out var running) && running == version)
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return false;
    }

    private static void StopFrom(string exe)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    // The file may be held a moment longer than the process that held it.
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 30)
            {
                Thread.Sleep(500);
            }
        }
    }
}
