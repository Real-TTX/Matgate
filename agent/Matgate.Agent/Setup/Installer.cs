using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace Matgate.Agent.Setup;

// What the setup decides. Comes from the dialog or from the command line.
public sealed class SetupOptions
{
    public int Port { get; set; } = 7488;

    public bool OpenFirewall { get; set; } = true;

    // "user": starts at logon as the signed-in user. "service": a Windows service under Account.
    public string Mode { get; set; } = "user";

    // For "service": DOMAIN\name, .\name or LocalSystem. The password goes to Windows, nowhere else.
    public string Account { get; set; } = "";

    public string Password { get; set; } = "";

    public List<AgentRoot> Roots { get; set; } = [];

    public bool Claude { get; set; } = true;

    public bool PowerShell { get; set; }

    public bool DryRun { get; set; }

    // The icon in the notification area, started at every logon.
    public bool Tray { get; set; } = true;

    // Matgate may bring new versions on its own.
    public bool AutoUpdate { get; set; } = true;

    // Filled from an existing installation only.
    public string Version { get; set; } = "";
}

// Installs the agent the way MatMon installs its agent: the exe copies itself to Program Files, the
// configuration lives in ProgramData behind an ACL, one named firewall rule, an entry in "Apps &
// Features" - and uninstall takes every one of them away again.
public static class Installer
{
    public const string ProductName = "Matgate Agent";
    private const string TaskName = "Matgate Agent";
    private const string ServiceName = "MatgateAgent";
    private const string FirewallRule = "Matgate Agent";
    private const string TrayRunValue = "Matgate Agent Tray";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MatgateAgent";

    public static string ProgramFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string InstalledExe => Path.Combine(ProgramFolder, "matgate-agent.exe");

    public static string ConfigPath => Path.Combine(DataFolder, "matgate-agent.json");

    // The installed settings, to start the dialog from - a second setup is an update.
    public static SetupOptions? FindExisting()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
        if (key is null)
        {
            return null;
        }

