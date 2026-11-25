using System.Buffers.Binary;
using System.Text;

namespace Matgate.Display.Rdp;

// What went wrong, as a sentence a person can read. IsLogonFailure: the server refused the user name or the
// password, so asking for them again makes sense.
public sealed class RdpException : Exception
{
    public RdpException(string message, bool isLogonFailure = false, Exception? inner = null)
        : base(message, inner)
    {
        IsLogonFailure = isLogonFailure;
    }

    public bool IsLogonFailure { get; }
}

// The server's bytes do not hold what the protocol says they hold. Never shown as it is: the session turns
// it into an RdpException.
internal sealed class RdpProtocolException : Exception
{
    public RdpProtocolException(string message)
        : base(message)
    {
    }
}

// Reads a message from the server. Every read checks what is left: a length the server made up must end in
// a clean error, not in an IndexOutOfRangeException somewhere inside.
internal ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Remaining => _data.Length - _position;

    public readonly int Position => _position;

    private readonly void Need(int count)
    {
        if (count < 0 || count > _data.Length - _position)
        {
            throw new RdpProtocolException("A message from the RDP server ended early.");
        }
    }

    public byte U8()
    {
        Need(1);
        return _data[_position++];
    }

    public ushort U16()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        _position += 2;
        return value;
    }

    public ushort U16Be()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data[_position..]);
        _position += 2;
        return value;
    }

    public uint U32()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Need(count);
        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    public void Skip(int count)
    {
        Need(count);
        _position += count;
    }

    public ReadOnlySpan<byte> Rest()
    {
        var slice = _data[_position..];
        _position = _data.Length;
        return slice;
    }

    // BER (the MCS connect response): a length of one byte, or 0x81/0x82 and then one or two bytes.
    public int BerLength()
    {
        var first = U8();
        if ((first & 0x80) == 0)
        {
            return first;
        }

        return (first & 0x7F) switch
        {
            1 => U8(),
            2 => U16Be(),
            _ => throw new RdpProtocolException("A length in the RDP server's answer is not one Matgate can read."),
        };
    }

    // PER (the conference data inside it, and the length of every MCS data unit): one byte, or two with the top bit set.
    public int PerLength()
    {
        var first = U8();
        return (first & 0x80) == 0 ? first : ((first & 0x7F) << 8) | U8();
    }
}

// Builds a message for the server.
internal sealed class WireWriter
{
    private byte[] _buffer;
    private int _length;

    public WireWriter(int capacity = 256)
    {
        _buffer = new byte[capacity];
    }

    public int Length => _length;

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    private Span<byte> Take(int count)
    {
        if (_length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        }

        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public WireWriter U8(int value)
    {
        Take(1)[0] = (byte)value;
        return this;
    }

    public WireWriter U16(int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Take(2), (ushort)value);
        return this;
    }

    public WireWriter U16Be(int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(Take(2), (ushort)value);
        return this;
    }

    public WireWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);
        return this;
    }

    public WireWriter I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
        return this;
    }

    public WireWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Take(bytes.Length));
        return this;
    }

    public WireWriter Zero(int count)
    {
        Take(count).Clear();
        return this;
    }

    // UTF-16, as nearly every string in RDP; `terminate` adds the two zero bytes.
    public WireWriter Utf16(string text, bool terminate)
    {
        var count = Encoding.Unicode.GetByteCount(text);
        Encoding.Unicode.GetBytes(text, Take(count));
        return terminate ? Zero(2) : this;
    }

    // A fixed field of `size` bytes holding a string and, always, its terminator: longer text is cut.
    public WireWriter Utf16Field(string text, int size)
    {
        var field = Take(size);
        field.Clear();
        var units = Math.Min(text.Length, size / 2 - 1);
        Encoding.Unicode.GetBytes(text.AsSpan(0, units), field);
        return this;
    }

    public void SetU16(int at, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(at, 2), (ushort)value);
    }

    public void SetU16Be(int at, int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(at, 2), (ushort)value);
    }

    public byte[] ToArray()
    {
        return _buffer.AsSpan(0, _length).ToArray();
    }
}
