using System.Text;
using Matgate.Display.Rdp.Graphics;

namespace Matgate.Display.Rdp.Orders;

// What an orders update changed on the screen.
internal readonly record struct DrawnArea(int X, int Y, int Width, int Height);

// Drawing orders (MS-RDPEGDI): reads an orders update - a count, then primary, secondary and alternate secondary
// orders - and draws it into the session's BGRX framebuffer. Servers that paint only with orders (xrdp without
// a codec: cache a bitmap, then MemBlt it; ScrBlt, OpaqueRect and friends for the rest) need it.
//
// What it draws completely - and what the client therefore announces (NegotiatedOrders, BitmapCache.Entries) -
// is DstBlt, PatBlt (solid, hatched and 8 x 8 pattern brushes), ScrBlt, OpaqueRect, MultiOpaqueRect, MemBlt and
// Mem3Blt, with all 256 raster operations, and the Cache Bitmap (revisions 1 and 2) and Cache Color Table
// secondary orders. LineTo, GlyphIndex and Cache Glyph are drawn too, although not announced, because xrdp sends
// them to any client that takes orders (its own login and log windows). Other orders are skipped where their length is known (the remaining secondary orders, frame markers,
// surface switches); a primary order of another kind has no length, so the rest of its update is dropped, with
// one line in the log.
//
// Everything in an update was written by the server: lengths, counts, cache ids and coordinates are checked, every
// drawing is clipped to the framebuffer (Canvas), the caches have fixed limits, and nothing throws - a malformed
// order ends the update it is in, and that is all.
//
// Not thread-safe: the session's read loop is its only user. The caller holds the frame lock while it draws.
internal sealed partial class OrderDecoder
{
    // controlFlags of every order (2.2.2.2.1.1.2, 2.2.2.2.1.2.1.1, 2.2.2.2.1.3.1.1).
    private const int StandardFlag = 0x01;
    private const int SecondaryFlag = 0x02;
    private const int BoundsFlag = 0x04;
    private const int TypeChangeFlag = 0x08;
    private const int DeltaCoordinatesFlag = 0x10;
    private const int ZeroBoundsDeltasFlag = 0x20;

    private const int ColorTables = 6;

    // How many distinct problems the log hears about; a hostile server cannot fill it.
    private const int MaxReported = 64;

    private readonly Action<string>? _log;
    private readonly HashSet<string> _reported = [];
    private readonly BitmapCache _cache = new();
    private readonly uint[][] _colorTables = new uint[ColorTables][];
    private readonly uint[] _palette = new uint[256];

    // Counters by kind of order: 0-31 primary, 32-47 secondary, 48-111 alternate secondary.
    private readonly long[] _sinceSummary = new long[112];
    private readonly long[] _total = new long[112];
    private int _colorDepth = 32;

    public OrderDecoder(Action<string>? log = null)
    {
        _log = log;
        Reset(32);
    }

    // The negotiation indices (orderSupport of the Order Capability Set, MS-RDPBCGR 2.2.7.1.3) of the primary
    // orders drawn completely: DstBlt, PatBlt (and with it OpaqueRect), ScrBlt, MemBlt, Mem3Blt, MultiOpaqueRect.
    public static ReadOnlySpan<byte> NegotiatedOrders => [0x00, 0x01, 0x02, 0x03, 0x04, 0x12];

    // Forgets everything: the session is (re)activated, and the server starts its caches and its order encoding
    // afresh. colorDepth: what the server settled on (8, 15, 16, 24 or 32) - it says how colours and bitmaps in
    // orders are encoded.
    public void Reset(int colorDepth)
    {
        _colorDepth = colorDepth;
        _cache.Clear();
        _glyphs.Clear();
        for (var i = 0; i < ColorTables; i++)
        {
            _colorTables[i] = [];
        }

        ResetPrimary();
    }

    // FASTPATH_UPDATETYPE_ORDERS (MS-RDPBCGR 2.2.9.1.2.1.2): the number of orders, then the orders.
    public bool DecodeFastPath(ReadOnlySpan<byte> update, Span<byte> framebuffer, int width, int height,
        ReadOnlySpan<uint> palette, out DrawnArea drawn)
    {
        var reader = new OrderReader(update);
        int count = reader.U16();
        if (reader.Failed)
        {
            drawn = default;
            return false;
        }

        return Decode(count, update[2..], framebuffer, width, height, palette, out drawn);
    }

    // TS_UPDATE_ORDERS_PDU_DATA (MS-RDPBCGR 2.2.9.1.1.3.1.1) after its updateType: two bytes of padding, the
    // number of orders, two more bytes of padding, the orders.
    public bool DecodeSlowPath(ReadOnlySpan<byte> update, Span<byte> framebuffer, int width, int height,
        ReadOnlySpan<uint> palette, out DrawnArea drawn)
    {
        var reader = new OrderReader(update);
        reader.Skip(2);
        int count = reader.U16();
        reader.Skip(2);
        if (reader.Failed)
        {
            drawn = default;
            return false;
        }

        return Decode(count, update[6..], framebuffer, width, height, palette, out drawn);
    }

