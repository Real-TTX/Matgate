namespace Matgate.Display.Rdp.Orders;

// Secondary drawing orders (MS-RDPEGDI 2.2.2.2.1.2), which fill the caches, and alternate secondary drawing orders
// (2.2.2.2.1.3). A secondary order carries its length, so one that is broken or of an unknown kind costs only
// itself; an alternate secondary order does not, so only the kinds whose size this decoder knows are skipped.
internal sealed partial class OrderDecoder
{
    // TS_CACHE_* (orderType of the secondary order header).
    private const int CacheBitmapUncompressed = 0x00;
    private const int CacheColorTable = 0x01;
    private const int CacheBitmapCompressed = 0x02;
    private const int CacheBitmapUncompressedRev2 = 0x04;
    private const int CacheBitmapCompressedRev2 = 0x05;

    // extraFlags of Cache Bitmap revision 1.
    private const int NoBitmapCompressionHeader = 0x0400;

    // flags of Cache Bitmap revision 2 (the top nine bits of extraFlags).
    private const int HeightSameAsWidth = 0x01;
    private const int PersistentKeyPresent = 0x02;
    private const int NoBitmapCompressionHeaderRev2 = 0x08;
    private const int DoNotCache = 0x10;

    // TS_ALTSEC_*.
    private const int SwitchSurface = 0x00;
    private const int CreateOffscreenBitmap = 0x01;
    private const int WindowOrder = 0x0B;
    private const int FrameMarker = 0x0D;

    // The screen, as the target of Switch Surface.
    private const int ScreenSurface = 0xFFFF;

    private bool SecondaryOrder(ref OrderReader reader)
    {
        // orderLength is 13 less than the whole order; 6 of those 13 are this header.
        var length = reader.I16() + 7;
        var extraFlags = reader.U16();
        var type = reader.U8();
        if (reader.Failed || length < 0 || length > reader.Remaining)
        {
            Report("secondary length", "a secondary order longer than its update; the rest of the update is dropped");
            return false;
        }

        var body = new OrderReader(reader.Bytes(length));
        Count(32 + (type & 0x0F));
        switch (type)
        {
            case CacheBitmapUncompressed:
            case CacheBitmapCompressed:
                CacheBitmap(ref body, extraFlags, type == CacheBitmapCompressed);
                break;
            case CacheBitmapUncompressedRev2:
            case CacheBitmapCompressedRev2:
                CacheBitmapRev2(ref body, extraFlags, type == CacheBitmapCompressedRev2);
                break;
            case CacheColorTable:
                ColorTable(ref body);
                break;
            case CacheGlyph:
                CacheGlyphs(ref body, extraFlags);
                break;
            default:
                // Brushes, revision 3 bitmaps: never announced, so a server has no reason to send them.
                Report("secondary " + type, $"a secondary order of type 0x{type:X2}, which this client never announced; it is skipped");
                break;
        }

        return true;
    }

    // Cache Bitmap - Revision 1 (2.2.2.2.1.2.2): cacheId, pad, bitmapWidth, bitmapHeight, bitmapBitsPerPel,
    // bitmapLength, cacheIndex, then the bitmap (with an 8-byte compression header unless the flags say otherwise).
    private void CacheBitmap(ref OrderReader body, int extraFlags, bool compressed)
    {
        int cacheId = body.U8();
        body.U8();
        int width = body.U8();
        int height = body.U8();
        int bitsPerPixel = body.U8();
        int length = body.U16();
        int index = body.U16();
        var data = body.Bytes(length);
        if (body.Failed)
        {
            Report("cache bitmap short", "a Cache Bitmap order that ends early; it is skipped");
            return;
        }

        var header = compressed && (extraFlags & NoBitmapCompressionHeader) == 0;
        Store(cacheId, index, width, height, WireDepth(bitsPerPixel), compressed, header, data);
    }

