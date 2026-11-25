using System.Buffers.Binary;
using System.Text;

namespace Matgate.Display.Rdp.Channels;

// The "drdynvc" static channel (MS-RDPEDYC): the client's manager of dynamic virtual channels. The server
// opens a channel by naming a listener; a channel whose name nobody registered is refused. Every message of
// this static channel is one PDU of at most 1600 bytes, so a longer message of a dynamic channel travels as
// a Data First PDU and as many Data PDUs as it takes, and is put together again on the other side.
//
// Receive, Opened and Closed belong to the core's thread; Register, Close and a channel's Send may be called
// from any thread. No lock is held while a channel's handler runs or a message is handed to the core.
public sealed class DynamicChannels : StaticChannel
{
    // What one PDU may come to, header included (MS-RDPEDYC 2.2.3).
    public const int MaxPduLength = 1600;

    // What a message of a dynamic channel may come to once it is put together. A longer one passes by.
    public const int MaxMessageLength = 16 * 1024 * 1024;

    // A listener's name cannot be longer than this, and a server asking for a longer one is refused.
    public const int MaxNameLength = 256;

    // The highest version this client answers with. Version 3 would allow the server to send compressed
    // data, and this client has no decompressor for it.
    public const int HighestVersion = 2;

    // A message up to this length goes out as a single Data PDU, a longer one starts with Data First
    // (MS-RDPEDYC 3.1.5.1.1).
    private const int UnfragmentedLength = 1590;
    private const int FirstReservation = 64 * 1024;

    private const int CommandCreate = 0x01;
    private const int CommandDataFirst = 0x02;
    private const int CommandData = 0x03;
    private const int CommandClose = 0x04;
    private const int CommandCapabilities = 0x05;
    private const int CommandDataFirstCompressed = 0x06;
    private const int CommandDataCompressed = 0x07;
    private const int CommandSoftSyncRequest = 0x08;
    private const int CommandSoftSyncResponse = 0x09;

    // Any negative HRESULT says "not created" (MS-RDPEDYC 2.2.2.2); this is E_FAIL's NT cousin STATUS_UNSUCCESSFUL.
    private const uint CreationRefused = 0xC0000001;

    private readonly Lock _gate = new();
    private readonly Outbox _outbox = new();
    private readonly Dictionary<string, DynamicChannel> _listeners = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, OpenChannel> _open = [];
    private int _version;

    private sealed class OpenChannel(uint id, DynamicChannel handler)
    {
        public uint Id { get; } = id;

        public DynamicChannel Handler { get; } = handler;

        // The message being put together, how long it will be and how much of it is there.
        public byte[]? Buffer { get; set; }

        public int Expected { get; set; }

        public int Received { get; set; }

        // What is left of a message that was announced too long and is let pass.
        public long Skipping { get; set; }
    }

    public override string Name => "drdynvc";

    // The version agreed on with the server, 0 before it asked.
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    // Makes a listener known under its name. Meant for before the session starts; a listener registered later
    // is found by the next request of the server. Throws ArgumentException for a name that cannot be asked for
    // or is taken.
    public void Register(DynamicChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var name = channel.Name;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(character => character is '\0' or > 'ÿ'))
        {
            throw new ArgumentException("A dynamic channel needs a name of 1 to 256 ANSI characters.", nameof(channel));
        }

        lock (_gate)
        {
            if (!_listeners.TryAdd(name, channel))
            {
                throw new ArgumentException($"A dynamic channel named {name} is registered already.", nameof(channel));
            }
        }

