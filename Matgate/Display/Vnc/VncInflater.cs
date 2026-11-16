using System.IO.Compression;

namespace Matgate.Display.Vnc;

// One of the zlib streams of a VNC connection. Such a stream is opened once and lives as long as the
// connection: every rectangle brings the next piece of it, and what the piece unpacks to depends on all the
// pieces before. So the unpacker is kept, and is fed through a stream of its own that hands it exactly the
// compressed bytes of the rectangle at hand - no byte of the message behind it.
internal sealed class VncInflater : IDisposable
{
    private readonly Feeder _feeder = new();
    private readonly byte[] _buffer;
    private ZLibStream? _zlib;
    private int _start;
    private int _end;

    // The buffer has to hold the largest piece a decoder takes at once: a row of 16384 three-byte pixels.
    public VncInflater(int bufferSize = 64 * 1024)
    {
        _buffer = new byte[bufferSize];
    }

    // A rectangle begins: the next `length` bytes from the server belong to this stream.
    public void Begin(VncReader source, long length)
    {
        _feeder.Begin(source, length);
        _start = 0;
        _end = 0;
    }

    // The rectangle is drawn. The unpacker takes its input in blocks of its own size, so the last bytes of
    // the piece - the mark that ends it - can still be on the line although the decoder has all its pixels.
    // They are part of the stream and have to go through the unpacker before the next message is read; and
    // should a server have packed more into the piece than the rectangle needed, that is thrown away here
    // rather than taken for the beginning of the next rectangle.
    public void End()
    {
        _start = 0;
        _end = 0;
        try
        {
            while (Inflate(_buffer) > 0)
            {
            }
        }
        catch (VncException exception) when (exception.InnerException is InvalidDataException && _feeder.Remaining == 0)
        {
            // A runtime that is set to be strict about streams (System.IO.Compression.UseStrictValidation)
            // calls it an error when a stream has no more input - which is the normal state of this one
            // between two rectangles. Nothing is lost: what is really wrong with the data shows again at
            // the next rectangle.
        }

        if (_feeder.Remaining > 0)
        {
            // Nothing came out although there is input left: the stream has reached its end mark.
            throw new VncException("The VNC server ended a compressed stream that has to last for the whole connection.");
        }
    }

    // The server starts this stream anew (Tight says so in a rectangle's first byte).
    public void Reset()
    {
        _zlib?.Dispose();
        _zlib = null;
        _start = 0;
        _end = 0;
    }

    public byte ReadByte()
    {
        if (_start == _end)
        {
            Fill(1);
        }

        return _buffer[_start++];
    }

    // The next unpacked bytes, valid until the next read.
    public ReadOnlySpan<byte> Take(int count)
    {
        if (_end - _start < count)
        {
            Fill(count);
        }

        var bytes = _buffer.AsSpan(_start, count);
        _start += count;
        return bytes;
    }

    public void Dispose()
    {
        _zlib?.Dispose();
        _zlib = null;
    }

    private void Fill(int count)
    {
        if (count > _buffer.Length)
        {
            throw new VncException("The VNC server sent a piece of data larger than the protocol allows.");
        }

        var kept = _end - _start;
        if (_start > 0 && kept > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, kept);
        }

        _start = 0;
        _end = kept;
        while (_end < count)
        {
            var read = Inflate(_buffer.AsSpan(_end));
            if (read == 0)
            {
                throw new VncException("The VNC server sent less compressed data than the picture needs.");
            }

            _end += read;
        }
    }

    private int Inflate(Span<byte> destination)
    {
        _zlib ??= new ZLibStream(_feeder, CompressionMode.Decompress, leaveOpen: true);
        try
        {
            return _zlib.Read(destination);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new VncException("The VNC server sent compressed data that cannot be unpacked.", exception);
        }
    }

    // What the unpacker reads from: the compressed bytes of the current rectangle, taken from the line as the
    // unpacker asks for them, and nothing beyond.
    private sealed class Feeder : Stream
    {
        private VncReader? _source;

        // Compressed bytes of the current rectangle that are still on the line.
        public long Remaining { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Begin(VncReader source, long length)
        {
            _source = source;
            Remaining = length;
        }

        public override int Read(Span<byte> buffer)
        {
            if (Remaining == 0 || buffer.IsEmpty)
            {
                return 0;
            }

            var read = _source!.ReadSome(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            Remaining -= read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
