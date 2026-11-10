using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Matgate.Agent.Screen;

// A monitor in desktop coordinates (physical pixels - the agent is per-monitor DPI aware).
public sealed record MonitorInfo(int Index, string Name, int Left, int Top, int Width, int Height, bool Primary);

// What the mouse pointer looks like and where it is - sent beside the video, so the browser can show it as
// its own cursor (no waiting for the next picture while the mouse moves).
public sealed class PointerState
{
    public int X { get; set; }

    public int Y { get; set; }

    public bool Visible { get; set; }

    public bool Moved { get; set; }

    // A new shape as PNG, with its hot spot; null when it did not change.
    public byte[]? Shape { get; set; }

    public int HotX { get; set; }

    public int HotY { get; set; }
}

public enum Grab
{
    Nothing,
    Image,
    PointerOnly,
    Lost,
}

// Where pictures come from: the desktop, or (for the tests) a generated picture.
public interface IScreenSource : IDisposable
{
    MonitorInfo Monitor { get; }

    // Waits up to `timeout` for a change. On Image, `image` gets the BGRA picture (pointer, row pitch).
    unsafe Grab Next(int timeout, PointerState pointer, Action<nint, int> image);
}

// The desktop through DXGI desktop duplication: the GPU hands over each changed picture, nothing is
// copied while the screen stands still.
public sealed unsafe class DesktopCapture : IScreenSource
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private byte[] _shape = new byte[64 * 64 * 4];

    public DesktopCapture(int monitor)
    {
        Open(monitor);
    }

    public MonitorInfo Monitor { get; private set; } = new(0, "", 0, 0, 0, 0, true);

    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        Walk((_, output, info) =>
        {
            list.Add(info);
            return false;
        });
        return list;
    }

    // Every output attached to the desktop, in a stable order; `visit` returns true to stop.
    private static void Walk(Func<IDXGIAdapter1, IDXGIOutput, MonitorInfo, bool> visit)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var index = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        if (!description.AttachedToDesktop)
                        {
                            continue;
                        }

                        var bounds = description.DesktopCoordinates;
                        var info = new MonitorInfo(index++, description.DeviceName.TrimStart('\\', '.'), bounds.Left, bounds.Top,
                            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, bounds.Left == 0 && bounds.Top == 0);
                        if (visit(adapter, output, info))
                        {
                            return;
                        }
                    }
                }
            }
        }
    }

    private void Open(int monitor)
    {
        Close();
        var found = false;
        for (var attempt = 0; attempt < 2 && !found; attempt++)
        {
            var wanted = attempt == 0 ? monitor : 0;
            Walk((adapter, output, info) =>
            {
                if (info.Index != wanted)
                {
                    return false;
                }

                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out _device, out _context).CheckError();
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(_device);
                var mode = _duplication.Description.ModeDescription;
                Monitor = info with { Width = (int)mode.Width, Height = (int)mode.Height };
                _staging = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = mode.Width,
                    Height = mode.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                });
                found = true;
                return true;
            });
        }

        if (!found)
        {
            throw new InvalidOperationException("This machine shows no screen right now.");
        }
    }

    public Grab Next(int timeout, PointerState pointer, Action<nint, int> image)
    {
        if (_duplication is null)
        {
            var before = (Monitor.Width, Monitor.Height);
            Open(Monitor.Index);
            if ((Monitor.Width, Monitor.Height) != before)
            {
                // Another size now: the picture buffers are made for the old one.
                return Grab.Lost;
            }
        }

        var result = _duplication!.AcquireNextFrame((uint)timeout, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
        {
            return Grab.Nothing;
        }

        if (result.Failure)
        {
            // The desktop switched (lock screen, UAC prompt) or the resolution changed: start over.
            Close();
            return Grab.Lost;
        }

        var changed = false;
        try
        {
            if (info.LastMouseUpdateTime != 0)
            {
                pointer.Visible = info.PointerPosition.Visible;
                pointer.X = info.PointerPosition.Position.X;
                pointer.Y = info.PointerPosition.Position.Y;
                pointer.Moved = true;
            }

            if (info.PointerShapeBufferSize > 0)
            {
                ReadPointerShape((int)info.PointerShapeBufferSize, pointer);
            }

            if (info.LastPresentTime != 0)
            {
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                _context!.CopyResource(_staging, texture);
                changed = true;
            }
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }

        if (!changed)
        {
            return Grab.PointerOnly;
        }

        var map = _context!.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            image(map.DataPointer, (int)map.RowPitch);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        return Grab.Image;
    }

    private void ReadPointerShape(int size, PointerState pointer)
    {
        if (_shape.Length < size)
        {
            _shape = new byte[size];
        }

        fixed (byte* buffer = _shape)
        {
            if (_duplication!.GetFramePointerShape((uint)size, (nint)buffer, out _, out var shape).Failure)
            {
                return;
            }

            pointer.Shape = CursorImage.ToPng(buffer, (int)shape.Type, (int)shape.Width, (int)shape.Height, (int)shape.Pitch);
            pointer.HotX = shape.HotSpot.X;
            pointer.HotY = shape.HotSpot.Y;
        }
    }

    private void Close()
    {
        _staging?.Dispose();
        _staging = null;
        _duplication?.Dispose();
        _duplication = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => Close();
}

// Pointer shapes as the browser can show them (PNG, for a CSS cursor).
internal static unsafe class CursorImage
{
    public static byte[]? ToPng(byte* data, int type, int width, int height, int pitch)
    {
        // Monochrome shapes carry an AND mask and an XOR mask stacked, so the picture is half as high.
        var rows = type == 1 ? height / 2 : height;
        if (width <= 0 || rows <= 0 || width > 256 || rows > 256)
        {
            return null;
        }

        using var bitmap = new System.Drawing.Bitmap(width, rows, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, rows), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            for (var y = 0; y < rows; y++)
            {
                var target = (uint*)((byte*)locked.Scan0 + (long)y * locked.Stride);
                for (var x = 0; x < width; x++)
                {
                    target[x] = type switch
                    {
                        1 => Monochrome(data, pitch, rows, x, y),
                        4 => MaskedColor(*(uint*)(data + (long)y * pitch + x * 4)),
                        _ => *(uint*)(data + (long)y * pitch + x * 4),
                    };
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return png.ToArray();
    }

    // AND 0 / XOR 0 = black, AND 0 / XOR 1 = white, AND 1 / XOR 0 = see-through, AND 1 / XOR 1 = inverted
    // (a browser cannot invert, black with a white edge reads best - here: black).
    private static uint Monochrome(byte* data, int pitch, int rows, int x, int y)
    {
        var bit = 0x80 >> (x % 8);
        var and = (data[(long)y * pitch + x / 8] & bit) != 0;
        var xor = (data[(long)(y + rows) * pitch + x / 8] & bit) != 0;
        return (and, xor) switch
        {
            (false, false) => 0xFF000000,
            (false, true) => 0xFFFFFFFF,
            (true, false) => 0x00000000,
            _ => 0xFF000000,
        };
    }

    // Alpha 0 = the colour as it is, alpha 0xFF = XOR with the screen (shown as black where it would draw).
    private static uint MaskedColor(uint pixel)
    {
        var alpha = pixel >> 24;
        var colour = pixel & 0x00FFFFFF;
        return alpha == 0 ? 0xFF000000 | colour : colour == 0 ? 0u : 0xFF000000;
    }
}

// A generated picture instead of the desktop: the tests see moving content without anyone's real screen.
public sealed unsafe class TestPattern : IScreenSource
{
    private readonly byte[] _pixels;
    private readonly int _pitch;
    private long _frame;
    private DateTime _next = DateTime.UtcNow;
    private bool _shapeSent;

    // Two monitors side by side, as large as TestDisplay has them right now.
    public TestPattern(int monitor)
    {
        var (width, height) = TestDisplay.Size(monitor == 1 ? 1 : 0);
        Monitor = monitor == 1
            ? new MonitorInfo(1, "TEST2", TestDisplay.Size(0).Width, 0, width, height, false)
            : new MonitorInfo(0, "TEST1", 0, 0, width, height, true);
        _pitch = Monitor.Width * 4;
        _pixels = new byte[_pitch * Monitor.Height];
    }

    public static List<MonitorInfo> Monitors() => [new TestPattern(0).Monitor, new TestPattern(1).Monitor];

    public MonitorInfo Monitor { get; }

    public Grab Next(int timeout, PointerState pointer, Action<nint, int> image)
    {
        if (TestDisplay.Size(Monitor.Index) != (Monitor.Width, Monitor.Height))
        {
            // The "resolution" changed, as DXGI reports it for a real monitor.
            return Grab.Lost;
        }

        if (!_shapeSent)
        {
            _shapeSent = true;
            var arrow = new byte[16 * 16 * 4];
            for (var i = 0; i < 16; i++)
            {
                for (var j = 0; j <= i; j++)
                {
                    BitConverter.GetBytes(0xFF000000u).CopyTo(arrow, (i * 16 + j) * 4);
                }
            }

            fixed (byte* shape = arrow)
            {
                pointer.Shape = CursorImage.ToPng(shape, 2, 16, 16, 64);
            }

            pointer.Visible = true;
            pointer.Moved = true;
        }

        var wait = _next - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait < TimeSpan.FromMilliseconds(timeout) ? wait : TimeSpan.FromMilliseconds(timeout));
            if (_next > DateTime.UtcNow)
            {
                return Grab.Nothing;
            }
        }

        _next = DateTime.UtcNow.AddMilliseconds(100);
        _frame++;
        // A moving bar over a gradient, the frame count readable in the top-left block.
        var bar = (int)(_frame * 16 % Monitor.Width);
        for (var y = 0; y < Monitor.Height; y++)
        {
            for (var x = 0; x < Monitor.Width; x++)
            {
                var offset = y * _pitch + x * 4;
                var inBar = x >= bar && x < bar + 40;
                _pixels[offset] = (byte)(inBar ? 255 : x * 255 / Monitor.Width);
                _pixels[offset + 1] = (byte)(inBar ? 255 : y * 255 / Monitor.Height);
                _pixels[offset + 2] = (byte)(inBar ? 255 : 96);
                _pixels[offset + 3] = 255;
            }
        }

        fixed (byte* pixels = _pixels)
        {
            image((nint)pixels, _pitch);
        }

        return Grab.Image;
    }

    public void Dispose()
    {
    }
}
