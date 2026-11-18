// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;

namespace MatDesktop;

/// <summary>
/// The base of every window of a program (DESK-02): the page background and the text colour of the tokens, the font and the body size of MatUi (14 px with a mouse), and on Windows a
/// title bar of the system that follows light and dark. <see cref="UseOwnTitleBar"/> prepares the window for <see cref="MatTitleBar"/> (DESK-03).
/// </summary>
public class MatWindow : Window
{
    /// <summary>A window in the colours of the theme, with <see cref="DefaultIcon"/> as its icon.</summary>
    public MatWindow()
    {
        if (DefaultIcon is { } icon)
        {
            Icon = icon;
        }

        this.Use(BackgroundProperty, "Mat.Bg");
        this.Use(ForegroundProperty, "Mat.Text");
        this.Use(FontFamilyProperty, "Mat.Font");
        FontSize = MatTokens.Type.FsMd;
        UseLayoutRounding = true;
        Opened += (_, _) => ApplyTitleBarTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
    }

    /// <summary>
    /// The icon of every window of the program (the task bar, Alt+Tab): set it once at the start (<c>MatWindow.DefaultIcon = new WindowIcon(...)</c>, or <see cref="MatThemeOptions.Icon"/>)
    /// and each <see cref="MatWindow"/> made after that has it, the setup and the Info window included. A window may still set its own.
    /// </summary>
    public static WindowIcon? DefaultIcon { get; set; }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Window);

    /// <summary>
    /// Lets the content reach into the title bar so that <see cref="MatTitleBar"/> draws it (DESK-03): on Windows without the system's chrome (the bar draws the caption buttons), on macOS
    /// with the traffic lights of the system, which the bar leaves room for. <paramref name="height"/> is the height of the bar.
    /// </summary>
    public void UseOwnTitleBar(double height = MatTitleBar.DefaultHeight)
    {
        ExtendClientAreaToDecorationsHint = true;
        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = height;
        }
        else
        {
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 0;
        }
    }

    /// <summary>The usable area of the screen the window is on (or the primary screen) in device-independent pixels.</summary>
    public Rect WorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return new Rect(0, 0, 1280, 800);
        }

        var area = screen.WorkingArea;
        return new Rect(area.X / screen.Scaling, area.Y / screen.Scaling, area.Width / screen.Scaling, area.Height / screen.Scaling);
    }

    /// <summary>Shows the window and brings it to the front, also when it was minimised or hidden (a second start, DESK-05; Open in the tray, TRAY-01).</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ApplyTitleBarTheme()
    {
        // only a real window of Windows (a headless window of the tests has no HWND)
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: var handle } && handle != IntPtr.Zero)
        {
            SetDarkTitleBar(handle, ActualThemeVariant == ThemeVariant.Dark);
        }
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private static void SetDarkTitleBar(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.UseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static class NativeMethods
    {
        /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004 and later, Windows 11).</summary>
        public const int UseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
