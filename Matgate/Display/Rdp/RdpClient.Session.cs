using System.Buffers.Binary;
using Matgate.Display.Rdp.Graphics;
using Matgate.Display.Rdp.Orders;

namespace Matgate.Display.Rdp;

// The session itself: the licence, the capabilities both sides exchange (again whenever the desktop gets
// another size), and then pictures and pointers from the server.
public sealed partial class RdpClient
{
    // The largest picture update, put together from fragments, this client accepts. Announced to the server.
    private const int MaxFragmented = 8 * 1024 * 1024;
    private const int PointerCacheSize = 32;

    private readonly uint[] _palette = new uint[256];
    private readonly CachedPointer?[] _pointers = new CachedPointer?[PointerCacheSize];
    private byte[] _fragments = [];
    private int _fragmentsLength;
    private int _fragmentsCode = -1;
    private bool _licensed;
    private volatile bool _active;
    private uint _shareId;
    private int _chunkSize = 1600;
    private bool _serverFastPathInput;
    private bool _warnedOrders;

    // Drawing orders and the caches they draw from; made at the first activation, emptied at every one.
    private OrderDecoder? _orders;

    // MATGATE_RDP_TRACE=1: every message from the server in the log, by kind - for looking at a server that
    // behaves unexpectedly.
    private static readonly bool Tracing = Environment.GetEnvironmentVariable("MATGATE_RDP_TRACE") == "1";

    private void Trace(string message)
    {
        if (Tracing)
        {
            _log?.Invoke("trace: " + message);
        }
    }

    private sealed record CachedPointer(int HotX, int HotY, int Width, int Height, byte[] Rgba);

    // ---- messages in the classic framing (TPKT, X.224, MCS) ---------------------------------------------

    private void SlowPath(ReadOnlySpan<byte> pdu)
    {
        var reader = new WireReader(pdu);
        reader.Skip(5);
        if (reader.U8() != 0xF0)
        {
            // Anything but data at this point is the server leaving (an X.224 disconnect request).
            throw new SessionEndedException();
        }

        reader.U8();
        var choice = reader.U8() >> 2;
        if (choice == 8)
        {
            // MCS Disconnect Provider Ultimatum.
            EndReason ??= RdpErrorInfo.Describe(0);
            throw new SessionEndedException();
        }

        if (choice != 26)
        {
            return;
        }

        // Send Data Indication: who sent it, on which channel, a byte of flags, the length.
        reader.Skip(2);
        var channel = reader.U16Be();
        reader.U8();
        var length = reader.PerLength();
        var data = reader.Bytes(Math.Min(length, reader.Remaining));
        if (channel == _ioChannel)
        {
            IoData(data);
        }
        else if (_channelsById.TryGetValue(channel, out var joined))
        {
            if (joined.Reassembler.Add(data, out var message))
            {
                try
                {
                    joined.Channel.Receive(message.Span);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A channel that trips over a message must not take the picture with it.
                    _log?.Invoke("channel " + joined.Channel.Name + " failed on a message: " + exception.Message);
                }
            }
        }
    }

    private void IoData(ReadOnlySpan<byte> data)
    {
        if (!_licensed)
        {
            // Until the licence is settled the server's messages carry a security header; its flags say
            // "licence". Anything else means the server skipped that step.
            if (data.Length >= 4 && (BinaryPrimitives.ReadUInt16LittleEndian(data) & 0x0080) != 0)
            {
                License(data[4..]);
                return;
            }

            _licensed = true;
        }

        // One data unit may hold several messages, each with its own length.
        var reader = new WireReader(data);
        while (reader.Remaining >= 6)
        {
            var total = reader.U16();
            if (total == 0x8000)
            {
                // A flow-control message, a relic: eight bytes in all.
                reader.Skip(Math.Min(6, reader.Remaining));
                continue;
            }

            var type = reader.U16() & 0x0F;
            reader.U16();
            if (total < 6)
            {
                throw new RdpProtocolException("a message of impossible length");
            }

            var body = reader.Bytes(Math.Min(total - 6, reader.Remaining));
            Trace($"share control type {type}, {body.Length} bytes");
            switch (type)
            {
                case 1:
                    DemandActive(body);
                    break;
                case 6:
                    // Deactivate All: the server is about to offer its capabilities again (a new desktop size).
                    _active = false;
                    break;
                case 7:
                    Data(body);
                    break;
                case 10:
                    throw new RdpException("This server passes the session on to another one (a connection broker), which Matgate's own engine cannot follow yet. Open this connection with Guacamole.");
            }
        }
    }

