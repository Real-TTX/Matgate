using Matgate.Agent.Ui;

namespace Matgate.Agent.Setup;

// setup-preview <folder>: draws every window of the agent into image files without showing them on the screen - for checking the
// layout and for the pictures of the READMEs and of MatDesign. Everything comes from sample data (the computer ANNA-PC, the user Anna,
// a sample password and fingerprint, a pinned start time), never from this machine (MatDesign, docs/DESKTOP.md section 6), in the light
// and in the dark mode: <name>-light.png and <name>-dark.png.
internal static class SetupPreview
{
    public const string SampleToken = "q3Vx9Lk2-example-token-Rt7mWp";
    public const string SampleFingerprint = "B6A1A74FF49B59E48728364E704707897F3C88F02CCAA3EAD9237DFE224BA135";

    // An installation as setup-preview shows it in the update, the uninstall and the tray.
    private static SetupOptions Installed() => new()
    {
        Port = 7488,
        Mode = "user",
        Version = "0.7.0.17",
        Roots = [new AgentRoot { Name = "Home", Path = Machine.Home }, new AgentRoot { Name = "Projekte", Path = @"D:\Projekte" }],
        Claude = true,
        Screen = true,
    };

    private static AgentStatus.Snapshot Status() =>
        new(AgentVersion.Number, 7488, "user", 1, new DateTimeOffset(2026, 11, 1, 8, 2, 0, TimeSpan.FromHours(1)));

    private static readonly string[] Progress =
    [
        @"Installing Matgate Agent to C:\Program Files\Matgate Agent, settings in C:\ProgramData\Matgate Agent.",
        @"Starts at logon as ANNA-PC\Anna.",
        "  netsh advfirewall: ok",
        "  schtasks /Create: ok",
        "  schtasks /Run: ok",
        "  Symbol im Infobereich: eingerichtet",
    ];

    public static void Save(string folder)
    {
        Machine.Sample = true;
        Directory.CreateDirectory(folder);
        foreach (var (dark, suffix) in new[] { (false, "light"), (true, "dark") })
        {
            Theme.Use(dark);
            DrawAll(folder, suffix);
        }

        Theme.Use(false);
        foreach (var state in Enum.GetValues<AgentState>())
        {
            using var bitmap = Mark.Picture(state, 64);
            bitmap.Save(Path.Combine(folder, $"tray-icon-{state.ToString().ToLowerInvariant()}.png"));
        }
    }

    private static void DrawAll(string folder, string suffix)
    {
        string File(string name) => Path.Combine(folder, $"{name}-{suffix}.png");
        var result = new Installer.Result(SampleToken, SampleFingerprint, 7488, true);
        foreach (var page in new[] { "Welcome", "Connection", "Content", "Finish" })
        {
            var wizard = new SetupWizard(uninstall: false, installed: null);
            Draw(wizard, File("setup-" + page.ToLowerInvariant()), () => wizard.Preview(page, result));
        }

        var progress = new SetupWizard(uninstall: false, installed: null);
        Draw(progress, File("setup-progress"), () => progress.Preview("Progress", progress: Progress));

        var update = new SetupWizard(uninstall: false, Installed());
        Draw(update, File("setup-update"), () => { });

        var uninstall = new SetupWizard(uninstall: true, Installed());
        Draw(uninstall, File("uninstall-confirm"), () => { });

        var (state, detail) = TrayApplication.Describe(Status(), Installed(), null, starting: false);
        var info = new InfoWindow(() => { }, () => { });
        Draw(info, File("tray-info"), () => info.Show(state, detail, TrayApplication.InfoRows(Status(), Installed())));

        var (waiting, waitingDetail) = TrayApplication.Describe(null, Installed(), null, starting: true);
        var infoWaiting = new InfoWindow(() => { }, () => { });
        Draw(infoWaiting, File("tray-info-waiting"), () => infoWaiting.Show(waiting, waitingDetail, TrayApplication.InfoRows(null, Installed())));

        Draw(ConnectionWindow.Preview(), File("tray-connection"), () => { });
        DrawMenu(File("tray-menu"), AgentState.Running);
        DrawMenu(File("tray-menu-stopped"), AgentState.Stopped);
    }

    // write-icon <file.ico>: the mark as a Windows icon with every size Explorer asks for.
    public static void WriteIcon(string path)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = sizes.Select(size =>
        {
            using var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);
                Mark.Draw(graphics, new RectangleF(0, 0, size, size));
            }

            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            return png.ToArray();
        }).ToArray();

        using var output = new BinaryWriter(System.IO.File.Create(path));
        output.Write((short)0);
        output.Write((short)1);
        output.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            output.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            output.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            output.Write((byte)0);
            output.Write((byte)0);
            output.Write((short)1);
            output.Write((short)32);
            output.Write(images[i].Length);
            output.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            output.Write(image);
        }
    }

    private static IEnumerable<Control> AllControls(Control root) => root.Controls.Cast<Control>().SelectMany(c => AllControls(c).Prepend(c));

    private static void Draw(Form form, string path, Action prepare)
    {
        using (form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Show();
            prepare();
            Application.DoEvents();
            // Whether the content fits the window or needs its scroll bar.
            foreach (var panel in AllControls(form).OfType<FlowLayoutPanel>().Where(p => p.AutoScroll))
            {
                Console.WriteLine($"{Path.GetFileName(path)}: content {panel.DisplayRectangle.Height} px in {panel.ClientSize.Height} px{(panel.DisplayRectangle.Height > panel.ClientSize.Height ? " - scrolls" : "")}");
            }

            // The window without the frame and the title bar of the system: DrawToBitmap paints those in the light look whatever the
            // mode, which a picture of the dark mode must not show. A line of the mode stands for the frame.
            using var whole = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(whole, new Rectangle(0, 0, form.Width, form.Height));
            var client = form.RectangleToScreen(form.ClientRectangle);
            using var bitmap = whole.Clone(new Rectangle(client.X - form.Left, client.Y - form.Top, client.Width, client.Height), whole.PixelFormat);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var frame = new Pen(Theme.LineStrong))
            {
                graphics.DrawRectangle(frame, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
            }

            bitmap.Save(path);
            form.Hide();
        }
    }

    // The tray menu as it opens, drawn into a picture (it is never shown on the screen here).
    private static void DrawMenu(string path, AgentState state)
    {
        using var menu = new ContextMenuStrip();
        TrayApplication.Fill(menu, state, Installed(), set: true, TrayActions.None);
        menu.CreateControl();
        menu.PerformLayout();
        var size = menu.GetPreferredSize(Size.Empty);
        menu.Size = size;
        using var bitmap = new Bitmap(size.Width, size.Height);
        menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        bitmap.Save(path);
    }
}
