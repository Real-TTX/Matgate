using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Channels;

// What the remote desktop plays (MS-RDPEA), over the "rdpsnd" static channel. The server lists the formats it
// can send; the client answers with the ones it takes - here plain 16-bit PCM and nothing else, so no decoder
// is needed - and the server then sends the sound in blocks, each of which the client confirms. The server
// measures the way to the client by those confirmations.
//
// Everything happens on the thread that calls Receive; Enabled may be set from any thread.
public sealed class SoundChannel : StaticChannel
{
    // The version this client announces. From 8 on a server sends a block in one message (Wave2) instead of
    // two (WaveInfo and Wave); both are understood.
    public const ushort ClientVersion = 8;

    // The client's list is at most this long. No server has that many PCM formats, and a block names its
    // format by its place in this list.
    public const int MaxFormats = 32;

    private const byte TypeClose = 0x01;
    private const byte TypeWaveInfo = 0x02;
    private const byte TypeSetVolume = 0x03;
    private const byte TypeSetPitch = 0x04;
    private const byte TypeWaveConfirm = 0x05;
    private const byte TypeTraining = 0x06;
    private const byte TypeFormats = 0x07;
    private const byte TypeCryptKey = 0x08;
    private const byte TypeWaveEncrypt = 0x09;
    private const byte TypeUdpWave = 0x0A;
    private const byte TypeUdpWaveLast = 0x0B;
    private const byte TypeQualityMode = 0x0C;
    private const byte TypeWave2 = 0x0D;

    private const int HeaderLength = 4;
    private const int FormatLength = 18;
    private const ushort Pcm = 0x0001;

    // PCM has no extra format data; a few bytes of it are tolerated, a list that would no longer fit into
    // the 16-bit size of the answer is not.
    private const int MaxFormatExtra = 64;

    // TSSNDCAPS_ALIVE: the client takes sound. Volume and pitch are not announced, so the server applies its
    // volume to the samples itself and has no reason to send either.
    private const uint Alive = 0x00000001;

    // HIGH_QUALITY: the best format on the list, whatever the bandwidth. The list only has PCM anyway.
    private const ushort HighQuality = 0x0002;

    private readonly List<Format> _formats = [];
    private volatile bool _enabled = true;
    private ushort _serverVersion;

    // A WaveInfo PDU is under way: the next message is its Wave PDU, which has no header of its own.
    private bool _waveExpected;
    private ushort _waveTimeStamp;
    private ushort _waveFormat;
    private byte _waveBlock;
    private int _waveLength;
    private uint _waveStart;

    private readonly record struct Format(int Rate, int Channels);

    public override string Name => "rdpsnd";

    // Interleaved 16-bit little-endian PCM as it came from the server, with its rate and channel count. The
    // memory belongs to the handler. The block is confirmed to the server when the handler returns.
    public event Action<ReadOnlyMemory<byte>, int, int>? Samples;

    // The server stopped sending (SNDC_CLOSE): what was played is complete, the next samples start anew.
    public event Action? Stopped;

    // When false, wave data is confirmed but not raised.
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    // Milliseconds from a clock that only goes forward; the tests bring their own.
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    public override void Receive(ReadOnlySpan<byte> message)
    {
        var arrived = Clock();
        if (_waveExpected)
        {
            Wave(message, arrived);
            return;
        }

        // The pad byte between type and size is arbitrary.
        if (message.Length < HeaderLength)
        {
            Report("dropped a message shorter than its header");
            return;
        }

        var type = message[0];
        var size = BinaryPrimitives.ReadUInt16LittleEndian(message[2..]);
        if (type == TypeWaveInfo)
        {
            // Its size counts the sound of the Wave PDU that follows, not what is in this message.
            WaveInfo(message[HeaderLength..], size);
            return;
        }

        if (size > message.Length - HeaderLength)
        {
            Report($"dropped message {type}: it says {size} bytes and has {message.Length - HeaderLength}");
            return;
        }

        var body = message.Slice(HeaderLength, size);
        switch (type)
        {
            case TypeFormats:
                ServerFormats(body);
                break;
            case TypeTraining:
                Training(body);
                break;
            case TypeWave2:
                Wave2(body, arrived);
                break;
            case TypeClose:
                Guarded.Raise(Stopped, Log, "rdpsnd: the handler of Stopped");
                break;
            case TypeSetVolume:
            case TypeSetPitch:
                // Neither was announced as something this client does.
                break;
            case TypeCryptKey:
            case TypeWaveEncrypt:
            case TypeUdpWave:
            case TypeUdpWaveLast:
                Report($"ignored message {type}, which belongs to sound over UDP");
                break;
            default:
                Report($"ignored message {type}, which this client does not know");
                break;
        }
    }

    public override void Closed()
    {
        _formats.Clear();
        _serverVersion = 0;
        _waveExpected = false;
    }

