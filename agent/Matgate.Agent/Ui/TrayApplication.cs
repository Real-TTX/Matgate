using System.Diagnostics;
using Matgate.Agent.Setup;

namespace Matgate.Agent.Ui;

// The icon in the notification area, after MatMon's agent tray: a separate process in the signed-in
// user's session (the agent may run as a service in session 0 and cannot show anything). It only LOOKS
// at the agent (the status pipe) and opens windows; it never talks to Matgate.
internal sealed class TrayApplication : ApplicationContext
{
    private const string MutexName = @"Local\MatgateAgentTray";
    // A second start (the Start menu entry while the icon runs) asks the running one to show Info.
    private const string ShowInfoEventName = @"Local\MatgateAgentTrayShowInfo";

    private enum State
    {
        NotInstalled,
        Running,
        Stopped,
    }

    private readonly NotifyIcon _icon;
    private readonly Dictionary<State, Icon> _icons;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 4000 };
    private readonly ToolStripMenuItem _stateItem = new() { Enabled = false };
    private InfoWindow? _info;
    private AgentStatus.Snapshot? _status;
    private State _state = State.NotInstalled;

    private TrayApplication()
    {
        _icons = Enum.GetValues<State>().ToDictionary(state => state, state => Mark.Create(state switch
        {
            State.Running => Brand.Ok,
            State.Stopped => Brand.Error,
            _ => Brand.Neutral,
        }));

        var menu = new ContextMenuStrip();
        var info = new ToolStripMenuItem("Info ...", null, (_, _) => ShowInfo());
        info.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripMenuItem($"Matgate Agent {Brand.Version}") { Enabled = false });
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(info);
        menu.Items.Add(new ToolStripMenuItem("Verbindungsdaten ...", null, (_, _) => ShowConnectionDetails()));
        menu.Items.Add(new ToolStripMenuItem("Einstellungen ändern ...", null, (_, _) => Launch([], elevated: true)));
        menu.Items.Add(new ToolStripMenuItem("Agent neu starten", null, (_, _) => Launch(["restart"], elevated: true)));
        menu.Items.Add(new ToolStripMenuItem("Log-Ordner öffnen", null, (_, _) => OpenLogFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Symbol ausblenden", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Icon = _icons[_state], Text = "Matgate Agent", Visible = true };
        _icon.DoubleClick += (_, _) => ShowInfo();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
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
    }

    private void Refresh()
    {
        _status = AgentStatus.Query(Installer.FindExisting()?.Port ?? 7488);
        _state = _status switch
        {
            not null => State.Running,
            null when File.Exists(Installer.InstalledExe) => State.Stopped,
            _ => State.NotInstalled,
        };
        _icon.Icon = _icons[_state];
        var (title, _) = Describe();
        _stateItem.Text = title;
        // The tooltip is limited to 127 characters.
        _icon.Text = ("Matgate Agent - " + title)[..Math.Min(127, 16 + title.Length)];
        if (_info is { Visible: true })
        {
            _info.Show(Color(), Describe(), Rows());
        }
    }

    private Color Color() => _state switch
    {
        State.Running => Brand.Ok,
        State.Stopped => Brand.Error,
        _ => Brand.Neutral,
    };

    private (string Title, string Detail) Describe() => _state switch
    {
        // Whether Matgate is connected right now is left out on purpose: Matgate keeps its connection open
        // while idle, so "connected" said nothing. Who connected when is in the log.
        State.Running => ("Läuft", $"Bereit für Matgate auf Port {_status!.Port}. Wer sich wann verbunden hat, steht im Log."),
        State.Stopped => ("Agent läuft nicht", "Installiert, antwortet aber nicht. \"Agent neu starten\" im Menü, oder das Log ansehen."),
        _ => ("Nicht eingerichtet", "Das Setup richtet den Agent ein: matgate-agent.exe doppelklicken."),
    };

    private IReadOnlyList<KeyValuePair<string, string>> Rows()
    {
        var installed = Installer.FindExisting();
        var rows = new List<KeyValuePair<string, string>> { new("Rechner", Environment.MachineName) };
        if (_status is not null || installed is not null)
        {
            rows.Add(new("Port", (_status?.Port ?? installed?.Port ?? 0).ToString()));
        }

        if (installed is not null)
        {
            rows.Add(new("Modus", installed.Mode == "service" ? $"als Dienst ({installed.Account})" : "als angemeldeter Benutzer"));
        }

        if (_status is not null)
        {
            rows.Add(new("Gestartet", $"{_status.Started:dd.MM.yyyy HH:mm} Uhr"));
            rows.Add(new("Version", _status.Version));
            // Worth knowing before "Agent neu starten" - a restart ends them.
            rows.Add(new("Terminals", _status.Terminals == 0 ? "keine offen" : $"{_status.Terminals} offen"));
        }

        rows.Add(new("Einstellungen", Installer.DataFolder));
        return rows;
    }

    private void ShowInfo()
    {
        _info ??= new InfoWindow(ShowConnectionDetails, OpenLogFolder);
        _info.Show(Color(), Describe(), Rows());
        _info.Show();
        _info.Activate();
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

    private static void Launch(string[] arguments, bool elevated)
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
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined.
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _info?.Dispose();
        base.ExitThreadCore();
    }
}

