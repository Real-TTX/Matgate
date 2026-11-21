using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Matgate.Agent;
using Matgate.Agent.Setup;
using Matgate.Agent.Ui;

// The Matgate agent: a small server on the machine that Matgate connects to - like an RDP server, with
// host, port and a password (the token). What it offers is up to the agent: Matgate asks after
// connecting and shows exactly that. One program on Windows, Linux and macOS: the agent itself, its setup
// and its icon in the notification area.
//
//   matgate-agent                      the setup window (asks for administrator rights); on a machine without
//                                      a desktop: this list
//   matgate-agent run [--config path]  the agent itself (what the service or the logon task starts)
//   matgate-agent setup [--port 7488] [--root Name=Path ...] [--no-claude] [--no-auto-update] [--no-screen]
//                       [--no-tray] [--logo picture|--no-logo] [--dry-run]
//                Windows:      [--firewall|--no-firewall] [--mode user|system|service] [--account .\name|LocalSystem]
//                              [--password pw] [--powershell]
//                Linux, macOS: [--user name] [--shell]
//   matgate-agent uninstall [--keep-config] [--gui]
//   matgate-agent tray                 the icon in the notification area (started at sign-in)
//   matgate-agent start|stop|restart   the service or the logon task (administrator rights; what the tray asks for)
//   matgate-agent status [--port n]    what the tray sees
//   matgate-agent snapshot <dir>       every window as a picture, from sample data
//   matgate-agent --version
var command = args.FirstOrDefault() ?? "";
if (args.Length > 0)
{
    AgentLog.AttachToParentConsole();
}

// All four parts: a CI build is 0.3.2.57, and Matgate compares against exactly that.
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
try
{
    switch (command)
    {
        case "":
            if (!AgentApp.HasDisplay)
            {
                PrintHelp(version);
                return 2;
            }

            return OnWindowThread(() => RunSetupWindow(uninstall: false));
        case "setup-window":
            return OnWindowThread(() => RunSetupWindow(uninstall: false));
        case "run":
        case "--config":
            await RunAgentAsync(args, version);
            return 0;
        case "setup":
        case "install":
            return RunSetup(args);
        case "uninstall":
            return OnWindowThread(() => RunUninstall(args));
        case "tray":
            return OnWindowThread(AgentTray.Run);
        case "connection-info":
            // The values for Matgate in a window of their own - what the tray opens with administrator rights where
            // the settings are not the signed-in user's to read.
            return OnWindowThread(() => AgentApp.Run((_, lifetime) =>
            {
                if (ConnectionWindow.TryCreate(out var notReadable) is not { } window)
                {
                    lifetime.Shutdown();
                    return;
                }

                window.Closed += (_, _) => lifetime.Shutdown();
                window.Show();
            }));
        case "connection-json":
            // The same values for a program to read (the tray on Linux and macOS, which asks with an administrator's rights).
            return PrintConnection();
        case "start":
            // The tray's "Dienst starten ..." (elevated): the service, or the logon task.
            return AgentInstall.Start(Console.WriteLine) ? 0 : 1;
        case "stop":
            // The tray's "Dienst beenden ..." (elevated). The tray itself keeps running and shows the agent stopped.
            return AgentInstall.Stop(Console.WriteLine) ? 0 : 1;
        case "restart":
            return AgentInstall.Restart(Console.WriteLine) ? 0 : 1;
        case "snapshot":
        case "setup-preview":
            // Draws every window into image files without showing them, from sample data (ANNA-PC) - for checking the
            // layout and for pictures that may be published.
            return OnWindowThread(() => Snapshot.Run(args.Length > 1 ? args[1] : "."));
        case "status":
            // What the tray icon sees, for a console.
            var portIndex = Array.IndexOf(args, "--port");
            var status = portIndex > 0 && portIndex + 1 < args.Length ? AgentStatus.Query(int.Parse(args[portIndex + 1])) : AgentStatus.QueryInstalled();
            Console.WriteLine(status is null ? "The agent is not running." : System.Text.Json.JsonSerializer.Serialize(status));
            return status is null ? 1 : 0;
        case "session-host":
            // A helper the agent starts where the monitor is (another Windows session, the X display, the
            // Mac's window server) - never by hand.
            return await SessionHost.RunAsync(args);
#if AGENT_WINDOWS
        case "apply-update":
            return UpdatePlugin.Run(args);
        case "screen-probe":
            // Captures and encodes for a few seconds and prints what came out - for checking a machine.
            return Matgate.Agent.Screen.ScreenProbe.Run(args);
#else
        case "as-user":
            // What the agent starts for a program in a terminal that runs as another user - never by hand.
            return UnixUser.Become(args);
        case "screen-probe":
            // Says which screen the agent would show, and captures and encodes a little of it.
            return Matgate.Agent.Unix.Screen.UnixScreenProbe.Run(args);
#endif
        case "--version":
            Console.WriteLine(version);
            return 0;
        case "--help":
        case "help":
            PrintHelp(version);
            return 0;
        default:
            Console.WriteLine($"Unknown command \"{command}\". Commands: run, setup, uninstall, tray, status, --version.");
            return 2;
    }
}
catch (Exception exception) when (command is "setup" or "install" or "uninstall")
{
    Console.WriteLine("Failed: " + exception.Message);
    return 1;
}

