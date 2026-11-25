using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Channels;

// The display control channel (MS-RDPEDISP): how a client tells the server that its window has another size.
// The server opens the channel and says what it can take; from then on the client may send a monitor layout
// whenever the size changes. Nothing comes back on this channel - when the server takes the layout it
// deactivates the session and brings it up again in the new size (MS-RDPBCGR 1.3.1.3), and when it does not,
// nothing happens at all.
public sealed class DisplayControlChannel : DynamicChannel
{
    public const string ChannelName = "Microsoft::Windows::RDS::DisplayControl";

    // What a monitor may measure (MS-RDPEDISP 2.2.2.2.1). The width must be even on top of that.
    public const int MinWidth = 200;
    public const int MinHeight = 200;
    public const int MaxMonitorWidth = 8192;
    public const int MaxMonitorHeight = 8192;

    private const uint TypeMonitorLayout = 0x00000002;
    private const uint TypeCapabilities = 0x00000005;
    private const int HeaderLength = 8;
    private const int MonitorLength = 40;
    private const uint Primary = 0x00000001;

    private readonly Lock _gate = new();
    private bool _ready;
    private int _maxMonitors;
    private int _factorA;
    private int _factorB;
    private long _maxArea;

    public override string Name => ChannelName;

    // The server said what it takes: SetSize works. Raised again when the server says it anew.
    public event Action? Ready;

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    // What the server announced (0 before it did): how many monitors it takes, and the two factors whose
    // product with that number is the largest area all monitors together may cover, in pixels.
    public int MaxMonitors => Read(() => _maxMonitors);

    public int MaxAreaFactorA => Read(() => _factorA);

    public int MaxAreaFactorB => Read(() => _factorB);

    public long MaxArea
    {
        get
        {
            lock (_gate)
            {
                return _maxArea;
            }
        }
    }

    // The widest and the highest a desktop of one monitor can be on this server (0 before it is ready). Both at
    // once may be more than the area allows; Fit says what a size comes to.
    public int MaxWidth => Read(() => _ready ? (int)Math.Min(MaxMonitorWidth, _maxArea / MinHeight) & ~1 : 0);

    public int MaxHeight => Read(() => _ready ? (int)Math.Min(MaxMonitorHeight, _maxArea / MinWidth) : 0);

    // Asks for a desktop of one monitor in this size. The size is brought within what the protocol and the
    // server allow first (see Fit). False when the server is not ready for it; true says the request went
    // out, not that the server will follow it.
    public bool SetSize(int width, int height)
    {
        var send = Send;
        long area;
        lock (_gate)
        {
            if (!_ready || send is null)
            {
                return false;
            }

            area = _maxArea;
        }

        (width, height) = Fit(width, height, area);

        // One primary monitor at 0,0. The physical size stays 0, which a server has to ignore (anything
        // outside 10 to 10000 mm is), no rotation, and scale factors of 100 percent.
        var layout = new ByteWriter(HeaderLength + 8 + MonitorLength)
            .U32(TypeMonitorLayout)
            .U32(HeaderLength + 8 + MonitorLength)
            .U32(MonitorLength)
            .U32(1)
            .U32(Primary)
            .U32(0)
            .U32(0)
            .U32((uint)width)
            .U32((uint)height)
            .U32(0)
            .U32(0)
            .U32(0)
            .U32(100)
            .U32(100)
            .ToArray();
        try
        {
            send(layout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report("sending the monitor layout failed: " + exception.Message);
            return false;
        }

        return true;
    }

    // The size SetSize would ask for: at least 200 by 200, at most 8192 by 8192, an even width, and - keeping
    // the proportions as far as possible - no more area than the server announced.
    public (int Width, int Height) Fit(int width, int height) => Fit(width, height, MaxArea);

    public override void Receive(ReadOnlySpan<byte> message)
    {
        var reader = new ByteReader(message);
        if (!reader.TryU32(out var type) || !reader.TryU32(out var length))
        {
            Report("dropped a message shorter than its header");
            return;
        }

        if (type != TypeCapabilities)
        {
            Report($"ignored message {type}, which this client does not know");
            return;
        }

        // The length counts the header in; more bytes than it says are not looked at.
        if (length < HeaderLength + 12 || length > (uint)message.Length
            || !reader.TryU32(out var monitors) || !reader.TryU32(out var factorA) || !reader.TryU32(out var factorB))
        {
            Report("dropped capabilities that are too short");
            return;
        }

        var area = (UInt128)monitors * factorA * factorB;
        if (area < (UInt128)(MinWidth * MinHeight))
        {
            // No monitor fits into that; a server that says so cannot be asked for anything.
            Report($"ignored capabilities that leave no room for a monitor ({monitors} x {factorA} x {factorB})");
            return;
        }

        lock (_gate)
        {
            _ready = true;
            _maxMonitors = (int)Math.Min(monitors, int.MaxValue);
            _factorA = (int)Math.Min(factorA, int.MaxValue);
            _factorB = (int)Math.Min(factorB, int.MaxValue);
            _maxArea = area > (UInt128)long.MaxValue ? long.MaxValue : (long)area;
        }

        Guarded.Raise(Ready, Log, ChannelName + ": the handler of Ready");
    }

    public override void Closed()
    {
        lock (_gate)
        {
            _ready = false;
            _maxMonitors = 0;
            _factorA = 0;
            _factorB = 0;
            _maxArea = 0;
        }
    }

    private int Read(Func<int> value)
    {
        lock (_gate)
        {
            return value();
        }
    }

    private static (int Width, int Height) Fit(int width, int height, long maxArea)
    {
        width = Math.Clamp(width, MinWidth, MaxMonitorWidth) & ~1;
        height = Math.Clamp(height, MinHeight, MaxMonitorHeight);
        if (maxArea <= 0 || (long)width * height <= maxArea)
        {
            return (width, height);
        }

        // Both sides shrink by the same factor; rounding down keeps the result inside the area.
        var scale = Math.Sqrt((double)maxArea / ((long)width * height));
        width = Math.Max(MinWidth, (int)(width * scale) & ~1);
        height = Math.Max(MinHeight, (int)(height * scale));

        // Where one side hit its minimum the other has to give way.
        if ((long)width * height > maxArea)
        {
            if (width == MinWidth)
            {
                height = (int)Math.Max(MinHeight, maxArea / width);
            }
            else
            {
                width = (int)Math.Max(MinWidth, maxArea / height) & ~1;
            }
        }

        return (width, height);
    }
}
