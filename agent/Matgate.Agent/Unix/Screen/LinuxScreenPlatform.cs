using System.Diagnostics;
using System.Runtime.InteropServices;
using Matgate.Agent.Screen;
using Matgate.Display.Video;

namespace Matgate.Agent.Unix.Screen;

// An X server on this machine: its display (":0"), the file with the key that lets a program in, and the
// process it is.
internal sealed record XServer(string Display, string? Authority, int Pid, int Terminal);

// Which screen the machine shows right now. The system's service has no display of its own - but as root it
// may use any: the sign-in screen's (the display manager's X server) as well as the one of whoever is signed
// in. It is found the way the machine itself keeps it: the X server's process, its command line (display,
// key file, virtual terminal) and which virtual terminal the monitor shows.
internal static class XServers
{
    private static readonly string[] Programs = ["Xorg", "X", "Xorg.bin", "Xvfb", "Xvnc", "Xtigervnc", "Xephyr"];
    private static readonly string[] WaylandPrograms = ["gnome-shell", "kwin_wayland", "sway", "weston", "Hyprland", "labwc", "wayfire", "cosmic-comp", "Xwayland"];

    // Null when there is none; `why` is then "wayland" or "none".
    public static XServer? Find(out string why)
    {
        why = "none";
        // Said from outside: a display the agent cannot find itself.
        if (Environment.GetEnvironmentVariable("MATGATE_AGENT_DISPLAY") is { Length: > 0 } given)
        {
            return new XServer(given, Environment.GetEnvironmentVariable("MATGATE_AGENT_XAUTHORITY"), 0, 0);
        }

        if (!UnixInstaller.IsRoot)
        {
            // Started by somebody on their desktop: their own display.
            return Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } own
                ? new XServer(own, Environment.GetEnvironmentVariable("XAUTHORITY"), 0, 0)
                : null;
        }

