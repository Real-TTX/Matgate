using System.Diagnostics;

namespace Matgate.Agent.Setup;

// The double-click setup: port, firewall, how the agent runs, the shared folders and the programs.
// A second run starts from what is installed and updates it.
public sealed class SetupForm : Form
{
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 7488, Width = 90 };
    private readonly CheckBox _firewall = new() { Text = "Zugriff von anderen Rechnern erlauben (Windows-Firewall)", AutoSize = true, Checked = true };
    private readonly RadioButton _modeUser = new() { Text = "Als angemeldeter Benutzer (startet beim Anmelden)", AutoSize = true, Checked = true };
    private readonly RadioButton _modeService = new() { Text = "Als Windows-Dienst unter diesem Konto:", AutoSize = true };
    private readonly TextBox _account = new() { Width = 220, PlaceholderText = @".\benutzer, DOMAIN\name oder LocalSystem" };
    private readonly TextBox _password = new() { Width = 220, UseSystemPasswordChar = true, PlaceholderText = "Passwort des Kontos" };
    private readonly ListBox _roots = new() { Height = 96, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly CheckBox _claude = new() { Text = "Claude Code", AutoSize = true, Checked = true };
    private readonly CheckBox _powerShell = new() { Text = "PowerShell", AutoSize = true };
    private readonly Button _install = new() { Text = "Installieren", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(520, 0) };
    private readonly List<AgentRoot> _rootList = [];

    public SetupForm()
    {
        Text = "Matgate Agent - Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);

        var existing = Installer.FindExisting();
        if (existing is not null)
        {
            _port.Value = existing.Port;
            _firewall.Checked = existing.OpenFirewall;
            _modeService.Checked = existing.Mode == "service";
            _modeUser.Checked = !_modeService.Checked;
            _account.Text = existing.Account;
            _rootList.AddRange(existing.Roots);
            _claude.Checked = existing.Claude;
            _powerShell.Checked = existing.PowerShell;
            _install.Text = "Aktualisieren";
        }
        else
        {
            _rootList.Add(new AgentRoot { Name = "Home", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
        }

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(Heading("Matgate verbindet sich zu diesem Rechner wie zu einem RDP-Server: Host, Port und das Token, das nach der Installation angezeigt wird."));

        layout.Controls.Add(Section("Verbindung"));
        var portRow = Row(new Label { Text = "Port:", AutoSize = true, Anchor = AnchorStyles.Left }, _port);
        layout.Controls.Add(portRow);
        layout.Controls.Add(_firewall);

        layout.Controls.Add(Section("Ausführen"));
        layout.Controls.Add(_modeUser);
        layout.Controls.Add(Hint("Programme wie Claude Code laufen mit deinen Rechten und deinem Login. Kein Passwort nötig; läuft, solange du angemeldet bist."));
        layout.Controls.Add(_modeService);
        layout.Controls.Add(Row(_account, _password));
        layout.Controls.Add(Hint("Läuft auch ohne Anmeldung. Das Passwort speichert Windows in der Dienstkonfiguration, nicht der Agent."));

        layout.Controls.Add(Section("Freigegebene Ordner (Dateien in Matgate)"));
        var rootPanel = new TableLayoutPanel { ColumnCount = 2, Width = 520, Height = 100 };
        rootPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var rootButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        var add = new Button { Text = "Hinzufügen ...", AutoSize = true };
        var remove = new Button { Text = "Entfernen", AutoSize = true };
        rootButtons.Controls.AddRange([add, remove]);
        rootPanel.Controls.Add(_roots, 0, 0);
        rootPanel.Controls.Add(rootButtons, 1, 0);
        layout.Controls.Add(rootPanel);

        layout.Controls.Add(Section("Programme im Browser-Terminal"));
        layout.Controls.Add(Row(_claude, _powerShell));

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true };
        buttons.Controls.AddRange([_install, cancel]);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_status);
        Controls.Add(layout);

        AcceptButton = _install;
        CancelButton = cancel;
        RefreshRoots();
        UpdateModeFields();
        _modeUser.CheckedChanged += (_, _) => UpdateModeFields();
        _modeService.CheckedChanged += (_, _) => UpdateModeFields();
        add.Click += (_, _) => AddRoot();
        remove.Click += (_, _) =>
        {
            if (_roots.SelectedIndex >= 0)
            {
                _rootList.RemoveAt(_roots.SelectedIndex);
                RefreshRoots();
            }
        };
        cancel.Click += (_, _) => Close();
        _install.Click += async (_, _) => await InstallAsync();
    }

    private void UpdateModeFields()
    {
        _account.Enabled = _modeService.Checked;
        _password.Enabled = _modeService.Checked && !Setup.WindowsNative.IsLocalSystem(_account.Text);
    }

    private void AddRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "Ordner, den Matgate sehen darf", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var name = new DirectoryInfo(dialog.SelectedPath).Name.TrimEnd(':', (char)92);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Laufwerk " + dialog.SelectedPath[..1];
        }

        // Names must be unique: they are the top level in Matgate.
        var unique = name;
        for (var i = 2; _rootList.Any(root => root.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++)
        {
            unique = $"{name} {i}";
        }

        _rootList.Add(new AgentRoot { Name = unique, Path = dialog.SelectedPath });
        RefreshRoots();
    }

    private void RefreshRoots()
    {
        _roots.Items.Clear();
        foreach (var root in _rootList)
        {
            _roots.Items.Add($"{root.Name}  -  {root.Path}");
        }
    }

    private async Task InstallAsync()
    {
        var options = new SetupOptions
        {
            Port = (int)_port.Value,
            OpenFirewall = _firewall.Checked,
            Mode = _modeService.Checked ? "service" : "user",
            Account = _account.Text.Trim(),
            Password = _password.Text,
            Roots = [.. _rootList],
            Claude = _claude.Checked,
            PowerShell = _powerShell.Checked,
        };
        if (options.Mode == "service" && string.IsNullOrWhiteSpace(options.Account))
        {
            _status.Text = "Bitte ein Konto für den Dienst angeben (oder LocalSystem).";
            return;
        }

        _install.Enabled = false;
        UseWaitCursor = true;
        _status.Text = "Installiere ...";
        try
        {
            var result = await Task.Run(() => Installer.Install(options, line => BeginInvoke(() => _status.Text = line)));
            UseWaitCursor = false;
            Hide();
            using var done = new ResultForm(result);
            done.ShowDialog();
            Close();
        }
        catch (Exception exception)
        {
            UseWaitCursor = false;
            _install.Enabled = true;
            _status.ForeColor = Color.Firebrick;
            _status.Text = exception.Message;
        }
    }

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 6) };

    private static Label Section(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(520, 0), Margin = new Padding(20, 0, 0, 4) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 0) };
        row.Controls.AddRange(controls);
        return row;
    }
}

