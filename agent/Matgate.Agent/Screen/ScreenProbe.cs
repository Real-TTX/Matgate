using System.Diagnostics;

namespace Matgate.Agent.Screen;

// screen-probe [--test] [--seconds N] [--out file.h264]: captures and encodes like a viewer would and prints
// the numbers - whether this machine can share its screen, and how fast. The real desktop is never written
// to a file; --out only works with --test (the generated picture).
internal static unsafe class ScreenProbe
{
    public static int Run(string[] args)
    {
        if (args.Contains("--audio"))
        {
            // What the machine plays, for two seconds: counts packets, keeps nothing.
            var packets = 0;
            using (var audio = new AudioStreamer(opus: true, _ => Interlocked.Increment(ref packets)))
            {
                Thread.Sleep(2000);
            }

            Console.WriteLine($"sound: {packets} packets in 2 s (none while nothing plays)");
            return 0;
        }

        if (args.Contains("--clipboard"))
        {
            // The clipboard watcher starts and stops; what is on the clipboard is neither read nor changed.
            var clipboard = new WindowsClipboard();
            Thread.Sleep(500);
            clipboard.Dispose();
            Console.WriteLine("clipboard watcher: ok");
            return 0;
        }

        var test = args.Contains("--test");
        var secondsIndex = Array.IndexOf(args, "--seconds");
        var seconds = secondsIndex > 0 && secondsIndex + 1 < args.Length ? int.Parse(args[secondsIndex + 1]) : 3;
        var outIndex = Array.IndexOf(args, "--out");
        var outPath = test && outIndex > 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null;

        using IScreenSource source = test ? new TestPattern(0) : new DesktopCapture(0);
        var monitor = source.Monitor;
        var scale = monitor.Width > 2560 ? 2 : 1;
        var (width, height) = Nv12.Scaled(monitor.Width, monitor.Height, scale);
        Console.WriteLine($"monitor {monitor.Name} {monitor.Width}x{monitor.Height}, video {width}x{height}");
        using var output = outPath is null ? null : File.Create(outPath);
        long frames = 0, bytes = 0, keys = 0;
        if (args.Contains("--software"))
        {
            Environment.SetEnvironmentVariable("MATGATE_AGENT_SOFTWARE_ENCODER", "1");
        }

        using var encoder = VideoEncoders.Create(width, height, 60, 12_000_000, (data, key) =>
        {
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref bytes, data.Length);
            Interlocked.Add(ref keys, key ? 1 : 0);
            lock (args)
            {
                output?.Write(data);
            }
        });
        Console.WriteLine($"encoder: {encoder.Name}");
        var picture = new byte[Nv12.Size(width, height)];
        var pointer = new PointerState();
        long submitted = 0;
        double convert = 0, encode = 0;
        var clock = Stopwatch.StartNew();
        var first = true;
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var step = Stopwatch.StartNew();
            var grab = source.Next(100, pointer, (pixels, pitch) => Nv12.FromBgra((byte*)pixels, pitch, scale, picture, width, height));
            if (grab != Grab.Image && !first)
            {
                continue;
            }

            convert += step.Elapsed.TotalMilliseconds;
            step.Restart();
            if (encoder.Encode(picture, first))
            {
                submitted++;
            }

            encode += step.Elapsed.TotalMilliseconds;
            first = false;
        }

        Thread.Sleep(300);
        Console.WriteLine($"{submitted} pictures in, {frames} frames out ({keys} key) in {seconds} s, {bytes / 1024} KB, " +
            $"capture+convert {convert / Math.Max(1, submitted):0.0} ms, encode {encode / Math.Max(1, submitted):0.0} ms per picture, pointer shape {(pointer.Shape is null ? "none" : pointer.Shape.Length + " bytes")}");
        return frames > 0 ? 0 : 1;
    }
}