static void PrintHelp(string version)
{
    Console.WriteLine($"Matgate agent {version} ({AgentPlatform.Id})");
    Console.WriteLine(OperatingSystem.IsWindows()
        ? "  matgate-agent setup [--port 7488] [--mode user|system|service] [--root Name=C:\\Path ...]"
        : "  sudo matgate-agent setup [--port 7488] [--root Name=/path ...] [--user name] [--no-claude] [--shell]");
    Console.WriteLine("  matgate-agent uninstall [--keep-config]");
    Console.WriteLine("  matgate-agent start|stop|restart");
    Console.WriteLine("  matgate-agent run [--config path]");
    Console.WriteLine("  matgate-agent status");
}

// A window needs a thread of its own kind: on Windows a single-threaded apartment (the folder picker, the
// clipboard), everywhere not the thread pool the async entry point runs on.
static int OnWindowThread(Func<int> body)
{
    var result = 0;
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            result = body();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    if (OperatingSystem.IsWindows())
    {
        thread.SetApartmentState(ApartmentState.STA);
    }

    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    return result;
}

// The setup in its window. On Windows the window itself has administrator rights (asked for when it starts)
// and does the work; on Linux and macOS it runs as the signed-in person and hands the work to a second run
// of this program with an administrator's rights - a window of root's is nothing a desktop there shows.
static int RunSetupWindow(bool uninstall)
{
    if (OperatingSystem.IsWindows() && !AgentInstall.IsElevated)
    {
        return AgentInstall.RunElevated(uninstall ? ["uninstall", "--gui"] : [], wait: false) ? 0 : 1;
    }

    return AgentApp.Run((_, lifetime) =>
    {
        var setup = AgentSetup.Create(uninstall, AgentInstall.FindExisting(), InstallFromWindow, UninstallFromWindow);
        setup.Window.Finished += (_, _) => lifetime.Shutdown();
        setup.Window.Show();
        setup.Window.Activate();
    });
}

static SetupResult InstallFromWindow(SetupOptions options, Action<string> log)
{
    if (AgentInstall.IsElevated)
    {
        return AgentInstall.Install(options, log);
    }

    // Linux and macOS, as the signed-in person: the same setup as a command, with an administrator's rights.
    // It says what it does line by line and ends with the values Matgate needs.
    SetupResult? result = null;
    var lines = new List<string>();
    var arguments = AgentInstall.SetupArguments(options);
    arguments.Add("--result-json");
    var ok = AgentInstall.RunElevated(arguments, wait: true, line =>
    {
        if (line.StartsWith("RESULT ", StringComparison.Ordinal))
        {
            result = System.Text.Json.JsonSerializer.Deserialize<SetupResult>(line[7..]);
        }
        else if (!string.IsNullOrWhiteSpace(line))
        {
            lines.Add(line);
            log(line);
        }
    });
    if (!ok || result is null)
    {
        throw new InvalidOperationException(lines.Count > 0
            ? lines[^1]
            : "Das Setup braucht die Rechte eines Administrators und hat sie nicht bekommen. In einem Terminal geht es so: sudo ./matgate-agent setup");
    }

    return result;
}

static void UninstallFromWindow(bool keepSettings, Action<string> log)
{
    if (AgentInstall.IsElevated)
    {
        AgentInstall.Uninstall(keepSettings, log);
        return;
    }

    var arguments = new List<string> { "uninstall" };
    if (keepSettings)
    {
        arguments.Add("--keep-config");
    }

    if (!AgentInstall.RunElevated(arguments, wait: true, log))
    {
        throw new InvalidOperationException("Das Entfernen braucht die Rechte eines Administrators und hat sie nicht bekommen.");
    }
}