    private void License(ReadOnlySpan<byte> data)
    {
        var reader = new WireReader(data);
        var type = reader.U8();
        reader.Skip(3);
        if (type == 0xFF)
        {
            // An "error" that is none: code 7 means this client needs no licence.
            var code = reader.U32();
            if (code == 0x00000007)
            {
                _licensed = true;
                return;
            }

            throw new RdpException("The RDP server refused the connection for licensing reasons (code " + code + ").");
        }

        // A real licence exchange, as terminal servers with client access licences start it.
        throw new RdpException("This server asks for a Remote Desktop licence (a terminal server). Matgate's own engine cannot do that yet - open this connection with Guacamole.");
    }

    // ---- capabilities -----------------------------------------------------------------------------------

    private void DemandActive(ReadOnlySpan<byte> body)
    {
        var reader = new WireReader(body);
        _shareId = reader.U32();
        var sourceLength = reader.U16();
        reader.U16();
        reader.Skip(sourceLength);
        var count = reader.U16();
        reader.U16();
        var width = _options.Width;
        var height = _options.Height;
        for (var i = 0; i < count && reader.Remaining >= 4; i++)
        {
            var type = reader.U16();
            var length = reader.U16();
            if (length < 4)
            {
                throw new RdpProtocolException("a capability of impossible length");
            }

            var set = new WireReader(reader.Bytes(Math.Min(length - 4, reader.Remaining)));
            switch (type)
            {
                case 0x0002 when set.Remaining >= 12:
                    ColorDepth = set.U16();
                    set.Skip(6);
                    width = set.U16();
                    height = set.U16();
                    break;
                case 0x000D when set.Remaining >= 2:
                    // Fast-path input in either of its two forms.
                    _serverFastPathInput = (set.U16() & 0x0028) != 0;
                    break;
                case 0x0014 when set.Remaining >= 8:
                    set.U32();
                    var chunk = set.U32();
                    if (chunk is >= 1600 and <= 16256)
                    {
                        _chunkSize = (int)chunk;
                    }

                    break;
            }
        }

        if (width < 1 || height < 1 || width > 16384 || height > 16384)
        {
            throw new RdpProtocolException("a desktop of impossible size");
        }

        var resized = false;
        lock (FrameLock)
        {
            if (width != Width || height != Height || Framebuffer.Length == 0)
            {
                resized = Framebuffer.Length != 0;
                Framebuffer = new byte[width * height * 4];
                Width = width;
                Height = height;
            }
        }

        // The server starts its bitmap caches and its order encoding afresh with every activation.
        _orders ??= new OrderDecoder(message => _log?.Invoke(message));
        _orders.Reset(ColorDepth is 8 or 15 or 16 or 24 or 32 ? ColorDepth : 32);

        ConfirmActive();
        FinishActivation();
        if (resized)
        {
            _handler.Resized(width, height);
        }
    }

    private void ConfirmActive() => SendControl(0x13, ConfirmActiveBody(ColorDepth));

