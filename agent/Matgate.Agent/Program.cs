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
        case "setup-preview":
            // Draws the dialogs into image files without showing them - for checking the layout.
            Application.EnableVisualStyles();
            SetupPreview.Save(args[1]);
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
    Application.Run(new SetupForm());
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
    var keepConfig = args.Contains("--keep-config");
    if (gui && !args.Contains("--confirmed"))
    {
        if (MessageBox.Show("Matgate Agent von diesem Rechner entfernen?", "Matgate Agent", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return 0;
        }

        keepConfig = MessageBox.Show(
            "Einstellungen, Token und Zertifikat behalten (für eine spätere Neuinstallation mit demselben Token)?",
            "Matgate Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    if (!WindowsNative.IsElevated())
    {
        if (!gui)
        {
            Console.WriteLine("Uninstall needs administrator rights - run it in an administrator console.");
            return 1;
        }

        return Relaunch(["uninstall", "--gui", "--confirmed", .. keepConfig ? new[] { "--keep-config" } : []]);
    }

    try
    {
        Installer.Uninstall(keepConfig, Console.WriteLine);
        if (gui)
        {
            MessageBox.Show("Matgate Agent wurde entfernt.", "Matgate Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return 0;
    }
    catch (Exception exception) when (gui)
    {
        MessageBox.Show("Entfernen fehlgeschlagen: " + exception.Message, "Matgate Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }
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
        await new Connection(socket, config, version, files, terminals).RunAsync(context.RequestAborted);
        AgentLog.Write("Matgate disconnected");
    });

    await app.RunAsync();
}