static int RunSetup(string[] args)
{
    var options = new SetupOptions();
    var resultAsJson = false;
    for (var i = 1; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"{args[i]} needs a value.");
        switch (args[i])
        {
            case "--port": options.Port = int.Parse(Next()); break;
            case "--firewall": options.OpenFirewall = true; break;
            case "--no-firewall": options.OpenFirewall = false; break;
            case "--mode":
                // "system": a LocalSystem service - the screen also at the sign-in screen, programs as the signed-in user.
                var mode = Next();
                options.Mode = !OperatingSystem.IsWindows() || mode is "service" or "system" ? "service" : "user";
                if (mode == "system")
                {
                    options.Account = "LocalSystem";
                }

                break;
            case "--account": options.Account = Next(); break;
            case "--password": options.Password = Next(); break;
            case "--user": options.RunAs = Next(); break;
            case "--no-claude": options.Claude = false; break;
            case "--powershell": options.PowerShell = true; break;
            case "--shell": options.Shell = true; break;
            case "--no-auto-update": options.AutoUpdate = false; break;
            case "--no-screen": options.Screen = false; break;
            case "--logo": options.Logo = Path.GetFullPath(Next()); break;
            case "--no-logo": options.Logo = ""; break;
            case "--no-tray": options.Tray = false; break;
            case "--dry-run": options.DryRun = true; break;
            case "--result-json": resultAsJson = true; break;
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

    if (!options.DryRun && !AgentInstall.IsElevated)
    {
        Console.WriteLine(OperatingSystem.IsWindows()
            ? "Setup needs administrator rights - run it in an administrator console."
            : "Setup needs root - run it with sudo.");
        return 1;
    }

    if (OperatingSystem.IsWindows() && options.Roots.Count == 0 && AgentInstall.FindExisting() is null)
    {
        options.Roots.Add(new AgentRoot { Name = "Home", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
    }

    var result = AgentInstall.Install(options, Console.WriteLine);
    if (resultAsJson)
    {
        Console.WriteLine("RESULT " + System.Text.Json.JsonSerializer.Serialize(result));
    }
    else if (!options.DryRun)
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
    if (!gui)
    {
        if (!AgentInstall.IsElevated)
        {
            Console.WriteLine(OperatingSystem.IsWindows()
                ? "Uninstall needs administrator rights - run it in an administrator console."
                : "Uninstalling needs root - run it with sudo.");
            return 1;
        }

        AgentInstall.Uninstall(args.Contains("--keep-config"), Console.WriteLine);
        return 0;
    }

    if (OperatingSystem.IsWindows() && !AgentInstall.IsElevated)
    {
        return AgentInstall.RunElevated(["uninstall", "--gui"], wait: false) ? 0 : 1;
    }

    // Windows: the program folder cannot go while this exe runs from it - like MatMon's uninstaller, a copy in
    // %TEMP% does the work and removes itself afterwards.
    var singleFile = string.IsNullOrEmpty(typeof(AgentInstall).Assembly.Location);
    if (OperatingSystem.IsWindows() && singleFile && !args.Contains("--relaunched")
        && Environment.ProcessPath!.StartsWith(AgentInstall.ProgramFolder, StringComparison.OrdinalIgnoreCase))
    {
        var copy = Path.Combine(Path.GetTempPath(), $"Matgate Agent deinstallieren {Guid.NewGuid():N}.exe");
        File.Copy(Environment.ProcessPath!, copy);
        Process.Start(new ProcessStartInfo(copy) { UseShellExecute = false, ArgumentList = { "uninstall", "--gui", "--relaunched" } });
        return 0;
    }

    var code = RunSetupWindow(uninstall: true);
    if (OperatingSystem.IsWindows() && args.Contains("--relaunched"))
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{Environment.ProcessPath}\"") { CreateNoWindow = true, UseShellExecute = false });
    }

    return code;
}

