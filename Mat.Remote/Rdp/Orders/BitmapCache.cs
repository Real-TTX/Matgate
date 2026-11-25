using System.Runtime.InteropServices;
using Matgate.Display.Rdp.Graphics;

namespace Matgate.Display.Rdp.Orders;

// One cached bitmap, top-down. Pixels are framebuffer pixels (Bgrx.Store, opaque) - or, for 8-bit bitmaps,
// palette indices in the low byte: which colour table applies is only known when a MemBlt draws it.
internal sealed class CachedBitmap(int width, int height, uint[] pixels, bool indexed)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public uint[] Pixels { get; } = pixels;

    public bool Indexed { get; } = indexed;
}

// The bitmap caches of MS-RDPEGDI 3.1.1.1.1, in the layout this client announces in its Revision 2 Bitmap Cache
// Capability Set: three caches for tiles of 16 x 16, 32 x 32 and 64 x 64 pixels. A bitmap larger than its cache's
// tile, an id or an index beyond what was announced, is refused - so the memory a server can make this client
// hold is bounded: (600 * 256 + 600 * 1024 + 1024 * 4096) pixels of 4 bytes, about 20 MB, plus one waiting-list
// entry per cache.
internal sealed class BitmapCache
{
    // Entries per cache, as announced. The specification allows up to 600 in the first two.
    public static readonly int[] Entries = [600, 600, 1024];

    // The tile size of each cache in pixels.
    public static readonly int[] TilePixels = [16 * 16, 32 * 32, 64 * 64];

    // BITMAPCACHE_WAITING_LIST_INDEX: "the last entry" of a cache, where a bitmap goes that is not to be kept
    // (CBR2_DO_NOT_CACHE). Each cache has it in addition to its announced entries.
    public const int WaitingListIndex = 0x7FFF;

    // Decoding an 8-bit bitmap through this palette leaves the index itself in the low byte of each pixel.
    private static readonly uint[] IdentityPalette = CreateIdentityPalette();

    private readonly CachedBitmap?[][] _caches = [new CachedBitmap?[Entries[0] + 1], new CachedBitmap?[Entries[1] + 1], new CachedBitmap?[Entries[2] + 1]];

    public void Clear()
    {
        foreach (var cache in _caches)
        {
            Array.Clear(cache);
        }
    }

    private static int Slot(int cacheId, int index)
    {
        if (cacheId < 0 || cacheId >= Entries.Length)
        {
            return -1;
        }

        if (index == WaitingListIndex)
        {
            return Entries[cacheId];
        }

        return index >= 0 && index < Entries[cacheId] ? index : -1;
    }

    public CachedBitmap? Get(int cacheId, int index)
    {
        var slot = Slot(cacheId, index);
        return slot < 0 ? null : _caches[cacheId][slot];
    }

    // Decodes a bitmap of a Cache Bitmap order into the cache. data: bitmapComprHdr (when hasCompressionHeader)
    // and bitmapDataStream, as in the order. Returns null when it was stored, otherwise why not.
    public string? Store(int cacheId, int index, int width, int height, int bitsPerPixel, bool compressed,
        bool hasCompressionHeader, ReadOnlySpan<byte> data)
    {
        var slot = Slot(cacheId, index);
        if (slot < 0)
        {
            return "a cached bitmap for a cache or an entry this client did not announce";
        }

        if (width <= 0 || height <= 0 || (long)width * height > TilePixels[cacheId])
        {
            return "a cached bitmap larger than its cache's tiles";
        }

        if (bitsPerPixel is not (8 or 15 or 16 or 24 or 32))
        {
            return "a cached bitmap of an unknown colour depth";
        }

        // A fresh array each time: the old one may be the same size, but a failed decode must not leave half of
        // a new picture in an old entry that a MemBlt might still draw. The decoder clips nothing here: the
        // rectangle is the bitmap itself.
        var pixels = new uint[width * height];

        // Opaque black where a broken bitmap leaves gaps: the framebuffer's X byte stays 0xFF whatever is drawn.
        Array.Fill(pixels, Bgrx.Store(Bgrx.Opaque));
        var ok = BitmapDecoder.Decode(data, bitsPerPixel, compressed, hasCompressionHeader, width, height,
            MemoryMarshal.AsBytes(pixels.AsSpan()), width, height, 0, 0, width - 1, height - 1, IdentityPalette);
        if (!ok)
        {
            // What did decode stays: rows of a truncated bitmap are better than the bitmap that was there.
            _caches[cacheId][slot] = new CachedBitmap(width, height, pixels, bitsPerPixel == 8);
            return "a cached bitmap that could not be decoded";
        }

        _caches[cacheId][slot] = new CachedBitmap(width, height, pixels, bitsPerPixel == 8);
        return null;
    }

    private static uint[] CreateIdentityPalette()
    {
        var palette = new uint[256];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = (uint)i;
        }

        return palette;
    }
}
