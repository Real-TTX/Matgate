// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace MatDesktop;

/// <summary>What the install (or the update, or the uninstall) gets from the window: where to report its progress and what the user chose.</summary>
/// <param name="Progress">Lines of the progress list and how far the whole is.</param>
/// <param name="Options">The switches of Options by their key.</param>
/// <param name="Role">The key of the chosen role, or null.</param>
/// <param name="DeleteData">The uninstall also deletes the data (SETUP-04: off unless the user turned it on).</param>
public sealed record MatSetupRun(IProgress<MatSetupReport> Progress, IReadOnlyDictionary<string, bool> Options, string? Role, bool DeleteData);

/// <summary>What the setup window does: the program, the mode, the steps the program fills (SETUP-02) and the work itself.</summary>
/// <param name="AppName">The name of the program.</param>
/// <param name="Version">The version this setup brings (or, uninstalling, the one installed).</param>
/// <param name="Channel">The channel (release, nightly, local).</param>
/// <param name="Mode">Setup, Update or Uninstall.</param>
/// <param name="Steps">
/// The steps before the work: Setup takes Welcome, Role, Connect and Options in this order, each only if given; Update asks nothing and shows Welcome with the installed and the new version
/// (the content of a Welcome given here, the note where it installs, is kept); Uninstall needs the Uninstall step.
/// </param>
/// <param name="Run">The work; it reports its steps and throws when it fails (the message is shown on Finish).</param>
public sealed record MatSetup(string AppName, string Version, string Channel, MatSetupMode Mode, IReadOnlyList<MatSetupStep> Steps, Func<MatSetupRun, Task> Run)
{
    /// <summary>The logo of the header, 44 px.</summary>
    public IImage? Logo { get; init; }

    /// <summary>The version that is installed now (the Welcome of an update names it).</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>The Finish step; without it Finish says that the program is ready and offers to start it.</summary>
    public MatSetupStep? Finish { get; init; }

    /// <summary>The word mark of the program for the header instead of its name as text (a control, as in the title bar); the name stays its accessible name.</summary>
    public Control? WordMark { get; init; }

    /// <summary>
    /// The words of the switch of an uninstall when the program says what its data is (<c>Auch meinen Verlauf löschen</c>, docs/DESKTOP.md); null takes <c>Also delete my data</c>. The
    /// switch stays off (SETUP-04).
    /// </summary>
    public string? DeleteDataText { get; init; }

    /// <summary>The sentence under the switch (what goes, what stays without the tick); null takes the sentence of MatDesktop.</summary>
    public string? DeleteDataDescription { get; init; }
}

/// <summary>
/// The one setup window (SETUP-01) for Setup, Update and Uninstall: 600 × 560 and not resizable; the header shows the logo, the name and the mode in words; one step at a time, its title
/// and what it asks, like a wizard of the web (FORM-26); the footer shows <c>Version x · channel</c> at the left and the buttons at the right: the primary action first, then Back, then
/// Cancel. Progress shows a bar and the steps with their state and can be neither cancelled nor closed; Finish offers to start the program now. An uninstall says what goes, offers
/// "Also delete my data" (off) and has a danger button.
/// </summary>
public sealed class MatSetupWindow : MatWindow
{
    /// <summary>The width of the window (fixed).</summary>
    public const double SetupWidth = 600;

    /// <summary>The height of the window (fixed).</summary>
    public const double SetupHeight = 560;

