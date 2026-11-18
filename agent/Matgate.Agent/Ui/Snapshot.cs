using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using MatDesktop;
using Matgate.Agent.Setup;

namespace Matgate.Agent.Ui;

// matgate-agent snapshot <folder>: every window of the agent as a picture, drawn without a screen (Avalonia's
// headless platform with Skia), in light and dark - for looking at the layout and for pictures that may be
// published. Everything comes from sample data (the computer ANNA-PC, the user Anna, a sample password and
// fingerprint, a pinned start time), never from this machine (MatDesign, docs/DESKTOP.md section 6), and no
// window does anything: nothing is installed, started or removed.
internal static class Snapshot
{
    public const string SampleToken = "q3Vx9Lk2-example-token-Rt7mWp";
    public const string SampleFingerprint = "B6A1A74FF49B59E48728364E704707897F3C88F02CCAA3EAD9237DFE224BA135";

    private static SetupOptions SampleInstalled(string version) => new()
    {
        Port = 7488,
        Mode = "service",
        Account = "LocalSystem",
        Roots = [new AgentRoot { Name = "Home", Path = @"C:\Users\Anna" }, new AgentRoot { Name = "Projekte", Path = @"D:\Projekte" }],
        Claude = true,
        Screen = true,
        Tray = true,
        AutoUpdate = true,
        Version = version,
        RunAs = "anna",
    };

    public static int Run(string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        Machine.Sample = true;
        AgentApp.Started = null;
        AppBuilder.Configure<AgentApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        AvaloniaSynchronizationContext.InstallIfNeeded();
        MatWindow.DefaultIcon = null;

        var written = 0;
        try
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                written += Render(folder, theme);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Snapshot failed: " + exception);
            return 1;
        }

        Console.WriteLine($"{written} pictures in {folder}");
        return 0;
    }

    private static int Render(string folder, string theme)
    {
        var count = 0;
        void Shot(string name, Window window)
        {
            window.Show();
            Settle();
            var picture = window.CaptureRenderedFrame() ?? throw new InvalidOperationException(name + ": no picture was drawn");
            var file = Path.Combine(folder, $"{name}-{theme}.png");
            picture.Save(file);
            // How much of the content the window shows: a page that scrolls is a page that is too full (SETUP-01).
            var scroll = window.GetVisualDescendantsOf<ScrollViewer>().FirstOrDefault();
            var fits = scroll is null ? "" : scroll.Extent.Height > scroll.Viewport.Height + 0.5
                ? $" - content {scroll.Extent.Height:0} px in {scroll.Viewport.Height:0} px, scrolls"
                : $" - content {scroll.Extent.Height:0} px in {scroll.Viewport.Height:0} px";
            Console.WriteLine($"{Path.GetFileName(file)}: {picture.PixelSize.Width}x{picture.PixelSize.Height}{fits}");
            window.Close();
            Settle();
            count++;
        }

        static SetupResult NoInstall(SetupOptions options, Action<string> log) => new(SampleToken, SampleFingerprint, options.Port, true);
        static void NoUninstall(bool keep, Action<string> log)
        {
        }

        var version = AgentVersion.Number;

        // The setup: every question, the progress, the last page.
        var setup = AgentSetup.Create(uninstall: false, installed: null, NoInstall, NoUninstall);
        Shot("setup-welcome", setup.Window);
        setup = AgentSetup.Create(uninstall: false, installed: null, NoInstall, NoUninstall);
        setup.Window.GoTo(MatSetupStepKind.Connect);
        Shot("setup-connection", setup.Window);
        setup = AgentSetup.Create(uninstall: false, installed: null, NoInstall, NoUninstall);
        setup.AddRoot(@"D:\Projekte");
        setup.Window.GoTo(MatSetupStepKind.Options);
        Shot("setup-content", setup.Window);
        setup = AgentSetup.Create(uninstall: false, installed: null, NoInstall, NoUninstall);
        setup.Window.GoTo(MatSetupStepKind.Options);
        setup.Window.PrimaryButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Shot("setup-operation", setup.Window);
        setup = AgentSetup.Create(uninstall: false, installed: null, NoInstall, NoUninstall);
        setup.Window.Show();
        setup.Window.RunAsync().GetAwaiter();
        Settle();
        Shot("setup-finish", setup.Window);

        // Installed already: the same version asks again (the settings), an older one is an update that asks nothing.
        setup = AgentSetup.Create(uninstall: false, SampleInstalled("0.9.0.17"), NoInstall, NoUninstall);
        Shot("setup-update", setup.Window);
        setup = AgentSetup.Create(uninstall: true, SampleInstalled(version), NoInstall, NoUninstall);
        Shot("uninstall-confirm", setup.Window);

        // The tray's windows.
        var started = new DateTimeOffset(2026, 11, 12, 9, 12, 0, TimeSpan.FromHours(1));
        var running = new AgentStatus.Snapshot(version, 7488, "service", 1, started);
        var (state, detail) = AgentTray.StateOf(running, SampleInstalled(version), false, true, false);
        Shot("tray-info", AgentTray.InfoWindow(state, detail, running, SampleInstalled(version), () => { }, () => { }));
        (state, detail) = AgentTray.StateOf(null, SampleInstalled(version), false, false, false);
        Shot("tray-info-stopped", AgentTray.InfoWindow(state, detail, null, SampleInstalled(version), () => { }, () => { }));
        Shot("tray-connection", new ConnectionWindow(SampleToken, SampleFingerprint, 7488));
        return count;
    }

    // Lets the layout and the drawing happen, as a screen's ticks would.
    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> GetVisualDescendantsOf<T>(this Visual root) where T : Visual
    {
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root))
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in child.GetVisualDescendantsOf<T>())
            {
                yield return deeper;
            }
        }
    }
}