    private void ServerFormats(ReadOnlySpan<byte> body)
    {
        // Flags, volume, pitch and the UDP port mean nothing in the server's message; then the number of
        // formats, the block number the server starts counting from, its version and a pad byte.
        var reader = new ByteReader(body);
        if (!reader.TrySkip(14) || !reader.TryU16(out var count) || !reader.TrySkip(1)
            || !reader.TryU16(out var version) || !reader.TrySkip(1))
        {
            Report("dropped formats too short for their fixed part");
            return;
        }

        var first = new List<(Format Format, byte[] Wire)>();
        var others = new List<(Format Format, byte[] Wire)>();
        for (var i = 0; i < count; i++)
        {
            var start = reader.Rest;
            if (!reader.TryU16(out var tag) || !reader.TryU16(out var channels) || !reader.TryU32(out var rate)
                || !reader.TrySkip(4) || !reader.TryU16(out var align) || !reader.TryU16(out var bits)
                || !reader.TryU16(out var extra) || !reader.TrySkip(extra))
            {
                Report("dropped formats: the list is shorter than it says");
                return;
            }

            // Anything that is not plain 16-bit PCM in a shape a player can do something with is left out.
            if (tag != Pcm || bits != 16 || channels is < 1 or > 8 || rate is < 8000 or > 192000 || align != channels * 2
                || extra > MaxFormatExtra)
            {
                continue;
            }

            // The server has to find each format in its own list again, so it goes back byte for byte.
            var entry = (new Format((int)rate, channels), start[..(FormatLength + extra)].ToArray());
            if (channels == 2 && rate is 48000 or 44100)
            {
                // 48000 Hz stereo before 44100 Hz stereo, each once.
                if (first.All(known => known.Format != entry.Item1))
                {
                    first.Insert(rate == 48000 ? 0 : first.Count, entry);
                }
            }
            else if (others.Count < MaxFormats)
            {
                others.Add(entry);
            }
        }

        // A new list starts everything over: block numbers and format numbers count from here.
        var offered = first.Concat(others).Take(MaxFormats).ToList();
        _formats.Clear();
        _formats.AddRange(offered.Select(entry => entry.Format));
        _serverVersion = version;
        _waveExpected = false;

        var length = 20 + offered.Sum(entry => entry.Wire.Length);
        var answer = Message(TypeFormats, length)
            .U32(Alive)
            .U32(0xFFFFFFFF)
            .U32(0)
            .U16(0)
            .U16((ushort)offered.Count)
            .U8(0)
            .U16(ClientVersion)
            .U8(0);
        foreach (var entry in offered)
        {
            answer.Bytes(entry.Wire);
        }

        Transmit(answer.ToArray());
        if (version >= 6)
        {
            // Asked of a client whenever both sides are at version 6 or later (MS-RDPEA 3.2.5.1.1.3).
            Transmit(Message(TypeQualityMode, 4).U16(HighQuality).U16(0).ToArray());
        }

        if (offered.Count == 0)
        {
            Report("the server offers no 16-bit PCM: there will be no sound");
        }
    }

    // The server measures the connection: time stamp and size go back as they came.
    private void Training(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            Report("dropped a training message that is too short");
            return;
        }

        Transmit(Message(TypeTraining, 4).Bytes(body[..4]).ToArray());
    }

    // The first of two messages for one block: time stamp, format, block number and the first four bytes of
    // the sound. The rest follows in a message of its own.
    private void WaveInfo(ReadOnlySpan<byte> body, ushort size)
    {
        // The size is that of the sound plus the eight bytes before it in this message.
        if (body.Length < 12 || size < 12)
        {
            Report("dropped a wave info that is too short");
            return;
        }

        _waveTimeStamp = BinaryPrimitives.ReadUInt16LittleEndian(body);
        _waveFormat = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        _waveBlock = body[4];
        _waveStart = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        _waveLength = size - 8;
        _waveExpected = true;
    }

    // The second message: four bytes of padding where the first four bytes of the sound belong, then the rest.
    private void Wave(ReadOnlySpan<byte> message, long arrived)
    {
        _waveExpected = false;
        byte[]? sound = null;
        if (message.Length < _waveLength)
        {
            Report($"dropped a wave of {message.Length} bytes that was announced with {_waveLength}");
        }
        else if (_enabled && Playable(_waveFormat, _waveLength))
        {
            sound = message[.._waveLength].ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(sound, _waveStart);
        }

        Play(sound, _waveFormat, _waveTimeStamp, _waveBlock, arrived);
    }

    // A block in one message: time stamp, format, block number, the time the server took the sound, the sound.
    private void Wave2(ReadOnlySpan<byte> body, long arrived)
    {
        if (body.Length < 12)
        {
            Report("dropped a wave that is too short");
            return;
        }

        var timeStamp = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var format = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        var block = body[4];
        var data = body[12..];
        Play(_enabled && Playable(format, data.Length) ? data.ToArray() : null, format, timeStamp, block, arrived);
    }

    private bool Playable(ushort format, int length)
    {
        if (format >= _formats.Count)
        {
            Report($"dropped a wave in format {format}, which was never agreed on");
            return false;
        }

        return length > 0;
    }

    // Hands the sound on and confirms the block. A block is confirmed whatever became of it - played, switched
    // off or dropped - because the server counts on it (MS-RDPEA 1.3.2.2).
    private void Play(byte[]? sound, ushort format, ushort timeStamp, byte block, long arrived)
    {
        if (sound is not null)
        {
            // Only whole sample frames: a player cannot do anything with half of one.
            var known = _formats[format];
            var whole = sound.Length - sound.Length % (known.Channels * 2);
            if (whole > 0)
            {
                Guarded.Raise(Samples, new ReadOnlyMemory<byte>(sound, 0, whole), known.Rate, known.Channels, Log, "rdpsnd: the handler of Samples");
            }
        }

        // The server's time stamp plus the milliseconds between the block's arrival and this confirmation
        // (MS-RDPEA 3.2.5.2.1.6), in 16 bits that wrap around.
        var held = Math.Max(0, Clock() - arrived);
        Transmit(Message(TypeWaveConfirm, 4).U16((ushort)(timeStamp + held)).U8(block).U8(0).ToArray());
    }

    private void Transmit(byte[] message)
    {
        var send = Send;
        if (send is null)
        {
            Report("a message is dropped, the channel has no way to send yet");
            return;
        }

        try
        {
            send(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The connection is going down; the core notices that by itself.
            Report("sending failed: " + exception.Message);
        }
    }

    private static ByteWriter Message(byte type, int length) =>
        new ByteWriter(HeaderLength + length).U8(type).U8(0).U16((ushort)length);
}