// "Info": the state, what the agent is and does, and the way to the connection details.
internal sealed class InfoWindow : BrandWindow
{
    private const int KeyWidth = 140;
    private readonly StateLine _state = new();
    private readonly TableLayoutPanel _rows = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 6, 0, 0) };
    private Color? _iconColor;

    public InfoWindow(Action connectionDetails, Action logFolder) : base("Matgate Agent - Info", "Info")
    {
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, KeyWidth));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var close = Brand.FooterButton("Schließen", width: 96, gap: 16);
        close.Click += (_, _) => Hide();
        var details = Brand.FooterButton("Verbindungsdaten", width: 140);
        details.Click += (_, _) => connectionDetails();
        var log = Brand.FooterButton("Log-Ordner", width: 104);
        log.Click += (_, _) => logFolder();
        CancelButton = close;
        Content.Controls.AddRange([_state, _rows]);
        Complete(close, details, log);
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

    public void Show(Color color, (string Title, string Detail) state, IReadOnlyList<KeyValuePair<string, string>> rows)
    {
        _state.Set(color, state.Title, state.Detail);
        if (_iconColor != color)
        {
            _iconColor = color;
            Icon = Mark.Create(color);
        }

        _rows.SuspendLayout();
        _rows.Controls.Clear();
        foreach (var (key, value) in rows)
        {
            _rows.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = Brand.Muted, Margin = new Padding(0, 4, 12, 4) });
            _rows.Controls.Add(new Label { Text = value, AutoSize = true, ForeColor = Brand.TextColor, MaximumSize = new Size(Brand.ContentWidth - KeyWidth, 0), Margin = new Padding(0, 4, 0, 4) });
        }

        _rows.ResumeLayout();
        FitHeight();
    }
}

// Host, port, token, fingerprint - what Matgate needs, ready to copy.
internal sealed class ConnectionWindow : BrandWindow
{
    private ConnectionWindow(AgentConfig config, string fingerprint) : base("Matgate Agent - Verbindungsdaten", "Verbindung")
    {
        Content.Controls.Add(Brand.Paragraph("In Matgate eine Verbindung vom Typ \"Agent\" anlegen, diese Werte eintragen und \"Zertifikat ignorieren\" anhaken."));
        foreach (var field in ConnectionDetails.Fields(config.Token, fingerprint, config.Port))
        {
            Content.Controls.Add(field);
        }

        Content.Controls.Add(new BrandNotice("Das Token ist der einzige Schlüssel zu diesem Rechner - nur in Matgate eintragen.", NoticeTone.Warning));
        var close = Brand.FooterButton("Schließen", width: 96, gap: 16);
        close.Click += (_, _) => Close();
        CancelButton = close;
        Complete(close);
    }

    internal static ConnectionWindow Preview() => new(new AgentConfig { Port = 7488, Token = "q3Vx9Lk2-example-token-Rt7mWp" }, "B6A1A74FF49B59E48728364E704707897F3C88F02CCAA3EAD9237DFE224BA135");

    // False when the settings cannot be read with the current rights.
    public static bool TryShow(bool modal = false)
    {
        try
        {
            var path = File.Exists(Installer.ConfigPath) ? Installer.ConfigPath : Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
            if (!File.Exists(path))
            {
                MessageBox.Show("Der Agent ist noch nicht eingerichtet.", "Matgate Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