    // What this client can take. Deliberately little: bitmaps in full colour (or in the colours asked for),
    // pointers, virtual channels, and the few drawing orders with their bitmap caches that the order decoder
    // draws completely (servers like xrdp paint with orders only) - no glyphs, brushes or offscreen caches, no
    // codecs, so the rest comes as plain compressed bitmaps. serverDepth is the colour depth the server settled
    // on. (Internal for the tests, which read the bytes.)
    internal byte[] ConfirmActiveBody(int serverDepth)
    {
        var sets = new WireWriter(512);
        var count = 0;

        void Begin(int type, int length)
        {
            sets.U16(type).U16(length);
            count++;
        }

        // General: Windows, protocol version 2; fast-path output, no compression header in front of bitmaps,
        // long credentials, salted checksums.
        Begin(0x0001, 24);
        sets.U16(1).U16(3).U16(0x0200).U16(0).U16(0).U16(0x0415).U16(0).U16(0).U16(0).U8(0).U8(0);

        // Bitmap: 32 bits a pixel (or the depth the server settled on, where fewer colours were asked for), this
        // desktop size, resizing allowed, compression on; the alpha plane may be left out.
        Begin(0x0002, 28);
        sets.U16(BitmapDepth(serverDepth)).U16(1).U16(1).U16(1).U16(Width).U16(Height).U16(0).U16(1).U16(1).U8(0).U8(0x08).U16(1).U16(0);

        // Order: the primary orders the decoder draws completely, by their negotiation index (DstBlt, PatBlt with
        // OpaqueRect, ScrBlt, MemBlt, Mem3Blt, MultiOpaqueRect); orders by negotiation, zero bounds deltas, colour
        // indices; no SaveBitmap (desktop save size zero), no extra flags (no frame markers, no revision 3 bitmaps).
        Begin(0x0003, 88);
        Span<byte> support = stackalloc byte[32];
        foreach (var index in OrderDecoder.NegotiatedOrders)
        {
            support[index] = 1;
        }

        sets.Zero(16).U32(0).U16(1).U16(20).U16(0).U16(1).U16(0).U16(0x002A).Bytes(support).U16(0).U16(0).U32(0).U32(0).U16(0).U16(0).U16(0).U16(0);

        // Bitmap cache, revision 2: the three caches the decoder keeps (16 x 16, 32 x 32 and 64 x 64 tiles), in
        // memory only - no persistent keys, no waiting list.
        Begin(0x0013, 40);
        sets.U16(0).U8(0).U8(BitmapCache.Entries.Length);
        for (var cache = 0; cache < 5; cache++)
        {
            sets.U32(cache < BitmapCache.Entries.Length ? (uint)BitmapCache.Entries[cache] : 0);
        }

        sets.Zero(12);

        // Pointer: coloured pointers, cached under these many numbers.
        Begin(0x0008, 10);
        sets.U16(1).U16(PointerCacheSize).U16(PointerCacheSize);

        // Input: scancodes, the extra mouse buttons, Unicode characters, fast-path, a horizontal wheel.
        Begin(0x000D, 88);
        sets.U16(0x013D).U16(0).U32(_options.KeyboardLayout).U32(4).U32(0).U32(12).Zero(64);

        // Brush, glyph cache, off-screen cache: none.
        Begin(0x000F, 8);
        sets.U32(0);
        Begin(0x0010, 52);
        sets.Zero(48);
        Begin(0x0011, 12);
        sets.U32(0).U16(0).U16(0);

        // Virtual channels: uncompressed.
        Begin(0x0014, 12);
        sets.U32(0).U32(1600);

        // Sound (beeps), share, font, control, window activation, colour cache: the customary values.
        Begin(0x000C, 8);
        sets.U16(1).U16(0);
        Begin(0x0009, 8);
        sets.U16(0).U16(0);
        Begin(0x000E, 8);
        sets.U16(1).U16(0);
        Begin(0x0005, 12);
        sets.U16(0).U16(0).U16(2).U16(2);
        Begin(0x0007, 12);
        sets.U16(0).U16(0).U16(0).U16(0);
        Begin(0x000A, 8);
        sets.U16(6).U16(0);

        // Large picture updates may come in fragments of up to this size in total.
        Begin(0x001A, 8);
        sets.U32(MaxFragmented);

        // Pointers of up to 96 x 96 and 384 x 384 pixels.
        Begin(0x001B, 6);
        sets.U16(0x0003);

        const string Source = "MSTSC";
        var body = new WireWriter(sets.Length + 32);
        body.U32(_shareId).U16(0x03EA).U16(Source.Length + 1).U16(sets.Length + 4);
        body.Bytes(System.Text.Encoding.ASCII.GetBytes(Source)).U8(0);
        body.U16(count).U16(0).Bytes(sets.Span);
        return body.ToArray();
    }