// What Matgate needs, ready to copy: host, port, token, fingerprint.
public sealed class ResultForm : Form
{
    public ResultForm(Installer.Result result)
    {
        Text = "Matgate Agent - installiert";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true };
        layout.Controls.Add(new Label
        {
            Text = result.Listening
                ? "Der Agent läuft. In Matgate eine Verbindung vom Typ \"Agent\" anlegen:"
                : "Installiert, aber der Agent antwortet noch nicht - siehe agent.log in " + Installer.DataFolder + ". In Matgate eine Verbindung vom Typ \"Agent\" anlegen:",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 0, 0, 10),
        }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 3);

        AddLine(layout, 1, "Host", Environment.MachineName);
        AddLine(layout, 2, "Port", result.Port.ToString());
        AddLine(layout, 3, "Verbindungspasswort", result.Token);
        AddLine(layout, 4, "Zertifikat (SHA-256)", result.Fingerprint);

        var hint = new Label
        {
            Text = "\"Zertifikat ignorieren\" anhaken (selbst signiert). Deinstallieren: Einstellungen > Apps > Installierte Apps > Matgate Agent.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 10, 0, 0),
        };
        layout.Controls.Add(hint, 0, 5);
        layout.SetColumnSpan(hint, 3);
        var close = new Button { Text = "Schließen", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        layout.Controls.Add(close, 0, 6);
        Controls.Add(layout);
        AcceptButton = close;
    }

    private static void AddLine(TableLayoutPanel layout, int row, string label, string value)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(new TextBox { Text = value, ReadOnly = true, Width = 480, Font = new Font("Consolas", 9.5f) }, 1, row);
        var copy = new Button { Text = "Kopieren", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(value);
        layout.Controls.Add(copy, 2, row);
    }

    public static void OpenFolder(string folder) => Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
}
