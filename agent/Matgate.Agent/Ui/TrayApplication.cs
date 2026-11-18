using System.Diagnostics;
using Matgate.Agent.Setup;
using Microsoft.Win32;

namespace Matgate.Agent.Ui;

// The icon in the notification area, as every tray of the Mat family (TRAY-01 ... TRAY-05): a separate process in the signed-in user's
// session (the agent may run as a service in session 0 and cannot show anything). It only LOOKS at the agent (the status pipe, which
// carries no secret), opens windows and asks - with administrator rights - for the agent to start, stop or restart; it never talks to
// Matgate. Quitting it ends the icon, never the agent (AGENT-04).
internal sealed class TrayApplication : ApplicationContext
{
    private const string MutexName = @"Local\MatgateAgentTray";
    // A second start (the Start menu entry while the icon runs) asks the running one to show Info.
    private const string ShowInfoEventName = @"Local\MatgateAgentTrayShowInfo";

    // How long a start is given before a silent agent counts as stopped: at sign-in the agent and this icon start together, and a
    // service that was just asked to start needs a moment too.
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(90);

    private readonly NotifyIcon _icon;
    private readonly Dictionary<AgentState, Icon> _icons;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 4000 };
    private readonly ContextMenuStrip _menu = new();
    private InfoWindow? _info;
    private AgentStatus.Snapshot? _status;
    private SetupOptions? _installed;
    private AgentState _state = AgentState.NotSetUp;
    private string _detail = "";
    private DateTime _waitUntil = DateTime.UtcNow + StartGrace;
    private static Font? _bold;

    // The program file as it was when the icon started: an update puts a new one there (see UpdatePlugin), and
    // the icon then starts again from it - otherwise it would show the old version until the next sign-in.
    private static readonly (DateTime, long) StartedFrom = Stamp();
    private static bool _restart;

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

    private TrayApplication()
    {
        var size = SystemInformation.SmallIconSize.Width;
        _icons = Enum.GetValues<AgentState>().ToDictionary(state => state, state => Mark.Create(state, size));
        _menu.Opening += (_, _) => BuildMenu();
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Icon = _icons[_state], Text = _state.Tooltip(), Visible = true };
        // TRAY-03: on Windows a left click runs the default item (Open), the right click opens the menu.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Open();
            }
        };
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
        BuildMenu();
    }

    public static void Run()
    {
        using var showInfo = new EventWaitHandle(false, EventResetMode.AutoReset, ShowInfoEventName);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            showInfo.Set();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Theme.FollowSystem();
        var tray = new TrayApplication();
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        new Thread(() =>
        {
            try
            {
                while (showInfo.WaitOne())
                {
                    ui.Post(_ => tray.ShowInfo(), null);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }) { IsBackground = true }.Start();
        Application.Run(tray);
        if (_restart)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "tray") { UseShellExecute = false });
        }
    }

    private void Refresh()
    {
        _installed = Installer.FindExisting();
        _status = AgentStatus.QueryInstalled();
        (_state, _detail) = Describe(_status, _installed, _installed?.Mode == "service" ? Installer.ServiceStatus() : null, DateTime.UtcNow < _waitUntil);
        if (_state == AgentState.Running)
        {
            // Seen running: from now on silence means stopped, not starting.
            _waitUntil = DateTime.MinValue;
        }

        _icon.Icon = _icons[_state];
        if (_state == AgentState.Running && StartedFrom != default && Stamp() is var now && now != default && now != StartedFrom)
        {
            _restart = true;
            _icon.Visible = false;
            ExitThread();
            return;
        }

        // The tooltip is limited to 127 characters.
        var tooltip = _state.Tooltip();
        _icon.Text = tooltip[..Math.Min(127, tooltip.Length)];
        if (_info is { Visible: true })
        {
            _info.Show(_state, _detail, Rows());
        }
    }

    // The state and the one sentence that explains it (TRAY-02, TRAY-05). Amber is a start or a stop under way - a service that is
    // starting or stopping, or a start that was asked for a moment ago.
    internal static (AgentState State, string Detail) Describe(AgentStatus.Snapshot? status, SetupOptions? installed,
        System.ServiceProcess.ServiceControllerStatus? service, bool starting)
    {
        var noun = installed?.Mode == "service" ? "Dienst" : "Agent";
        if (status is not null)
        {
            // Whether Matgate is connected right now is left out on purpose: Matgate keeps its connection open while idle, so
            // "connected" said nothing. Who connected when is in the log.
            return (AgentState.Running, $"Bereit für Matgate auf Port {status.Port}. Wer sich wann verbunden hat, steht im Log.");
        }

        if (installed is null && !File.Exists(Installer.InstalledExe))
        {
            return (AgentState.NotSetUp, "Der Agent ist auf diesem Rechner nicht eingerichtet. „Einrichten …“ im Menü richtet ihn ein.");
        }

        var port = installed?.Port ?? 7488;
        switch (service)
        {
            case System.ServiceProcess.ServiceControllerStatus.StartPending or System.ServiceProcess.ServiceControllerStatus.ContinuePending:
                return (AgentState.Waiting, $"Der Dienst startet gerade und ist gleich auf Port {port} bereit.");
            case System.ServiceProcess.ServiceControllerStatus.StopPending or System.ServiceProcess.ServiceControllerStatus.PausePending:
                return (AgentState.Waiting, "Der Dienst wird gerade beendet.");
        }

        if (starting)
        {
            return (AgentState.Waiting, $"Der {noun} startet gerade und ist gleich auf Port {port} bereit.");
        }

        return service == System.ServiceProcess.ServiceControllerStatus.Running
            ? (AgentState.Stopped, "Der Dienst läuft, antwortet aber nicht. „Dienst neu starten …“ im Menü, oder das Log ansehen.")
            : (AgentState.Stopped, $"Installiert, aber der {noun} läuft nicht. „{noun} starten …“ im Menü, oder das Log ansehen.");
    }

    // The menu in the order of TRAY-01: name and version, the state (both disabled); Open (bold, the default); the agent's own item;
    // Info, Set up, Open log folder; the actions of the service with the shield; Quit, last. "Install update …" is left out: the
    // agent has no update that waits - Matgate brings and applies it. There is no Uninstall (Installed apps does that, SETUP-04).
    private void BuildMenu() => Fill(_menu, _state, _installed, _installed is not null || File.Exists(Installer.InstalledExe), new TrayActions(
        Open, ShowConnectionDetails, ShowInfo, () => Launch([], elevated: true), OpenLogFolder, Control, ExitThread));

    internal static void Fill(ContextMenuStrip menu, AgentState state, SetupOptions? installed, bool set, TrayActions act)
    {
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToList())
        {
            item.Dispose();
        }

        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"{Installer.ProductName} {AgentVersion.Number}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(state.Word()) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        var open = new ToolStripMenuItem($"{Installer.ProductName} öffnen", null, (_, _) => act.Open());
        // The default item in bold (the font of the menu, kept for the next time it opens).
        _bold ??= new Font(menu.Font, FontStyle.Bold);
        open.Font = _bold;
        menu.Items.Add(open);
        if (set)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Verbindungsdaten …", null, (_, _) => act.ConnectionDetails()));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Info …", null, (_, _) => act.Info()));
        menu.Items.Add(new ToolStripMenuItem("Einrichten …", Brand.Shield(), (_, _) => act.SetUp()));
        if (set)
        {
            menu.Items.Add(new ToolStripMenuItem("Log-Ordner öffnen", null, (_, _) => act.LogFolder()));
        }

        var services = ServiceItems(state, installed, act).ToList();
        if (services.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.AddRange([.. services]);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) => act.Quit()));
    }

    // Start, stop, restart - in that order, each with the shield: they ask for administrator rights. Only what applies now: start a
    // stopped agent, stop or restart a running one. In user mode the agent is a task at sign-in, not a service, and says so.
    private static IEnumerable<ToolStripMenuItem> ServiceItems(AgentState state, SetupOptions? installed, TrayActions act)
    {
        if (installed is null)
        {
            yield break;
        }

        var noun = installed.Mode == "service" ? "Dienst" : "Agent";
        if (state is AgentState.Stopped)
        {
            yield return new ToolStripMenuItem($"{noun} starten …", Brand.Shield(), (_, _) => act.Service("start"));
        }

        if (state is AgentState.Running or AgentState.Waiting)
        {
            yield return new ToolStripMenuItem($"{noun} beenden …", Brand.Shield(), (_, _) => act.Service("stop"));
            yield return new ToolStripMenuItem($"{noun} neu starten …", Brand.Shield(), (_, _) => act.Service("restart"));
        }
    }

    private void Control(string command)
    {
        if (Launch([command], elevated: true) && command != "stop")
        {
            _waitUntil = DateTime.UtcNow + StartGrace;
            Refresh();
        }
    }

    // "Matgate Agent öffnen": the agent has no page of its own and does not know the address of the Matgate that connects to it, so it
    // opens Info (TRAY-01: the window, or the web page, or Info).
    private void Open() => ShowInfo();

    private IReadOnlyList<KeyValuePair<string, string>> Rows() => InfoRows(_status, _installed);

    // The facts of Info (TRAY-05): machine, port, mode, started, version, terminals, data folder. Never the token (AGENT-03).
    internal static IReadOnlyList<KeyValuePair<string, string>> InfoRows(AgentStatus.Snapshot? status, SetupOptions? installed)
    {
        var rows = new List<KeyValuePair<string, string>> { new("Rechner", Machine.Name) };
        if (status is not null || installed is not null)
        {
            rows.Add(new("Port", (status?.Port ?? installed?.Port ?? 0).ToString()));
        }

        if (installed is not null)
        {
            rows.Add(new("Modus", installed.Mode != "service"
                ? "als angemeldeter Benutzer"
                : WindowsNative.IsLocalSystem(installed.Account) ? "als Systemdienst" : $"als Dienst unter {installed.Account}"));
        }

        if (status is not null)
        {
            rows.Add(new("Gestartet", $"{status.Started:dd.MM.yyyy HH:mm} Uhr"));
            rows.Add(new("Version", AgentVersion.Value(status.Version)));
            // Worth knowing before a restart - it ends them.
            rows.Add(new("Terminals", status.Terminals == 0 ? "keine offen" : $"{status.Terminals} offen"));
        }

        rows.Add(new("Datenordner", Machine.DataFolder));
        return rows;
    }

    private void ShowInfo()
    {
        _info ??= new InfoWindow(ShowConnectionDetails, OpenLogFolder);
        _info.Show(_state, _detail, Rows());
        _info.Show();
        _info.Activate();
    }

    // The system switched between light and dark: the next windows take the new mode, an open Info is drawn again in it.
    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Theme.SystemIsDark() == Theme.IsDark)
        {
            return;
        }

        Theme.FollowSystem();
        var wasOpen = _info is { Visible: true };
        _info?.Dispose();
        _info = null;
        if (wasOpen)
        {
            ShowInfo();
        }
    }

    private void ShowConnectionDetails()
    {
        // In user mode the settings are the user's to read; a service's need administrator rights.
        if (!ConnectionWindow.TryShow())
        {
            Launch(["connection-info"], elevated: true);
        }
    }

    private static void OpenLogFolder()
    {
        if (Directory.Exists(Installer.DataFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Installer.DataFolder}\"") { UseShellExecute = true });
        }
    }

    // False when the administrator prompt was declined.
    private static bool Launch(string[] arguments, bool elevated)
    {
        var exe = File.Exists(Installer.InstalledExe) ? Installer.InstalledExe : Environment.ProcessPath!;
        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = elevated ? "runas" : "" };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined.
            return false;
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _info?.Dispose();
        base.ExitThreadCore();
    }
}

