using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Matgate.Agent;
using Matgate.Agent.Setup;

// The Matgate agent: a small server on the machine that Matgate connects to - like an RDP server, with
// host, port and a password (the token). What it offers is up to the agent: Matgate asks after
// connecting and shows exactly that.
//
//   matgate-agent                      double-click: the setup dialog (asks for administrator rights)
//   matgate-agent run [--config path]  the agent itself (what the service or the logon task starts)
//   matgate-agent setup [--port 7488] [--firewall|--no-firewall] [--mode user|service]
//                       [--account .\name|LocalSystem] [--password pw] [--root Name=C:\Path ...]
//                       [--no-claude] [--powershell] [--dry-run]
//   matgate-agent uninstall [--keep-config] [--gui]
//   matgate-agent --version
var command = args.FirstOrDefault() ?? "";
if (args.Length > 0)
{
    AgentLog.AttachToParentConsole();
}

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
try
{
    switch (command)
    {
        case "":
            return RunSetupDialog();
        case "run":
        case "--config":
            await RunAgentAsync(args, version);
            return 0;
        case "setup":
        case "install":
            return RunSetup(args);
        case "uninstall":
            return RunUninstall(args);
        case "tray":
            Matgate.Agent.Ui.TrayApplication.Run();
            return 0;
        case "connection-info":
            Application.EnableVisualStyles();
            Matgate.Agent.Ui.ConnectionWindow.TryShow(modal: true);
            return 0;
        case "restart":
            return Installer.Restart(Console.WriteLine) ? 0 : 1;
        case "setup-preview":
            // Draws the dialogs into image files without showing them - for checking the layout.
            Application.EnableVisualStyles();
            SetupPreview.Save(args[1]);
            return 0;
        case "status":
            // What the tray icon sees, for a console.
            var status = AgentStatus.Query();
            Console.WriteLine(status is null ? "The agent is not running." : System.Text.Json.JsonSerializer.Serialize(status));
            return status is null ? 1 : 0;
        case "write-icon":
            SetupPreview.WriteIcon(args[1]);
            return 0;
        case "--version":
            Console.WriteLine(version);
            return 0;
        default:
            Console.WriteLine($"Unknown command \"{command}\". Commands: run, setup, uninstall, --version.");
            return 2;
    }
}
catch (Exception exception) when (command is "setup" or "install" or "uninstall")
{
    Console.WriteLine("Failed: " + exception.Message);
    return 1;
}

static int RunSetupDialog()
{
    if (!WindowsNative.IsElevated())
    {
        return Relaunch([]);
    }

    Application.EnableVisualStyles();
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new SetupWizard(uninstall: false));
    return 0;
}

static int RunSetup(string[] args)
{
    var options = new SetupOptions();
    for (var i = 1; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"{args[i]} needs a value.");
        switch (args[i])
        {
            case "--port": options.Port = int.Parse(Next()); break;
            case "--firewall": options.OpenFirewall = true; break;
            case "--no-firewall": options.OpenFirewall = false; break;
            case "--mode": options.Mode = Next() == "service" ? "service" : "user"; break;
            case "--account": options.Account = Next(); break;
            case "--password": options.Password = Next(); break;
            case "--no-claude": options.Claude = false; break;
            case "--powershell": options.PowerShell = true; break;
            case "--dry-run": options.DryRun = true; break;
            case "--root":
                var value = Next();
                var separator = value.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidOperationException("--root expects Name=Path.");
                }

                options.Roots.Add(new AgentRoot { Name = value[..separator], Path = value[(separator + 1)..] });
                break;
            default: throw new InvalidOperationException($"Unknown option {args[i]}.");
        }
    }

    if (!options.DryRun && !WindowsNative.IsElevated())
    {
        Console.WriteLine("Setup needs administrator rights - run it in an administrator console.");
        return 1;
    }

    if (options.Roots.Count == 0 && Installer.FindExisting() is null)
    {
        options.Roots.Add(new AgentRoot { Name = "Home", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
    }

    var result = Installer.Install(options, Console.WriteLine);
    if (!options.DryRun)
    {
        Console.WriteLine();
        Console.WriteLine($"In Matgate: connection type Agent, host {Environment.MachineName}, port {result.Port}, \"ignore certificate\".");
        Console.WriteLine($"  connection password: {result.Token}");
        Console.WriteLine($"  certificate SHA-256: {result.Fingerprint}");
    }

    return result.Listening || options.DryRun ? 0 : 3;
}

static int RunUninstall(string[] args)
{
    var gui = args.Contains("--gui");
    if (!WindowsNative.IsElevated())
    {
        if (!gui)
        {
            Console.WriteLine("Uninstall needs administrator rights - run it in an administrator console.");
            return 1;
        }

        return Relaunch(["uninstall", "--gui"]);
    }

    if (!gui)
    {
        Installer.Uninstall(args.Contains("--keep-config"), Console.WriteLine);
        return 0;
    }

    // The program folder cannot go while this exe runs from it: like MatMon's uninstaller, a copy in
    // %TEMP% does the work and removes itself afterwards.
    var singleFile = string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
    if (singleFile && !args.Contains("--relaunched") && Environment.ProcessPath!.StartsWith(Installer.ProgramFolder, StringComparison.OrdinalIgnoreCase))
    {
        var copy = Path.Combine(Path.GetTempPath(), $"Matgate Agent deinstallieren {Guid.NewGuid():N}.exe");
        File.Copy(Environment.ProcessPath!, copy);
        Process.Start(new ProcessStartInfo(copy) { UseShellExecute = false, ArgumentList = { "uninstall", "--gui", "--relaunched" } });
        return 0;
    }

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    Application.Run(new SetupWizard(uninstall: true));
    if (args.Contains("--relaunched"))
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{Environment.ProcessPath}\"") { CreateNoWindow = true, UseShellExecute = false });
    }

    return 0;
}

