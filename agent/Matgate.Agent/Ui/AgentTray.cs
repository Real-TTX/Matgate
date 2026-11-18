using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using Matgate.Agent.Setup;

namespace Matgate.Agent.Ui;

// The icon in the notification area, as every tray of the Mat family (TRAY-01 ... TRAY-05): a separate process in the session of
// whoever is signed in (the agent runs as the system's service and can show nothing). It only LOOKS at the agent (the status
// channel, which carries no secret), opens windows and asks - with administrator rights - for the agent to start, stop or
// restart; it never talks to Matgate. Quitting it ends the icon, never the agent (AGENT-04).
internal sealed class AgentTray
{
    // How long a start is given before a silent agent counts as stopped: at sign-in the agent and this icon start together, and a
    // service that was just asked to start needs a moment too.
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(90);

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly MatTrayMenu _menu;
    private readonly MatTray _tray;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private MatInfoWindow? _info;
    private ConnectionWindow? _connection;
    private AgentStatus.Snapshot? _status;
    private SetupOptions? _installed;
    private MatState _state = MatState.NotSetUp;
    private string _detail = "";
    private DateTime _waitUntil = DateTime.UtcNow + StartGrace;

    // The program file as it was when the icon started: an update puts a new one there, and the icon then starts again from it -
    // otherwise it would show the old version until the next sign-in.
    private static readonly (DateTime, long) StartedFrom = Stamp();

