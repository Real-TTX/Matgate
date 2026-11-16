using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Matgate.Display.Rdp.Channels.Tests;

// Bytes the way the specifications print them.
internal static class Hex
{
    // "07 00 0c 00": whitespace is ignored, a '#' starts a comment that runs to the end of its line.
    public static byte[] Bytes(string text)
    {
        var bytes = new List<byte>();
        foreach (var line in text.Split('\n'))
        {
            var cut = line.IndexOf('#');
            foreach (var token in (cut >= 0 ? line[..cut] : line).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                bytes.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        return bytes.ToArray();
    }

    // For comparisons: a failing test then shows which bytes differ instead of two object names.
    public static string Text(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 3);
        foreach (var value in bytes)
        {
            text.Append(value.ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
        }

        return text.ToString().TrimEnd();
    }

    // One of the long dumps under Spec/.
    public static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Spec/" + name)
            ?? throw new InvalidOperationException("There is no resource Spec/" + name);
        using var reader = new StreamReader(stream);
        return Bytes(reader.ReadToEnd());
    }

    public static void Same(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) => Assert.Equal(Text(expected), Text(actual));

    public static void Same(string expected, ReadOnlySpan<byte> actual) => Assert.Equal(Text(Bytes(expected)), Text(actual));
}

// The core as a channel sees it: what the channel sends and what it logs ends up here.
internal sealed class Wire
{
    public List<byte[]> Sent { get; } = [];

    public List<string> Log { get; } = [];

    public Wire(StaticChannel channel)
    {
        channel.Send = message => Sent.Add(message.ToArray());
        channel.Log = Log.Add;
    }

    // Everything sent since the last look.
    public byte[][] Take()
    {
        var sent = Sent.ToArray();
        Sent.Clear();
        return sent;
    }

    // The one message sent since the last look.
    public byte[] One()
    {
        var sent = Take();
        Assert.True(sent.Length == 1, $"expected one message, got {sent.Length}: {string.Join(" | ", sent.Select(message => Hex.Text(message)))}");
        return sent[0];
    }

    public void Nothing() => Assert.Empty(Take());
}

// A clipboard server that says what a test tells it to: it builds the server's messages by hand, so the
// channel is not checked against its own encoder.
internal static class ClipServer
{
    public const ushort MonitorReadyType = 1;
    public const ushort FormatListType = 2;
    public const ushort FormatListResponseType = 3;
    public const ushort DataRequestType = 4;
    public const ushort DataResponseType = 5;
    public const ushort CapabilitiesType = 7;
    public const ushort FileRequestType = 8;
    public const ushort FileResponseType = 9;
    public const ushort LockType = 10;
    public const ushort UnlockType = 11;

    public const ushort Ok = 1;
    public const ushort Fail = 2;