// Asks for administrator rights by starting this exe again through UAC.
static int Relaunch(string[] arguments)
{
    try
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info);
        return 0;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        // UAC declined.
        return 1;
    }
}

static async Task RunAgentAsync(string[] args, string version)
{
    var configPath = Environment.GetEnvironmentVariable("MATGATE_AGENT_CONFIG");
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--config")
        {
            configPath = Path.GetFullPath(args[i + 1]);
        }
    }

    // Installed: the settings in ProgramData. Started by hand from a folder: next to the exe.
    configPath ??= File.Exists(Installer.ConfigPath)
        ? Installer.ConfigPath
        : Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
    var directory = Path.GetDirectoryName(configPath)!;
    AgentLog.UseFile(directory);

    var config = AgentConfig.LoadOrCreate(configPath, out var created);
    var certificate = AgentCertificate.LoadOrCreate(config, directory);

    AgentLog.Write($"Matgate agent {version}");
    AgentLog.Write($"  configuration: {configPath}{(created ? " (created)" : "")}");
    AgentLog.Write($"  listening on:  {config.ListenAddress}:{config.Port}");
    AgentLog.Write($"  certificate:   SHA-256 {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
    if (created)
    {
        // Only when it was just made - after that it stays in the configuration, not in every log.
        AgentLog.Write($"  token:         {config.Token}");
    }

    var files = new FilesPlugin(config.Roots);
    var terminals = new TerminalPlugin(config, files);
    AgentStatus.Port = config.Port;
    AgentStatus.Mode = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? "service" : "user";
    AgentStatus.OpenTerminals = () => terminals.Count;
    var tokenBytes = Encoding.UTF8.GetBytes(config.Token);

    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
    builder.Host.UseWindowsService(options => options.ServiceName = "MatgateAgent");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(options =>
    {
        var address = IPAddress.TryParse(config.ListenAddress, out var parsed) ? parsed : IPAddress.Any;
        options.Listen(address, config.Port, listen => listen.UseHttps(certificate));
    });
    var app = builder.Build();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

    app.Map("/matgate", async context =>
    {
        // The token is compared in constant time, and a wrong one gets nothing back but the status.
        var header = context.Request.Headers.Authorization.ToString();
        var offered = header.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(header[7..]) : [];
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(offered, tokenBytes))
        {
            AgentLog.Write($"Refused a connection with a wrong token from {context.Connection.RemoteIpAddress}");
            await Task.Delay(500);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        AgentLog.Write($"Matgate connected from {context.Connection.RemoteIpAddress}");
        AgentStatus.Connected(context.Connection.RemoteIpAddress?.ToString() ?? "");
        try
        {
        await new Connection(socket, config, version, files, terminals).RunAsync(context.RequestAborted);
        }
        finally
        {
            AgentStatus.Disconnected();
        }

        AgentLog.Write("Matgate disconnected");
    });

    AgentStatus.StartServer(app.Lifetime.ApplicationStopping);
    await app.RunAsync();
}