static int PrintConnection()
{
    try
    {
        var path = File.Exists(AgentInstall.ConfigPath) ? AgentInstall.ConfigPath : Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
        if (!File.Exists(path))
        {
            Console.WriteLine("The agent is not set up.");
            return 1;
        }

        var config = AgentConfig.LoadOrCreate(path, out _);
        var certificate = AgentCertificate.LoadOrCreate(config, Path.GetDirectoryName(path)!);
        Console.WriteLine("RESULT " + System.Text.Json.JsonSerializer.Serialize(new SetupResult(config.Token, certificate.GetCertHashString(HashAlgorithmName.SHA256), config.Port, true)));
        return 0;
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine("The settings are for administrators.");
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

    // Installed: the system's settings folder. Started by hand from a folder: next to the program.
    configPath ??= File.Exists(AgentInstall.ConfigPath)
        ? AgentInstall.ConfigPath
        : Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
    var directory = Path.GetDirectoryName(configPath)!;
    Directory.CreateDirectory(directory);
    AgentLog.UseFile(directory);

    var config = AgentConfig.LoadOrCreate(configPath, out var created);
    var certificate = AgentCertificate.LoadOrCreate(config, directory);
    // 60 minutes was the old default for terminals nobody looks at; sessions now stay until they are ended.
    if (config.DetachedSessionMinutes == 60)
    {
        config.DetachedSessionMinutes = 0;
        try
        {
            config.Save(configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

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
    AgentStatus.Port = config.Port;
    AgentStatus.Installed = string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(AgentInstall.ConfigPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
#if AGENT_WINDOWS
    UpdatePlugin.TidyUp();
    var terminals = new TerminalPlugin(config, files, new PtyLauncher());
    IAgentUpdates updates = new UpdatePlugin(config, configPath);
    AgentStatus.Mode = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? "service" : "user";
#else
    var terminals = new TerminalPlugin(config, files, new UnixPtyStarter(config));
    IAgentUpdates updates = new UnixUpdates(configPath);
    AgentStatus.Mode = AgentStatus.Installed ? "service" : "user";
#endif
    IAgentScreen screen = new Matgate.Agent.Screen.ScreenPlugin(config);
    if (config.Screen && !screen.IsAvailable && Matgate.Agent.Screen.ScreenPlatform.Current.UnsupportedReason is { } noScreen)
    {
        // Wanted, but something is missing on this machine: said once, with what to install.
        AgentLog.Write("Screen: " + noScreen);
    }

    AgentStatus.OpenTerminals = () => terminals.Count;
    var tokenBytes = Encoding.UTF8.GetBytes(config.Token.Trim());

    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
#if AGENT_WINDOWS
    builder.Host.UseWindowsService(options => options.ServiceName = Installer.ServiceName);
#endif
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
        // Trimmed: a token pasted with a space or a line break at its end is still the token.
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
        await new Connection(socket, config, directory, version, files, terminals, updates, screen, () =>
        {
            // A new version: as a service a non-zero exit makes the system start the agent again (Windows' failure
            // actions with its updater, systemd, launchd).
            Environment.Exit(AgentStatus.Mode == "service" ? 1 : 0);
        }).RunAsync(context.RequestAborted);

        AgentLog.Write("Matgate disconnected");
    });

    // With Matgate's address set, the agent connects to Matgate itself - for a machine Matgate cannot reach - and
    // keeps that connection, again and again when it breaks. It is the same connection as one Matgate opens.
    if (Uri.TryCreate(config.MatgateUrl.Trim(), UriKind.Absolute, out var matgateUri) && matgateUri.Scheme is "https" or "http")
    {
        var attach = new UriBuilder(matgateUri) { Scheme = matgateUri.Scheme == "https" ? "wss" : "ws", Path = matgateUri.AbsolutePath.TrimEnd('/') + "/api/agent/attach" }.Uri;
        var stopping = app.Lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            var wait = TimeSpan.FromSeconds(5);
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    using var outgoing = new System.Net.WebSockets.ClientWebSocket();
                    outgoing.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    outgoing.Options.SetRequestHeader("Authorization", "Bearer " + config.Token.Trim());
                    await outgoing.ConnectAsync(attach, stopping);
                    AgentLog.Write($"Connected to Matgate at {matgateUri.Host}");
                    wait = TimeSpan.FromSeconds(5);
                    await new Connection(outgoing, config, directory, version, files, terminals, updates, screen, () =>
                    {
                        Environment.Exit(AgentStatus.Mode == "service" ? 1 : 0);
                    }).RunAsync(stopping);
                    AgentLog.Write("The connection to Matgate ended");
                }
                catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or HttpRequestException or IOException or InvalidOperationException)
                {
                    AgentLog.Write($"Matgate at {matgateUri.Host} not reached ({exception.Message.Split('\n')[0]}) - trying again in {wait.TotalSeconds:0} s");
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await Task.Delay(wait, stopping);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
            }
        });
    }

    AgentStatus.StartServer(app.Lifetime.ApplicationStopping);
    await app.RunAsync();
}