    // Draws `count` orders from `orders` into the framebuffer (BGRX, 4 bytes a pixel, stride width * 4).
    // palette: the session's palette (0xFFRRGGBB, 256 entries), used at 8 bits per pixel. Returns whether anything
    // was drawn, and the bounding box of it.
    public bool Decode(int count, ReadOnlySpan<byte> orders, Span<byte> framebuffer, int width, int height,
        ReadOnlySpan<uint> palette, out DrawnArea drawn)
    {
        if (palette.Length >= 256)
        {
            palette[..256].CopyTo(_palette);
        }

        var canvas = new Canvas(framebuffer, width, height);
        var reader = new OrderReader(orders);
        for (var i = 0; i < count; i++)
        {
            if (reader.Remaining == 0)
            {
                Report("short", "an orders update with fewer orders than it announces");
                break;
            }

            var control = reader.U8();
            bool ok;
            if ((control & StandardFlag) == 0)
            {
                ok = AlternateSecondary(ref reader, control);
            }
            else if ((control & SecondaryFlag) != 0)
            {
                ok = SecondaryOrder(ref reader);
            }
            else
            {
                ok = Primary(ref reader, control, ref canvas);
            }

            if (!ok)
            {
                break;
            }
        }

        if (canvas.TryGetDirty(out var x, out var y, out var drawnWidth, out var drawnHeight))
        {
            drawn = new DrawnArea(x, y, drawnWidth, drawnHeight);
            return true;
        }

        drawn = default;
        return false;
    }

    // What the tests look at: the Primary Drawing Order History and the caches.
    internal int LastOrderType => _orderType;

    internal (int Left, int Top, int Right, int Bottom) LastBounds => (_boundsLeft, _boundsTop, _boundsRight, _boundsBottom);

    internal DstBltFields DstBltHistory => _dstBlt;

    internal PatBltFields PatBltHistory => _patBlt;

    internal ScrBltFields ScrBltHistory => _scrBlt;

    internal OpaqueRectFields OpaqueRectHistory => _opaqueRect;

    internal MultiOpaqueRectFields MultiOpaqueRectHistory => _multiOpaqueRect;

    internal MemBltFields MemBltHistory => _memBlt;

    internal MemBltFields Mem3BltHistory => _mem3Blt;

    internal LineToFields LineToHistory => _lineTo;

    internal GlyphIndexFields GlyphIndexHistory => _glyphIndex;

    internal BitmapCache Bitmaps => _cache;

    internal GlyphCache Glyphs => _glyphs;

    internal uint[] ColorTable(int index) => _colorTables[index];

    // The orders decoded since the last call, by kind - for the session's trace.
    public string TakeSummary() => Summarize(_sinceSummary, true);

    // All orders decoded since the decoder was made, by kind.
    public string TotalSummary() => Summarize(_total, false);

    private string Summarize(long[] counts, bool clear)
    {
        var text = new StringBuilder();
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append(KindName(i)).Append(" x").Append(counts[i]);
        }

        if (clear)
        {
            Array.Clear(counts);
        }

        return text.Length == 0 ? "none" : text.ToString();
    }

    private static string KindName(int kind) => kind switch
    {
        DstBltType => "DstBlt",
        PatBltType => "PatBlt",
        ScrBltType => "ScrBlt",
        LineToType => "LineTo",
        OpaqueRectType => "OpaqueRect",
        MemBltType => "MemBlt",
        Mem3BltType => "Mem3Blt",
        MultiOpaqueRectType => "MultiOpaqueRect",
        GlyphIndexType => "GlyphIndex",
        < 32 => $"primary 0x{kind:X2}",
        32 => "CacheBitmap",
        33 => "CacheColorTable",
        34 => "CacheBitmapCompressed",
        35 => "CacheGlyph",
        36 => "CacheBitmapRev2",
        37 => "CacheBitmapCompressedRev2",
        39 => "CacheBrush",
        40 => "CacheBitmapRev3",
        < 48 => $"secondary 0x{kind - 32:X2}",
        48 => "SwitchSurface",
        49 => "CreateOffscreenBitmap",
        59 => "Window",
        61 => "FrameMarker",
        _ => $"alternate secondary 0x{kind - 48:X2}",
    };

    private void Count(int kind)
    {
        _sinceSummary[kind]++;
        _total[kind]++;
    }

    // One log line per kind of problem: a server that keeps doing the same wrong thing does not flood the log.
    private void Report(string key, string message)
    {
        if (_reported.Count < MaxReported && _reported.Add(key))
        {
            _log?.Invoke("drawing orders: " + message);
        }
    }

    // A TS_COLOR (2.2.2.2.1.1.1.8) as a framebuffer pixel. At 24 and 32 bits it is red, green, blue; at 15 and 16
    // bits the first two bytes hold the pixel value as in a bitmap (as Windows and xrdp send it, and as FreeRDP
    // reads it); at 8 bits the first byte is a palette index.
    private uint Color(int wire)
    {
        var value = _colorDepth switch
        {
            <= 8 => _palette[wire & 0xFF],
            15 => Bgrx.FromRgb555((uint)wire & 0xFFFF),
            16 => Bgrx.FromRgb565((uint)wire & 0xFFFF),
            _ => ((uint)(wire & 0xFF) << 16) | (uint)(wire & 0xFF00) | (uint)((wire >> 16) & 0xFF),
        };
        return Bgrx.Store(value | Bgrx.Opaque);
    }
}