    public static byte[] Message(ushort type, ushort flags, params byte[][] parts)
    {
        var body = parts.SelectMany(part => part).ToArray();
        var message = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(message, type);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)body.Length);
        body.CopyTo(message, 8);
        return message;
    }

    public static byte[] U16(ushort value) => [(byte)value, (byte)(value >> 8)];

    public static byte[] U32(uint value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    public static byte[] U64(ulong value) => [.. U32((uint)value), .. U32((uint)(value >> 32))];

    public static byte[] Capabilities(uint flags) =>
        Message(CapabilitiesType, 0, U16(1), U16(0), U16(1), U16(12), U32(2), U32(flags));

    public static byte[] MonitorReady() => Message(MonitorReadyType, 0);

    public static byte[] LongFormatList(params (uint Id, string? Name)[] formats) =>
        Message(FormatListType, 0, formats.Select(format => (byte[])[.. U32(format.Id), .. Encoding.Unicode.GetBytes(format.Name ?? ""), 0, 0]).ToArray());

    public static byte[] ShortFormatList(bool ascii, params (uint Id, string? Name)[] formats) =>
        Message(FormatListType, (ushort)(ascii ? 4 : 0), formats.Select(format =>
        {
            var entry = new byte[36];
            U32(format.Id).CopyTo(entry, 0);
            var name = ascii ? Encoding.ASCII.GetBytes(format.Name ?? "") : Encoding.Unicode.GetBytes(format.Name ?? "");
            name.AsSpan(0, Math.Min(name.Length, 32)).CopyTo(entry.AsSpan(4));
            return entry;
        }).ToArray());

    public static byte[] FormatListResponse(bool ok) => Message(FormatListResponseType, ok ? Ok : Fail);

    public static byte[] DataRequest(uint formatId) => Message(DataRequestType, 0, U32(formatId));

    public static byte[] DataResponse(byte[]? data) => data is null ? Message(DataResponseType, Fail) : Message(DataResponseType, Ok, data);

    public static byte[] FileSizeRequest(uint streamId, int index, uint? clipDataId = null) =>
        Message(FileRequestType, 0, U32(streamId), U32((uint)index), U32(1), U32(0), U32(0), U32(8), clipDataId is { } id ? U32(id) : []);

    public static byte[] FileRangeRequest(uint streamId, int index, ulong offset, uint count, uint? clipDataId = null) =>
        Message(FileRequestType, 0, U32(streamId), U32((uint)index), U32(2), U32((uint)offset), U32((uint)(offset >> 32)), U32(count), clipDataId is { } id ? U32(id) : []);

    public static byte[] FileResponse(uint streamId, byte[]? data) =>
        data is null ? Message(FileResponseType, Fail, U32(streamId)) : Message(FileResponseType, Ok, U32(streamId), data);

    public static byte[] Lock(uint clipDataId) => Message(LockType, 0, U32(clipDataId));

    public static byte[] Unlock(uint clipDataId) => Message(UnlockType, 0, U32(clipDataId));

    // What the client sent, taken apart: type, flags and the body the header announces.
    public static (ushort Type, ushort Flags, byte[] Body) Parse(byte[] message)
    {
        Assert.True(message.Length >= 8, "a clipboard message has a header of eight bytes");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4));
        Assert.Equal((uint)(message.Length - 8), length);
        return (BinaryPrimitives.ReadUInt16LittleEndian(message), BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(2)), message[8..]);
    }
}

// A clipboard channel with everything its events say written down, in order.
internal sealed class ClipClient
{
    public ClipboardChannel Channel { get; } = new();

    public Wire Wire { get; }

    public List<string> Events { get; } = [];

    public List<IReadOnlyList<ClipFormat>> Remote { get; } = [];

    public List<byte[]?> Data { get; } = [];

    public List<(uint StreamId, byte[]? Data)> Files { get; } = [];

    public List<FileContentsRequest> FileRequests { get; } = [];

    public ClipClient()
    {
        Wire = new Wire(Channel);
        Channel.Ready += () => Events.Add("ready");
        Channel.RemoteFormats += formats =>
        {
            Remote.Add(formats);
            Events.Add("formats " + string.Join(",", formats.Select(format => format.Name is null ? format.Id.ToString() : $"{format.Id}={format.Name}")));
        };
        Channel.FormatsAcknowledged += accepted => Events.Add("acknowledged " + accepted);
        Channel.DataRequested += format => Events.Add("requested " + format);
        Channel.DataReceived += data =>
        {
            Data.Add(data);
            Events.Add(data is null ? "data refused" : $"data {data.Length}");
        };
        Channel.FileContentsRequested += request =>
        {
            FileRequests.Add(request);
            Events.Add(request.IsSizeRequest ? $"file {request.StreamId} size of {request.Index}" : $"file {request.StreamId} range of {request.Index} at {request.Offset} for {request.Count}");
        };
        Channel.FileContentsReceived += (stream, data) =>
        {
            Files.Add((stream, data));
            Events.Add(data is null ? $"contents {stream} refused" : $"contents {stream} {data.Length}");
        };
        Channel.Locked += id => Events.Add("locked " + id);
        Channel.Unlocked += id => Events.Add("unlocked " + id);
    }

    // The usual start: the server's capabilities, then its monitor ready. Leaves the wire and the events
    // empty, with the first (empty) format list acknowledged.
    public static ClipClient Started(uint serverFlags = 0x3E)
    {
        var client = new ClipClient();
        client.Channel.Receive(ClipServer.Capabilities(serverFlags));
        client.Channel.Receive(ClipServer.MonitorReady());
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Wire.Take();
        client.Events.Clear();
        return client;
    }

    public string[] TakeEvents()
    {
        var events = Events.ToArray();
        Events.Clear();
        return events;
    }
}

// A listener that writes down what happens to it.
internal sealed class RecordingChannel(string name) : DynamicChannel
{
    public override string Name => name;

