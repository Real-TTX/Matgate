using System.Runtime.InteropServices;

namespace Matgate.Display.Video;

// A picture in I420: a luma plane of the full size and two chroma planes of half the width and half the height.
// The planes live in native memory, so the encoder reads them where they are - nothing is pinned and nothing is
// copied on the managed side. One frame belongs to one screen: the converter writes what changed into it and
// the encoder is given the same frame again and again.
public sealed unsafe class I420Frame : IDisposable
{
    // Rows start on a cache line, and a chroma row is exactly half a luma row.
    private const int RowAlignment = 64;

    private byte* _memory;

    public I420Frame(int width, int height)
    {
        if (width < 2 || (width & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width of a picture is even and at least 2.");
        }

        if (height < 2 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height of a picture is even and at least 2.");
        }

        Width = width;
        Height = height;
        StrideY = (width + RowAlignment - 1) & ~(RowAlignment - 1);
        StrideU = StrideY / 2;
        StrideV = StrideY / 2;
        var lumaSize = (nuint)StrideY * (nuint)height;
        var chromaSize = (nuint)StrideU * (nuint)(height / 2);
        _memory = (byte*)NativeMemory.AlignedAlloc(lumaSize + 2 * chromaSize, RowAlignment);
        PointerY = _memory;
        PointerU = _memory + lumaSize;
        PointerV = PointerU + chromaSize;

        // Black, so that a part nobody has converted yet is not shown as green.
        NativeMemory.Fill(PointerY, lumaSize, 16);
        NativeMemory.Fill(PointerU, 2 * chromaSize, 128);
        GC.AddMemoryPressure((long)(lumaSize + 2 * chromaSize));
    }

    ~I420Frame()
    {
        Release();
    }

    public int Width { get; }

    public int Height { get; }

    // Bytes from one row to the next; at least the width of the plane.
    public int StrideY { get; }

    public int StrideU { get; }

    public int StrideV { get; }

    public Span<byte> Y => new(Alive(PointerY), StrideY * Height);

    public Span<byte> U => new(Alive(PointerU), StrideU * (Height / 2));

    public Span<byte> V => new(Alive(PointerV), StrideV * (Height / 2));

    // Counts every change of the picture. The encoder compares it with the count it saw last: a frame that
    // comes again unchanged is the caller asking for the same picture, only sharper.
    public long Version { get; private set; }

    // How many pixels all changes so far covered, added up - with the version, the encoder derives from it how
    // much of the picture is new (a blinking cursor or the whole screen).
    public long ChangedPixels { get; private set; }

    internal byte* PointerY { get; }

    internal byte* PointerU { get; }

    internal byte* PointerV { get; }

    internal bool IsDisposed => _memory == null;

    // For a caller that writes into the planes itself: the whole picture counts as new.
    public void MarkChanged() => MarkChanged(Width, Height);

    internal void MarkChanged(int width, int height)
    {
        Version++;
        ChangedPixels += (long)width * height;
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private byte* Alive(byte* plane)
    {
        ObjectDisposedException.ThrowIf(_memory == null, this);
        return plane;
    }

    private void Release()
    {
        if (_memory == null)
        {
            return;
        }

        NativeMemory.AlignedFree(_memory);
        _memory = null;
        GC.RemoveMemoryPressure((long)StrideY * Height + 2L * StrideU * (Height / 2));
    }
}
