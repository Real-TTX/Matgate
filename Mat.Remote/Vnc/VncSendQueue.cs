using System.Buffers.Binary;

namespace Matgate.Display.Vnc;

// Everything Matgate sends to the server once the handshake is over. Messages come from several threads - the
// pointer and the keyboard of the viewer, the read loop with its answers - and none of them may wait for a
// server that is slow to take them. So a message is only put behind the others here, under a short lock, and
// one loop writes what has gathered. The order of the messages is the order in which they were queued.
internal sealed class VncSendQueue
{
    // A server that has let this much pile up is not taking anything any more; the client gives the
    // connection up rather than let the queue grow for as long as somebody moves the mouse. Large enough for
    // the largest clipboard text (16 MB, twice over when every line break becomes two bytes).
    private const int Limit = 48 * 1024 * 1024;
    private const int Idle = 16 * 1024;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private byte[] _pending = new byte[Idle];
    private int _length;
    private int _lastPointer = -1;
    private bool _open;

    // The queue ran over its limit: the server is gone as far as input is concerned.
    public bool Overflowed { get; private set; }

    public void Open()
    {
        lock (_gate)
        {
            _open = true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _open = false;
            _length = 0;
            _lastPointer = -1;
        }
    }

    // False when the message was not taken: nothing is connected, or the queue is full.
    public bool Enqueue(ReadOnlySpan<byte> message)
    {
        bool wake;
        lock (_gate)
        {
            if (!Reserve(message.Length))
            {
                return false;
            }

            wake = _length == 0;
            message.CopyTo(_pending.AsSpan(_length));
            _length += message.Length;
            _lastPointer = -1;
        }

        if (wake)
        {
            _wake.Release();
        }

        return true;
    }

    // A pointer event (message 5). While the one before it has not left yet and the buttons are the same, the
    // new position takes its place: a server that is slow gets the pointer where it is now, not a trail of
    // where it has been.
    public bool EnqueuePointer(byte buttons, ushort x, ushort y)
    {
        bool wake;
        lock (_gate)
        {
            if (_lastPointer >= 0 && _pending[_lastPointer + 1] == buttons)
            {
                BinaryPrimitives.WriteUInt16BigEndian(_pending.AsSpan(_lastPointer + 2), x);
                BinaryPrimitives.WriteUInt16BigEndian(_pending.AsSpan(_lastPointer + 4), y);
                return true;
            }

            if (!Reserve(6))
            {
                return false;
            }

            wake = _length == 0;
            var message = _pending.AsSpan(_length, 6);
            message[0] = 5;
            message[1] = buttons;
            BinaryPrimitives.WriteUInt16BigEndian(message[2..], x);
            BinaryPrimitives.WriteUInt16BigEndian(message[4..], y);
            _lastPointer = _length;
            _length += 6;
        }

        if (wake)
        {
            _wake.Release();
        }

        return true;
    }

    // The one writer. Ends with the token, or with the exception of a write that failed.
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        var sending = new byte[Idle];
        while (true)
        {
            await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            int length;
            lock (_gate)
            {
                // The two buffers change places: what has gathered is written, the other one gathers meanwhile.
                length = _length;
                (sending, _pending) = (_pending, sending);
                _length = 0;
                _lastPointer = -1;
            }

            if (length > 0)
            {
                await stream.WriteAsync(sending.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (sending.Length > Idle * 16)
            {
                // A clipboard text made it large; it need not stay that way.
                sending = new byte[Idle];
            }
        }
    }

    private bool Reserve(int count)
    {
        if (!_open)
        {
            return false;
        }

        var needed = (long)_length + count;
        if (needed > Limit)
        {
            Overflowed = true;
            return false;
        }

        if (needed > _pending.Length)
        {
            var larger = new byte[(int)Math.Min(Limit, Math.Max(needed, _pending.Length * 2L))];
            _pending.AsSpan(0, _length).CopyTo(larger);
            _pending = larger;
        }

        return true;
    }
}