    private readonly MatSetup _setup;
    private readonly List<MatSetupStep> _steps = [];
    private readonly List<Control> _pages = [];
    private readonly Panel _body = new();
    private readonly Dictionary<string, ToggleSwitch> _switches = new(StringComparer.Ordinal);
    private readonly List<RadioButton> _roles = [];
    private readonly List<(string Step, MatSetupStepState State, Control Row)> _lines = [];
    private readonly StackPanel _progressList = new() { Spacing = 6 };
    private readonly TextBlock _connectError = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), IsVisible = false };
    private readonly ToggleSwitch _deleteData = new() { IsChecked = false, OnContent = string.Empty, OffContent = string.Empty };
    private readonly ToggleSwitch _startNow = new() { IsChecked = true, OnContent = string.Empty, OffContent = string.Empty };
    private readonly TextBlock _finishTitle = new();
    private readonly TextBlock _finishText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly Control _startRow;
    private int _index;

    /// <summary>A setup window for <paramref name="setup"/>, at its first step.</summary>
    public MatSetupWindow(MatSetup setup)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        Width = SetupWidth;
        Height = SetupHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var modeWord = MatText.Get(ModeKey("setup.mode"));
        Title = $"{setup.AppName} – {modeWord}";

        _connectError.Use(TextBlock.ForegroundProperty, "Mat.DangerText");
        _finishTitle.Themed("MatText.SectionTitle");
        _finishTitle.Margin = new Thickness(0, 0, 0, 10);
        _startRow = new Border
        {
            Padding = new Thickness(16, 0),
            Child = new MatSettingRow { Title = MatText.Format("setup.startNow", setup.AppName), Content = _startNow, BorderThickness = new Thickness(0) },
        }.Themed("MatCard");
        AutomationProperties.SetName(_startNow, MatText.Format("setup.startNow", setup.AppName));

        _steps.AddRange(Questions(setup));
        _steps.Add(MatSetupStep.Progress(MatText.Get(ModeKey("setup.progress"))));
        _steps.Add(setup.Finish ?? MatSetupStep.Finish(string.Empty));
        foreach (var step in _steps)
        {
            var page = Page(step);
            page.IsVisible = false;
            _pages.Add(page);
            _body.Children.Add(page);
        }

        ModeText = new TextBlock();
        var header = MatDialogChrome.Header(setup.Logo, setup.AppName, modeWord, ModeText, setup.WordMark);
        PrimaryButton = new Button { IsDefault = true };
        BackButton = new Button { Content = MatText.Get("setup.back") };
        CancelButton = new Button { Content = MatText.Get("setup.cancel"), IsCancel = true };
        PrimaryButton.Click += OnPrimaryClick;
        BackButton.Click += (_, _) => Show(_index - 1);
        CancelButton.Click += (_, _) => Close();
        VersionText = MatDialogChrome.VersionText(setup.Version, setup.Channel);
        Footer = MatDialogChrome.Footer(VersionText, PrimaryButton, BackButton, CancelButton);

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(Footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(Footer);
        dock.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(28, 22, 28, 20), Child = _body } });
        Content = dock;
        Show(0);
    }

    /// <summary>Raised when the window is closed after Finish (with what the user chose) or by Cancel before the work (Succeeded false, no error).</summary>
    public event EventHandler? Finished;

    /// <summary>The mode in the header.</summary>
    public TextBlock ModeText { get; }

    /// <summary>The version at the left of the footer.</summary>
    public TextBlock VersionText { get; }

    /// <summary>The footer (version and buttons).</summary>
    public Border Footer { get; }

    /// <summary>The primary action: Next, Install, Update, Uninstall (a danger button), Done.</summary>
    public Button PrimaryButton { get; }

    /// <summary>Back.</summary>
    public Button BackButton { get; }

    /// <summary>Cancel.</summary>
    public Button CancelButton { get; }

    /// <summary>The steps of this window, in order (the questions, Progress, Finish).</summary>
    public IReadOnlyList<MatSetupStep> Steps => _steps;

    /// <summary>The step shown now.</summary>
    public MatSetupStep CurrentStep => _steps[_index];

    /// <summary>The progress bar.</summary>
    public ProgressBar ProgressBar { get; } = new() { IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 18) };

    /// <summary>The lines of the progress list with their state.</summary>
    public IReadOnlyList<(string Step, MatSetupStepState State)> ProgressLines => _lines.Select(l => (l.Step, l.State)).ToList();

    /// <summary>True while the work runs: the window cannot be closed and nothing can be pressed.</summary>
    public bool IsWorking { get; private set; }

    /// <summary>True when the work ran and did not fail.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>The message of the failure, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether "Start &lt;App&gt; now" is on (Finish of a setup or an update that worked).</summary>
    public bool StartNow => Succeeded && _setup.Mode != MatSetupMode.Uninstall && CurrentStep.OffersStart && _startNow.IsChecked == true;

    /// <summary>Whether "Also delete my data" is on.</summary>
    public bool DeleteData => _deleteData.IsChecked == true;

    /// <summary>The switches of Options by their key.</summary>
    public IReadOnlyDictionary<string, bool> OptionValues => _switches.ToDictionary(s => s.Key, s => s.Value.IsChecked == true, StringComparer.Ordinal);

    /// <summary>The key of the chosen role, or null.</summary>
    public string? SelectedRole => _steps.FirstOrDefault(s => s.Kind == MatSetupStepKind.Role) is { } role
        ? role.Roles[Math.Max(0, _roles.FindIndex(r => r.IsChecked == true))].Key
        : null;

    /// <summary>Goes to the step of a kind (for the pictures of the setup: no work is done). Progress and Finish are reached by the work.</summary>
    public void GoTo(MatSetupStepKind kind)
    {
        var index = _steps.FindIndex(s => s.Kind == kind);
        if (index < 0 || kind is MatSetupStepKind.Progress or MatSetupStepKind.Finish)
        {
            throw new InvalidOperationException($"{kind} is not a question of this setup.");
        }

        Show(index);
    }

    /// <summary>Runs the work as the primary button of the last question does: Progress, then Finish.</summary>
    public async Task RunAsync()
    {
        IsWorking = true;
        Show(_steps.FindIndex(s => s.Kind == MatSetupStepKind.Progress));
        var progress = new UiProgress(this);
        try
        {
            await _setup.Run(new MatSetupRun(progress, OptionValues, SelectedRole, DeleteData));
            Succeeded = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = exception.Message;
        }
        finally
        {
            IsWorking = false;
        }

        ShowFinish();
    }

    /// <summary>Shows a line of the progress list (a line with the same text changes its state) and how far the whole is.</summary>
    public void Report(MatSetupReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var index = _lines.FindIndex(l => l.Step == report.Step);
        var row = Line(report.Step, report.State);
        if (index < 0)
        {
            _lines.Add((report.Step, report.State, row));
            _progressList.Children.Add(row);
        }
        else
        {
            _progressList.Children[index] = row;
            _lines[index] = (report.Step, report.State, row);
        }

        if (report.Percent is { } percent)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Clamp(percent, 0, 100);
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // half an installation is worse than a slow one (SETUP-02)
        if (IsWorking)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private string ModeKey(string prefix) => prefix + "." + _setup.Mode switch
    {
        MatSetupMode.Update => "update",
        MatSetupMode.Uninstall => "uninstall",
        _ => "setup",
    };

    private static IEnumerable<MatSetupStep> Questions(MatSetup setup)
    {
        switch (setup.Mode)
        {
            case MatSetupMode.Update:
                // an update asks nothing: which version is installed and which comes, the settings are kept
                var note = setup.Steps.FirstOrDefault(s => s.Kind == MatSetupStepKind.Welcome)?.Content;
                var welcome = MatSetupStep.Welcome(MatText.Format("setup.updateText", setup.InstalledVersion ?? "?", setup.Version), note);
                welcome.Title = MatText.Get("setup.mode.update");
                return [welcome];
            case MatSetupMode.Uninstall:
                var uninstall = setup.Steps.FirstOrDefault(s => s.Kind == MatSetupStepKind.Uninstall)
                    ?? throw new ArgumentException("An uninstall needs the Uninstall step: what will be removed (SETUP-04).", nameof(setup));
                uninstall.Title = MatText.Format("setup.uninstallTitle", setup.AppName);
                return [uninstall];
            default:
                var order = new[] { MatSetupStepKind.Welcome, MatSetupStepKind.Role, MatSetupStepKind.Connect, MatSetupStepKind.Options };
                if (setup.Steps.Any(s => !order.Contains(s.Kind)))
                {
                    throw new ArgumentException("A setup asks Welcome, Role, Connect and Options; Progress and Finish are the window's.", nameof(setup));
                }

                return setup.Steps.OrderBy(s => Array.IndexOf(order, s.Kind)).ToList();
        }
    }

    private Control Page(MatSetupStep step)
    {
        var page = new StackPanel();
        if (step.Kind != MatSetupStepKind.Finish)
        {
            page.Children.Add(new TextBlock { Text = step.Title, Margin = new Thickness(0, 0, 0, 10) }.Themed("MatText.SectionTitle"));
        }

        if (!string.IsNullOrEmpty(step.Text) && step.Kind != MatSetupStepKind.Finish)
        {
            page.Children.Add(new TextBlock { Text = step.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), LineHeight = MatTokens.Type.FsMd * MatTokens.Type.Lh });
        }

        switch (step.Kind)
        {
            case MatSetupStepKind.Role:
                foreach (var role in step.Roles)
                {
                    var radio = new RadioButton
                    {
                        GroupName = "role",
                        IsChecked = _roles.Count == 0,
                        Margin = new Thickness(0, 0, 0, 8),
                        Content = new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = role.Title, FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = role.Description }.Themed("MatText.Muted"),
                            },
                        },
                    };
                    AutomationProperties.SetName(radio, role.Title);
                    _roles.Add(radio);
                    page.Children.Add(radio);
                }

                break;
            case MatSetupStepKind.Options:
                var rows = new StackPanel();
                for (var i = 0; i < step.Options.Count; i++)
                {
                    var option = step.Options[i];
                    var toggle = new ToggleSwitch { IsChecked = option.IsOn, OnContent = string.Empty, OffContent = string.Empty };
                    AutomationProperties.SetName(toggle, option.Title);
                    _switches[option.Key] = toggle;
                    rows.Children.Add(new MatSettingRow
                    {
                        Title = option.Title,
                        Description = option.Description,
                        Content = toggle,
                        BorderThickness = new Thickness(0, 0, 0, i == step.Options.Count - 1 ? 0 : 1),
                    });
                }

                page.Children.Add(new Border { Padding = new Thickness(16, 0), Child = rows }.Themed("MatCard"));
                break;
            case MatSetupStepKind.Uninstall:
                var deleteText = string.IsNullOrWhiteSpace(_setup.DeleteDataText) ? MatText.Get("setup.deleteData") : _setup.DeleteDataText.Trim();
                AutomationProperties.SetName(_deleteData, deleteText);
                page.Children.Add(new Border
                {
                    Padding = new Thickness(16, 0),
                    Child = new MatSettingRow
                    {
                        Title = deleteText,
                        Description = string.IsNullOrWhiteSpace(_setup.DeleteDataDescription) ? MatText.Get("setup.deleteData.text") : _setup.DeleteDataDescription.Trim(),
                        Content = _deleteData,
                        BorderThickness = new Thickness(0),
                    },
                }.Themed("MatCard"));
                break;
            case MatSetupStepKind.Progress:
                ProgressBar.Themed("MatProgress");
                page.Children.Add(ProgressBar);
                page.Children.Add(_progressList);
                break;
            case MatSetupStepKind.Finish:
                page.Children.Add(_finishTitle);
                page.Children.Add(_finishText);
                page.Children.Add(_startRow);
                break;
        }

        if (step.Content is { } content && step.Kind != MatSetupStepKind.Progress)
        {
            if (content.Parent is Panel parent)
            {
                parent.Children.Remove(content);
            }

            content.Margin = new Thickness(0, step.Kind == MatSetupStepKind.Finish ? 16 : 0, 0, 0);
            page.Children.Add(content);
        }

        if (step.Kind == MatSetupStepKind.Connect)
        {
            page.Children.Add(_connectError);
        }

        return page;
    }

    private static Control Line(string text, MatSetupStepState state)
    {
        var (icon, brush, word) = state switch
        {
            MatSetupStepState.Done => ("check-circle", "Mat.SuccessText", "setup.step.done"),
            MatSetupStepState.Failed => ("x-circle", "Mat.DangerText", "setup.step.failed"),
            MatSetupStepState.Running => ("refresh", "Mat.AccentText", "setup.step.running"),
            _ => ("circle", "Mat.TextFaint", "setup.step.waiting"),
        };
        var row = new DockPanel();
        var mark = new MatIcon { Icon = icon, Size = 16, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top }.Use(MatIcon.ForegroundProperty, brush);
        var label = new TextBlock { Text = MatText.Get(word), Margin = new Thickness(12, 0, 0, 0), FontSize = MatTokens.Type.FsSm }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(label, Dock.Right);
        row.Children.Add(mark);
        row.Children.Add(label);
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(row, $"{text}: {MatText.Get(word)}");
        return row;
    }

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Count - 1);
        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].IsVisible = i == _index;
        }

        var step = CurrentStep;
        var progress = step.Kind == MatSetupStepKind.Progress;
        var finish = step.Kind == MatSetupStepKind.Finish;
        var nextIsWork = _index + 1 < _steps.Count && _steps[_index + 1].Kind == MatSetupStepKind.Progress;
        PrimaryButton.Content = finish ? MatText.Get("setup.done")
            : nextIsWork || progress ? MatText.Get(_setup.Mode switch { MatSetupMode.Update => "setup.update", MatSetupMode.Uninstall => "setup.uninstall", _ => "setup.install" })
            : MatText.Get("setup.next");
        PrimaryButton.Themed(_setup.Mode == MatSetupMode.Uninstall && !finish ? "MatButton.Danger" : "MatButton.Primary");
        PrimaryButton.IsEnabled = !progress;
        BackButton.IsVisible = _index > 0 && !progress && !finish;
        CancelButton.IsVisible = !finish;
        CancelButton.IsEnabled = !progress;
        BackButton.IsEnabled = !progress;
    }

    private void ShowFinish()
    {
        var finish = _steps.Count - 1;
        var step = _steps[finish];
        _finishTitle.Text = Succeeded
            ? MatText.Format(ModeKey("setup.finish"), _setup.AppName)
            : MatText.Get(ModeKey("setup.failed"));
        _finishText.Text = Succeeded ? step.Text : Error;
        _finishText.IsVisible = !string.IsNullOrEmpty(_finishText.Text);
        if (Succeeded)
        {
            _finishText.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            _finishText.Use(TextBlock.ForegroundProperty, "Mat.DangerText");
        }

        _startRow.IsVisible = Succeeded && step.OffersStart && _setup.Mode != MatSetupMode.Uninstall;
        if (step.Content is { } content)
        {
            content.IsVisible = Succeeded;
        }

        Show(finish);
    }

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        var step = CurrentStep;
        if (step.Kind == MatSetupStepKind.Finish)
        {
            Close();
            return;
        }

        if (step.Kind == MatSetupStepKind.Connect && step.Check is { } check)
        {
            PrimaryButton.IsEnabled = BackButton.IsEnabled = false;
            var error = await check();
            PrimaryButton.IsEnabled = BackButton.IsEnabled = true;
            _connectError.Text = error;
            _connectError.IsVisible = !string.IsNullOrEmpty(error);
            if (_connectError.IsVisible)
            {
                return;
            }
        }

        if (_steps[_index + 1].Kind == MatSetupStepKind.Progress)
        {
            await RunAsync();
        }
        else
        {
            Show(_index + 1);
        }
    }

    /// <summary>Reports of the work: at once on the UI thread (so that a line reported last is in the list before Finish), posted to it from any other thread.</summary>
    private sealed class UiProgress(MatSetupWindow window) : IProgress<MatSetupReport>
    {
        public void Report(MatSetupReport value)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                window.Report(value);
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Report(value));
            }
        }
    }
}