    // The colour depth the bitmap capability names. Full colour stays 32, as it always was - also where a server
    // limited by policy settled on fewer. Where fewer colours were asked for, the depth the server settled on,
    // as the specification wants it (and other clients send it).
    private int BitmapDepth(int serverDepth) =>
        _options.ColorDepth >= 32 ? 32 : serverDepth is 8 or 15 or 16 or 24 or 32 ? serverDepth : _options.ColorDepth;

    // The messages that finish the exchange: synchronise, take control, and the font list that makes the
    // server start drawing.
    private void FinishActivation()
    {
        SendData(0x1F, [0x01, 0x00, 0xEA, 0x03]);
        SendData(0x14, [0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        SendData(0x14, [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        SendData(0x27, [0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x32, 0x00]);
    }

    // A message of the session with its "share control" header.
    private void SendControl(int type, ReadOnlySpan<byte> body)
    {
        var message = new WireWriter(body.Length + 6);
        message.U16(body.Length + 6).U16(type | 0x10).U16(_userChannel).Bytes(body);
        Send(McsData(_ioChannel, message.Span));
    }

    // A data message of the session: the share control header, the share data header, the content.
    private void SendData(int type, ReadOnlySpan<byte> content)
    {
        var body = new WireWriter(content.Length + 12);
        body.U32(_shareId).U8(0).U8(1).U16(content.Length + 4).U8(type).U8(0).U16(0).Bytes(content);
        SendControl(0x07, body.Span);
    }

    private void Data(ReadOnlySpan<byte> body)
    {
        var reader = new WireReader(body);
        reader.Skip(8);
        var type = reader.U8();
        var compression = reader.U8();
        reader.U16();
        Trace($"data type 0x{type:X2}, {body.Length} bytes");
        if ((compression & 0x20) != 0)
        {
            throw new RdpProtocolException("compressed data, which this client never asked for");
        }

        switch (type)
        {
            case 0x02:
                Update(reader.Rest());
                break;
            case 0x1B:
                SlowPointer(reader.Rest());
                break;
            case 0x28:
                // The font map: the last of the server's finishing messages. The desktop is live.
                _active = true;
                OpenChannels();
                break;
            case 0x2F:
                var code = reader.U32();
                if (code != 0)
                {
                    EndReason = RdpErrorInfo.Describe(code);
                    _log?.Invoke("the server gives a reason: 0x" + code.ToString("X8"));
                }

                break;
        }
    }

    private void OpenChannels()
    {
        if (_channelsOpened)
        {
            return;
        }

        _channelsOpened = true;
        foreach (var joined in _channelsById.Values)
        {
            try
            {
                joined.Channel.Opened();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _log?.Invoke("channel " + joined.Channel.Name + " failed to start: " + exception.Message);
            }
        }
    }

    // ---- fast-path: pictures and pointers ----------------------------------------------------------------

    private void FastPath(ReadOnlySpan<byte> pdu, int header)
    {
        if ((pdu[0] & 0x80) != 0)
        {
            throw new RdpProtocolException("an encrypted message on a connection that TLS secures");
        }

        var reader = new WireReader(pdu[header..]);
        while (reader.Remaining >= 3)
        {
            var update = reader.U8();
            var code = update & 0x0F;
            var fragment = (update >> 4) & 0x03;
            if ((update & 0x80) != 0)
            {
                throw new RdpProtocolException("compressed data, which this client never asked for");
            }

            var data = reader.Bytes(reader.U16());
            Trace($"fast-path update {code}, fragment {fragment}, {data.Length} bytes");
            switch (fragment)
            {
                case 0:
                    FastUpdate(code, data);
                    break;
                case 2:
                    // The first piece of an update too large for one message.
                    _fragmentsLength = 0;
                    _fragmentsCode = code;
                    AppendFragment(data);
                    break;
                case 3:
                    AppendFragment(data);
                    break;
                case 1:
                    AppendFragment(data);
                    var whole = _fragmentsCode;
                    _fragmentsCode = -1;
                    if (whole >= 0)
                    {
                        FastUpdate(whole, _fragments.AsSpan(0, _fragmentsLength));
                    }

                    _fragmentsLength = 0;
                    break;
            }
        }
    }

    private void AppendFragment(ReadOnlySpan<byte> data)
    {
        if (_fragmentsCode < 0)
        {
            // A piece without a beginning: dropped.
            return;
        }

        if (_fragmentsLength + data.Length > MaxFragmented)
        {
            throw new RdpProtocolException("a picture update larger than this client allowed");
        }

        if (_fragmentsLength + data.Length > _fragments.Length)
        {
            Array.Resize(ref _fragments, Math.Max(_fragments.Length * 2, _fragmentsLength + data.Length));
        }

        data.CopyTo(_fragments.AsSpan(_fragmentsLength));
        _fragmentsLength += data.Length;
    }

    private void FastUpdate(int code, ReadOnlySpan<byte> data)
    {
        var reader = new WireReader(data);
        switch (code)
        {
            case 0:
                Orders(data, true);
                break;
            case 4:
                // Surface commands: never announced, so a server sending them is in error. The picture would be
                // wrong from here on; say so once in the log.
                if (!_warnedOrders)
                {
                    _warnedOrders = true;
                    _log?.Invoke("the server sends drawing commands this client did not ask for (update code " + code + ")");
                }

                break;
            case 1:
            case 2:
                Update(data);
                break;
            case 5:
                _handler.CursorHidden();
                break;
            case 6:
                _handler.CursorDefault();
                break;
            case 9:
                ColorPointer(ref reader, 24, false);
                break;
            case 10:
                CachedPointerUpdate(reader.U16());
                break;
            case 11:
                ColorPointer(ref reader, reader.U16(), false);
                break;
            case 12:
                ColorPointer(ref reader, reader.U16(), true);
                break;
        }
    }

    // A graphics update in the form both framings share: its kind first.
    private void Update(ReadOnlySpan<byte> data)
    {
        var reader = new WireReader(data);
        switch (reader.U16())
        {
            case 0:
                Orders(reader.Rest(), false);
                break;
            case 1:
                Bitmaps(ref reader);
                break;
            case 2:
                // A palette for eight-bit colour: red, green, blue per entry.
                reader.U16();
                var colors = (int)Math.Min(reader.U32(), 256u);
                for (var i = 0; i < colors; i++)
                {
                    var entry = reader.Bytes(3);
                    _palette[i] = 0xFF000000u | ((uint)entry[0] << 16) | ((uint)entry[1] << 8) | entry[2];
                }

                break;
        }
    }

    // Drawing orders in either framing: fast-path starts with the number of orders, the slow-path update has
    // padding around it.
    private void Orders(ReadOnlySpan<byte> data, bool fastPath)
    {
        if (_orders is null)
        {
            // Orders before the first activation: there are no caches they could refer to.
            return;
        }

        bool drawn;
        DrawnArea area;
        lock (FrameLock)
        {
            try
            {
                drawn = fastPath
                    ? _orders.DecodeFastPath(data, Framebuffer, Width, Height, _palette, out area)
                    : _orders.DecodeSlowPath(data, Framebuffer, Width, Height, _palette, out area);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The decoder never throws on what the server sends; should it trip over its own feet, the
                // picture suffers, not the session.
                _log?.Invoke("drawing orders failed: " + exception.Message);
                return;
            }
        }

        if (Tracing)
        {
            Trace("orders: " + _orders.TakeSummary());
        }

        if (drawn)
        {
            _handler.Updated(area.X, area.Y, area.Width, area.Height);
        }
    }

    private void Bitmaps(ref WireReader reader)
    {
        var count = reader.U16();
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = -1;
        var bottom = -1;
        lock (FrameLock)
        {
            for (var i = 0; i < count; i++)
            {
                int destLeft = reader.U16();
                int destTop = reader.U16();
                int destRight = reader.U16();
                int destBottom = reader.U16();
                int width = reader.U16();
                int height = reader.U16();
                int bitsPerPixel = reader.U16();
                var flags = reader.U16();
                var bits = reader.Bytes(reader.U16());
                var compressed = (flags & 0x0001) != 0;
                var ok = BitmapDecoder.Decode(
                    bits, bitsPerPixel, compressed, compressed && (flags & 0x0400) == 0, width, height,
                    Framebuffer, Width, Height, destLeft, destTop, destRight, destBottom, _palette);
                if (!ok)
                {
                    _log?.Invoke($"a bitmap of {width}x{height} at {bitsPerPixel} bits could not be decoded");
                }

                left = Math.Min(left, destLeft);
                top = Math.Min(top, destTop);
                right = Math.Max(right, destRight);
                bottom = Math.Max(bottom, destBottom);
            }
        }

        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Min(right, Width - 1);
        bottom = Math.Min(bottom, Height - 1);
        if (right >= left && bottom >= top)
        {
            _handler.Updated(left, top, right - left + 1, bottom - top + 1);
        }
    }

    // ---- pointers ------------------------------------------------------------------------------------------

    private void SlowPointer(ReadOnlySpan<byte> data)
    {
        var reader = new WireReader(data);
        var type = reader.U16();
        reader.U16();
        switch (type)
        {
            case 1:
                if (reader.U32() == 0)
                {
                    _handler.CursorHidden();
                }
                else
                {
                    _handler.CursorDefault();
                }

                break;
            case 6:
                ColorPointer(ref reader, 24, false);
                break;
            case 7:
                CachedPointerUpdate(reader.U16());
                break;
            case 8:
                ColorPointer(ref reader, reader.U16(), false);
                break;
        }
    }

    private void ColorPointer(ref WireReader reader, int bitsPerPixel, bool large)
    {
        var index = reader.U16();
        int hotX = reader.U16();
        int hotY = reader.U16();
        int width = reader.U16();
        int height = reader.U16();
        var andLength = large ? (int)Math.Min(reader.U32(), int.MaxValue) : reader.U16();
        var xorLength = large ? (int)Math.Min(reader.U32(), int.MaxValue) : reader.U16();
        var xor = reader.Bytes(xorLength);
        var and = reader.Bytes(andLength);
        if (width is < 1 or > 384 || height is < 1 or > 384)
        {
            return;
        }

        var rgba = PointerDecoder.Decode(bitsPerPixel, width, height, xor, and, _palette);
        if (rgba is null)
        {
            _log?.Invoke($"a pointer of {width}x{height} at {bitsPerPixel} bits could not be decoded");
            return;
        }

        var pointer = new CachedPointer(Math.Min(hotX, width - 1), Math.Min(hotY, height - 1), width, height, rgba);
        if (index < _pointers.Length)
        {
            _pointers[index] = pointer;
        }

        _handler.Cursor(pointer.HotX, pointer.HotY, width, height, rgba);
    }

    private void CachedPointerUpdate(int index)
    {
        if (index < _pointers.Length && _pointers[index] is { } pointer)
        {
            _handler.Cursor(pointer.HotX, pointer.HotY, pointer.Width, pointer.Height, pointer.Rgba);
        }
    }
}
