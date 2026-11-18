using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Matgate.Agent;

// Installing the agent on Linux and macOS: as the system's own service, the way it is a LocalSystem
// service on Windows - it starts with the machine, before and without anyone signing in.
//
//   Linux   the program in /opt/matgate-agent, the settings in /etc/matgate-agent (root only), a systemd unit
//           matgate-agent.service
//   macOS   the program in /usr/local/lib/matgate-agent, the settings in
//           /Library/Application Support/Matgate Agent (root only), a LaunchDaemon com.matgate.agent
//
// The service is root. Programs in a terminal run as the user named at setup (RunAs) - by default whoever
// called sudo.
public sealed class UnixSetupOptions
{
    public int Port { get; set; } = 7488;

    public List<AgentRoot> Roots { get; set; } = [];

    public bool Claude { get; set; } = true;

    public bool Shell { get; set; }

    public bool AutoUpdate { get; set; } = true;

    // The user programs run as. Null: whoever called sudo, else whatever the settings already say.
    public string? RunAs { get; set; }

    // The logo: a path takes that picture, empty removes it, null leaves it.
    public string? Logo { get; set; }

    // Says what would be done, and does none of it.
    public bool DryRun { get; set; }
}

public static class UnixInstaller
{
    public const string ServiceName = "matgate-agent";
    public const string LaunchLabel = "com.matgate.agent";

    public static bool Mac => OperatingSystem.IsMacOS();

    public static string ProgramFolder => Mac ? "/usr/local/lib/matgate-agent" : "/opt/matgate-agent";

    public static string DataFolder => Mac ? "/Library/Application Support/Matgate Agent" : "/etc/matgate-agent";

    public static string InstalledProgram => Path.Combine(ProgramFolder, "matgate-agent");

    public static string ConfigPath => Path.Combine(DataFolder, "matgate-agent.json");

    public static string UnitPath => Mac ? $"/Library/LaunchDaemons/{LaunchLabel}.plist" : $"/etc/systemd/system/{ServiceName}.service";

    public static bool IsRoot => Environment.UserName == "root";

    public sealed record Result(string Token, string Fingerprint, int Port, bool Listening);

