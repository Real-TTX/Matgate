using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Channels;

// Puts the chunks of one static channel together again (MS-RDPBCGR 3.1.5.2.2.1). One reassembler per channel:
// the chunks of a message arrive in order and nothing of the same channel comes in between.
public sealed class ChannelReassembler
{
    // What one message may come to. The server announces the length with the first chunk; a longer one is
    // refused before a byte of it is kept.
    public const int MaxMessageLength = 64 * 1024 * 1024;

    // What is set aside when a long message starts. The announced length is only a claim until the bytes
    // arrive: the buffer grows with what was really sent.
    private const int FirstReservation = 64 * 1024;

    private readonly int _limit;
    private byte[]? _buffer;
    private int _expected;
    private int _received;

    public ChannelReassembler()
        : this(MaxMessageLength)
    {
    }

    // A channel whose messages are known to be small can be held to less than MaxMessageLength.
    public ChannelReassembler(int maxMessageLength)
    {
        _limit = Math.Clamp(maxMessageLength, 0, MaxMessageLength);
    }

    // Where dropped chunks are reported.
    public Action<string>? Log { get; set; }

    // Feeds one chunk (header included). Returns true and the complete message when the LAST chunk has arrived.
    // The message then belongs to the caller: the reassembler does not touch that memory again.
    public bool Add(ReadOnlySpan<byte> chunk, out ReadOnlyMemory<byte> message)
    {
        message = default;
        if (chunk.Length < ChannelChunks.HeaderLength)
        {
            Drop("a chunk too short for its header");
            return false;
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
        var data = chunk[ChannelChunks.HeaderLength..];

        if ((flags & (ChannelChunks.FlagSuspend | ChannelChunks.FlagResume)) != 0)
        {
            // A signal about the traffic as a whole (a session being shadowed), not a piece of a message.
            return false;
        }

        if ((flags & ChannelChunks.FlagCompressed) != 0)
        {
            Drop("a compressed chunk, which this client never asked for");
            return false;
        }

        if (total > (uint)_limit)
        {
            Drop($"a message of {total} bytes, more than the {_limit} allowed");
            return false;
        }

        var first = (flags & ChannelChunks.FlagFirst) != 0;
        var last = (flags & ChannelChunks.FlagLast) != 0;
        var length = (int)total;

        if (first)
        {
            if (_buffer is not null)
            {
                Drop("a message that was never finished");
            }

            if (last)
            {
                // The usual case: the whole message in one chunk.
                if (data.Length != length)
                {
                    Drop($"a message announced with {length} bytes that carries {data.Length}");
                    return false;
                }

                message = data.ToArray();
                return true;
            }

            if (data.Length > length)
            {
                Drop($"a first chunk of {data.Length} bytes for a message announced with {length}");
                return false;
            }

            _buffer = new byte[Math.Min(length, Math.Max(data.Length, FirstReservation))];
            _expected = length;
            _received = 0;
        }
        else if (_buffer is null)
        {
            if (!last && data.Length == length)
            {
                // No flags and no sequence under way: complete as it is (MS-RDPBCGR 3.1.5.2.2).
                message = data.ToArray();
                return true;
            }

            Drop("a chunk without the first chunk of its message");
            return false;
        }
        else if (length != _expected)
        {
            Drop($"a chunk announcing {length} bytes inside a message of {_expected}");
            return false;
        }

        if (data.Length > _expected - _received)
        {
            Drop($"more data than the {_expected} bytes announced");
            return false;
        }

        if (_received + data.Length > _buffer.Length)
        {
            var grown = new byte[Math.Min(_expected, Math.Max(_buffer.Length * 2L, _received + data.Length))];
            _buffer.AsSpan(0, _received).CopyTo(grown);
            _buffer = grown;
        }

        data.CopyTo(_buffer.AsSpan(_received));
        _received += data.Length;
        if (!last)
        {
            return false;
        }

        if (_received != _expected)
        {
            Drop($"a message announced with {_expected} bytes that ended after {_received}");
            return false;
        }

        // The buffer never grows past the announced length, so a finished message fills it exactly.
        message = _buffer;
        _buffer = null;
        return true;
    }

    // Forgets a message under way (the session ended, or the channel starts over).
    public void Reset()
    {
        _buffer = null;
        _expected = 0;
        _received = 0;
    }

    // Whatever was collected goes with it: after a chunk that does not fit, the rest of its message cannot be
    // trusted either. The next chunk flagged FIRST starts clean.
    private void Drop(string what)
    {
        Reset();
        Guarded.Log(Log, "channel chunks: dropped " + what);
    }
}
