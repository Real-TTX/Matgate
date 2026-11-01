using Matgate.Agent.Ui;

namespace Matgate.Agent.Setup;

// setup-preview <folder>: draws every window of the agent into image files without showing them on the
// screen - for checking the layout.
internal static class SetupPreview
{
    public static void Save(string folder)
    {
        // The folder picker and the clipboard need it; without, the "Hinzufügen" button did nothing.
        Console.WriteLine($"apartment: {Thread.CurrentThread.GetApartmentState()}");
        Directory.CreateDirectory(folder);
        var result = new Installer.Result("q3Vx9Lk2-example-token-Rt7mWp", "B6A1A74FF49B59E48728364E704707897F3C88F02CCAA3EAD9237DFE224BA135", 7488, true);
        foreach (var page in new[] { "Welcome", "Connection", "Content", "Finish" })
        {
            var wizard = new SetupWizard(uninstall: false);
            Draw(wizard, Path.Combine(folder, "setup-" + page.ToLowerInvariant() + ".png"), () => wizard.Preview(page, result));
        }

        var uninstall = new SetupWizard(uninstall: true);
        Draw(uninstall, Path.Combine(folder, "uninstall-confirm.png"), () => { });

        var info = new InfoWindow(() => { }, () => { });
        Draw(info, Path.Combine(folder, "tray-info.png"), () => info.Show(Brand.Ok, ("Mit Matgate verbunden", "Matgate (192.168.1.20) ist seit 09:41 Uhr verbunden, 1 Terminal(s) offen."),
            [new("Rechner", Environment.MachineName), new("Port", "7488"), new("Läuft", "als angemeldeter Benutzer"), new("Gestartet", "01.11.2026 08:02 Uhr"), new("Version", Brand.Version), new("Einstellungen", Installer.DataFolder)]));
        Draw(ConnectionWindow.Preview(), Path.Combine(folder, "tray-connection.png"), () => { });

        using var icon = Mark.Create(Brand.Ok, 64);
        using var bitmap = icon.ToBitmap();
        bitmap.Save(Path.Combine(folder, "tray-icon.png"));
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

        using var output = new BinaryWriter(File.Create(path));
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
            // This machine's screen may be smaller than the window; say whether the content fits the real size.
            foreach (var panel in AllControls(form).OfType<FlowLayoutPanel>().Where(p => p.AutoScroll))
            {
                Console.WriteLine($"{Path.GetFileName(path)}: content {panel.DisplayRectangle.Height} px, room at 560 px client height {560 - 84 - 58 - 34} px");
            }
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(path);
            form.Hide();
        }
    }
}