        channel.Log ??= message => Guarded.Log(Log, message);
    }

    // Closes a channel from this side. The server is told and does not answer (MS-RDPEDYC 3.2.5.2); it may
    // open the channel again later. False when the channel is not open.
    public bool Close(DynamicChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            var open = _open.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Handler, channel));
            if (open is null)
            {
                return false;
            }

            _open.Remove(open.Id);
            _outbox.Add(Pdu(CommandClose, 0, open.Id, 0).ToArray());
        }

        Flush();
        Farewell(channel);
        return true;
    }

    public override void Receive(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1)
        {
            Report("dropped an empty message");
            return;
        }

        // One byte of header: the width of the channel id, two bits whose meaning depends on the command
        // (and which Windows leaves uninitialised where they have none), and the command.
        var idWidth = message[0] & 0x03;
        var extra = (message[0] >> 2) & 0x03;
        var command = message[0] >> 4;
        var reader = new ByteReader(message[1..]);
        switch (command)
        {
            case CommandCapabilities:
                Capabilities(reader);
                break;
            case CommandCreate:
                Create(idWidth, reader);
                break;
            case CommandDataFirst:
                Data(idWidth, extra, true, reader);
                break;
            case CommandData:
                Data(idWidth, extra, false, reader);
                break;
            case CommandClose:
                ServerClose(idWidth, reader);
                break;
            case CommandDataFirstCompressed:
            case CommandDataCompressed:
                Compressed(idWidth, reader);
                break;
            case CommandSoftSyncRequest:
            case CommandSoftSyncResponse:
                // Without an answer everything stays on this channel, which is the only transport there is.
                Report("ignored a soft-sync message: there are no UDP transports to move to");
                break;
            default:
                Report($"dropped a message with the unknown command {command}");
                break;
        }
    }

    public override void Closed()
    {
        List<DynamicChannel> handlers;
        lock (_gate)
        {
            handlers = _open.Values.Select(channel => channel.Handler).ToList();
            _open.Clear();
            _outbox.Clear();
            _version = 0;
        }

        foreach (var handler in handlers)
        {
            Farewell(handler);
        }
    }

    // ---- the server's messages -----------------------------------------------------------------------------------

    private void Capabilities(ByteReader reader)
    {
        // The priority charges of versions 2 and 3 follow; this client sends too little to weigh it.
        if (!reader.TrySkip(1) || !reader.TryU16(out var version) || version == 0)
        {
            Report("dropped capabilities that do not name a version");
            return;
        }

        lock (_gate)
        {
            _version = Math.Min((int)version, HighestVersion);
            QueueCapabilities();
        }

        Flush();
    }

    private void Create(int idWidth, ByteReader reader)
    {
        if (!TryVariable(ref reader, idWidth, out var id))
        {
            Report("dropped a create request without a channel id");
            return;
        }

        var rest = reader.Rest;
        var end = rest.IndexOf((byte)0);
        var name = end is >= 1 and <= MaxNameLength ? Encoding.Latin1.GetString(rest[..end]) : null;
        DynamicChannel? opened = null;
        DynamicChannel? replaced = null;
        string? refusal = null;
        lock (_gate)
        {
            if (_version == 0)
            {
                // Some servers open channels without asking for the capabilities first; FreeRDP answers them
                // unasked then, and so does this.
                _version = HighestVersion;
                QueueCapabilities();
            }

            if (_open.Remove(id, out var forgotten))
            {
                // The server would not hand out an id twice: it has let go of the channel that had it.
                replaced = forgotten.Handler;
            }

            if (name is null)
            {
                refusal = "its name is missing, unterminated or too long";
            }
            else if (!_listeners.TryGetValue(name, out var listener))
            {
                refusal = $"nobody listens for \"{name}\"";
            }
            else if (_open.Values.Any(channel => ReferenceEquals(channel.Handler, listener)))
            {
                refusal = $"\"{name}\" is open already";
            }
            else
            {
                var channel = new OpenChannel(id, listener);
                _open[id] = channel;
                opened = listener;
            }

            // The answer carries the id in the width the request had.
            var response = new ByteWriter(1 + (1 << idWidth) + 4).U8((byte)((CommandCreate << 4) | idWidth));
            WriteVariable(response, idWidth, id);
            _outbox.Add(response.U32(opened is null ? CreationRefused : 0).ToArray());
        }

        Flush();
        if (replaced is not null)
        {
            Report($"channel {id} is opened again; \"{replaced.Name}\", which had that id, is closed");
            Farewell(replaced);
        }

        if (refusal is not null)
        {
            Report($"refused channel {id}: {refusal}");
        }

        if (opened is not null)
        {
            // The answer is on its way: the server may hear from the channel now.
            lock (_gate)
            {
                if (_open.TryGetValue(id, out var channel) && ReferenceEquals(channel.Handler, opened))
                {
                    opened.Send = message => SendOn(channel, message);
                }
            }

            Guarded.Run(opened.Opened, Log, $"drdynvc: opening \"{opened.Name}\"");
        }
    }

    private void Data(int idWidth, int lengthWidth, bool first, ByteReader reader)
    {
        uint total = 0;
        if (!TryVariable(ref reader, idWidth, out var id) || (first && !TryVariable(ref reader, lengthWidth, out total)))
        {
            Report("dropped data without a channel id or a length");
            return;
        }

        var data = reader.Rest;
        DynamicChannel? handler;
        byte[]? assembled;
        string? note;
        lock (_gate)
        {
            note = Collect(id, first, total, data, out handler, out assembled);
        }

        if (note is not null)
        {
            Report(note);
        }

        if (handler is not null)
        {
            // Either the message came in one piece - then it is still in the core's buffer - or it was put
            // together here.
            Deliver(handler, assembled is null ? data : assembled);
        }
    }

    // Takes one piece of a channel's data. When a message is complete with it, handler says whose it is and
    // assembled holds it - unless the piece was the whole message, which is then delivered as it came.
    private string? Collect(uint id, bool first, uint total, ReadOnlySpan<byte> data, out DynamicChannel? handler, out byte[]? assembled)
    {
        handler = null;
        assembled = null;
        if (!_open.TryGetValue(id, out var channel))
        {
            return $"dropped data for channel {id}, which is not open";
        }

        string? note = null;
        if (first)
        {
            if (channel.Buffer is not null)
            {
                note = $"channel {id}: a message was never finished";
            }

            channel.Buffer = null;
            channel.Skipping = 0;
            if (total > MaxMessageLength)
            {
                // Its pieces are not messages of their own: they are counted off and let pass.
                channel.Skipping = total - Math.Min(total, (uint)data.Length);
                return $"channel {id}: a message of {total} bytes is more than the {MaxMessageLength} allowed";
            }

            if (data.Length > total)
            {
                return $"channel {id}: dropped a first piece longer than the {total} bytes of its message";
            }

            if (data.Length == total)
            {
                handler = channel.Handler;
                return note;
            }

            channel.Buffer = new byte[Math.Min((int)total, Math.Max(data.Length, FirstReservation))];
            channel.Expected = (int)total;
            channel.Received = 0;
        }
        else if (channel.Skipping > 0)
        {
            channel.Skipping -= Math.Min(channel.Skipping, data.Length);
            return null;
        }
        else if (channel.Buffer is null)
        {
            // Data without a Data First before it is a message of its own (MS-RDPEDYC 3.1.5.2.2).
            handler = channel.Handler;
            return null;
        }

        var buffer = channel.Buffer;
        if (data.Length > channel.Expected - channel.Received)
        {
            channel.Buffer = null;
            return $"channel {id}: dropped a message that grew past the {channel.Expected} bytes announced";
        }

        if (channel.Received + data.Length > buffer.Length)
        {
            // The announced length is only a claim until the bytes arrive: the buffer grows with them.
            var grown = new byte[Math.Min(channel.Expected, Math.Max(buffer.Length * 2L, channel.Received + data.Length))];
            buffer.AsSpan(0, channel.Received).CopyTo(grown);
            channel.Buffer = buffer = grown;
        }

        data.CopyTo(buffer.AsSpan(channel.Received));
        channel.Received += data.Length;
        if (channel.Received == channel.Expected)
        {
            handler = channel.Handler;
            assembled = buffer;
            channel.Buffer = null;
        }

        return note;
    }

    private void ServerClose(int idWidth, ByteReader reader)
    {
        if (!TryVariable(ref reader, idWidth, out var id))
        {
            Report("dropped a close without a channel id");
            return;
        }

        DynamicChannel handler;
        lock (_gate)
        {
            if (!_open.Remove(id, out var channel))
            {
                // Not answered either (MS-RDPEDYC 3.2.5.2). Old servers close what they failed to create.
                return;
            }

            handler = channel.Handler;
            _outbox.Add(Pdu(CommandClose, 0, id, 0).ToArray());
        }

        Flush();
        Farewell(handler);
    }

    private void Compressed(int idWidth, ByteReader reader)
    {
        lock (_gate)
        {
            // Whatever was being put together on that channel has a hole now.
            if (TryVariable(ref reader, idWidth, out var id) && _open.TryGetValue(id, out var channel))
            {
                channel.Buffer = null;
                channel.Skipping = 0;
            }
        }

        Report("dropped compressed data, which version 2 does not have");
    }

    // ---- what goes out -----------------------------------------------------------------------------------------

    private void SendOn(OpenChannel channel, ReadOnlyMemory<byte> message)
    {
        var open = false;
        lock (_gate)
        {
            if (_open.TryGetValue(channel.Id, out var current) && ReferenceEquals(current, channel))
            {
                open = true;

                // All pieces at once: nothing else of this channel can get in between them.
                QueueMessage(channel.Id, message.Span);
            }
        }

        if (!open)
        {
            Report($"dropped a message for \"{channel.Handler.Name}\", which is closed");
            return;
        }

        Flush();
    }

    private void QueueMessage(uint id, ReadOnlySpan<byte> message)
    {
        var header = 1 + (1 << Width(id));
        if (message.Length <= UnfragmentedLength)
        {
            _outbox.Add(Pdu(CommandData, 0, id, message.Length).Bytes(message).ToArray());
            return;
        }

        var lengthWidth = Width((uint)message.Length);
        var count = Math.Min(message.Length, MaxPduLength - header - (1 << lengthWidth));
        var first = Pdu(CommandDataFirst, lengthWidth, id, (1 << lengthWidth) + count);
        WriteVariable(first, lengthWidth, (uint)message.Length);
        _outbox.Add(first.Bytes(message[..count]).ToArray());
        for (var offset = count; offset < message.Length; offset += count)
        {
            count = Math.Min(message.Length - offset, MaxPduLength - header);
            _outbox.Add(Pdu(CommandData, 0, id, count).Bytes(message.Slice(offset, count)).ToArray());
        }
    }

    private void QueueCapabilities() =>
        _outbox.Add(new ByteWriter(4).U8(CommandCapabilities << 4).U8(0).U16((ushort)_version).ToArray());

    private void Flush() => _outbox.Flush(Send, Log, Name);

    private void Deliver(DynamicChannel handler, ReadOnlySpan<byte> message)
    {
        try
        {
            handler.Receive(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report($"\"{handler.Name}\" failed on a message: {exception.Message}");
        }
    }

    private void Farewell(DynamicChannel handler)
    {
        handler.Send = null;
        Guarded.Run(handler.Closed, Log, $"drdynvc: closing \"{handler.Name}\"");
    }

    // ---- the wire format -----------------------------------------------------------------------------------------

    // A PDU about one channel: the header byte, the channel id in as few bytes as it needs, room for the rest.
    private static ByteWriter Pdu(int command, int extra, uint id, int rest)
    {
        var width = Width(id);
        var writer = new ByteWriter(1 + (1 << width) + rest).U8((byte)((command << 4) | (extra << 2) | width));
        WriteVariable(writer, width, id);
        return writer;
    }

    // The two bits that say how wide a channel id or a length is: 0, 1, 2 for one, two, four bytes.
    private static int Width(uint value) => value <= 0xFF ? 0 : value <= 0xFFFF ? 1 : 2;

    private static void WriteVariable(ByteWriter writer, int width, uint value)
    {
        switch (width)
        {
            case 0:
                writer.U8((byte)value);
                break;
            case 1:
                writer.U16((ushort)value);
                break;
            default:
                writer.U32(value);
                break;
        }
    }

    private static bool TryVariable(ref ByteReader reader, int width, out uint value)
    {
        switch (width)
        {
            case 0:
                var one = reader.TryU8(out var small);
                value = small;
                return one;
            case 1:
                var two = reader.TryU16(out var medium);
                value = medium;
                return two;
            case 2:
                return reader.TryU32(out value);
            default:
                // The fourth combination of the two bits is not a width.
                value = 0;
                return false;
        }
    }
}
