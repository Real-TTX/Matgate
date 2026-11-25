namespace Matgate.Display.Rdp.Orders;

// One cached glyph: a one-bit bitmap, rows top-down, each (Width + 7) / 8 bytes, the leftmost pixel in the high
// bit; (X, Y) is where the glyph's origin lies within it, so it is drawn at (x + X, y + Y).
internal sealed class Glyph(int x, int y, int width, int height, byte[] bits)
{
    public int X { get; } = x;

    public int Y { get; } = y;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Bits { get; } = bits;

    public int Stride => (Width + 7) / 8;
}

// The glyph and fragment caches of MS-RDPEGDI 3.1.1.1.2. This client announces none (Glyph Cache Capability Set,
// support level none), but xrdp draws the text of its own windows (the login dialog, the connection log) with
// glyph orders whatever the client says - and with cache ids up to 11, beyond the ten the specification knows.
// Fixed limits: 16 caches of 256 glyphs of at most 2048 bytes each (the specification's largest cell), 256
// fragments of at most 255 bytes - about 8 MB at the very worst.
internal sealed class GlyphCache
{
    public const int Caches = 16;
    public const int Entries = 256;
    public const int MaxGlyphBytes = 2048;
    public const int Fragments = 256;

    private readonly Glyph?[][] _glyphs = new Glyph?[Caches][];
    private readonly byte[]?[] _fragments = new byte[Fragments][];

    public void Clear()
    {
        Array.Clear(_glyphs);
        Array.Clear(_fragments);
    }

    public Glyph? Get(int cacheId, int index)
    {
        if (cacheId is < 0 or >= Caches || index is < 0 or >= Entries)
        {
            return null;
        }

        return _glyphs[cacheId]?[index];
    }

    // False when the id, index or size is beyond the limits.
    public bool Put(int cacheId, int index, Glyph glyph)
    {
        if (cacheId is < 0 or >= Caches || index is < 0 or >= Entries)
        {
            return false;
        }

        (_glyphs[cacheId] ??= new Glyph?[Entries])[index] = glyph;
        return true;
    }

    public byte[]? GetFragment(int index) => _fragments[index & 0xFF];

    public void PutFragment(int index, ReadOnlySpan<byte> fragment) => _fragments[index & 0xFF] = fragment.ToArray();
}