// What the items of the tray menu do (TrayApplication.Fill); setup-preview draws the menu with actions that do nothing.
internal sealed record TrayActions(Action Open, Action ConnectionDetails, Action Info, Action SetUp, Action LogFolder, Action<string> Service, Action Quit)
{
    public static TrayActions None { get; } = new(() => { }, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { });
}

// "Info", the one window of the tray (TRAY-05): the state as a dot, a word and a sentence, the facts, and the footer with the version,
// the way to the connection details, the log folder and Close.
internal sealed class InfoWindow : BrandWindow
{
    private const int KeyWidth = 140;
    private readonly StateLine _state = new();
    private readonly TableLayoutPanel _rows = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 6, 0, 0) };
    private AgentState? _iconState;

    public InfoWindow(Action connectionDetails, Action logFolder) : base($"{Installer.ProductName} – Info", "Info")
    {
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, KeyWidth));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var details = Brand.FooterButton("Verbindungsdaten", width: 140);
        details.Click += (_, _) => connectionDetails();
        var log = Brand.FooterButton("Log-Ordner", width: 104);
        log.Click += (_, _) => logFolder();
        var close = Brand.FooterButton("Schließen", width: 96);
        close.Click += (_, _) => Hide();
        CancelButton = close;
        Content.Controls.AddRange([_state, _rows]);
        Complete(details, log, close);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    public void Show(AgentState state, string detail, IReadOnlyList<KeyValuePair<string, string>> rows)
    {
        _state.Set(state, detail);
        if (_iconState != state)
        {
            _iconState = state;
            var previous = Icon;
            Icon = Mark.Create(state);
            previous?.Dispose();
        }

        // Drawn again every few seconds while Info is open: the labels of the last time go, not only out of the table.
        _rows.SuspendLayout();
        foreach (var label in _rows.Controls.Cast<Control>().ToList())
        {
            label.Dispose();
        }

        _rows.Controls.Clear();
        foreach (var (key, value) in rows)
        {
            _rows.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 4, 12, 4) });
            _rows.Controls.Add(new Label { Text = value, AutoSize = true, ForeColor = Theme.Text, MaximumSize = new Size(Brand.ContentWidth - KeyWidth, 0), Margin = new Padding(0, 4, 0, 4) });
        }

        _rows.ResumeLayout();
        FitHeight();
    }
}

