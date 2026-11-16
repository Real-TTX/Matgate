using Matgate.Agent.Ui;

namespace Matgate.Agent.Setup;

// The one setup window of the agent (SETUP-01): Setup, Update and Uninstall ("Einrichtung", "Update", "Deinstallation") in the same
// window of a fixed size, with the header (mark, name, mode) and the footer (Version x · channel on the left; the primary action,
// Back and Cancel on the right). Install: Welcome -> Connection -> Content -> Progress -> Finish. Uninstall: Confirm -> Progress ->
// Finish. The work runs on a background task and reports into the progress page; the window only steers.
internal sealed class SetupWizard : Form
{
    private readonly bool _uninstall;
    private readonly SetupOptions? _installed;
    private readonly BrandHeader _header;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 8) };
    private readonly Button _back = Brand.FooterButton("Zurück", width: 96);
    private readonly Button _next = Brand.FooterButton("Weiter", kind: ButtonKind.Primary);
    private readonly Button _cancel = Brand.FooterButton("Abbrechen", width: 104);

    private readonly NumericUpDown _port = Brand.Field(new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 7488, Width = 100 });
    private readonly CheckBox _firewall = new() { Text = "Zugriff von anderen Rechnern erlauben (Windows-Firewall)", AutoSize = true, Checked = true };
    private readonly RadioButton _modeUser = new() { Text = "Als angemeldeter Benutzer - startet beim Anmelden", AutoSize = true, Checked = true };
    private readonly RadioButton _modeSystem = new() { Text = "Als Systemdienst - auch ohne Anmeldung, wie eine Fernwartung", AutoSize = true };
    private readonly RadioButton _modeService = new() { Text = "Als Dienst unter einem Konto", AutoSize = true };
    private readonly TextBox _account = Brand.Field(new TextBox { Width = 260, PlaceholderText = @"Konto: .\name" });
    private readonly TextBox _password = Brand.Field(new TextBox { Width = 260, UseSystemPasswordChar = true });
    private readonly CheckBox _autoUpdate = new() { Text = "Automatisch aktualisieren - über Matgate, wenn kein Terminal offen ist", AutoSize = true, Checked = true };
    private readonly CheckBox _tray = new() { Text = "Symbol im Infobereich anzeigen (Status, Verbindungsdaten, Log)", AutoSize = true, Checked = true };
    private readonly ListView _roots = Brand.Field(new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = 400, Height = 84, BorderStyle = BorderStyle.FixedSingle });
    private readonly CheckBox _claude = new() { Text = "Claude Code", AutoSize = true, Checked = true };
    private readonly CheckBox _screen = new() { Text = "Bildschirm - den Rechner aus Matgate bedienen, mit Ton und Zwischenablage", AutoSize = true, Checked = true };
    private readonly CheckBox _powerShell = new() { Text = "PowerShell", AutoSize = true };
    private readonly CheckBox _deleteSettings = new() { AutoSize = true, MaximumSize = new Size(Brand.ContentWidth, 0) };
    private readonly MarqueeBar _progress = new() { Height = 8, Width = Brand.ContentWidth - 8, Margin = new Padding(3, 4, 3, 4) };
    private readonly ListBox _log = Brand.Field(new ListBox { IntegralHeight = false, HorizontalScrollbar = true, BorderStyle = BorderStyle.FixedSingle, Width = Brand.ContentWidth - 8, Height = 200 });
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

    // installed: what is installed on this machine (Installer.FindExisting), or the sample of setup-preview; null for a first setup.
    public SetupWizard(bool uninstall, SetupOptions? installed)
    {
        _uninstall = uninstall;
        _installed = installed;
        var mode = uninstall ? "Deinstallation" : _installed is null ? "Einrichtung" : "Update";

        Text = $"{Installer.ProductName} – {mode}";
        Icon = Mark.Create(null);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 560);
        Font = Brand.BodyFont();
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;

        if (_installed is not null)
        {
            _port.Value = _installed.Port;
            _firewall.Checked = _installed.OpenFirewall;
            var system = _installed.Mode == "service" && WindowsNative.IsLocalSystem(_installed.Account);
            _modeSystem.Checked = system;
            _modeService.Checked = _installed.Mode == "service" && !system;
            _modeUser.Checked = _installed.Mode != "service";
            _account.Text = system ? "" : _installed.Account;
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
            _rootList.Add(new AgentRoot { Name = "Home", Path = Machine.Home });
        }

        _deleteSettings.Text = "Auch meine Daten löschen (Token, Zertifikat, freigegebene Ordner, Log)";
        _roots.Columns.Add("Name in Matgate", 130);
        _roots.Columns.Add("Ordner auf diesem Rechner", 250);

        // The primary action first, then Back, then Cancel (SETUP-01).
        var footer = Brand.Footer(AgentVersion.Text, _next, _back, _cancel);
        _back.Click += (_, _) => GoBack();
        _next.Click += (_, _) => Advance();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;
        _modeUser.CheckedChanged += (_, _) => UpdateModeFields();
        _modeService.CheckedChanged += (_, _) => UpdateModeFields();
        _account.TextChanged += (_, _) => UpdateModeFields();

        _header = new BrandHeader(mode);
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
                    ? "Der Matgate Agent macht diesen Rechner in Matgate erreichbar: seine freigegebenen Ordner im Dateimanager, Programme wie Claude Code in einem Terminal im Browser und den Bildschirm."
                    : $"Installiert ist Version {_installed.Version}, dieses Setup bringt {AgentVersion.Number}. Token und Zertifikat bleiben - die Verbindung in Matgate bleibt gültig."));
                content.Controls.Add(Brand.Paragraph("Matgate verbindet sich zu diesem Rechner wie zu einem RDP-Server: mit Host, Port und einem Verbindungspasswort, das am Ende angezeigt wird."));
                content.Controls.Add(Brand.Paragraph($"Das Programm kommt nach {Machine.ProgramFolder}, die Einstellungen nach {Machine.DataFolder}."));
                content.Controls.Add(new BrandNotice("Für die Installation braucht es einmal Administratorrechte. Programme im Terminal laufen später mit den Rechten des Kontos, unter dem der Agent läuft.", NoticeTone.Info));
                break;

            case Page.Connection:
                content.Controls.Add(Brand.Heading("Verbindung und Ausführung"));
                var port = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
                port.Controls.Add(new Label { Text = "Port", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 6, 10, 0) });
                port.Controls.Add(_port);
                _firewall.Margin = new Padding(18, 4, 0, 0);
                port.Controls.Add(_firewall);
                content.Controls.Add(port);
                content.Controls.Add(Brand.Hint("Matgate verbindet sich zu diesem Port - wie zu 3389 bei RDP."));
                content.Controls.Add(Brand.FieldLabel("Der Agent läuft"));
                _modeUser.Margin = _modeSystem.Margin = new Padding(3, 2, 3, 0);
                content.Controls.Add(_modeUser);
                content.Controls.Add(Brand.Hint($"Mit den Rechten von {Machine.User} - Claude Code nutzt deinen Login.", 20));
                content.Controls.Add(_modeSystem);
                content.Controls.Add(Brand.Hint("Auch am Anmelde- und Sperrbildschirm, in Administratorfenstern und mit Strg+Alt+Entf. Programme laufen als der angemeldete Benutzer.", 20));
                // The account and its password on the line of their choice.
                var account = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
                _modeService.Margin = new Padding(3, 6, 10, 0);
                _account.Width = 170;
                _account.Margin = new Padding(0, 2, 6, 0);
                _password.Width = 140;
                _password.Margin = new Padding(0, 2, 0, 0);
                _password.PlaceholderText = _installed is null ? "Passwort" : "Passwort erneut eingeben";
                account.Controls.AddRange([_modeService, _account, _password]);
                content.Controls.Add(account);
                content.Controls.Add(Brand.Hint("Auch ohne Anmeldung. Das Passwort geht an Windows, der Agent speichert es nicht.", 20));
                content.Controls.Add(_tray);
                content.Controls.Add(_autoUpdate);
                UpdateModeFields();
                break;

            case Page.Content:
                content.Controls.Add(Brand.Heading("Was Matgate sehen darf"));
                content.Controls.Add(Brand.FieldLabel("Freigegebene Ordner"));
                var roots = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(8, 0, 0, 0) };
                var add = new Button { Text = "Hinzufügen …", Size = new Size(112, 30), Margin = new Padding(0, 0, 0, 6) };
                var remove = new Button { Text = "Entfernen", Size = new Size(112, 30), Margin = Padding.Empty };
                Brand.Style(add, ButtonKind.Secondary);
                Brand.Style(remove, ButtonKind.Secondary);
                add.Click += (_, _) => AddRoot();
                remove.Click += (_, _) => RemoveRoot();
                buttons.Controls.AddRange([add, remove]);
                roots.Controls.AddRange([_roots, buttons]);
                content.Controls.Add(roots);
                content.Controls.Add(Brand.Hint("Nur diese Ordner sieht Matgate - auch „..“ und Verknüpfungen führen nicht hinaus."));
                content.Controls.Add(Brand.FieldLabel("Programme im Browser-Terminal"));
                var programs = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
                programs.Controls.AddRange([_claude, _powerShell]);
                content.Controls.Add(programs);
                content.Controls.Add(Brand.Hint("Starten im Ordner, der in Matgate offen ist. Claude Code muss hier installiert sein."));
                content.Controls.Add(_screen);
                content.Controls.Add(Brand.Hint("Als angemeldeter Benutzer oder als Systemdienst, nicht unter einem anderen Konto.", 20));
                RefreshRoots();
                break;

            case Page.Confirm:
                content.Controls.Add(Brand.Heading("Matgate Agent entfernen"));
                content.Controls.Add(Brand.Paragraph(_installed is null
                    ? "Der Matgate Agent ist nicht vollständig installiert; was davon da ist, wird entfernt."
                    : $"Entfernt werden {(_installed.Mode == "service" ? "der Dienst" : "die Anmeldeaufgabe")}, die Firewall-Regel, das Symbol im Infobereich, der Eintrag unter Apps und das Programm in {Machine.ProgramFolder}. Eine geänderte Strg+Alt+Entf-Richtlinie wird zurückgesetzt."));
                content.Controls.Add(_deleteSettings);
                content.Controls.Add(Brand.Hint($"Ohne Häkchen bleiben sie in {Machine.DataFolder}: eine spätere Installation nimmt dasselbe Token, die Verbindung in Matgate gilt weiter.", 20));
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
                            : $"Programm, Firewall-Regel und Autostart sind entfernt. Die Einstellungen liegen weiter in {Machine.DataFolder}."));
                    }
                    else if (_result is { } result)
                    {
                        content.Controls.Add(result.Listening
                            ? new BrandNotice($"Der Agent läuft und wartet auf Port {result.Port} auf Matgate.", NoticeTone.Success)
                            : new BrandNotice($"Installiert, aber der Agent antwortet noch nicht auf Port {result.Port}. Das Log liegt in {Machine.DataFolder}.", NoticeTone.Warning));
                        content.Controls.Add(Brand.Paragraph("In Matgate eine Verbindung vom Typ „Agent“ anlegen und „Zertifikat ignorieren“ anhaken:"));
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

        // Progress can be neither cancelled nor closed (SETUP-02); Finish has only its one button.
        _back.Visible = page is Page.Connection or Page.Content;
        _cancel.Visible = page is not (Page.Finish or Page.Progress);
        _next.Visible = page != Page.Progress;
        _next.Text = page switch
        {
            Page.Content => _installed is null ? "Installieren" : "Aktualisieren",
            Page.Confirm => "Entfernen",
            Page.Finish => "Fertig",
            _ => "Weiter",
        };
        Brand.Style(_next, page == Page.Confirm ? ButtonKind.Danger : ButtonKind.Primary);
    }

    private async void Run()
    {
        var options = new SetupOptions
        {
            Port = (int)_port.Value,
            OpenFirewall = _firewall.Checked,
            Mode = _modeUser.Checked ? "user" : "service",
            Account = _modeSystem.Checked ? "LocalSystem" : _account.Text.Trim(),
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

    // For the pictures (setup-preview): a page as it would show, with sample results and sample lines of progress.
    internal void Preview(string page, Installer.Result? result = null, IEnumerable<string>? progress = null)
    {
        _result = result;
        foreach (var line in progress ?? [])
        {
            _log.Items.Add(line);
        }

        Show(Enum.Parse<Page>(page));
    }

    private static Control Spacer(int height) => new Panel { Height = height, Width = 1, Margin = Padding.Empty };
}

// The values Matgate needs, ready to copy - in the wizard's last page and in the tray's window - and the sentence that the password is
// the only key to this machine (AGENT-02, AGENT-03).
internal static class ConnectionDetails
{
    public static IEnumerable<Control> Fields(string token, string fingerprint, int port)
    {
        yield return new CopyField("Host", Machine.Name);
        yield return new CopyField("Port", port.ToString());
        yield return new CopyField("Verbindungspasswort", token, monospace: true);
        // 64 hex digits: in two rows of 32, so it fits without scrolling sideways.
        yield return new CopyField("Zertifikat (SHA-256)", fingerprint, monospace: true, wrap: true);
        yield return new BrandNotice("Das Verbindungspasswort ist der einzige Schlüssel zu diesem Rechner – nur in Matgate eintragen.", NoticeTone.Warning);
    }
}
