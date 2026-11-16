using System.Buffers.Binary;
using System.Diagnostics;
using Matgate.Display.Rdp;

namespace Matgate.Display.Probe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: <host> <port> <user> <password> [seconds] [picture.bmp] [width height]");
            return 2;
        }

        var seconds = args.Length > 4 ? int.Parse(args[4]) : 8;
        var picture = args.Length > 5 && args[5] != "-" ? args[5] : null;
        var options = new RdpOptions
        {
            Host = args[0],
            Port = int.Parse(args[1]),
            UserName = args[2],
            Password = args[3],
            Width = args.Length > 7 ? int.Parse(args[6]) : 1280,
            Height = args.Length > 7 ? int.Parse(args[7]) : 800,
        };
        var watch = Stopwatch.StartNew();
        void Log(string message) => Console.WriteLine($"[{watch.ElapsedMilliseconds,6} ms] {message}");
        var handler = new Handler(Log);
        await using var client = new RdpClient(options, handler, Log);
        try
        {
            await client.ConnectAsync(CancellationToken.None);
        }
        catch (RdpException exception)
        {
            Log("FAILED: " + exception.Message + (exception.IsLogonFailure ? " (logon failure)" : ""));
            if (exception.InnerException is { } inner)
            {
                Log("  cause: " + inner.GetType().Name + ": " + inner.Message);
            }

            return 1;
        }

        Log($"connected: {client.Width}x{client.Height}, {client.ColorDepth} bits");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var running = client.RunAsync(limit.Token);
        // A little input, so the desktop has a reason to draw.
        await Task.Delay(1500);
        for (var i = 0; i < 20; i++)
        {
            client.MouseMove(100 + i * 20, 100 + i * 10);
            await Task.Delay(30);
        }

        try
        {
            await running;
        }
        catch (RdpException exception)
        {
            Log("ENDED WITH AN ERROR: " + exception.Message);
        }

        Log($"updates: {handler.Updates}, area {handler.Pixels} pixels, cursors: {handler.Cursors}, resizes: {handler.Resizes}; end reason: {client.EndReason ?? "none"}");
#if STUB_GRAPHICS
        Log($"bitmaps: {Matgate.Display.Rdp.Graphics.BitmapDecoder.Rectangles} ({Matgate.Display.Rdp.Graphics.BitmapDecoder.Bytes} bytes): "
            + string.Join("; ", Matgate.Display.Rdp.Graphics.BitmapDecoder.Kinds.Select(kind => kind.Key + " x" + kind.Value)));
#endif
        if (picture is not null)
        {
            lock (client.FrameLock)
            {
                WriteBmp(picture, client.Framebuffer, client.Width, client.Height);
            }

            Log("picture written to " + picture);
        }

        client.Disconnect();
        return 0;
    }

    // A 32-bit .bmp, top-down - exactly the framebuffer's own layout behind a header.
    private static void WriteBmp(string path, byte[] bgrx, int width, int height)
    {
        var header = new byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), 54 + bgrx.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), -height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(34), bgrx.Length);
        using var file = File.Create(path);
        file.Write(header);
        file.Write(bgrx);
    }

    private sealed class Handler(Action<string> log) : IRdpHandler
    {
        public int Updates;
        public long Pixels;
        public int Cursors;
        public int Resizes;

        public void Updated(int x, int y, int width, int height)
        {
            if (Updates++ < 5)
            {
                log($"update {x},{y} {width}x{height}");
            }

            Pixels += (long)width * height;
        }

        public void Resized(int width, int height)
        {
            Resizes++;
            log($"resized to {width}x{height}");
        }

        public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
        {
            if (Cursors++ < 5)
            {
                log($"cursor {width}x{height}, hot spot {hotX},{hotY}");
            }
        }

        public void CursorHidden() => log("cursor hidden");

        public void CursorDefault() => log("cursor default");
    }
}
