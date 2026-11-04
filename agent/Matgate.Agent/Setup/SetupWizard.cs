using Matgate.Agent.Ui;

namespace Matgate.Agent.Setup;

// The setup wizard, after MatMon's (Matmon.Setup/SetupForm.cs). Install: Welcome -> Connection -> Content ->
// Progress -> Finish. Uninstall: Confirm -> Progress -> Finish. The work runs on a background task and
// reports into the progress page; the window only steers.
internal sealed class SetupWizard : Form
{
    private readonly bool _uninstall;
    private readonly SetupOptions? _installed;
    private readonly BrandHeader _header;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 12) };
    private readonly Button _back = Brand.FooterButton("< Zurück");
    private readonly Button _next = Brand.FooterButton("Weiter >");
    private readonly Button _cancel = Brand.FooterButton("Abbrechen", gap: 16);

    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 7488, Width = 100 };
    private readonly CheckBox _firewall = new() { Text = "Zugriff von anderen Rechnern erlauben (Windows-Firewall)", AutoSize = true, Checked = true };
    private readonly RadioButton _modeUser = new() { Text = "Als angemeldeter Benutzer - startet beim Anmelden", AutoSize = true, Checked = true };
    private readonly RadioButton _modeService = new() { Text = "Als Windows-Dienst unter einem Konto", AutoSize = true };
    private readonly TextBox _account = new() { Width = 260, PlaceholderText = @"Konto: .\name oder LocalSystem" };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly CheckBox _autoUpdate = new() { Text = "Automatisch aktualisieren - über Matgate, wenn kein Terminal offen ist", AutoSize = true, Checked = true };
    private readonly CheckBox _tray = new() { Text = "Symbol im Infobereich anzeigen (Status, Verbindungsdaten, Log)", AutoSize = true, Checked = true };
    private readonly ListView _roots = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = 420, Height = 96, BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox _claude = new() { Text = "Claude Code", AutoSize = true, Checked = true };
    private readonly CheckBox _screen = new() { Text = "Bildschirm - den Rechner aus Matgate bedienen, mit Ton und Zwischenablage", AutoSize = true, Checked = true };
    private readonly CheckBox _powerShell = new() { Text = "PowerShell", AutoSize = true };
    private readonly CheckBox _deleteSettings = new() { AutoSize = true, MaximumSize = new Size(Brand.ContentWidth, 0) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Height = 18, Width = Brand.ContentWidth };
    private readonly ListBox _log = new() { IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, Width = Brand.ContentWidth, Height = 200 };
    private readonly List<AgentRoot> _rootList = [];
    private readonly Stack<Page> _history = new();

    private Page _page;
    private bool _working;
    private string? _failure;
    private Installer.Result? _result;

    private enum Page
    {
        Welcome,
        Connection,
        Content,
        Confirm,
        Progress,
        Finish,
    }

    public SetupWizard(bool uninstall)
    {
        _uninstall = uninstall;
        _installed = Installer.FindExisting();

        Text = uninstall ? "Matgate Agent deinstallieren" : $"Matgate Agent {Brand.Version} Setup";
        Icon = Mark.Create(null);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 560);
        Font = Brand.BodyFont();
        BackColor = Color.White;

        if (_installed is not null)
        {
            _port.Value = _installed.Port;
            _firewall.Checked = _installed.OpenFirewall;
            _modeService.Checked = _installed.Mode == "service";
            _modeUser.Checked = !_modeService.Checked;
            _account.Text = _installed.Account;
            _tray.Checked = _installed.Tray;
            _autoUpdate.Checked = _installed.AutoUpdate;
            _rootList.AddRange(_installed.Roots);
            _claude.Checked = _installed.Claude;
            _screen.Checked = _installed.Screen;
            _powerShell.Checked = _installed.PowerShell;
            _password.PlaceholderText = "für den Dienst erneut eingeben";
        }
        else
        {
            _rootList.Add(new AgentRoot { Name = "Home", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
        }

        _deleteSettings.Text = "Auch die Einstellungen löschen (Token, Zertifikat, freigegebene Ordner)";
        _roots.Columns.Add("Name in Matgate", 130);
        _roots.Columns.Add("Ordner auf diesem Rechner", 270);

        var footer = Brand.Footer($"Version {(uninstall ? _installed?.Version ?? Brand.Version : Brand.Version)}", _cancel, _next, _back);
        _back.Click += (_, _) => GoBack();
        _next.Click += (_, _) => Advance();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;
        _modeUser.CheckedChanged += (_, _) => UpdateModeFields();
        _account.TextChanged += (_, _) => UpdateModeFields();

        _header = new BrandHeader(uninstall ? "Deinstallation" : _installed is null ? "Setup" : "Update");
        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(_header);
        Show(uninstall ? Page.Confirm : Page.Welcome);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_working)
        {
            e.Cancel = true; // half an installation is worse than a slow one
            return;
        }

        base.OnFormClosing(e);
    }

    private void Advance()
    {
        switch (_page)
        {
            case Page.Welcome:
                Forward(Page.Connection);
                break;
            case Page.Connection:
                if (_modeService.Checked && string.IsNullOrWhiteSpace(_account.Text))
                {
                    MessageBox.Show(this, "Bitte das Konto für den Dienst angeben - oder LocalSystem.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Forward(Page.Content);
                break;
            case Page.Content:
            case Page.Confirm:
                Run();
                break;
            case Page.Finish:
                Close();
                break;
        }
    }

    private void Forward(Page page)
    {
        _history.Push(_page);
        Show(page);
    }

    private void GoBack()
    {
        if (_history.Count > 0)
        {
            Show(_history.Pop());
        }
    }

    private void Show(Page page)
    {
        _page = page;
        _body.SuspendLayout();
        foreach (Control control in _body.Controls)
        {
            control.Controls.Clear();
        }

        _body.Controls.Clear();
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };

        switch (page)
        {
            case Page.Welcome:
                content.Controls.Add(Brand.Heading(_installed is null ? "Willkommen" : "Matgate Agent aktualisieren"));
                content.Controls.Add(Brand.Paragraph(_installed is null
                    ? "Der Matgate Agent macht diesen Rechner in Matgate erreichbar: seine freigegebenen Ordner im Dateimanager und Programme wie Claude Code in einem Terminal im Browser."
                    : $"Installiert ist Version {_installed.Version}. Das Update behält Token und Zertifikat - die Verbindung in Matgate bleibt gültig."));
                content.Controls.Add(Brand.Paragraph("Matgate verbindet sich zu diesem Rechner wie zu einem RDP-Server: mit Host, Port und einem Token, das am Ende angezeigt wird."));
                content.Controls.Add(new BrandNotice("Für die Installation braucht es Administratorrechte. Programme im Terminal laufen später mit den Rechten des Kontos, unter dem der Agent läuft.", NoticeTone.Info));
                break;

            case Page.Connection:
                content.Controls.Add(Brand.Heading("Verbindung und Ausführung"));
                var port = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
                port.Controls.Add(new Label { Text = "Port", AutoSize = true, ForeColor = Brand.HeadingColor, Margin = new Padding(0, 6, 10, 0) });
                port.Controls.Add(_port);
                _firewall.Margin = new Padding(18, 4, 0, 0);
                port.Controls.Add(_firewall);
                content.Controls.Add(port);
                content.Controls.Add(Brand.Hint("Matgate verbindet sich zu diesem Port - wie zu 3389 bei RDP."));
                content.Controls.Add(Brand.FieldLabel("Der Agent läuft"));
                content.Controls.Add(_modeUser);
                content.Controls.Add(Brand.Hint($"Mit den Rechten von {WindowsNative.InteractiveUser()} - Claude Code nutzt deinen Login. Kein Passwort nötig.", 20));
                content.Controls.Add(_modeService);
                var account = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(20, 2, 0, 0) };
                _account.Width = 230;
                _password.Width = 180;
                _password.PlaceholderText = _installed is null ? "Passwort" : "Passwort erneut eingeben";
                account.Controls.AddRange([_account, _password]);
                content.Controls.Add(account);
                content.Controls.Add(Brand.Hint("Läuft auch ohne Anmeldung. Das Passwort geht an Windows, der Agent speichert es nicht.", 20));
                content.Controls.Add(_tray);
                content.Controls.Add(_autoUpdate);
                UpdateModeFields();
                break;

            case Page.Content:
                content.Controls.Add(Brand.Heading("Was Matgate sehen darf"));
                content.Controls.Add(Brand.FieldLabel("Freigegebene Ordner"));
                var roots = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(8, 0, 0, 0) };
                var add = new Button { Text = "Hinzufügen ...", Size = new Size(112, 30) };
                var remove = new Button { Text = "Entfernen", Size = new Size(112, 30) };
                add.Click += (_, _) => AddRoot();
                remove.Click += (_, _) => RemoveRoot();
                buttons.Controls.AddRange([add, remove]);
                roots.Controls.AddRange([_roots, buttons]);
                content.Controls.Add(roots);
                content.Controls.Add(Brand.Hint("Nur diese Ordner sind in Matgate sichtbar - auch \"..\" und Verknüpfungen führen nicht hinaus."));
                content.Controls.Add(Brand.FieldLabel("Programme im Browser-Terminal"));
                var programs = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
                programs.Controls.AddRange([_claude, _powerShell]);
                content.Controls.Add(programs);
                content.Controls.Add(Brand.Hint("Starten im Ordner, der in Matgate offen ist. Claude Code muss hier installiert sein."));
                content.Controls.Add(_screen);
                content.Controls.Add(Brand.Hint("Zeigt, was der Monitor zeigt - nur als angemeldeter Benutzer, nicht als Dienst.", 20));
                RefreshRoots();
                break;

            case Page.Confirm:
                content.Controls.Add(Brand.Heading("Matgate Agent entfernen"));
                content.Controls.Add(Brand.Paragraph(_installed is null
                    ? "Der Matgate Agent ist nicht vollständig installiert; was davon da ist, wird entfernt."
                    : $"Entfernt werden {(_installed.Mode == "service" ? "der Dienst" : "die Anmeldeaufgabe")}, die Firewall-Regel, das Symbol im Infobereich, der Eintrag unter Apps und das Programm in {Installer.ProgramFolder}."));
                content.Controls.Add(_deleteSettings);
                content.Controls.Add(Brand.Hint($"Ohne Häkchen bleiben sie in {Installer.DataFolder}: eine spätere Installation nimmt dasselbe Token, die Verbindung in Matgate gilt weiter.", 20));
                content.Controls.Add(new BrandNotice("Die Verbindung in Matgate bleibt bestehen, bis sie dort gelöscht wird.", NoticeTone.Info));
                break;

            case Page.Progress:
                content.Controls.Add(Brand.Heading(_uninstall ? "Matgate Agent wird entfernt" : "Matgate Agent wird installiert"));
                content.Controls.Add(_progress);
                content.Controls.Add(Spacer(8));
                content.Controls.Add(_log);
                break;

            case Page.Finish:
                if (_failure is null)
                {
                    content.Controls.Add(Brand.Heading(_uninstall ? "Matgate Agent wurde entfernt" : "Matgate Agent ist bereit"));
                    if (_uninstall)
                    {
                        content.Controls.Add(Brand.Paragraph(_deleteSettings.Checked
                            ? "Programm, Einstellungen, Firewall-Regel und Autostart sind entfernt."
                            : $"Programm, Firewall-Regel und Autostart sind entfernt. Die Einstellungen liegen weiter in {Installer.DataFolder}."));
                    }
                    else if (_result is { } result)
                    {
                        content.Controls.Add(result.Listening
                            ? new BrandNotice($"Der Agent läuft und wartet auf Port {result.Port} auf Matgate.", NoticeTone.Success)
                            : new BrandNotice($"Installiert, aber der Agent antwortet noch nicht auf Port {result.Port}. Das Log liegt in {Installer.DataFolder}.", NoticeTone.Warning));
                        content.Controls.Add(Brand.Paragraph("In Matgate eine Verbindung vom Typ \"Agent\" anlegen und \"Zertifikat ignorieren\" anhaken:"));
                        foreach (var field in ConnectionDetails.Fields(result.Token, result.Fingerprint, result.Port))
                        {
                            content.Controls.Add(field);
                        }
                    }
                }
                else
                {
                    content.Controls.Add(Brand.Heading(_uninstall ? "Deinstallation nicht abgeschlossen" : "Setup nicht abgeschlossen"));
                    content.Controls.Add(new BrandNotice(_failure, NoticeTone.Error));
                    content.Controls.Add(_log);
                }

                break;
        }

        _body.Controls.Add(content);
        _body.ResumeLayout();

        _back.Visible = page is Page.Connection or Page.Content;
        _cancel.Visible = page != Page.Finish;
        _cancel.Enabled = page != Page.Progress;
        _next.Enabled = page != Page.Progress;
        _next.Text = page switch
        {
            Page.Content => _installed is null ? "Installieren" : "Aktualisieren",
            Page.Confirm => "Entfernen",
            Page.Finish => "Fertig",
            _ => "Weiter >",
        };
    }

    private async void Run()
    {
        var options = new SetupOptions
        {
            Port = (int)_port.Value,
            OpenFirewall = _firewall.Checked,
            Mode = _modeService.Checked ? "service" : "user",
            Account = _account.Text.Trim(),
            Password = _password.Text,
            Tray = _tray.Checked,
            AutoUpdate = _autoUpdate.Checked,
            Roots = [.. _rootList],
            Claude = _claude.Checked,
            Screen = _screen.Checked,
            PowerShell = _powerShell.Checked,
        };
        var keepConfig = !_deleteSettings.Checked;

        _history.Clear();
        Show(Page.Progress);
        _working = true;
        void Log(string line) => BeginInvoke(() =>
        {
            _log.Items.Add(line);
            _log.TopIndex = _log.Items.Count - 1;
        });

        try
        {
            if (_uninstall)
            {
                await Task.Run(() => Installer.Uninstall(keepConfig, Log));
            }
            else
            {
                _result = await Task.Run(() => Installer.Install(options, Log));
            }
        }
        catch (Exception exception)
        {
            _failure = exception.Message;
        }
        finally
        {
            _working = false;
        }

        Show(Page.Finish);
    }

    private void UpdateModeFields()
    {
        _account.Enabled = _modeService.Checked;
        _password.Enabled = _modeService.Checked && !WindowsNative.IsLocalSystem(_account.Text);
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

        // Names are the top level in Matgate, so they must be unique.
        var unique = name;
        for (var i = 2; _rootList.Any(root => root.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++)
        {
            unique = $"{name} {i}";
        }

        _rootList.Add(new AgentRoot { Name = unique, Path = dialog.SelectedPath });
        RefreshRoots();
    }

    private void RemoveRoot()
    {
        if (_roots.SelectedIndices.Count > 0)
        {
            _rootList.RemoveAt(_roots.SelectedIndices[0]);
            RefreshRoots();
        }
    }

    private void RefreshRoots()
    {
        _roots.Items.Clear();
        foreach (var root in _rootList)
        {
            _roots.Items.Add(new ListViewItem([root.Name, root.Path]));
        }
    }

    // For the layout preview (setup-preview): a page as it would show.
    internal void Preview(string page, Installer.Result? result = null)
    {
        _result = result;
        Show(Enum.Parse<Page>(page));
    }

    private static Control Spacer(int height) => new Panel { Height = height, Width = 1, Margin = Padding.Empty };
}

// The values Matgate needs, ready to copy - in the wizard's last page and in the tray's window.
internal static class ConnectionDetails
{
    public static IEnumerable<Control> Fields(string token, string fingerprint, int port)
    {
        yield return new CopyField("Host", Environment.MachineName);
        yield return new CopyField("Port", port.ToString());
        yield return new CopyField("Verbindungspasswort", token, monospace: true);
        // 64 hex digits: in two rows of 32, so it fits without scrolling sideways.
        yield return new CopyField("Zertifikat (SHA-256)", fingerprint, monospace: true, wrap: true);
    }
}