        var servers = new List<XServer>();
        var wayland = false;
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), out var pid))
            {
                continue;
            }

            try
            {
                var program = File.ReadAllText(Path.Combine(folder, "comm")).Trim();
                wayland |= WaylandPrograms.Contains(program);
                if (Programs.Contains(program) && Read(pid) is { } server)
                {
                    servers.Add(server);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Gone meanwhile.
            }
        }

        if (servers.Count == 0)
        {
            why = wayland ? "wayland" : "none";
            return null;
        }

        var active = ActiveTerminal();
        return servers.FirstOrDefault(server => server.Terminal != 0 && server.Terminal == active)
            ?? servers.FirstOrDefault(server => server.Terminal == 0)
            ?? servers[0];
    }

    // The virtual terminal the monitor shows (0 when the machine has none - a container, a server without a console).
    public static int ActiveTerminal()
    {
        try
        {
            var name = File.ReadAllText("/sys/class/tty/tty0/active").Trim();
            return name.StartsWith("tty", StringComparison.Ordinal) && int.TryParse(name[3..], out var number) ? number : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static int Owner(int pid)
    {
        try
        {
            var line = File.ReadLines($"/proc/{pid}/status").FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
            return line is not null && int.TryParse(line.Split('\t', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var uid) ? uid : -1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static XServer? Read(int pid)
    {
        var arguments = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        string? display = null;
        string? authority = null;
        var terminal = 0;
        for (var i = 1; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument.Length > 1 && argument[0] == ':' && char.IsAsciiDigit(argument[1]))
            {
                display = argument;
            }
            else if (argument == "-auth" && i + 1 < arguments.Length)
            {
                authority = arguments[++i];
            }
            else if (argument.StartsWith("vt", StringComparison.Ordinal) && int.TryParse(argument[2..], out var number))
            {
                terminal = number;
            }
        }

        // Started with "-displayfd": the number is not on the command line, but the socket it listens on is.
        display ??= DisplayOfSocket(pid);
        if (display is null)
        {
            return null;
        }

        if (authority is null || !File.Exists(authority))
        {
            authority = ServerEnvironment(pid, "XAUTHORITY") is { } fromServer && File.Exists(fromServer) ? fromServer : null;
        }

        return new XServer(display, authority, pid, terminal);
    }

    private static string? DisplayOfSocket(int pid)
    {
        // /proc/net/unix: ... inode path. The X sockets are /tmp/.X11-unix/X<n>.
        var sockets = new Dictionary<string, string>();
        foreach (var line in File.ReadLines("/proc/net/unix"))
        {
            var at = line.IndexOf("/tmp/.X11-unix/X", StringComparison.Ordinal);
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (at > 0 && parts.Length >= 8 && int.TryParse(line[(at + 16)..].Trim(), out var number))
            {
                sockets[parts[6]] = ":" + number;
            }
        }

        foreach (var descriptor in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
        {
            // "socket:[12345]"
            var target = new FileInfo(descriptor).LinkTarget;
            if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && sockets.TryGetValue(target[8..^1], out var display))
            {
                return display;
            }
        }

        return null;
    }

    private static string? ServerEnvironment(int pid, string name)
    {
        var prefix = name + "=";
        return File.ReadAllText($"/proc/{pid}/environ").Split('\0').FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }
}

// Linux: the screen of an X display, captured by a helper process that belongs to that display - when the
// X server ends (somebody signs out, the sign-in screen gives way to a desktop), the helper ends with it and
// the agent starts a new one on the display the monitor shows then. Wayland is not captured.
internal sealed unsafe class LinuxScreenPlatform : ScreenPlatform
{
    private readonly int _terminalAtStart = XServers.ActiveTerminal();

    public override bool Supported => UnsupportedReason is null;

    public override string? UnsupportedReason =>
        X11.Missing is { } missing ? $"The screen needs X11's libraries; {missing} is not installed (Debian/Ubuntu: apt install libx11-6 libxext6 libxfixes3 libxtst6 libxdamage1 libxrandr2)."
        : !OpenH264.IsAvailable ? "The screen needs the H.264 encoder OpenH264: " + OpenH264.UnavailableReason + " (Debian/Ubuntu: apt install libopenh264-7, Fedora: dnf install openh264)"
        : null;

    public override bool UsesHelper => !ScreenPlugin.TestMode;

    public override bool IsSystem => UnixInstaller.IsRoot;

    public override Process StartHelper(string arguments)
    {
        var server = XServers.Find(out var why) ?? throw new InvalidOperationException(why == "wayland"
            ? "This machine's desktop runs on Wayland, which the agent cannot show yet - choose an Xorg session at the sign-in screen."
            : "The machine shows no screen right now (no X server is running).");
        var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DISPLAY"] = server.Display;
        start.Environment["MATGATE_AGENT_XPID"] = server.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (server.Authority is not null)
        {
            start.Environment["XAUTHORITY"] = server.Authority;
        }
        else
        {
            start.Environment.Remove("XAUTHORITY");
        }

        AgentLog.Write($"Screen: the X display {server.Display}" + (server.Terminal != 0 ? $" on the virtual terminal {server.Terminal}" : ""));
        return Process.Start(start) ?? throw new InvalidOperationException("The screen helper did not start.");
    }

    // The monitor shows another virtual terminal than at the start, and there is another X server on it.
    public override bool HelperMisplaced()
    {
        if (XServers.ActiveTerminal() == _terminalAtStart || !int.TryParse(System.Environment.GetEnvironmentVariable("MATGATE_AGENT_XPID"), out var mine) || mine == 0)
        {
            return false;
        }

        return XServers.Find(out _) is { } now && now.Pid != mine;
    }

    public override IReadOnlyList<MonitorInfo> Monitors() => X11Monitors.List();

    public override IScreenSource Source(int monitor)
    {
        var source = new X11Capture(monitor);
        return source;
    }

    public override IVideoEncoder Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output) =>
        new OpenH264Encoder(width, height, framesPerSecond, bitrate, output);

    public override IInputSink Input() => new X11Input();

    public override IClipboardBridge Clipboard() => new X11Clipboard();

    public override IDisplayControl Display() => new X11DisplayControl();

    public override IWindowList Windows() => new X11WindowList();

    // Ctrl+Alt+Del is a key combination like any other here: the desktop's own dialog answers it.
    public override bool SecureAttention(IInputSink input)
    {
        input.Key(0x1D, false, true);
        input.Key(0x38, false, true);
        input.Key(0x53, true, true);
        input.Key(0x53, true, false);
        input.Key(0x38, false, false);
        input.Key(0x1D, false, false);
        return true;
    }

    // The memory the picture is shared in belongs to root; an X server that runs as somebody else (as the
    // person signed in - the usual way today) may not attach it. So it is given to that user.
    public static void GiveToServer(int segment)
    {
        if (!UnixInstaller.IsRoot || !int.TryParse(System.Environment.GetEnvironmentVariable("MATGATE_AGENT_XPID"), out var pid) || pid == 0)
        {
            return;
        }

        var owner = XServers.Owner(pid);
        if (owner <= 0)
        {
            return;
        }

        // struct shmid_ds starts with struct ipc_perm: key, uid, gid, cuid, cgid, mode. IPC_STAT is 2, IPC_SET is 1.
        var description = stackalloc byte[256];
        if (X11.shmctl(segment, 2, (nint)description) == 0)
        {
            *(int*)(description + 4) = owner;
            X11.shmctl(segment, 1, (nint)description);
        }
    }
}

// What `matgate-agent screen-probe` prints: which screen the agent would show, and whether it can.
internal static unsafe class UnixScreenProbe
{
    public static int Run(string[] args)
    {
        var platform = ScreenPlatform.Current;
        Console.WriteLine("system:   " + AgentPlatform.Id);
        if (platform.UnsupportedReason is { } reason)
        {
            Console.WriteLine("screen:   no - " + reason);
            return 1;
        }

        if (OperatingSystem.IsLinux() && System.Environment.GetEnvironmentVariable("MATGATE_AGENT_XPID") is null)
        {
            // As the agent does it: find the display, then look at it from a process that belongs to it.
            var server = XServers.Find(out var why);
            if (server is null)
            {
                Console.WriteLine("screen:   none right now (" + why + ")");
                return 1;
            }

            Console.WriteLine($"display:  {server.Display}  key file: {server.Authority ?? "(none)"}  X server: pid {server.Pid}, terminal {server.Terminal}");
            System.Environment.SetEnvironmentVariable("MATGATE_AGENT_XPID", server.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var argument in args)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["DISPLAY"] = server.Display;
            start.Environment["MATGATE_AGENT_XPID"] = server.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (server.Authority is not null)
            {
                start.Environment["XAUTHORITY"] = server.Authority;
            }

            using var child = Process.Start(start)!;
            child.WaitForExit();
            return child.ExitCode;
        }

        try
        {
            foreach (var monitor in platform.Monitors())
            {
                Console.WriteLine($"monitor:  {monitor.Index} {monitor.Name} {monitor.Width}x{monitor.Height} at {monitor.Left},{monitor.Top}" + (monitor.Primary ? " (primary)" : ""));
            }

            using var source = platform.Source(0);
            var (width, height) = Nv12.Scaled(source.Monitor.Width, source.Monitor.Height, 1);
            var picture = new byte[Nv12.Size(width, height)];
            var frames = 0;
            long bytes = 0;
            var keys = 0;
            using var encoder = platform.Encoder(width, height, 30, 4_000_000, (data, key) =>
            {
                frames++;
                bytes += data.Length;
                keys += key ? 1 : 0;
            });
            var pointer = new PointerState();
            var images = 0;
            var clock = Stopwatch.StartNew();
            var first = true;
            while (clock.ElapsedMilliseconds < 2000)
            {
                var grab = source.Next(100, pointer, (pixels, pitch) => Nv12.FromBgra((byte*)pixels, pitch, 1, picture, width, height));
                if (grab == Grab.Lost)
                {
                    Console.WriteLine("capture:  lost");
                    return 1;
                }

                if (grab == Grab.Image || first)
                {
                    images += grab == Grab.Image ? 1 : 0;
                    encoder.Encode(picture, first);
                    first = false;
                }
            }

            // Not one colour only: a screen that is all black is a screen the agent may not see.
            var distinct = picture.AsSpan(0, width * height).ToArray().Distinct().Count();
            Console.WriteLine($"capture:  {images} pictures of {width}x{height} in 2 s, {distinct} shades of grey, pointer at {pointer.X},{pointer.Y}" + (pointer.Shape is null ? "" : " with a shape"));
            Console.WriteLine($"encoder:  {encoder.Name} - {frames} frames, {keys} key, {bytes} bytes");
            using (platform.Input() as IDisposable)
            {
                Console.WriteLine("input:    ok");
            }

            Console.WriteLine("windows:  " + platform.Windows().List().Count);
            return images > 0 && frames > 0 && keys > 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine("failed:   " + exception.Message);
            return 1;
        }
    }
}