        var options = new SetupOptions
        {
            Port = key.GetValue("MatgateAgentPort") is int port ? port : 7488,
            Mode = key.GetValue("MatgateAgentMode") as string ?? "user",
            Account = key.GetValue("MatgateAgentAccount") as string ?? "",
            OpenFirewall = (key.GetValue("MatgateAgentFirewall") as int? ?? 1) == 1,
            Version = key.GetValue("DisplayVersion") as string ?? "",
        };
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey))
        {
            options.Tray = run?.GetValue(TrayRunValue) is not null;
        }
        try
        {
            if (File.Exists(ConfigPath))
            {
                var config = AgentConfig.LoadOrCreate(ConfigPath, out _);
                options.Roots = config.Roots;
                options.Claude = config.Terminals.Any(t => t.Id == "claude" && t.Enabled);
                options.PowerShell = config.Terminals.Any(t => t.Id == "powershell" && t.Enabled);
                options.AutoUpdate = config.AutoUpdate;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A service's settings are for administrators; the tray of a normal user gets along without them.
        }

        return options;
    }

    public sealed record Result(string Token, string Fingerprint, int Port, bool Listening);

    public static Result Install(SetupOptions options, Action<string> log)
    {
        var service = options.Mode == "service";
        var runAccount = service ? (WindowsNative.IsLocalSystem(options.Account) ? "LocalSystem" : WindowsNative.Qualify(options.Account)) : WindowsNative.InteractiveUser();
        log($"Installing {ProductName} to {ProgramFolder}, settings in {DataFolder}.");
        log(service ? $"Runs as a Windows service under {runAccount}." : $"Starts at logon as {runAccount}.");
        if (options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The port must be between 1 and 65535.");
        }

        if (service && runAccount != "LocalSystem" && !options.DryRun)
        {
            // Before anything is changed: a wrong password must not leave a half-installed agent behind.
            WindowsNative.CheckPassword(runAccount, options.Password);
        }

        if (options.DryRun)
        {
            log("Dry run - nothing changed.");
            return new Result("", "", options.Port, false);
        }

        StopRunning(log);

        // The program. Published, it is this one file - and it may sit in the Downloads folder, whose other
        // files are none of the setup's business. Only a development build brings its folder along.
        Directory.CreateDirectory(ProgramFolder);
        var source = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var singleFile = string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
        if (singleFile)
        {
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
            }
        }
        else if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(ProgramFolder), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("matgate-agent.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || name.StartsWith("agent.log", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(file, Path.Combine(ProgramFolder, name), overwrite: true);
            }
        }

        // The agent replaces its own program on an update, so its account may write the program folder
        // (in user mode it is the user; LocalSystem may anyway).
        if (runAccount != "LocalSystem")
        {
            var programSecurity = new DirectoryInfo(ProgramFolder).GetAccessControl();
            programSecurity.AddAccessRule(new FileSystemAccessRule(new NTAccount(runAccount), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(ProgramFolder).SetAccessControl(programSecurity);
        }

        // The settings: token, certificate, folders. Readable only by Windows itself, administrators and
        // the account the agent runs as - the token is the only key to the machine.
        Directory.CreateDirectory(DataFolder);
        SecureFolder(DataFolder, runAccount);
        var config = AgentConfig.LoadOrCreate(ConfigPath, out _);
        config.Port = options.Port;
        config.ListenAddress = "0.0.0.0";
        config.AutoUpdate = options.AutoUpdate;
        if (config.DetachedSessionMinutes == 60)
        {
            config.DetachedSessionMinutes = 0;
        }
        if (options.Roots.Count > 0)
        {
            config.Roots = options.Roots;
        }

        SetTerminal(config, "claude", "Claude Code", "cmd.exe /c claude", options.Claude);
        SetTerminal(config, "powershell", "PowerShell", "powershell.exe -NoLogo", options.PowerShell);
        config.Save(ConfigPath);
        var certificate = AgentCertificate.LoadOrCreate(config, DataFolder);
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

        // One named rule: a changed port or an unticked box replaces it instead of piling rules up.
        Run("netsh", ["advfirewall", "firewall", "delete", "rule", "name=" + FirewallRule], log, ignoreFailure: true);
        if (options.OpenFirewall)
        {
            Run("netsh", ["advfirewall", "firewall", "add", "rule", "name=" + FirewallRule, "dir=in", "action=allow", "protocol=TCP",
                "localport=" + options.Port, "program=" + InstalledExe], log);
        }

        var commandLine = $"\"{InstalledExe}\" run";
        if (service)
        {
            RemoveLogonTask(log);
            if (runAccount != "LocalSystem")
            {
                WindowsNative.GrantServiceLogonRight(runAccount);
            }

            WindowsNative.CreateOrUpdateService(ServiceName, ProductName, commandLine,
                runAccount == "LocalSystem" ? "LocalSystem" : runAccount,
                runAccount == "LocalSystem" ? "" : options.Password);
            Run("sc.exe", ["description", ServiceName, "Matgate agent - offers files and programs of this machine to Matgate."], log);
            Run("sc.exe", ["failure", ServiceName, "reset=", "86400", "actions=", "restart/10000/restart/60000/restart/60000"], log);
            Run("sc.exe", ["start", ServiceName], log);
        }
        else
        {
            RemoveService(log);
            // At logon, as that user, with the user's own rights - Claude Code finds its login there.
            // Defined as XML: the schtasks switches alone would stop the agent on battery power and after
            // three days, and would want the user's password.
            var definition = Path.Combine(Path.GetTempPath(), "matgate-agent-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(definition, LogonTaskXml(runAccount), System.Text.Encoding.Unicode);
            try
            {
                Run("schtasks", ["/Create", "/F", "/TN", TaskName, "/XML", definition], log);
            }
            finally
            {
                File.Delete(definition);
            }
            Run("schtasks", ["/Run", "/TN", TaskName], log);
        }

        WriteUninstallEntry(options, runAccount);
        SetTray(options.Tray, log);
        var listening = WaitForPort(options.Port, TimeSpan.FromSeconds(20));
        log(listening ? $"The agent listens on port {options.Port}." : $"The agent did not answer on port {options.Port} yet - see {Path.Combine(DataFolder, "agent.log")}.");
        return new Result(config.Token, fingerprint, options.Port, listening);
    }

    // For the tray's "restart": the service, or the logon task, stopped and started again.
    public static bool Restart(Action<string> log)
    {
        var mode = FindExisting()?.Mode;
        if (mode is null)
        {
            log("The agent is not installed.");
            return false;
        }

        StopRunning(log);
        if (mode == "service")
        {
            Run("sc.exe", ["start", ServiceName], log);
        }
        else
        {
            Run("schtasks", ["/Run", "/TN", TaskName], log);
        }

        return true;
    }

    public static void Uninstall(bool keepConfig, Action<string> log)
    {
        log($"Removing {ProductName}.");
        StopRunning(log);
        RemoveService(log);
        RemoveLogonTask(log);
        Run("netsh", ["advfirewall", "firewall", "delete", "rule", "name=" + FirewallRule], log, ignoreFailure: true);
        SetTray(false, log);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        if (!keepConfig && Directory.Exists(DataFolder))
        {
            Directory.Delete(DataFolder, recursive: true);
            log("Settings, token and certificate deleted.");
        }

        // The program folder last. When this very exe runs from it, a short-lived cmd removes it after
        // this process has ended.
        if (Directory.Exists(ProgramFolder))
        {
            if (AppContext.BaseDirectory.StartsWith(ProgramFolder, StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{ProgramFolder}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            else
            {
                Directory.Delete(ProgramFolder, recursive: true);
            }
        }

        log($"{ProductName} removed.");
    }

    private static void StopRunning(Action<string> log)
    {
        Run("sc.exe", ["stop", ServiceName], log, ignoreFailure: true, quiet: true);
        Run("schtasks", ["/End", "/TN", TaskName], log, ignoreFailure: true, quiet: true);
        foreach (var process in Process.GetProcessesByName("matgate-agent"))
        {
            try
            {
                if (process.Id != Environment.ProcessId && (process.MainModule?.FileName ?? "").StartsWith(ProgramFolder, StringComparison.OrdinalIgnoreCase))
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

    private static void RemoveService(Action<string> log)
    {
        Run("sc.exe", ["stop", ServiceName], log, ignoreFailure: true, quiet: true);
        Run("sc.exe", ["delete", ServiceName], log, ignoreFailure: true, quiet: true);
    }

    private static void RemoveLogonTask(Action<string> log)
    {
        Run("schtasks", ["/Delete", "/F", "/TN", TaskName], log, ignoreFailure: true, quiet: true);
    }

    private static string LogonTaskXml(string account)
    {
        var user = System.Security.SecurityElement.Escape(account);
        var exe = System.Security.SecurityElement.Escape(InstalledExe);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Matgate agent - offers files and programs of this machine to Matgate.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
                <Hidden>false</Hidden>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>run</Arguments></Exec></Actions>
            </Task>
            """;
    }

    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName + ".lnk");

    // The icon in the notification area: started at every logon (all users, like MatMon's tray) and
    // from a Start menu entry; started right away for whoever is signed in.
    private static void SetTray(bool enabled, Action<string> log)
    {
        using (var run = Registry.LocalMachine.CreateSubKey(RunKey))
        {
            if (enabled)
            {
                run.SetValue(TrayRunValue, $"\"{InstalledExe}\" tray");
            }
            else
            {
                run.DeleteValue(TrayRunValue, throwOnMissingValue: false);
            }
        }

        if (!enabled)
        {
            File.Delete(StartMenuShortcut);
            return;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(StartMenuShortcut);
            shortcut.TargetPath = InstalledExe;
            shortcut.Arguments = "tray";
            shortcut.Description = "Matgate Agent - Status und Verbindungsdaten";
            shortcut.IconLocation = InstalledExe + ",0";
            shortcut.Save();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            log("  Start menu entry: " + exception.Message);
        }

        // Explorer opens the shortcut as the signed-in user - this process runs elevated, and the icon
        // must not.
        if (File.Exists(StartMenuShortcut))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{StartMenuShortcut}\"") { UseShellExecute = false });
        }

        log("  Symbol im Infobereich: eingerichtet");
    }

    private static void SetTerminal(AgentConfig config, string id, string title, string command, bool enabled)
    {
        var terminal = config.Terminals.FirstOrDefault(t => t.Id == id);
        if (terminal is null)
        {
            config.Terminals.Add(new AgentTerminal { Id = id, Title = title, Command = command, Enabled = enabled });
        }
        else
        {
            terminal.Enabled = enabled;
        }
    }

    private static void SecureFolder(string folder, string runAccount)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        if (runAccount != "LocalSystem")
        {
            security.AddAccessRule(new FileSystemAccessRule(new NTAccount(runAccount), FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new DirectoryInfo(folder).SetAccessControl(security);
    }

    private static void WriteUninstallEntry(SetupOptions options, string runAccount)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        var version = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0";
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Matgate");
        key.SetValue("DisplayIcon", InstalledExe + ",0");
        key.SetValue("InstallLocation", ProgramFolder);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" uninstall --gui");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(Directory.GetFiles(ProgramFolder).Sum(f => new FileInfo(f).Length) / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        // Read back by the next setup to start from what is installed.
        key.SetValue("MatgateAgentPort", options.Port, RegistryValueKind.DWord);
        key.SetValue("MatgateAgentMode", options.Mode);
        key.SetValue("MatgateAgentAccount", options.Mode == "service" ? runAccount : "");
        key.SetValue("MatgateAgentFirewall", options.OpenFirewall ? 1 : 0, RegistryValueKind.DWord);
    }

    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException)
            {
                Thread.Sleep(500);
            }
        }

        return false;
    }

    private static void Run(string file, string[] arguments, Action<string> log, bool ignoreFailure = false, bool quiet = false)
    {
        var info = new ProcessStartInfo(file)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException($"{file} {arguments.FirstOrDefault()} failed: {output.Trim()}");
        }

        if (!quiet)
        {
            log($"  {file} {string.Join(' ', arguments.Take(2))}: {(process.ExitCode == 0 ? "ok" : "skipped")}");
        }
    }
}