    public List<byte[]> Messages { get; } = [];

    public List<string> Events { get; } = [];

    public override void Receive(ReadOnlySpan<byte> message)
    {
        Messages.Add(message.ToArray());
        Events.Add("message " + message.Length);
    }

    public override void Opened() => Events.Add("opened");

    public override void Closed() => Events.Add("closed");

    public string[] TakeEvents()
    {
        var events = Events.ToArray();
        Events.Clear();
        return events;
    }
}

// The server's manager of dynamic channels, as far as a test needs one: PDUs built by hand, with the widths
// of channel id and length a test asks for (0, 1, 2 for one, two, four bytes).
internal static class DvcServer
{
    public static byte[] Capabilities(ushort version) =>
        version == 1 ? [0x50, 0x00, 0x01, 0x00] : [0x50, 0x00, (byte)version, (byte)(version >> 8), 0x33, 0x33, 0x11, 0x11, 0x3d, 0x0a, 0xa7, 0x04];

    public static byte[] Create(uint id, string name, int idWidth = 0) =>
        [(byte)(0x10 | idWidth), .. Variable(id, idWidth), .. Encoding.ASCII.GetBytes(name), 0];

    public static byte[] DataFirst(uint id, uint total, ReadOnlySpan<byte> data, int idWidth = 0, int lengthWidth = 1) =>
        [(byte)(0x20 | (lengthWidth << 2) | idWidth), .. Variable(id, idWidth), .. Variable(total, lengthWidth), .. data];

    public static byte[] Data(uint id, ReadOnlySpan<byte> data, int idWidth = 0) =>
        [(byte)(0x30 | idWidth), .. Variable(id, idWidth), .. data];

    public static byte[] Close(uint id, int idWidth = 0) => [(byte)(0x40 | idWidth), .. Variable(id, idWidth)];

    public static byte[] Variable(uint value, int width) => width switch
    {
        0 => [(byte)value],
        1 => [(byte)value, (byte)(value >> 8)],
        _ => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)],
    };

    // A message the way a server sends it: in one Data PDU when it fits, as Data First and Data PDUs of at
    // most 1600 bytes otherwise.
    public static List<byte[]> Message(uint id, byte[] message)
    {
        var pdus = new List<byte[]>();
        if (message.Length <= 1590)
        {
            pdus.Add(Data(id, message));
            return pdus;
        }

        var lengthWidth = message.Length <= 0xFFFF ? 1 : 2;
        var count = 1600 - 2 - (1 << lengthWidth);
        pdus.Add(DataFirst(id, (uint)message.Length, message.AsSpan(0, count), 0, lengthWidth));
        for (var offset = count; offset < message.Length; offset += count)
        {
            count = Math.Min(1598, message.Length - offset);
            pdus.Add(Data(id, message.AsSpan(offset, count)));
        }

        return pdus;
    }

    // What a client sent for one channel, put together again by the rules of MS-RDPEDYC 3.1.5.2: the messages
    // of that channel in order. Every PDU has to keep within 1600 bytes.
    public static List<byte[]> Messages(IEnumerable<byte[]> pdus, uint id)
    {
        var messages = new List<byte[]>();
        byte[]? buffer = null;
        var received = 0;
        foreach (var pdu in pdus)
        {
            Assert.InRange(pdu.Length, 1, 1600);
            var command = pdu[0] >> 4;
            if (command is not (2 or 3))
            {
                continue;
            }

            var idBytes = 1 << (pdu[0] & 3);
            var channel = idBytes == 1 ? pdu[1] : idBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(1)) : BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(1));
            if (channel != id)
            {
                continue;
            }

            var at = 1 + idBytes;
            if (command == 2)
            {
                Assert.Null(buffer);
                var lengthBytes = 1 << ((pdu[0] >> 2) & 3);
                var total = lengthBytes == 1 ? pdu[at] : lengthBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(at));
                at += lengthBytes;
                buffer = new byte[total];
                received = 0;
            }

            var data = pdu.AsSpan(at);
            if (buffer is null)
            {
                messages.Add(data.ToArray());
                continue;
            }

            data.CopyTo(buffer.AsSpan(received));
            received += data.Length;
            if (received == buffer.Length)
            {
                messages.Add(buffer);
                buffer = null;
            }
        }

        Assert.Null(buffer);
        return messages;
    }
}