    // Cache Bitmap - Revision 2 (2.2.2.2.1.2.3): cacheId, the colour depth and flags in extraFlags; the persistent
    // key (ignored - this client keeps nothing across sessions), bitmapWidth, bitmapHeight, bitmapLength and
    // cacheIndex in their compact encodings, then the bitmap.
    private void CacheBitmapRev2(ref OrderReader body, int extraFlags, bool compressed)
    {
        var cacheId = extraFlags & 0x07;
        var depthId = (extraFlags >> 3) & 0x0F;
        var flags = extraFlags >> 7;
        if ((flags & PersistentKeyPresent) != 0)
        {
            body.Skip(8);
        }

        var width = body.TwoByteUnsigned();
        var height = (flags & HeightSameAsWidth) != 0 ? width : body.TwoByteUnsigned();
        var length = body.FourByteUnsigned();
        var index = body.TwoByteUnsigned();
        var data = body.Bytes(length);
        if (body.Failed)
        {
            Report("cache bitmap 2 short", "a Cache Bitmap (revision 2) order that ends early; it is skipped");
            return;
        }

        if ((flags & DoNotCache) != 0)
        {
            index = BitmapCache.WaitingListIndex;
        }

        var bitsPerPixel = depthId switch
        {
            3 => 8,
            4 => 16,
            5 => 24,
            6 => 32,
            _ => 0,
        };
        var header = compressed && (flags & NoBitmapCompressionHeaderRev2) == 0;
        Store(cacheId, index, width, height, WireDepth(bitsPerPixel), compressed, header, data);
    }

    // A bitmap's colour depth as the decoders take it: "16" in a cache order of a 15-bit session means 15.
    private int WireDepth(int bitsPerPixel) => bitsPerPixel == 16 && _colorDepth == 15 ? 15 : bitsPerPixel;

    private void Store(int cacheId, int index, int width, int height, int bitsPerPixel, bool compressed, bool header, ReadOnlySpan<byte> data)
    {
        var problem = _cache.Store(cacheId, index, width, height, bitsPerPixel, compressed, header, data);
        if (problem is not null)
        {
            Report("store " + problem, $"{problem} (cache {cacheId}, entry {index}, {width}x{height} at {bitsPerPixel} bits)");
        }
    }

    // Cache Color Table (2.2.2.2.1.2.4): cacheIndex, numberColors (always 256), then the colours as blue, green,
    // red and a pad byte each.
    private void ColorTable(ref OrderReader body)
    {
        int index = body.U8();
        int count = body.U16();
        var entries = body.Bytes(count * 4);
        if (body.Failed || count != 256 || index >= ColorTables)
        {
            Report("color table", "a Cache Color Table order that is not one of 256 colours in one of six tables; it is skipped");
            return;
        }

        var table = new uint[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = Graphics.Bgrx.Opaque | ((uint)entries[(i * 4) + 2] << 16) | ((uint)entries[(i * 4) + 1] << 8) | entries[i * 4];
        }

        _colorTables[index] = table;
    }

    // An alternate secondary order: the order type in the top six bits of the control byte, then a body whose size
    // depends on the type.
    private bool AlternateSecondary(ref OrderReader reader, int control)
    {
        var type = control >> 2;
        Count(48 + type);
        switch (type)
        {
            case FrameMarker:
                // TS_FRAME_START or TS_FRAME_END: each order is drawn as it comes, so a frame needs no attention.
                reader.U32();
                break;
            case SwitchSurface:
                _offscreen = reader.U16() != ScreenSurface;
                if (_offscreen)
                {
                    Report("switch surface", "drawing switched to an offscreen surface, which this client never announced; drawing is skipped until it switches back");
                }

                break;
            case CreateOffscreenBitmap:
            {
                // flags (the id and whether a delete list follows), cx, cy, the delete list.
                var flags = reader.U16();
                reader.Skip(4);
                if ((flags & 0x8000) != 0)
                {
                    reader.Skip(reader.U16() * 2);
                }

                break;
            }

            case WindowOrder:
            {
                // MS-RDPERP: orderSize counts the whole order, this control byte included.
                var size = reader.U16();
                if (size < 3)
                {
                    Report("window order", "a window order of impossible size; the rest of the update is dropped");
                    return false;
                }

                reader.Skip(size - 3);
                break;
            }

            default:
                Report("altsec " + type, $"an alternate secondary order of type 0x{type:X2}, which this client cannot skip; the rest of its update is dropped");
                return false;
        }

        if (reader.Failed)
        {
            Report("altsec short", "an alternate secondary order that ends early; the rest of the update is dropped");
            return false;
        }

        return true;
    }
}
