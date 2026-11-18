using System.Diagnostics;

namespace Matgate.Agent.Setup;

// The installation, whatever the system: where the agent lives, what is installed, installing and removing
// it, starting and stopping it, and doing any of that with the rights it needs. The windows and the
// commands ask here; Windows' and Unix' own installers do the work.
internal static class AgentInstall
{
    public const string ProductName = "Matgate Agent";

#if AGENT_WINDOWS
    public static string ProgramFolder => Installer.ProgramFolder;

    public static string DataFolder => Installer.DataFolder;

    public static string InstalledProgram => Installer.InstalledExe;

    public static string ConfigPath => Installer.ConfigPath;

    public static bool IsElevated => WindowsNative.IsElevated();

    public static SetupOptions? FindExisting() => Installer.FindExisting();

    public static SetupResult Install(SetupOptions options, Action<string> log)
    {
        var result = Installer.Install(options, log);
        return new SetupResult(result.Token, result.Fingerprint, result.Port, result.Listening);
    }

    public static void Uninstall(bool keepSettings, Action<string> log) => Installer.Uninstall(keepSettings, log);

    public static bool Start(Action<string> log) => Installer.Start(log);

    public static bool Stop(Action<string> log) => Installer.Stop(log);

    public static bool Restart(Action<string> log) => Installer.Restart(log);

    // Whether the service (not the logon task) is on its way up or down: the icon then says "starting", not "stopped".
    public static bool? ServiceBusy()
    {
        var status = Installer.ServiceStatus();
        return status is null ? null
            : status is System.ServiceProcess.ServiceControllerStatus.StartPending or System.ServiceProcess.ServiceControllerStatus.ContinuePending
                or System.ServiceProcess.ServiceControllerStatus.StopPending or System.ServiceProcess.ServiceControllerStatus.PausePending;
    }

    public static bool? ServiceRunning() => Installer.ServiceStatus() is { } status ? status == System.ServiceProcess.ServiceControllerStatus.Running : null;

    public static string ModeText(SetupOptions installed) => installed.Mode != "service"
        ? "als angemeldeter Benutzer"
        : WindowsNative.IsLocalSystem(installed.Account) ? "als Systemdienst" : $"als Dienst unter {installed.Account}";

    public static string InteractiveUser => WindowsNative.InteractiveUser();
#else
    public static string ProgramFolder => UnixInstaller.ProgramFolder;

    public static string DataFolder => UnixInstaller.DataFolder;

    public static string InstalledProgram => UnixInstaller.InstalledProgram;

    public static string ConfigPath => UnixInstaller.ConfigPath;

    public static bool IsElevated => UnixInstaller.IsRoot;

    public static SetupOptions? FindExisting() => UnixInstaller.FindExisting();

    public static SetupResult Install(SetupOptions options, Action<string> log) => UnixInstaller.Install(options, log);

    public static void Uninstall(bool keepSettings, Action<string> log) => UnixInstaller.Uninstall(keepSettings, log);

    public static bool Start(Action<string> log) => UnixInstaller.Start(log);

    public static bool Stop(Action<string> log) => UnixInstaller.Stop(log);

    public static bool Restart(Action<string> log) => UnixInstaller.Restart(log);

    public static bool? ServiceBusy() => null;

    public static bool? ServiceRunning() => null;

    public static string ModeText(SetupOptions installed) => "als Dienst des Systems";

    public static string InteractiveUser => Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } caller ? caller : Environment.UserName;
#endif

    // The program a command is run with: the installed one where there is one (so an update is the one that answers).
    public static string Program => File.Exists(InstalledProgram) ? InstalledProgram : Environment.ProcessPath!;

    // Runs this program again with the rights of an administrator - Windows asks (UAC), Linux asks through
    // polkit (pkexec), macOS through its own password dialog. `output` gets what the command prints (not on
    // Windows, where the elevated program has no console of ours). False when the question was declined or
    // the command failed.
    public static bool RunElevated(IReadOnlyList<string> arguments, bool wait = true, Action<string>? output = null)
    {
        try
        {
            ProcessStartInfo info;
            if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo(Program) { UseShellExecute = true, Verb = IsElevated ? "" : "runas" };
                foreach (var argument in arguments)
                {
                    info.ArgumentList.Add(argument);
                }
            }
            else if (IsElevated)
            {
                info = Redirected(Program, arguments);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // AppleScript runs a shell command "with administrator privileges": the system asks for the password.
                var command = string.Join(' ', new[] { Program }.Concat(arguments).Select(ShellQuote));
                info = Redirected("/usr/bin/osascript", ["-e", $"do shell script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges"]);
            }
            else
            {
                info = Redirected("pkexec", new[] { Program }.Concat(arguments).ToArray());
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            if (info.RedirectStandardOutput)
            {
                process.OutputDataReceived += (_, line) => { if (line.Data is { } text) { output?.Invoke(text); } };
                process.ErrorDataReceived += (_, line) => { if (line.Data is { } text) { output?.Invoke(text); } };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            if (!wait)
            {
                return true;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Declined, or the system has no way to ask (no pkexec): the caller says what to do by hand.
            output?.Invoke(exception.Message);
            return false;
        }
    }

    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener) { UseShellExecute = false, ArgumentList = { folder } });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No file manager to show it with.
        }
    }

    private static ProcessStartInfo Redirected(string program, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    // The options as the command line of "setup" takes them - for a setup that runs in a second, elevated process.
    public static List<string> SetupArguments(SetupOptions options)
    {
        var arguments = new List<string> { "setup", "--port", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var root in options.Roots)
        {
            arguments.Add("--root");
            arguments.Add(root.Name + "=" + root.Path);
        }

        if (!options.Claude) { arguments.Add("--no-claude"); }
        if (!options.AutoUpdate) { arguments.Add("--no-auto-update"); }
        if (!options.Screen) { arguments.Add("--no-screen"); }
        if (!options.Tray) { arguments.Add("--no-tray"); }
        if (options.Logo is { Length: > 0 } logo) { arguments.Add("--logo"); arguments.Add(logo); }
        else if (options.Logo is not null) { arguments.Add("--no-logo"); }

        if (OperatingSystem.IsWindows())
        {
            arguments.Add(options.OpenFirewall ? "--firewall" : "--no-firewall");
            arguments.Add("--mode");
            arguments.Add(options.Mode == "service" ? "service" : "user");
            if (options.Mode == "service") { arguments.Add("--account"); arguments.Add(options.Account); }
            if (options.PowerShell) { arguments.Add("--powershell"); }
        }
        else
        {
            if (options.RunAs is { Length: > 0 } user) { arguments.Add("--user"); arguments.Add(user); }
            if (options.Shell) { arguments.Add("--shell"); }
        }

        return arguments;
    }
}
