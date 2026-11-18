using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Matgate.Agent;

// The Matgate agent on Linux and macOS: the same server Matgate connects to as on Windows - host, port and a
// password (the token) - with the machine's shared folders and programs in a terminal.
//
//   sudo matgate-agent setup [--port 7488] [--root Name=/path ...] [--user name] [--no-claude] [--shell]
//                            [--no-auto-update] [--logo picture|--no-logo] [--dry-run]
//                                       installs it as the system's service (systemd / launchd) and starts it
//   sudo matgate-agent uninstall [--keep-config]
//   sudo matgate-agent start|stop|restart
//   matgate-agent run [--config path]   the agent itself (what the service starts; by hand: settings next to the program)
//   matgate-agent status [--port n]     whether it runs
//   matgate-agent --version
var command = args.FirstOrDefault() ?? "";
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
try
{
    switch (command)
    {
        case "run":
        case "--config":
            await RunAgentAsync(args, version);
            return 0;
        case "setup":
        case "install":
            return RunSetup(args);
        case "uninstall":
            if (!UnixInstaller.IsRoot)
            {
                Console.WriteLine("Uninstalling needs root - run it with sudo.");
                return 1;
            }

            UnixInstaller.Uninstall(args.Contains("--keep-config"), Console.WriteLine);
            return 0;
        case "start":
            return NeedsRoot() ?? (UnixInstaller.Start(Console.WriteLine) ? 0 : 1);
        case "stop":
            return NeedsRoot() ?? (UnixInstaller.Stop(Console.WriteLine) ? 0 : 1);
        case "restart":
            return NeedsRoot() ?? (UnixInstaller.Restart(Console.WriteLine) ? 0 : 1);
        case "status":
            var portIndex = Array.IndexOf(args, "--port");
            var status = portIndex > 0 && portIndex + 1 < args.Length ? AgentStatus.Query(int.Parse(args[portIndex + 1])) : AgentStatus.QueryInstalled();
            Console.WriteLine(status is null ? "The agent is not running." : System.Text.Json.JsonSerializer.Serialize(status));
            return status is null ? 1 : 0;
        case "as-user":
            // What the agent starts for a program in a terminal that runs as another user - never by hand.
            return UnixUser.Become(args);
        case "--version":
            Console.WriteLine(version);
            return 0;
        case "":
        case "--help":
        case "help":
            Console.WriteLine($"Matgate agent {version} ({AgentPlatform.Id})");
            Console.WriteLine("  sudo matgate-agent setup [--port 7488] [--root Name=/path ...] [--user name] [--no-claude] [--shell]");
            Console.WriteLine("  sudo matgate-agent uninstall [--keep-config]");
            Console.WriteLine("  sudo matgate-agent start|stop|restart");
            Console.WriteLine("  matgate-agent run [--config path]");
            Console.WriteLine("  matgate-agent status");
            return command == "" ? 2 : 0;
        default:
            Console.WriteLine($"Unknown command \"{command}\". Try: setup, uninstall, run, status, --version.");
            return 2;
    }
}
catch (Exception exception) when (command is "setup" or "install" or "uninstall")
{
    Console.WriteLine("Failed: " + exception.Message);
    return 1;
}

static int? NeedsRoot()
{
    if (UnixInstaller.IsRoot)
    {
        return null;
    }

    Console.WriteLine("This needs root - run it with sudo.");
    return 1;
}

static int RunSetup(string[] args)
{
    var options = new UnixSetupOptions();
    for (var i = 1; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"{args[i]} needs a value.");
        switch (args[i])
        {
            case "--port": options.Port = int.Parse(Next()); break;
            case "--user": options.RunAs = Next(); break;
            case "--no-claude": options.Claude = false; break;
            case "--shell": options.Shell = true; break;
            case "--no-auto-update": options.AutoUpdate = false; break;
            case "--logo": options.Logo = Path.GetFullPath(Next()); break;
            case "--no-logo": options.Logo = ""; break;
            case "--dry-run": options.DryRun = true; break;
            case "--root":
                var value = Next();
                var separator = value.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidOperationException("--root expects Name=/path.");
                }

                options.Roots.Add(new AgentRoot { Name = value[..separator], Path = value[(separator + 1)..] });
                break;
            default: throw new InvalidOperationException($"Unknown option {args[i]}.");
        }
    }

    if (!options.DryRun && !UnixInstaller.IsRoot)
    {
        Console.WriteLine("Setup needs root - run it with sudo.");
        return 1;
    }

    var result = UnixInstaller.Install(options, Console.WriteLine);
    if (!options.DryRun)
    {
        Console.WriteLine();
        Console.WriteLine($"In Matgate: connection type Agent, host {Environment.MachineName}, port {result.Port}, \"ignore certificate\".");
        Console.WriteLine($"  connection password: {result.Token}");
        Console.WriteLine($"  certificate SHA-256: {result.Fingerprint}");
    }

    return result.Listening || options.DryRun ? 0 : 3;
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

    // Installed: the system's settings folder. Started by hand from a folder: next to the program.
    configPath ??= File.Exists(UnixInstaller.ConfigPath)
        ? UnixInstaller.ConfigPath
        : Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
    var directory = Path.GetDirectoryName(configPath)!;
    Directory.CreateDirectory(directory);
    AgentLog.UseFile(directory);

    var config = AgentConfig.LoadOrCreate(configPath, out var created);
    var certificate = AgentCertificate.LoadOrCreate(config, directory);
    AgentLog.Write($"Matgate agent {version} ({AgentPlatform.Id})");
    AgentLog.Write($"  configuration: {configPath}{(created ? " (created)" : "")}");
    AgentLog.Write($"  listening on:  {config.ListenAddress}:{config.Port}");
    AgentLog.Write($"  certificate:   SHA-256 {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
    if (created)
    {
        // Only when it was just made - after that it stays in the configuration, not in every log.
        AgentLog.Write($"  token:         {config.Token}");
    }

    var files = new FilesPlugin(config.Roots);
    var terminals = new TerminalPlugin(config, files, new UnixPtyStarter(config));
    var updates = new UnixUpdates(configPath);
    var screen = new NoScreen();
    AgentStatus.Port = config.Port;
    AgentStatus.Installed = string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(UnixInstaller.ConfigPath), StringComparison.Ordinal);
    AgentStatus.Mode = AgentStatus.Installed ? "service" : "user";
    AgentStatus.OpenTerminals = () => terminals.Count;
    var tokenBytes = Encoding.UTF8.GetBytes(config.Token.Trim());

    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
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
        var offered = header.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(header[7..].Trim()) : [];
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
        // A new version ends the agent with a failure, so that the service manager starts it again.
        await new Connection(socket, config, directory, version, files, terminals, updates, screen, () => Environment.Exit(1))
            .RunAsync(context.RequestAborted);
        AgentLog.Write("Matgate disconnected");
    });

    AgentStatus.StartServer(app.Lifetime.ApplicationStopping);
    await app.RunAsync();
}