    private AgentTray(AgentApp app, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        _menu = new MatTrayMenu(AgentInstall.ProductName, AgentVersion.Number, quit: () => lifetime.Shutdown())
        {
            // "Matgate Agent öffnen": the agent has no page of its own and does not know the address of the Matgate that connects
            // to it, so it opens Info (TRAY-01: the window, or the web page, or Info).
            Open = ShowInfo,
            Info = ShowInfo,
            SetUp = OpenSetup,
            LogFolder = () => AgentInstall.OpenFolder(AgentInstall.DataFolder),
        };
        Look();
        Describe();
        _tray = new MatTray(app, _menu, AgentMark.Picture(128));
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    // "Einrichten …": the setup window. On Windows it needs administrator rights to open at all; on Linux and
    // macOS it opens as this person and asks for an administrator's rights when it installs.
    private static void OpenSetup()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() => AgentInstall.RunElevated([], wait: false));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(AgentInstall.Program) { UseShellExecute = false, ArgumentList = { "setup-window" } });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The program is gone: nothing to set up with.
        }
    }

    // One icon per signed-in person; a second start (the Start menu entry while the icon runs) asks the running one to show Info.
    public static int Run()
    {
        using var single = new MatSingleInstance("MatgateAgentTray");
        if (!single.TryClaim(["info"]))
        {
            return 0;
        }

        var restart = false;
        var code = AgentApp.Run((app, lifetime) =>
        {
            var tray = new AgentTray(app, lifetime);
            single.SecondStart += (_, _) => Dispatcher.UIThread.Post(tray.ShowInfo);
            tray.RestartRequested += () =>
            {
                restart = true;
                lifetime.Shutdown();
            };
        });
        if (restart)
        {
            single.Dispose();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "tray") { UseShellExecute = false });
        }

        return code;
    }

    private event Action? RestartRequested;

    private static (DateTime, long) Stamp()
    {
        try
        {
            var file = new FileInfo(Environment.ProcessPath!);
            return (file.LastWriteTimeUtc, file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private void Look()
    {
        _installed = AgentInstall.FindExisting();
        _status = AgentStatus.QueryInstalled();
    }

    private void Refresh()
    {
        Look();
        Describe();
        if (_state.Kind == MatStateKind.Running)
        {
            // Seen running: from now on silence means stopped, not starting.
            _waitUntil = DateTime.MinValue;
            if (StartedFrom != default && Stamp() is var now && now != default && now != StartedFrom)
            {
                RestartRequested?.Invoke();
                return;
            }
        }

        _tray.Refresh();
        if (_info is { IsVisible: true })
        {
            FillInfo(_info);
        }
    }

    // The state, the one sentence that explains it (TRAY-02, TRAY-05) and what the menu offers for it.
    private void Describe()
    {
        (_state, _detail) = StateOf(_status, _installed, AgentInstall.ServiceBusy(), AgentInstall.ServiceRunning(), DateTime.UtcNow < _waitUntil);
        _menu.State = _state;
        _menu.TooltipText = _status is null ? null : $"Port {_status.Port}";
        _menu.ClearOwn();
        var isSetUp = _installed is not null || File.Exists(AgentInstall.InstalledProgram);
        if (isSetUp)
        {
            _menu.AddOwn("Verbindungsdaten …", ShowConnectionDetails);
        }

        // Start a stopped agent, stop or restart a running one - with the shield: it takes administrator rights.
        var stopped = _state.Kind == MatStateKind.Stopped;
        var up = _state.Kind is MatStateKind.Running or MatStateKind.Waiting;
        _menu.AddService(MatServiceAction.Start, () => Control("start"), enabled: isSetUp && stopped);
        _menu.AddService(MatServiceAction.Stop, () => Control("stop"), enabled: isSetUp && up);
        _menu.AddService(MatServiceAction.Restart, () => Control("restart"), enabled: isSetUp && up);
    }

    // Amber is a start or a stop under way - a service that is starting or stopping, or a start that was asked for a moment ago.
    internal static (MatState State, string Detail) StateOf(AgentStatus.Snapshot? status, SetupOptions? installed, bool? serviceBusy, bool? serviceRunning, bool starting)
    {
        var noun = installed?.Mode == "service" ? "Dienst" : "Agent";
        if (status is not null)
        {
            // Whether Matgate is connected right now is left out on purpose: Matgate keeps its connection open while idle, so
            // "connected" said nothing. Who connected when is in the log.
            return (MatState.Running, $"Bereit für Matgate auf Port {status.Port}. Wer sich wann verbunden hat, steht im Log.");
        }

        if (installed is null && !File.Exists(AgentInstall.InstalledProgram) && !Machine.Sample)
        {
            return (MatState.NotSetUp, "Der Agent ist auf diesem Rechner nicht eingerichtet. „Einrichten …“ im Menü richtet ihn ein.");
        }

        var port = installed?.Port ?? 7488;
        if (serviceBusy == true || starting)
        {
            return (MatState.Waiting, $"Der {noun} startet gerade und ist gleich auf Port {port} bereit.");
        }

        return serviceRunning == true
            ? (MatState.Stopped, "Der Dienst läuft, antwortet aber nicht. „Dienst neu starten …“ im Menü, oder das Log ansehen.")
            : (MatState.Stopped, $"Installiert, aber der {noun} läuft nicht. „Dienst starten …“ im Menü, oder das Log ansehen.");
    }

    private void Control(string command)
    {
        // Asked in the background: the question for administrator rights must not hold the icon.
        _ = Task.Run(() => AgentInstall.RunElevated([command])).ContinueWith(done =>
        {
            if (done.Result && command != "stop")
            {
                _waitUntil = DateTime.UtcNow + StartGrace;
            }

            Dispatcher.UIThread.Post(Refresh);
        });
    }

    // The facts of Info (TRAY-05): machine, port, mode, started, version, terminals, data folder. Never the token (AGENT-03).
    internal static IReadOnlyList<MatFact> Facts(AgentStatus.Snapshot? status, SetupOptions? installed)
    {
        var facts = new List<MatFact> { new("Rechner", Machine.Name) };
        if (status is not null || installed is not null)
        {
            facts.Add(new("Port", (status?.Port ?? installed?.Port ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (installed is not null)
        {
            facts.Add(new("Modus", AgentInstall.ModeText(installed)));
        }

        if (status is not null)
        {
            facts.Add(new("Gestartet", $"{status.Started:dd.MM.yyyy HH:mm} Uhr"));
            // Worth knowing before a restart - it ends them.
            facts.Add(new("Terminals", status.Terminals == 0 ? "keine offen" : $"{status.Terminals} offen"));
        }

        facts.Add(new("Datenordner", Machine.DataFolder, IsMono: true));
        return facts;
    }

    internal static MatInfoWindow InfoWindow(MatState state, string detail, AgentStatus.Snapshot? status, SetupOptions? installed, Action? connectionDetails, Action? logFolder)
    {
        var version = status?.Version is { Length: > 0 } running ? AgentVersion.Normalize(running) : AgentVersion.Number;
        var window = new MatInfoWindow(new MatInfo(AgentInstall.ProductName, version, AgentVersion.ChannelOf(version), state, detail, Facts(status, installed))
        {
            Logo = AgentMark.Picture(96),
            Open = connectionDetails,
            OpenLogFolder = logFolder,
        });
        // The agent has nothing to open but what Matgate needs to reach it.
        window.OpenButton.Content = "Verbindungsdaten";
        return window;
    }

    private void ShowInfo()
    {
        if (_info is null)
        {
            _info = InfoWindow(_state, _detail, _status, _installed, ShowConnectionDetails, () => AgentInstall.OpenFolder(AgentInstall.DataFolder));
            _info.Closed += (_, _) => _info = null;
        }

        FillInfo(_info);
        _info.Show();
        _info.BringToFront();
    }

    // Drawn again every few seconds while Info is open.
    private void FillInfo(MatInfoWindow window)
    {
        window.StateDot.State = _state;
        window.Facts.Facts = Facts(_status, _installed);
        if (window.Body.Children.Count > 1 && window.Body.Children[1] is TextBlock sentence)
        {
            sentence.Text = _detail;
        }
    }

    private void ShowConnectionDetails()
    {
        if (_connection is { IsVisible: true })
        {
            _connection.BringToFront();
            return;
        }

        // The settings are for administrators (on Windows in user mode also the user's): where they cannot be read, the same
        // window is opened by a second start with administrator rights.
        var window = ConnectionWindow.TryCreate(out var denied);
        if (window is not null)
        {
            Show(window);
            return;
        }

        if (!denied)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() => AgentInstall.RunElevated(["connection-info"], wait: false));
            return;
        }

        // Linux and macOS: the values are read with an administrator's rights, the window is this person's.
        _ = Task.Run(() =>
        {
            SetupResult? values = null;
            AgentInstall.RunElevated(["connection-json"], wait: true, line =>
            {
                if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                {
                    values = System.Text.Json.JsonSerializer.Deserialize<SetupResult>(line[7..]);
                }
            });
            if (values is { } read)
            {
                Dispatcher.UIThread.Post(() => Show(new ConnectionWindow(read.Token, read.Fingerprint, read.Port)));
            }
        });

        void Show(ConnectionWindow shown)
        {
            _connection = shown;
            shown.Closed += (_, _) => _connection = null;
            shown.Show();
            shown.BringToFront();
        }
    }
}

// Host, port, password, fingerprint - what Matgate needs, ready to copy (AGENT-02), with the sentence that the password is the only
// key (AGENT-03).
internal sealed class ConnectionWindow : MatWindow
{
    public ConnectionWindow(string token, string fingerprint, int port)
    {
        Width = 630;
        Height = 470;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = $"{AgentInstall.ProductName} – Verbindungsdaten";
        var body = new StackPanel { Margin = new Avalonia.Thickness(28, 22, 28, 20) };
        body.Children.Add(new TextBlock
        {
            Text = "In Matgate eine Verbindung vom Typ „Agent“ anlegen, diese Werte eintragen und „Zertifikat ignorieren“ anhaken.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 16),
        });
        foreach (var field in ConnectionDetails.Fields(token, fingerprint, port))
        {
            body.Children.Add(field);
        }

        var close = new Button { Content = "Schließen", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        Content = MatDialogChrome.Compose(
            MatDialogChrome.Header(AgentMark.Picture(96), AgentInstall.ProductName, "Verbindungsdaten"),
            new ScrollViewer { Content = body },
            MatDialogChrome.Footer(MatDialogChrome.VersionText(AgentVersion.Number, AgentVersion.Channel), close));
    }

    // The window with the values of this machine; null when the agent is not set up, or (denied) when the settings cannot be read
    // with the rights of this process.
    public static ConnectionWindow? TryCreate(out bool denied)
    {
        denied = false;
        try
        {
            var path = File.Exists(AgentInstall.ConfigPath) ? AgentInstall.ConfigPath : Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
            if (!File.Exists(path))
            {
                // Not there - or in a folder this user may not even look into.
                denied = !AgentInstall.IsElevated && File.Exists(AgentInstall.InstalledProgram);
                return null;
            }

            var config = AgentConfig.LoadOrCreate(path, out _);
            var certificate = AgentCertificate.LoadOrCreate(config, Path.GetDirectoryName(path)!);
            return new ConnectionWindow(config.Token, certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), config.Port);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.Cryptography.CryptographicException)
        {
            denied = true;
            return null;
        }
    }
}
