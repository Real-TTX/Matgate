using System.Buffers.Binary;
using System.Diagnostics;

namespace Matgate.Display.Vnc;

// What the server sends, as the read loop takes it: a buffer in front of the socket, so that the many small
// fields of the protocol do not cost a system call each. Every way the line can end comes out of here as a
// VncException - a decoder never sees anything else.
internal sealed class VncReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _start;
    private int _end;

    public VncReader(Stream stream, int bufferSize = 64 * 1024)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    // The first byte of the next message; false when the server closed the connection where a message would
    // have begun - the one place where that is no error.
    public bool TryReadByte(out byte value)
    {
        if (_start == _end)
        {
            _start = 0;
            _end = ReadStream(_buffer);
            if (_end == 0)
            {
                value = 0;
                return false;
            }
        }

        value = _buffer[_start++];
        return true;
    }

    public byte ReadByte()
    {
        if (_start == _end)
        {
            Fill(1);
        }

        return _buffer[_start++];
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    // The next bytes without copying them; they stay valid until the next read. For pieces of a known small
    // size only - never for a length the server named.
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

    public void ReadExactly(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var read = ReadSome(destination);
            destination = destination[read..];
        }
    }

    // Whatever is there, at least one byte.
    public int ReadSome(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        if (_start == _end)
        {
            if (destination.Length >= _buffer.Length)
            {
                // A large piece (raw pixels) goes straight to where it belongs.
                var direct = ReadStream(destination);
                return direct > 0 ? direct : throw Truncated();
            }

            Fill(1);
        }

        var count = Math.Min(destination.Length, _end - _start);
        _buffer.AsSpan(_start, count).CopyTo(destination);
        _start += count;
        return count;
    }

    public void Skip(long count)
    {
        while (count > 0)
        {
            if (_start == _end)
            {
                Fill(1);
            }

            var step = (int)Math.Min(count, _end - _start);
            _start += step;
            count -= step;
        }
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
            var read = ReadStream(_buffer.AsSpan(_end));
            if (read == 0)
            {
                throw Truncated();
            }

            _end += read;
        }
    }

    // The time spent in reads of the line so far, as Stopwatch ticks - waiting for the server, mostly. What a
    // message took minus this is what unpacking it took.
    public long Waited { get; private set; }

    // Bytes that came from the server.
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    private long _bytesRead;

    private int ReadStream(Span<byte> destination)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var read = _stream.Read(destination);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // InvalidOperationException covers a stream that was closed under the read (ObjectDisposedException).
            throw new VncException("The connection to the VNC server was lost.", exception);
        }
        finally
        {
            Waited += Stopwatch.GetTimestamp() - started;
        }
    }

    private static VncException Truncated() => new("The VNC server closed the connection in the middle of a message.");
}
