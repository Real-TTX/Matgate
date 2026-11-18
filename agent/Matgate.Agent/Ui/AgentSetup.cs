using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MatDesktop;
using Matgate.Agent.Setup;

namespace Matgate.Agent.Ui;

// The setup of the agent in MatDesktop's setup window (SETUP-01, SETUP-02): one window for installing,
// changing the settings and removing, 600 x 560, the version in the footer (SETUP-07).
//
//   Install / change:  Welcome -> Verbindung und Ausfuehrung -> Was Matgate sehen darf -> Betrieb und Aussehen -> Progress -> Finish
//                      (the values Matgate needs, ready to copy)
//   Update:            what is installed and what comes, nothing asked -> Progress -> Finish
//   Uninstall:         what goes, "Auch meine Daten loeschen" (off) -> Progress -> Finish
//
// The window only asks and shows; the work is `install` / `uninstall`, which the program hands in - on
// Windows the installer itself (the window already has administrator rights), on Linux and macOS a second,
// elevated run of this program.
internal sealed class AgentSetup
{
    private readonly SetupOptions? _installed;
    private readonly bool _windows = OperatingSystem.IsWindows();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 7488, Width = 130, FormatString = "0", Increment = 1 };
    private readonly CheckBox _firewall = new() { Content = "Zugriff von anderen Rechnern erlauben (Windows-Firewall)", IsChecked = true };
    private readonly RadioButton _modeUser = new() { GroupName = "mode", IsChecked = true };
    private readonly RadioButton _modeSystem = new() { GroupName = "mode" };
    private readonly RadioButton _modeService = new() { GroupName = "mode" };
    private readonly TextBox _account = new() { Width = 190, Watermark = @"Konto: .\name" };
    private readonly TextBox _password = new() { Width = 150, PasswordChar = '•', Watermark = "Passwort" };
    private readonly TextBox _runAs = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _rootRows = new() { Spacing = 0 };
    private readonly List<AgentRoot> _roots = [];
    private readonly Image _logoPicture = new() { Width = 32, Height = 32, Stretch = Stretch.Uniform };
    private readonly TextBlock _logoName = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 };
    private readonly StackPanel _details = new();
    private string _logo = "";
    private bool _logoChanged;

    private AgentSetup(SetupOptions? installed)
    {
        _installed = installed;
    }

    public MatSetupWindow Window { get; private set; } = null!;

    // What the setup found or made: the values of the last page.
    public SetupResult? Result { get; private set; }

    // installed: what is on this machine (AgentInstall.FindExisting), or the sample of the pictures; null for a first setup.
    // install / uninstall: the work; they report their lines and throw with a sentence for the person when it fails.
    public static AgentSetup Create(
        bool uninstall,
        SetupOptions? installed,
        Func<SetupOptions, Action<string>, SetupResult> install,
        Action<bool, Action<string>> uninstallWork)
    {
        var setup = new AgentSetup(installed);
        var version = AgentVersion.Number;
        // An older version installed: an update, which asks nothing and keeps every setting (SETUP-06). The same
        // version: the questions again, with what is set - that is how the settings are changed later.
        var isUpdate = !uninstall && installed is not null
            && Version.TryParse(installed.Version, out var has) && Version.TryParse(version, out var brings) && has < brings;
        var mode = uninstall ? MatSetupMode.Uninstall : isUpdate ? MatSetupMode.Update : MatSetupMode.Setup;
        setup.Fill();

        IReadOnlyList<MatSetupStep> steps = mode switch
        {
            MatSetupMode.Uninstall =>
            [
                MatSetupStep.Uninstall(installed is null
                    ? "Der Matgate Agent ist nicht vollständig installiert; was davon da ist, wird entfernt."
                    : setup.RemovedText()),
            ],
            MatSetupMode.Update =>
            [
                MatSetupStep.Welcome(string.Empty, new Notice("Token und Zertifikat bleiben - die Verbindung in Matgate bleibt gültig. Offene Terminals werden beendet.")),
            ],
            _ =>
            [
                MatSetupStep.Welcome(
                    installed is null
                        ? "Der Matgate Agent macht diesen Rechner in Matgate erreichbar: seine freigegebenen Ordner im Dateimanager, Programme wie Claude Code in einem Terminal im Browser und den Bildschirm.\n\nMatgate verbindet sich zu diesem Rechner wie zu einem RDP-Server: mit Host, Port und einem Token, das am Ende angezeigt wird."
                        : "Der Matgate Agent ist auf diesem Rechner eingerichtet. Hier lässt sich ändern, was er anbietet und wie er läuft. Token und Zertifikat bleiben - die Verbindung in Matgate bleibt gültig.",
                    setup.WelcomeNote()),
                setup.ConnectionStep(),
                setup.ContentStep(),
                setup.OperationStep(),
            ],
        };

        var finish = uninstall
            ? MatSetupStep.Finish("Programm, Dienst und Autostart sind entfernt.", offersStart: false)
            : MatSetupStep.Finish("Diese Werte gehören in Matgate in eine Verbindung vom Typ „Agent“.", offersStart: false, content: setup._details);
        var window = new MatSetupWindow(new MatSetup(AgentInstall.ProductName, uninstall ? installed?.Version is { Length: > 0 } old ? AgentVersion.Normalize(old) : version : version,
            AgentVersion.ChannelOf(version), mode, steps, async run =>
            {
                void Report(string line) => run.Progress.Report(new MatSetupReport(line, MatSetupStepState.Done));
                if (uninstall)
                {
                    await Task.Run(() => uninstallWork(!run.DeleteData, Report));
                    setup.Window.Steps[^1].Text = run.DeleteData
                        ? "Programm, Einstellungen, Dienst und Autostart sind entfernt."
                        : $"Programm, Dienst und Autostart sind entfernt. Die Einstellungen liegen weiter in {Machine.DataFolder}.";
                    return;
                }

                var options = isUpdate ? setup.Unchanged() : setup.Collect(run.Options);
                var result = await Task.Run(() => install(options, Report));
                setup.Result = result;
                setup.ShowDetails(result);
            })
        {
            Logo = AgentMark.Picture(96),
            InstalledVersion = installed?.Version is { Length: > 0 } installedVersion ? AgentVersion.Normalize(installedVersion) : null,
            Finish = finish,
            DeleteDataText = "Auch meine Daten löschen",
            DeleteDataDescription = "Token, Zertifikat, freigegebene Ordner und Log. Ohne Haken bleiben sie: eine spätere Installation nimmt dasselbe Token, die Verbindung in Matgate gilt weiter.",
        });
        setup.Window = window;
        return setup;
    }

    // ---- what is asked ---------------------------------------------------------------------------------------

    private void Fill()
    {
        if (_installed is { } has)
        {
            _port.Value = has.Port;
            _firewall.IsChecked = has.OpenFirewall;
#if AGENT_WINDOWS
            var system = has.Mode == "service" && WindowsNative.IsLocalSystem(has.Account);
            _modeSystem.IsChecked = system;
            _modeService.IsChecked = has.Mode == "service" && !system;
            _modeUser.IsChecked = has.Mode != "service";
            _account.Text = system ? "" : has.Account;
            _password.Watermark = "Passwort erneut eingeben";
#endif
            _runAs.Text = has.RunAs ?? "";
            _roots.AddRange(has.Roots);
            _logo = has.CurrentLogo;
        }
        else
        {
            _roots.Add(new AgentRoot { Name = "Home", Path = Machine.Home });
            _runAs.Text = Machine.User == "root" ? "" : Machine.User;
        }
    }

    private Notice WelcomeNote() => new(_windows
        ? $"Das Programm kommt nach {Machine.ProgramFolder}, die Einstellungen nach {Machine.DataFolder}. Für die Installation braucht es einmal Administratorrechte."
        : $"Das Programm kommt nach {Machine.ProgramFolder}, die Einstellungen nach {Machine.DataFolder}. Der Agent läuft als Dienst des Systems; dafür fragt das Setup einmal nach dem Passwort eines Administrators.");

    private MatSetupStep ConnectionStep()
    {
        var fields = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };
        var port = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        port.Children.Add(_port);
        if (_windows)
        {
            _firewall.VerticalAlignment = VerticalAlignment.Center;
            port.Children.Add(_firewall);
        }

        fields.Children.Add(new MatFormField { Label = "Port", Hint = "Matgate verbindet sich zu diesem Port - wie zu 3389 bei RDP.", Content = port });

        if (_windows)
        {
            _modeUser.Content = Choice("Als angemeldeter Benutzer - startet beim Anmelden", $"Mit den Rechten von {Machine.User} - Claude Code nutzt deinen Login.");
            _modeSystem.Content = Choice("Als Systemdienst - auch ohne Anmeldung, wie eine Fernwartung", "Auch am Anmelde- und Sperrbildschirm, in Administratorfenstern und mit Strg+Alt+Entf. Programme laufen als der angemeldete Benutzer.");
            var account = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            account.Children.Add(_account);
            account.Children.Add(_password);
            _modeService.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Als Dienst unter einem Konto", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Auch ohne Anmeldung. Das Passwort geht an Windows, der Agent speichert es nicht.", TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted"),
                    account,
                },
            };
            void Update()
            {
                _account.IsEnabled = _modeService.IsChecked == true;
                _password.IsEnabled = _modeService.IsChecked == true;
            }

            _modeUser.IsCheckedChanged += (_, _) => Update();
            _modeService.IsCheckedChanged += (_, _) => Update();
            Update();
            var modes = new StackPanel { Spacing = 8 };
            modes.Children.AddRange([_modeUser, _modeSystem, _modeService]);
            fields.Children.Add(new MatFormField { Label = "Der Agent läuft", Content = modes });
        }
        else
        {
            fields.Children.Add(new Notice("Der Agent läuft als Dienst des Systems: er startet mit dem Rechner, auch ohne dass sich jemand anmeldet."));
            fields.Children.Add(new MatFormField
            {
                Label = "Programme im Terminal laufen als",
                Hint = "Der Benutzer, mit dessen Rechten, Ordnern und Login Claude Code und die Shell laufen. Leer: als root.",
                Content = _runAs,
            });
        }

        var step = MatSetupStep.Connect(string.Empty, fields, () => Task.FromResult(CheckConnection()));
        step.Title = "Verbindung und Ausführung";
        return step;
    }

    private string? CheckConnection()
    {
        if (_windows && _modeService.IsChecked == true && string.IsNullOrWhiteSpace(_account.Text))
        {
            return "Bitte das Konto für den Dienst angeben - oder „Als Systemdienst“ wählen.";
        }

        return null;
    }

    // What the machine offers: the screen, the programs, the folders.
    private MatSetupStep ContentStep()
    {
        var has = _installed;
        MatSetupOption[] options =
        [
            new("screen", "Bildschirm", _windows
                ? "Den Rechner aus Matgate bedienen, mit Ton und Zwischenablage"
                : "Den Rechner aus Matgate bedienen (wo der Agent den Bildschirm dieses Systems zeigen kann)", has?.Screen ?? true),
            new("claude", "Claude Code", "Im Terminal im Browser; muss auf diesem Rechner installiert sein", has?.Claude ?? true),
            _windows
                ? new("powershell", "PowerShell", "Ein Terminal auf diesem Rechner", has?.PowerShell ?? false)
                : new("shell", "Shell", "Ein Terminal auf diesem Rechner (die Login-Shell des Benutzers)", has?.Shell ?? false),
        ];

        var add = new Button { Content = "Ordner hinzufügen …", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += async (_, _) => await AddRootAsync();
        var folders = new StackPanel { Spacing = 4 };
        folders.Children.Add(_rootRows);
        folders.Children.Add(add);
        RefreshRoots();

        var step = MatSetupStep.WithOptions(null, options);
        step.Title = "Was Matgate sehen darf";
        // (The window sets the margin of a step's content itself: the distance to the card above is padding.)
        step.Content = new Border
        {
            Padding = new Thickness(0, 10, 0, 0),
            Child = new MatFormField
            {
                Label = "Freigegebene Ordner",
                Hint = "Nur diese Ordner sieht Matgate - auch „..“ und Verknüpfungen führen nicht hinaus.",
                Content = folders,
            },
        };
        return step;
    }

    // How it keeps itself up to date and shows itself: updates, the icon, the logo.
    private MatSetupStep OperationStep()
    {
        var has = _installed;
        MatSetupOption[] options =
        [
            new("updates", "Automatisch aktualisieren", "Über Matgate, wenn kein Terminal offen ist", has?.AutoUpdate ?? true),
            new("tray", "Symbol im Infobereich", "Status, Verbindungsdaten und Log, für jeden, der sich anmeldet", has?.Tray ?? true),
        ];

        var pick = new Button { Content = "Auswählen …" };
        pick.Click += async (_, _) => await PickLogoAsync();
        var drop = new Button { Content = "Entfernen" };
        drop.Click += (_, _) => SetLogo("");
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        logo.Children.Add(new Border { Width = 40, Height = 40, Padding = new Thickness(3), Child = _logoPicture }.Use(Border.BackgroundProperty, "Mat.Surface2").Use(Border.CornerRadiusProperty, "Mat.RadiusSm"));
        logo.Children.Add(_logoName);
        logo.Children.Add(pick);
        logo.Children.Add(drop);
        RefreshLogo();

        var step = MatSetupStep.WithOptions(null, options);
        step.Title = "Betrieb und Aussehen";
        step.Content = new Border
        {
            Padding = new Thickness(0, 14, 0, 0),
            Child = new MatFormField
            {
                Label = "Logo auf der Startseite dieses Rechners in Matgate",
                Hint = "PNG, JPG, WebP, GIF oder SVG, höchstens 1 MB.",
                Content = logo,
            },
        };
        return step;
    }

    private static StackPanel Choice(string title, string text) => new()
    {
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted"),
        },
    };

    private void RefreshRoots()
    {
        _rootRows.Children.Clear();
        foreach (var root in _roots.ToList())
        {
            var remove = new Button { Content = new MatIcon { Icon = "x", Size = 14 } }.Themed("MatButton.Icon");
            ToolTip.SetTip(remove, $"{root.Name} nicht mehr freigeben");
            Avalonia.Automation.AutomationProperties.SetName(remove, $"{root.Name} nicht mehr freigeben");
            remove.Click += (_, _) =>
            {
                _roots.Remove(root);
                RefreshRoots();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,Auto") };
            var name = new TextBlock { Text = root.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var path = new TextBlock { Text = root.Path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.Themed("MatText.Muted");
            Grid.SetColumn(path, 1);
            Grid.SetColumn(remove, 2);
            row.Children.AddRange([name, path, remove]);
            _rootRows.Children.Add(row);
        }

        if (_roots.Count == 0)
        {
            _rootRows.Children.Add(new TextBlock { Text = "Kein Ordner freigegeben - Matgate zeigt dann keine Dateien dieses Rechners." }.Themed("MatText.Muted"));
        }
    }

    private async Task AddRootAsync()
    {
        var picked = await Window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Ordner, den Matgate sehen darf", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { Length: > 0 } folder)
        {
            return;
        }

        AddRoot(folder);
    }

    internal void AddRoot(string folder)
    {
        var name = new DirectoryInfo(folder).Name.TrimEnd(':', '\\', '/');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = OperatingSystem.IsWindows() ? "Laufwerk " + folder[..1] : "Wurzel";
        }

        // Names are the top level in Matgate, so they must be unique.
        var unique = name;
        for (var i = 2; _roots.Any(root => root.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++)
        {
            unique = $"{name} {i}";
        }

        _roots.Add(new AgentRoot { Name = unique, Path = folder });
        RefreshRoots();
    }

    private async Task PickLogoAsync()
    {
        var picked = await Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Logo für Matgate",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Bilder") { Patterns = AgentLogo.Extensions.Select(extension => "*" + extension).ToList() }],
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { Length: > 0 } file)
        {
            return;
        }

        if (AgentLogo.Problem(file) is { } problem)
        {
            _logoName.Text = problem;
            _logoName.Use(TextBlock.ForegroundProperty, "Mat.DangerText");
            return;
        }

        SetLogo(file);
    }

    internal void SetLogo(string path)
    {
        _logo = path;
        _logoChanged = true;
        RefreshLogo();
    }

    private void RefreshLogo()
    {
        _logoPicture.Source = null;
        _logoName.Text = _logo.Length == 0 ? "Kein Logo" : Path.GetFileName(_logo);
        _logoName.Use(TextBlock.ForegroundProperty, _logo.Length == 0 ? "Mat.TextMuted" : "Mat.Text");
        if (_logo.Length == 0 || AgentLogo.TypeOf(_logo) is "image/svg+xml" or null)
        {
            // An SVG is shown by Matgate, not here; its name has to do.
            return;
        }

        try
        {
            // From a copy in memory, so the file stays free to be replaced or deleted.
            _logoPicture.Source = new Bitmap(new MemoryStream(File.ReadAllBytes(_logo)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Not readable here: the name is shown, and Matgate decides whether it is a picture.
        }
    }

    // ---- what was answered -----------------------------------------------------------------------------------

    private SetupOptions Collect(IReadOnlyDictionary<string, bool> switches)
    {
        bool On(string key, bool fallback) => switches.TryGetValue(key, out var value) ? value : fallback;
        var options = new SetupOptions
        {
            Port = (int)(_port.Value ?? 7488),
            OpenFirewall = _firewall.IsChecked == true,
            Mode = !_windows ? "service" : _modeUser.IsChecked == true ? "user" : "service",
            Account = _modeSystem.IsChecked == true ? "LocalSystem" : (_account.Text ?? "").Trim(),
            Password = _password.Text ?? "",
            RunAs = _windows ? null : (_runAs.Text ?? "").Trim(),
            Roots = [.. _roots],
            Claude = On("claude", true),
            PowerShell = On("powershell", false),
            Shell = On("shell", false),
            Screen = On("screen", true),
            AutoUpdate = On("updates", true),
            Tray = On("tray", true),
            // Only what was chosen here is an instruction; an untouched logo stays as it is.
            Logo = _logoChanged ? _logo : null,
        };
        return options;
    }

    // An update changes nothing that was set.
    private SetupOptions Unchanged()
    {
        var has = _installed!;
        return new SetupOptions
        {
            Port = has.Port, OpenFirewall = has.OpenFirewall, Mode = has.Mode, Account = has.Account, Password = "", RunAs = has.RunAs,
            Roots = has.Roots, Claude = has.Claude, PowerShell = has.PowerShell, Shell = has.Shell, Screen = has.Screen,
            AutoUpdate = has.AutoUpdate, Tray = has.Tray, Logo = null,
        };
    }

    private string RemovedText() => _windows
        ? $"Entfernt werden {(_installed?.Mode == "service" ? "der Dienst" : "die Anmeldeaufgabe")}, die Firewall-Regel, das Symbol im Infobereich, der Eintrag unter Apps und das Programm in {Machine.ProgramFolder}. Die Verbindung in Matgate bleibt bestehen, bis sie dort gelöscht wird."
        : $"Entfernt werden der Dienst, das Symbol im Infobereich und das Programm in {Machine.ProgramFolder}. Die Verbindung in Matgate bleibt bestehen, bis sie dort gelöscht wird.";

    // The last page: whether the agent answers, and what Matgate needs - ready to copy (AGENT-02).
    internal void ShowDetails(SetupResult result)
    {
        _details.Children.Clear();
        _details.Children.Add(result.Listening
            ? new Notice($"Der Agent läuft und wartet auf Port {result.Port} auf Matgate.", "Success")
            : new Notice($"Installiert, aber der Agent antwortet noch nicht auf Port {result.Port}. Das Log liegt in {Machine.DataFolder}.", "Warning"));
        foreach (var field in ConnectionDetails.Fields(result.Token, result.Fingerprint, result.Port))
        {
            _details.Children.Add(field);
        }
    }
}

// The values Matgate needs, ready to copy - on the setup's last page and in the tray's window - and the sentence that the password is
// the only key to this machine (AGENT-02, AGENT-03).
internal static class ConnectionDetails
{
    public static IEnumerable<Control> Fields(string token, string fingerprint, int port)
    {
        yield return new MatCopyField("Host", Machine.Name);
        yield return new MatCopyField("Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // "Token" is what the field is called in Matgate's connection form.
        yield return new MatCopyField("Token", token);
        yield return new MatCopyField("Zertifikat (SHA-256)", fingerprint);
        yield return new Notice("Das Token ist der einzige Schlüssel zu diesem Rechner – nur in Matgate eintragen.", "Warning") { Margin = new Thickness(0, 8, 0, 0) };
    }
}
