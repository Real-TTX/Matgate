using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Matgate.Display.Rdp.Channels;

// Reads little-endian fields out of a message of the server. Nothing here trusts a length: every read says
// whether the bytes were there, and counts are taken as long so a 32-bit length from the wire cannot wrap.
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public ByteReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Remaining => _data.Length - _position;

    public readonly ReadOnlySpan<byte> Rest => _data[_position..];

    public bool TryU8(out byte value)
    {
        if (Remaining < 1)
        {
            value = 0;
            return false;
        }

        value = _data[_position];
        _position += 1;
        return true;
    }

    public bool TryU16(out ushort value)
    {
        if (Remaining < 2)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        _position += 2;
        return true;
    }

    public bool TryU32(out uint value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return true;
    }

    public bool TryU64(out ulong value)
    {
        if (Remaining < 8)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
        _position += 8;
        return true;
    }

    public bool TryBytes(long count, out ReadOnlySpan<byte> value)
    {
        if (count < 0 || count > Remaining)
        {
            value = default;
            return false;
        }

        value = _data.Slice(_position, (int)count);
        _position += (int)count;
        return true;
    }

    public bool TrySkip(long count) => TryBytes(count, out _);
}

// Writes a message whose length is known before the first byte.
internal sealed class ByteWriter
{
    private readonly byte[] _buffer;
    private int _position;

    public ByteWriter(int length)
    {
        _buffer = new byte[length];
    }

    public ByteWriter U8(byte value)
    {
        _buffer[_position] = value;
        _position += 1;
        return this;
    }

    public ByteWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
        return this;
    }

    public ByteWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
        return this;
    }

    public ByteWriter U64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
        return this;
    }

    public ByteWriter Bytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_buffer.AsSpan(_position));
        _position += value.Length;
        return this;
    }

    // Left as it is: a new array is all zeros already.
    public ByteWriter Zeros(int count)
    {
        _position += count;
        return this;
    }

    public byte[] ToArray() => _buffer;
}

// What a channel wants on the wire, in the order its state machine decided on. A channel queues its messages
// while it holds its lock and hands them to the core after it let go: a Send that blocks or calls back in can
// then neither stall the channel nor let two messages that depend on each other overtake one another.
internal sealed class Outbox
{
    private readonly ConcurrentQueue<ReadOnlyMemory<byte>> _queue = new();
    private int _sending;

    public void Add(ReadOnlyMemory<byte> message) => _queue.Enqueue(message);

    public void Clear() => _queue.Clear();

    public void Flush(Action<ReadOnlyMemory<byte>>? send, Action<string>? log, string channel)
    {
        while (!_queue.IsEmpty)
        {
            // Whoever is sending right now takes these along - also when it is this very thread, further up
            // the stack inside Send: the queue keeps the order either way.
            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
            {
                return;
            }

            try
            {
                while (_queue.TryDequeue(out var message))
                {
                    if (send is null)
                    {
                        Guarded.Log(log, channel + ": a message is dropped, the channel has no way to send yet");
                        continue;
                    }

                    try
                    {
                        send(message);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // The connection is going down; the core notices that by itself.
                        Guarded.Log(log, channel + ": sending failed: " + exception.Message);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _sending, 0);
            }
        }
    }
}

// Calls into code a channel does not own - the log, the application's event handlers, a dynamic channel's
// handler. Whatever goes wrong there is reported and stays there: it must not end the session's read loop,
// and it must not leave a channel half way through a state change.
internal static class Guarded
{
    public static void Log(Action<string>? log, string message)
    {
        try
        {
            log?.Invoke(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    public static void Run(Action action, Action<string>? log, string what)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(log, what + " failed: " + exception.Message);
        }
    }

    public static void Raise(Action? handler, Action<string>? log, string what)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(log, what + " failed: " + exception.Message);
        }
    }

    public static void Raise<T>(Action<T>? handler, T argument, Action<string>? log, string what)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(argument);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(log, what + " failed: " + exception.Message);
        }
    }

    public static void Raise<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second, Action<string>? log, string what)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(first, second);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(log, what + " failed: " + exception.Message);
        }
    }

    public static void Raise<T1, T2, T3>(Action<T1, T2, T3>? handler, T1 first, T2 second, T3 third, Action<string>? log, string what)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(first, second, third);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(log, what + " failed: " + exception.Message);
        }
    }
}