// Host, port, password, fingerprint - what Matgate needs, ready to copy (AGENT-02), with the sentence that the password is the only key
// (AGENT-03).
internal sealed class ConnectionWindow : BrandWindow
{
    private ConnectionWindow(AgentConfig config, string fingerprint) : base($"{Installer.ProductName} – Verbindungsdaten", "Verbindungsdaten")
    {
        Content.Controls.Add(Brand.Paragraph("In Matgate eine Verbindung vom Typ „Agent“ anlegen, diese Werte eintragen und „Zertifikat ignorieren“ anhaken."));
        foreach (var field in ConnectionDetails.Fields(config.Token, fingerprint, config.Port))
        {
            Content.Controls.Add(field);
        }

        var close = Brand.FooterButton("Schließen", width: 96);
        close.Click += (_, _) => Close();
        CancelButton = close;
        Complete(close);
    }

    internal static ConnectionWindow Preview() => new(new AgentConfig { Port = 7488, Token = SetupPreview.SampleToken }, SetupPreview.SampleFingerprint);

    // False when the settings cannot be read with the current rights.
    public static bool TryShow(bool modal = false)
    {
        try
        {
            var path = File.Exists(Installer.ConfigPath) ? Installer.ConfigPath : Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
            if (!File.Exists(path))
            {
                MessageBox.Show("Der Agent ist noch nicht eingerichtet.", Installer.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            var config = AgentConfig.LoadOrCreate(path, out _);
            var certificate = AgentCertificate.LoadOrCreate(config, Path.GetDirectoryName(path)!);
            var window = new ConnectionWindow(config, certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
            if (modal)
            {
                window.ShowDialog();
            }
            else
            {
                window.Show();
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