    public static Result Install(UnixSetupOptions options, Action<string> log)
    {
        var runAs = options.RunAs ?? Environment.GetEnvironmentVariable("SUDO_USER") ?? "";
        log($"Installing Matgate Agent to {ProgramFolder}, settings in {DataFolder}.");
        log(Mac ? $"Runs as a LaunchDaemon ({LaunchLabel}), started with the machine." : $"Runs as a systemd service ({ServiceName}), started with the machine.");
        log(runAs.Length > 0 && runAs != "root" ? $"Programs in a terminal run as {runAs}." : "Programs in a terminal run as root.");
        if (options.DryRun)
        {
            log("Dry run: nothing was changed.");
            return new Result("", "", options.Port, false);
        }

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("The program does not know where it is.");
        Stop(log);
        Directory.CreateDirectory(ProgramFolder);
        if (!string.Equals(Path.GetFullPath(source), InstalledProgram, StringComparison.Ordinal))
        {
            // Written beside and moved over: a program that still runs keeps its old file.
            var incoming = InstalledProgram + ".new";
            File.Copy(source, incoming, overwrite: true);
            File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(incoming, InstalledProgram, overwrite: true);
        }

        // The settings: token, certificate, folders. For root only - the token is the only key to the machine.
        Directory.CreateDirectory(DataFolder);
        File.SetUnixFileMode(DataFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = AgentConfig.LoadOrCreate(ConfigPath, out var created);
        config.Port = options.Port;
        config.ListenAddress = "0.0.0.0";
        config.AutoUpdate = options.AutoUpdate;
        if (options.RunAs is not null || created || string.IsNullOrEmpty(config.RunAs))
        {
            config.RunAs = runAs == "root" ? "" : runAs;
        }

        if (options.Roots.Count > 0)
        {
            config.Roots = options.Roots;
        }
        else if (created)
        {
            config.Roots = [new AgentRoot { Name = "Home", Path = HomeOf(config.RunAs) }];
        }

        SetTerminal(config, "claude", "Claude Code", "claude", options.Claude);
        SetTerminal(config, "shell", "Shell", "$SHELL -l", options.Shell);
        if (options.Logo is { Length: > 0 } picture)
        {
            config.Logo = AgentLogo.Store(picture, DataFolder);
        }
        else if (options.Logo is not null)
        {
            AgentLogo.Remove(DataFolder);
            config.Logo = "";
        }

        config.Save(ConfigPath);
        File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var certificate = AgentCertificate.LoadOrCreate(config, DataFolder);
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

        File.WriteAllText(UnitPath, Mac ? LaunchDaemon() : SystemdUnit());
        if (Mac)
        {
            Run(log, "launchctl", "bootstrap", "system", UnitPath);
            Run(log, "launchctl", "kickstart", "-k", "system/" + LaunchLabel);
        }
        else
        {
            Run(log, "systemctl", "daemon-reload");
            Run(log, "systemctl", "enable", "--now", ServiceName);
        }

        var listening = WaitForAgent(config.Port);
        log(listening ? $"The agent runs and waits for Matgate on port {config.Port}." : $"Installed, but the agent does not answer on port {config.Port} yet - see {Path.Combine(DataFolder, "agent.log")}.");
        return new Result(config.Token, fingerprint, config.Port, listening);
    }

    public static void Uninstall(bool keepSettings, Action<string> log)
    {
        Stop(log);
        if (!Mac)
        {
            Run(log, "systemctl", "disable", ServiceName);
        }

        Remove(UnitPath);
        if (!Mac)
        {
            Run(log, "systemctl", "daemon-reload");
        }

        // The program may be this very file: on these systems a running program's file can be removed.
        try
        {
            Directory.Delete(ProgramFolder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }

        if (!keepSettings)
        {
            try
            {
                Directory.Delete(DataFolder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }

        log(keepSettings ? $"Removed. The settings stay in {DataFolder}: a later installation takes the same token." : "Removed, with its settings.");
    }

    public static bool Start(Action<string> log) => Mac
        ? Run(log, "launchctl", "kickstart", "system/" + LaunchLabel)
        : Run(log, "systemctl", "start", ServiceName);

    public static bool Stop(Action<string> log)
    {
        if (!File.Exists(UnitPath))
        {
            return true;
        }

        return Mac ? Run(log, "launchctl", "bootout", "system/" + LaunchLabel) : Run(log, "systemctl", "stop", ServiceName);
    }

    public static bool Restart(Action<string> log) => Mac
        ? Run(log, "launchctl", "kickstart", "-k", "system/" + LaunchLabel)
        : Run(log, "systemctl", "restart", ServiceName);

    private static string SystemdUnit() => $"""
        [Unit]
        Description=Matgate Agent - this machine in Matgate (files, terminals)
        After=network-online.target
        Wants=network-online.target

        [Service]
        ExecStart={InstalledProgram} run --config {ConfigPath}
        Restart=always
        RestartSec=2
        User=root
        # A new version replaces the program and ends it; the service comes up again with the new one.
        # Programs in terminals are the service's own and end with it.
        KillMode=control-group

        [Install]
        WantedBy=multi-user.target

        """;

    private static string LaunchDaemon() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key><string>{LaunchLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{InstalledProgram}</string>
                <string>run</string>
                <string>--config</string>
                <string>{System.Security.SecurityElement.Escape(ConfigPath)}</string>
            </array>
            <key>RunAtLoad</key><true/>
            <key>KeepAlive</key><true/>
            <key>ProcessType</key><string>Interactive</string>
        </dict>
        </plist>

        """;

    private static void SetTerminal(AgentConfig config, string id, string title, string command, bool enabled)
    {
        var terminal = config.Terminals.FirstOrDefault(candidate => candidate.Id == id);
        if (terminal is null)
        {
            config.Terminals.Add(new AgentTerminal { Id = id, Title = title, Command = command, Enabled = enabled });
        }
        else
        {
            terminal.Enabled = enabled;
        }
    }

    private static string HomeOf(string user)
    {
        if (user.Length > 0)
        {
            foreach (var candidate in new[] { "/home/" + user, "/Users/" + user })
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static bool WaitForAgent(int port)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (AgentStatus.Query(port) is not null)
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return false;
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Run(Action<string> log, string program, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            var output = new StringBuilder();
            process.OutputDataReceived += (_, line) => output.AppendLine(line.Data);
            process.ErrorDataReceived += (_, line) => output.AppendLine(line.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                log($"{program} {string.Join(' ', arguments)}: no answer.");
                return false;
            }

            if (process.ExitCode != 0)
            {
                var said = output.ToString().Trim();
                log($"{program} {string.Join(' ', arguments)}: exit {process.ExitCode}{(said.Length > 0 ? " - " + said.Split('\n')[0].Trim() : "")}");
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"{program}: {exception.Message}");
            return false;
        }
    }
}
