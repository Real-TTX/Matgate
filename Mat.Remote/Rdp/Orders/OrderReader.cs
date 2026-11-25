using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Orders;

// Reads the bytes of an orders update. Everything in them was written by the server, so no read trusts a length:
// reading past the end sets Failed and yields zeros from then on. A parser reads a whole order and looks at Failed
// once, before anything of the order is drawn or kept - cheaper than an exception per field, and nothing can escape.
internal ref struct OrderReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private bool _failed;

    public OrderReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _failed = false;
    }

    public readonly bool Failed => _failed;

    public readonly int Remaining => _data.Length - _position;

    private bool Take(int count)
    {
        if (_failed || count < 0 || count > _data.Length - _position)
        {
            _failed = true;
            _position = _data.Length;
            return false;
        }

        return true;
    }

    public byte U8()
    {
        if (!Take(1))
        {
            return 0;
        }

        return _data[_position++];
    }

    public sbyte I8() => (sbyte)U8();

    public ushort U16()
    {
        if (!Take(2))
        {
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        _position += 2;
        return value;
    }

    public short I16() => (short)U16();

    public uint U32()
    {
        if (!Take(4))
        {
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    // Three bytes as one little-endian value: a TS_COLOR as it is on the wire.
    public int U24()
    {
        if (!Take(3))
        {
            return 0;
        }

        var value = _data[_position] | (_data[_position + 1] << 8) | (_data[_position + 2] << 16);
        _position += 3;
        return value;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (!Take(count))
        {
            return default;
        }

        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    public void Skip(int count)
    {
        if (Take(count))
        {
            _position += count;
        }
    }

    // TWO_BYTE_UNSIGNED_ENCODING (MS-RDPEGDI 2.2.2.2.1.2.1.2): 7 bits, or 15 when the top bit of the first byte is
    // set; the first byte holds the high bits.
    public int TwoByteUnsigned()
    {
        var first = U8();
        return (first & 0x80) == 0 ? first : ((first & 0x7F) << 8) | U8();
    }

    // FOUR_BYTE_UNSIGNED_ENCODING (2.2.2.2.1.2.1.4): the top two bits of the first byte count the bytes that follow
    // (0 to 3); together at most 30 bits, big-endian.
    public int FourByteUnsigned()
    {
        var first = U8();
        var value = first & 0x3F;
        for (var count = first >> 6; count > 0; count--)
        {
            value = (value << 8) | U8();
        }

        return value;
    }

    // The packed signed values of DELTA_RECTS_FIELD (2.2.2.2.1.1.1.5): 7 bits, or 15 when the top bit of the first
    // byte is set; either way the highest remaining bit is the sign.
    public int DeltaValue()
    {
        var first = U8();
        if ((first & 0x80) == 0)
        {
            return (first & 0x40) != 0 ? (first & 0x7F) - 0x80 : first;
        }

        var value = ((first & 0x7F) << 8) | U8();
        return (value & 0x4000) != 0 ? value - 0x8000 : value;
    }
}
