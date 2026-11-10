using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Matgate.Agent.Screen;

// The monitor's resolution, as RDP adapts it to the window it is shown in. A real monitor only has the modes
// its driver offers, so "automatic" takes the largest one that fits the browser's picture area. The change is
// temporary (not written to the registry): the viewer leaving puts the monitor back, and so does a restart.
public interface IDisplayControl
{
    // Every size the monitor can be set to, largest first.
    IReadOnlyList<(int Width, int Height)> Modes(MonitorInfo monitor);

    // The size Windows has saved for it - what it returns to.
    (int Width, int Height) Saved(MonitorInfo monitor);

    bool Apply(MonitorInfo monitor, int width, int height);

    void Restore(MonitorInfo monitor);
}

public static class Resolution
{
    // "1920x1080" -> (1920, 1080)
    public static (int Width, int Height)? Parse(string? value)
    {
        var parts = (value ?? "").Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height) && width >= 320 && height >= 200
            ? (width, height)
            : null;
    }

    public static string Text((int Width, int Height) size) => $"{size.Width}x{size.Height}";

    // The largest mode that fits into width x height; of equally large ones the one closest in shape. When
    // none fits (a tiny window), the smallest there is.
    public static (int Width, int Height)? Pick(IReadOnlyList<(int Width, int Height)> modes, int width, int height)
    {
        if (modes.Count == 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        var shape = (double)width / height;
        var fitting = modes.Where(mode => mode.Width <= width && mode.Height <= height).ToList();
        return fitting.Count > 0
            ? fitting.OrderByDescending(mode => (long)mode.Width * mode.Height).ThenBy(mode => Math.Abs((double)mode.Width / mode.Height - shape)).First()
            : modes.OrderBy(mode => (long)mode.Width * mode.Height).First();
    }
}

public sealed class WindowsDisplay : IDisplayControl
{
    private const int EnumCurrentSettings = -1;
    private const int EnumRegistrySettings = -2;
    private const int FullScreen = 0x00000004;
    private const int FieldsPelsWidth = 0x00080000;
    private const int FieldsPelsHeight = 0x00100000;
    private const int FieldsBitsPerPel = 0x00040000;
    private const int FieldsDisplayFrequency = 0x00400000;

    private static string Device(MonitorInfo monitor) => @"\\.\" + monitor.Name;

    public IReadOnlyList<(int Width, int Height)> Modes(MonitorInfo monitor)
    {
        var modes = new HashSet<(int, int)>();
        var mode = NewMode();
        for (var i = 0; EnumDisplaySettingsEx(Device(monitor), i, ref mode, 0); i++)
        {
            if (mode.BitsPerPel >= 24 && mode.PelsWidth >= 800 && mode.PelsHeight >= 600)
            {
                modes.Add((mode.PelsWidth, mode.PelsHeight));
            }
        }

        modes.Add((monitor.Width, monitor.Height));
        return modes.OrderByDescending(size => size.Item1).ThenByDescending(size => size.Item2).ToList();
    }

    public (int Width, int Height) Saved(MonitorInfo monitor)
    {
        var mode = NewMode();
        return EnumDisplaySettingsEx(Device(monitor), EnumRegistrySettings, ref mode, 0) && mode.PelsWidth > 0
            ? (mode.PelsWidth, mode.PelsHeight)
            : (monitor.Width, monitor.Height);
    }

    public bool Apply(MonitorInfo monitor, int width, int height)
    {
        var current = NewMode();
        if (!EnumDisplaySettingsEx(Device(monitor), EnumCurrentSettings, ref current, 0))
        {
            return false;
        }

        // The same refresh rate where the mode has it, otherwise whatever the driver picks for it.
        var target = NewMode();
        var found = false;
        var candidate = NewMode();
        for (var i = 0; EnumDisplaySettingsEx(Device(monitor), i, ref candidate, 0); i++)
        {
            if (candidate.PelsWidth == width && candidate.PelsHeight == height && candidate.BitsPerPel == current.BitsPerPel
                && (!found || candidate.DisplayFrequency == current.DisplayFrequency))
            {
                target = candidate;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        target.Fields = FieldsPelsWidth | FieldsPelsHeight | FieldsBitsPerPel | FieldsDisplayFrequency;
        return ChangeDisplaySettingsEx(Device(monitor), ref target, IntPtr.Zero, FullScreen, IntPtr.Zero) == 0;
    }

    public void Restore(MonitorInfo monitor) => ChangeDisplaySettingsEx(Device(monitor), IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    private static DevMode NewMode() => new() { Size = (short)Marshal.SizeOf<DevMode>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DevMode devMode, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DevMode devMode, IntPtr window, int flags, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, IntPtr devMode, IntPtr window, int flags, IntPtr parameter);
}

// For the tests: monitors whose size only exists in here (TestPattern draws in it), every change reported.
public sealed class TestDisplay(Action<string> report) : IDisplayControl
{
    private static readonly ConcurrentDictionary<int, (int Width, int Height)> Sizes = new();
    private static readonly (int, int)[] Available = [(1920, 1080), (1600, 900), (1280, 1024), (1280, 720), (1024, 768), (800, 600)];

    public static (int Width, int Height) Default(int index) => index == 1 ? (800, 600) : (1280, 720);

    public static (int Width, int Height) Size(int index) => Sizes.TryGetValue(index, out var size) ? size : Default(index);

    public IReadOnlyList<(int Width, int Height)> Modes(MonitorInfo monitor) => Available;

    public (int Width, int Height) Saved(MonitorInfo monitor) => Default(monitor.Index);

    public bool Apply(MonitorInfo monitor, int width, int height)
    {
        Sizes[monitor.Index] = (width, height);
        report($"display {monitor.Name} {width}x{height}");
        return true;
    }

    public void Restore(MonitorInfo monitor)
    {
        Sizes.TryRemove(monitor.Index, out _);
        report($"display {monitor.Name} restored");
    }
}
