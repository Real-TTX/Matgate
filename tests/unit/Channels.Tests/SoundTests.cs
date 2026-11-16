using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Channels.Tests;

// The audio output channel: the annotated examples of MS-RDPEA section 4, a whole sequence, and what a server
// can get wrong.
public class SoundTests
{
    // 4.1.1 Server Audio Formats and Version PDU: version 5, five formats - PCM 22050 Hz stereo 16 bit, A-law,
    // mu-law, ADPCM and DVI ADPCM.
    private const string ServerFormatsExample = """
        07 2b 90 00 08 fb 8b 00 e0 f1 09 00 70 27 1f 77
        00 00 05 00 ff 05 00 00 01 00 02 00 22 56 00 00
        88 58 01 00 04 00 10 00 00 00 06 00 02 00 22 56
        00 00 44 ac 00 00 02 00 08 00 00 00 07 00 02 00
        22 56 00 00 44 ac 00 00 02 00 08 00 00 00 02 00
        02 00 22 56 00 00 27 57 00 00 00 04 04 00 20 00
        f4 03 07 00 00 01 00 00 00 02 00 ff 00 00 00 00
        c0 00 40 00 f0 00 00 00 cc 01 30 ff 88 01 18 ff
        11 00 02 00 22 56 00 00 b9 56 00 00 00 04 04 00
        02 00 f9 03
        """;

    // 4.1.2 Client Audio Formats and Version PDU, as far as it is this client's to say: the example's client
    // takes all five formats and can set the volume; this one takes the PCM and leaves the volume to the
    // server. The format itself goes back as it came.
    private const string PcmFormatOfTheExample = "01 00 02 00 22 56 00 00 88 58 01 00 04 00 10 00 00 00";

    // 4.1.3 Training PDU: time stamp 0x89da, 1024 bytes in all. The example prints its beginning and its end.
    private const string TrainingStart = """
        06 23 fc 03 da 89 00 04 52 90 49 f1 f1 bb e9 eb
        b3 a6 db 3c 87 0c 3e 99 24 5e 0d 1c 06 b7 47 de
        b3 12 4d c8 43 bb 8b a6 1f 03 5a 7d 09 38 25 1f
        5d d4 cb fc 96 f5 45 3b 13 0d 89 0a 1c db ae 32
        """;

    private const string TrainingEnd = """
        20 9a 50 ee 40 78 36 fd 12 49 32 f6 9e 7d 49 dc
        ad 4f 14 f2 44 40 66 d0 6b c4 30 b7 32 3b a1 22
        f6 22 91 9d e1 8b 1f da b0 ca 99 02 b9 72 9d 49
        """;

    // 4.1.4 Training Confirm PDU (its pad byte is 0x55 in the example and arbitrary by definition).
    private const string TrainingConfirm = "06 55 04 00 da 89 00 04";

    // 4.2.1 WaveInfo PDU: time stamp 0xadd7, format 15, block 8, the first four bytes of 585 bytes of sound.
    private const string WaveInfo = "02 7e 51 02 d7 ad 0f 00 08 00 00 00 20 48 17 d6";

    // 4.2.2 Wave PDU: four bytes of padding and the other 581 bytes. Again the example prints both ends.
    private const string WaveStart = """
        00 00 00 00 84 02 80 24 49 92 24 89 02 80 24 49
        92 24 89 02 80 24 49 92 24 89 02 80 24 49 92 24
        09 82 74 61 4d 28 00 48 92 24 49 92 28 00 48 92
        """;

    private const string WaveEnd = """
        0f 7b de 20 b2 2a 72 74 37 d9 bc dd 5f 4d 0b 58
        a5 05 a9 12 3c 19 40 59 6a 48 aa 4e 11 4c 51 63
        55 cd 57 1f f8 91 ba 48 aa
        """;

    // 4.2.3 Wave Confirm PDU: time stamp 0x5ab7, block 8 (pad bytes 0x39 and 0x77, arbitrary).
    private const string WaveConfirm = "05 39 04 00 b7 5a 08 77";

    // 4.2.4 Wave2 PDU: time stamp 0xa116, format 3, block 2, audio time stamp 0x0dacb8c2, 248 bytes of sound.
    private const string Wave2Start = """
        0d 00 04 01 16 a1 03 00 02 00 00 00 c2 b8 ac 0d
        27 0c 45 83 04 84 82 20 b0 48 6c 18 0d 84 84 c3
        41 10 50 2a 11 19 84 42 a1 11 81 44 40 21 08 08
        4b fe c7 24 3c 1c 9f 07 45 9a 07 0e 13 28 a5 ba
        60 c4 03 c5 f8 57 1d d6 5a 2b f3 4f 83 9e 5f 04
        """;

    private const string Wave2End = """
        b1 51 00 09 8b 26 04 c1 71 41 71 34 eb bc 01 25
        45 c6 e0 14 2c 0c 20 b1 6b 67 d8 10 5c 48 a8 2e
        b0 dc 44 ba 53 00 55 c0
        """;

    // A message of which the specification prints the two ends: what lies between is filled in.
    private static byte[] Elided(int length, string start, string end)
    {
        var message = new byte[length];
        for (var i = 0; i < length; i++)
        {
            message[i] = (byte)(i * 5);
        }

        Hex.Bytes(start).CopyTo(message, 0);
        var tail = Hex.Bytes(end);
        tail.CopyTo(message, length - tail.Length);
        return message;
    }

    private static byte[] Format(ushort tag, ushort channels, uint rate, ushort bits, params byte[] extra)
    {
        var align = (ushort)(channels * bits / 8);
        return
        [
            .. ClipServer.U16(tag), .. ClipServer.U16(channels), .. ClipServer.U32(rate), .. ClipServer.U32(rate * align),
            .. ClipServer.U16(align), .. ClipServer.U16(bits), .. ClipServer.U16((ushort)extra.Length), .. extra,
        ];
    }

    private static byte[] Pcm(uint rate, ushort channels) => Format(1, channels, rate, 16);

    // A Server Audio Formats and Version PDU. Flags, volume, pitch and port are whatever the server had in
    // memory (MS-RDPEA 2.2.2.1): here they are set to values that would mislead a reader who used them.
    private static byte[] ServerFormats(ushort version, params byte[][] formats)
    {
        var list = formats.SelectMany(format => format).ToArray();
        return
        [
            0x07, 0xEE, .. ClipServer.U16((ushort)(20 + list.Length)),
            .. ClipServer.U32(0xDEADBEEF), .. ClipServer.U32(0x12345678), .. ClipServer.U32(0x9ABCDEF0), .. ClipServer.U16(0x1234),
            .. ClipServer.U16((ushort)formats.Length), 0xFF, .. ClipServer.U16(version), 0x77, .. list,
        ];
    }

    private static byte[] Wave2(ushort timeStamp, ushort format, byte block, byte[] sound) =>
    [
        0x0D, 0x00, .. ClipServer.U16((ushort)(12 + sound.Length)),
        .. ClipServer.U16(timeStamp), .. ClipServer.U16(format), block, 0xAA, 0xBB, 0xCC, .. ClipServer.U32(0x11223344), .. sound,
    ];

    // The two messages a server before version 8 sends for one block.
    private static (byte[] Info, byte[] Wave) Waves(ushort timeStamp, ushort format, byte block, byte[] sound) =>
    (
        [0x02, 0x00, .. ClipServer.U16((ushort)(sound.Length + 8)), .. ClipServer.U16(timeStamp), .. ClipServer.U16(format), block, 0xAA, 0xBB, 0xCC, .. sound.AsSpan(0, 4)],
        [0, 0, 0, 0, .. sound.AsSpan(4)]
    );

    private static byte[] Sound(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 3 + 1)).ToArray();

    private sealed class Player
    {
        public SoundChannel Channel { get; } = new();

        public Wire Wire { get; }

        public List<(byte[] Pcm, int Rate, int Channels)> Played { get; } = [];

        public long Now { get; set; } = 1_000_000;

        // How long the handler of Samples takes, by the channel's clock.
        public long Playing { get; set; }

        public int Stops { get; private set; }

        public Player()
        {
            Wire = new Wire(Channel);
            Channel.Clock = () => Now;
            Channel.Samples += (pcm, rate, channels) =>
            {
                Played.Add((pcm.ToArray(), rate, channels));
                Now += Playing;
            };
            Channel.Stopped += () => Stops++;
        }
    }

    // 4.1.1 and 4.1.2: the server's formats, and the client's answer.
    [Fact]
    public void Formats_as_in_the_specification()
    {
        var player = new Player();

        player.Channel.Receive(Hex.Bytes(ServerFormatsExample));

        // Version 5 has no quality mode: the answer is one message. It takes the one PCM format of the five,
        // says "alive" and nothing about volume or pitch, has no UDP port and is version 8.
        Hex.Same("07 00 26 00 01 00 00 00 ff ff ff ff 00 00 00 00 00 00 01 00 00 08 00 00 " + PcmFormatOfTheExample, player.Wire.One());
        Assert.Empty(player.Wire.Log);
    }

    // 4.1.3 and 4.1.4.
    [Fact]
    public void Training_as_in_the_specification()
    {
        var player = new Player();
        player.Channel.Receive(Hex.Bytes(ServerFormatsExample));
        player.Wire.Take();

        player.Channel.Receive(Elided(1024, TrainingStart, TrainingEnd));

        var confirm = player.Wire.One();
        var example = Hex.Bytes(TrainingConfirm);
        example[1] = 0;
        Hex.Same(example, confirm);
    }

    // 4.2.1, 4.2.2 and 4.2.3: a block in two messages and its confirmation.
    [Fact]
    public void A_wave_in_two_messages_as_in_the_specification()
    {
        var player = new Player();

        // The example's sound is in format 15 of the client's list: sixteen formats, the last one mono.
        player.Channel.Receive(ServerFormats(6, [.. Enumerable.Range(0, 15).Select(i => Pcm((uint)(8000 + i * 1000), 2)), Pcm(11025, 1)]));
        player.Wire.Take();

        player.Channel.Receive(Hex.Bytes(WaveInfo));
        player.Wire.Nothing();
        Assert.Empty(player.Played);

        // The confirmation carries the time stamp of the WaveInfo plus the milliseconds the client took. The
        // example's client took 44256 of them: 0xadd7 + 44256 = 0x15ab7, of which sixteen bits remain.
        player.Playing = 44256;
        var wave = Elided(585, WaveStart, WaveEnd);
        player.Channel.Receive(wave);

        var (pcm, rate, channels) = Assert.Single(player.Played);
        Assert.Equal((11025, 1), (rate, channels));

        // The four bytes of the WaveInfo, then what followed the padding - less the odd byte at the end,
        // which is half a sample.
        Hex.Same((byte[])[0x20, 0x48, 0x17, 0xd6, .. wave.AsSpan(4, 580)], pcm);

        var example = Hex.Bytes(WaveConfirm);
        example[1] = 0;
        example[7] = 0;
        Hex.Same(example, player.Wire.One());
    }

    // 4.2.4: a block in one message.
    [Fact]
    public void A_wave_in_one_message_as_in_the_specification()
    {
        var player = new Player();
        player.Channel.Receive(ServerFormats(8, Pcm(48000, 2), Pcm(44100, 2), Pcm(22050, 2), Pcm(44100, 1)));
        player.Wire.Take();

        var wave = Elided(264, Wave2Start, Wave2End);
        player.Channel.Receive(wave);

        var (pcm, rate, channels) = Assert.Single(player.Played);
        Assert.Equal((44100, 1), (rate, channels));
        Assert.Equal(248, pcm.Length);
        Hex.Same(wave.AsSpan(16), pcm);

        // Confirmed at once: the time stamp goes back as it came, with the block number.
        Hex.Same("05 00 04 00 16 a1 02 00", player.Wire.One());
    }

    [Fact]
    public void A_whole_sequence_with_both_kinds_of_wave()
    {
        var player = new Player();

        // A server of version 8 with formats this client cannot play around the ones it can.
        player.Channel.Receive(ServerFormats(
            8,
            Format(0xA106, 2, 44100, 16, 1, 2, 3),      // AAC
            Pcm(44100, 2),
            Pcm(22050, 2),
            Format(6, 2, 22050, 8),                     // A-law
            Format(1, 2, 44100, 8),                     // PCM, but 8 bit
            Pcm(48000, 2),
            Pcm(44100, 1)));

        // 48000 Hz stereo first, 44100 Hz stereo second, then the rest as the server listed it; and the
        // quality mode, which is asked of a client when both sides are at version 6 or later.
        var sent = player.Wire.Take();
        Assert.Equal(2, sent.Length);
        Hex.Same((byte[])
            [
                0x07, 0x00, 0x5C, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x04, 0x00, 0x00, 0x08, 0x00, 0x00,
                .. Pcm(48000, 2), .. Pcm(44100, 2), .. Pcm(22050, 2), .. Pcm(44100, 1),
            ],
            sent[0]);
        Hex.Same("0c 00 04 00 02 00 00 00", sent[1]);

        // The server tries the connection.
        player.Channel.Receive([0x06, 0x00, 0x04, 0x00, 0x34, 0x12, 0x00, 0x00]);
        Hex.Same("06 00 04 00 34 12 00 00", player.Wire.One());

        // A block in one message, in the first format. Playing it takes seven milliseconds.
        player.Playing = 7;
        var first = Sound(4800);
        player.Channel.Receive(Wave2(0x1000, 0, 1, first));
        Hex.Same("05 00 04 00 07 10 01 00", player.Wire.One());

        // A block in two messages, in the second format; the time counts from the arrival of the second.
        var second = Sound(4410);
        var (info, wave) = Waves(0xFFFE, 1, 2, second);
        player.Channel.Receive(info);
        player.Now += 500;
        player.Wire.Nothing();
        player.Channel.Receive(wave);
        Hex.Same("05 00 04 00 05 00 02 00", player.Wire.One());

        // Switched off: confirmed all the same, played not at all.
        player.Channel.Enabled = false;
        player.Channel.Receive(Wave2(0x2000, 0, 3, first));
        (info, wave) = Waves(0x2001, 1, 4, second);
        player.Channel.Receive(info);
        player.Channel.Receive(wave);
        Assert.Equal(["05 00 04 00 00 20 03 00", "05 00 04 00 01 20 04 00"], player.Wire.Take().Select(message => Hex.Text(message)));
        player.Channel.Enabled = true;

        // The server stops, changes the volume, and goes on: the format list still stands.
        player.Channel.Receive([0x01, 0x00, 0x00, 0x00]);
        player.Channel.Receive([0x03, 0x00, 0x04, 0x00, 0xFF, 0x7F, 0xFF, 0x7F]);
        player.Channel.Receive([0x04, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00]);
        player.Wire.Nothing();
        Assert.Equal(1, player.Stops);
        player.Channel.Receive(Wave2(0x3000, 3, 5, Sound(882)));
        Hex.Same("05 00 04 00 07 30 05 00", player.Wire.One());

        Assert.Equal(3, player.Played.Count);
        Assert.Equal((48000, 2), (player.Played[0].Rate, player.Played[0].Channels));
        Hex.Same(first, player.Played[0].Pcm);

        // 4410 bytes are 1102 frames of two channels and half a frame; the half is not played.
        Assert.Equal((44100, 2), (player.Played[1].Rate, player.Played[1].Channels));
        Hex.Same(second.AsSpan(0, 4408), player.Played[1].Pcm);
        Assert.Equal((44100, 1), (player.Played[2].Rate, player.Played[2].Channels));
        Assert.Empty(player.Wire.Log);
    }

    [Fact]
    public void Formats_are_picked_and_ordered()
    {
        var player = new Player();

        // Only what a player can do something with: 16-bit PCM, one to eight channels, a sane rate, a block
        // of two bytes per channel and next to no extra data. 48000 Hz stereo and 44100 Hz stereo are listed
        // once, in that order, wherever the server had them.
        player.Channel.Receive(ServerFormats(
            8,
            Pcm(22050, 1),
            Pcm(44100, 2),
            Pcm(44100, 2),
            Format(1, 0, 44100, 16),
            Format(1, 9, 44100, 16),
            Pcm(7999, 2),
            Pcm(192001, 2),
            [.. Pcm(32000, 2).AsSpan(0, 12), 5, 0, 16, 0, 0, 0],
            Format(1, 2, 16000, 16, new byte[65]),
            Format(1, 2, 16000, 16, new byte[64]),
            Format(0xFFFE, 2, 48000, 16, new byte[22]),
            Pcm(48000, 2),
            Pcm(8000, 8),
            Pcm(192000, 1)));

        var answer = player.Wire.Take()[0];
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(answer.AsSpan(18)));
        Hex.Same(
            (byte[])[.. Pcm(48000, 2), .. Pcm(44100, 2), .. Pcm(22050, 1), .. Format(1, 2, 16000, 16, new byte[64]), .. Pcm(8000, 8), .. Pcm(192000, 1)],
            answer.AsSpan(24));

        // A wave says which format it is in by its place in that list.
        player.Channel.Receive(Wave2(0, 4, 0, Sound(64)));
        Assert.Equal((8000, 8), (player.Played[0].Rate, player.Played[0].Channels));
    }

    [Fact]
    public void A_server_without_pcm_gets_an_empty_list_and_plays_nothing()
    {
        var player = new Player();

        player.Channel.Receive(ServerFormats(8, Format(6, 2, 22050, 8), Format(0xA106, 2, 44100, 16)));

        Hex.Same("07 00 14 00 01 00 00 00 ff ff ff ff 00 00 00 00 00 00 00 00 00 08 00 00", player.Wire.Take()[0]);
        Assert.Single(player.Wire.Log);

        // A server that sends sound anyway has it confirmed and not played.
        player.Channel.Receive(Wave2(0x10, 0, 9, Sound(64)));
        Hex.Same("05 00 04 00 10 00 09 00", player.Wire.One());
        Assert.Empty(player.Played);
    }

    [Fact]
    public void The_list_stops_at_32_formats()
    {
        var player = new Player();

        player.Channel.Receive(ServerFormats(8, Enumerable.Range(0, 100).Select(i => Pcm((uint)(8000 + i), 1)).ToArray()));

        var answer = player.Wire.Take()[0];
        Assert.Equal(SoundChannel.MaxFormats, BinaryPrimitives.ReadUInt16LittleEndian(answer.AsSpan(18)));
        Assert.Equal(24 + SoundChannel.MaxFormats * 18, answer.Length);
        Assert.Equal(answer.Length - 4, BinaryPrimitives.ReadUInt16LittleEndian(answer.AsSpan(2)));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(8, true)]
    public void The_quality_mode_goes_to_servers_from_version_6_on(int version, bool expected)
    {
        var player = new Player();

        player.Channel.Receive(ServerFormats((ushort)version, Pcm(44100, 2)));

        Assert.Equal(expected ? 2 : 1, player.Wire.Take().Length);
    }

    [Fact]
    public void The_time_stamp_of_a_confirmation_wraps_around()
    {
        var player = new Player();
        player.Channel.Receive(ServerFormats(8, Pcm(44100, 2)));
        player.Wire.Take();

        player.Playing = 3;
        player.Channel.Receive(Wave2(0xFFFF, 0, 0xFF, Sound(8)));
        Hex.Same("05 00 04 00 02 00 ff 00", player.Wire.One());

        // A handler that takes longer than the time stamp can count: the low sixteen bits.
        player.Playing = 70000;
        player.Channel.Receive(Wave2(0x0001, 0, 0, Sound(8)));
        Hex.Same("05 00 04 00 71 11 00 00", player.Wire.One());

        // A clock that jumps back does not make the client quicker than the server.
        player.Playing = -5000;
        player.Channel.Receive(Wave2(0x0100, 0, 1, Sound(8)));
        Hex.Same("05 00 04 00 00 01 01 00", player.Wire.One());
    }

    [Fact]
    public void Waves_that_do_not_hold_together_are_confirmed_and_not_played()
    {
        var player = new Player();
        player.Channel.Receive(ServerFormats(6, Pcm(44100, 2)));
        player.Wire.Take();
        var sound = Sound(400);

        // A format that was never agreed on, in both kinds of wave.
        player.Channel.Receive(Wave2(1, 1, 1, sound));
        var (info, wave) = Waves(2, 7, 2, sound);
        player.Channel.Receive(info);
        player.Channel.Receive(wave);

        // The second message shorter than the first one announced.
        (info, wave) = Waves(3, 0, 3, sound);
        player.Channel.Receive(info);
        player.Channel.Receive(wave.AsSpan(0, 399));

        // A wave without sound, and one with half a frame only.
        player.Channel.Receive(Wave2(4, 0, 4, []));
        player.Channel.Receive(Wave2(5, 0, 5, [1, 2, 3]));

        Assert.Equal(
            ["05 00 04 00 01 00 01 00", "05 00 04 00 02 00 02 00", "05 00 04 00 03 00 03 00", "05 00 04 00 04 00 04 00", "05 00 04 00 05 00 05 00"],
            player.Wire.Take().Select(message => Hex.Text(message)));
        Assert.Empty(player.Played);
        Assert.Equal(3, player.Wire.Log.Count);

        // The sequence is intact: the next block of either kind plays.
        (info, wave) = Waves(6, 0, 6, sound);
        player.Channel.Receive(info);
        player.Channel.Receive([.. wave, 0xEE, 0xEE]);
        player.Channel.Receive(Wave2(7, 0, 7, sound));
        Assert.Equal(2, player.Played.Count);
        Hex.Same(sound, player.Played[0].Pcm);
        Hex.Same(sound, player.Played[1].Pcm);
    }

    [Fact]
    public void Messages_that_do_not_hold_together_are_dropped()
    {
        var player = new Player();
        player.Channel.Receive(ServerFormats(8, Pcm(44100, 2)));
        player.Wire.Take();
        byte[][] broken =
        [
            [],
            [0x07, 0x00, 0x14],                                       // shorter than the header
            [0x0D, 0x00, 0x20, 0x00, 1, 2, 3, 4],                     // a size beyond the message
            [0x0D, 0x00, 0x0B, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],   // a wave too short for its fields
            [0x02, 0x00, 0x10, 0x00, 0, 0, 0, 0, 0, 0, 0],            // a wave info too short for its fields
            [0x02, 0x00, 0x0B, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4],   // a wave info for less than four bytes of sound
            [0x06, 0x00, 0x03, 0x00, 1, 2, 3],                        // a training without its size
            [0x07, 0x00, 0x13, 0x00, .. new byte[19]],                // formats without their fixed part
            [0x07, 0x00, 0x14, 0x00, .. new byte[14], 2, 0, 0, 8, 0, 0],                       // two formats announced, none there
            [0x07, 0x00, 0x26, 0x00, .. new byte[14], 2, 0, 0, 8, 0, 0, .. Pcm(44100, 2)],     // two announced, one there
            [0x07, 0x00, 0x26, 0x00, .. new byte[14], 1, 0, 0, 8, 0, 0, .. Pcm(44100, 2).AsSpan(0, 16), 9, 0],   // extra data that is not there
            [0x08, 0x00, 0x00, 0x00],                                 // sound over UDP: a key ...
            [0x09, 0x00, 0x00, 0x00],
            [0x0A, 0x00, 0x00, 0x00],
            [0x0B, 0x00, 0x00, 0x00],
            [0x0C, 0x00, 0x04, 0x00, 0, 0, 0, 0],                     // the client's own quality mode
            [0x05, 0x00, 0x04, 0x00, 0, 0, 0, 0],                     // the client's own confirmation
            [0x00, 0x00, 0x00, 0x00],
            [0xFF, 0x00, 0x00, 0x00],
        ];

        foreach (var message in broken)
        {
            player.Channel.Receive(message);
        }

        player.Wire.Nothing();
        Assert.Empty(player.Played);
        Assert.Equal(broken.Length, player.Wire.Log.Count);

        // The formats agreed on before are still the formats.
        player.Channel.Receive(Wave2(1, 0, 1, Sound(8)));
        Assert.Single(player.Played);
    }

    [Fact]
    public void The_examples_cut_short_are_dropped()
    {
        byte[][] examples =
        [
            Hex.Bytes(ServerFormatsExample),
            Elided(1024, TrainingStart, TrainingEnd),
            Elided(264, Wave2Start, Wave2End),
        ];

        foreach (var example in examples)
        {
            var player = new Player();
            player.Channel.Receive(ServerFormats(8, Pcm(48000, 2), Pcm(44100, 2), Pcm(22050, 2), Pcm(44100, 1)));
            player.Wire.Take();
            for (var length = 0; length < example.Length; length++)
            {
                player.Channel.Receive(example.AsSpan(0, length));
            }

            // The size in the header covers the whole message, so nothing short of it is one.
            player.Wire.Nothing();
            Assert.Empty(player.Played);
            Assert.Equal(example.Length, player.Wire.Log.Count);
        }

        // The two messages of a wave, each cut short: whatever arrives after a WaveInfo is taken for its
        // Wave, so every cut is confirmed - and never played, and never thrown on.
        var cut = new Player();
        cut.Channel.Receive(ServerFormats(6, [.. Enumerable.Range(0, 16).Select(i => Pcm((uint)(8000 + i), 1))]));
        cut.Wire.Take();
        var info = Hex.Bytes(WaveInfo);
        var wave = Elided(585, WaveStart, WaveEnd);
        for (var length = 0; length < info.Length; length++)
        {
            cut.Channel.Receive(info.AsSpan(0, length));
        }

        cut.Wire.Nothing();
        for (var length = 0; length < wave.Length; length++)
        {
            cut.Channel.Receive(info);
            cut.Channel.Receive(wave.AsSpan(0, length));
        }

        Assert.Equal(wave.Length, cut.Wire.Take().Length);
        Assert.Empty(cut.Played);
    }

    [Fact]
    public void A_session_that_ends_forgets_the_formats()
    {
        var player = new Player();
        player.Channel.Receive(ServerFormats(6, Pcm(44100, 2)));
        var (info, wave) = Waves(1, 0, 1, Sound(8));
        player.Channel.Receive(info);
        player.Wire.Take();

        player.Channel.Closed();

        // What would have been the second half of a wave is a message like any other again.
        player.Channel.Receive(wave);
        player.Channel.Receive(Wave2(2, 0, 2, Sound(8)));
        Assert.Empty(player.Played);
        Hex.Same("05 00 04 00 02 00 02 00", player.Wire.One());

        player.Channel.Receive(ServerFormats(8, Pcm(48000, 2)));
        player.Wire.Take();
        player.Channel.Receive(Wave2(3, 0, 3, Sound(8)));
        Assert.Equal((48000, 2), (player.Played[0].Rate, player.Played[0].Channels));
    }

    [Fact]
    public void A_handler_or_a_send_that_throws_is_reported()
    {
        var player = new Player();
        player.Channel.Samples += (_, _, _) => throw new InvalidOperationException("no speaker");
        player.Channel.Receive(ServerFormats(8, Pcm(48000, 2)));
        player.Wire.Take();

        // The block is confirmed all the same.
        player.Channel.Receive(Wave2(3, 0, 3, Sound(8)));
        Assert.Single(player.Wire.Take());
        Assert.Contains("no speaker", Assert.Single(player.Wire.Log));

        player.Channel.Send = _ => throw new IOException("gone");
        player.Channel.Receive(Wave2(4, 0, 4, Sound(8)));
        Assert.Equal(3, player.Wire.Log.Count);

        player.Channel.Send = null;
        player.Channel.Receive(Wave2(5, 0, 5, Sound(8)));
        Assert.Equal(5, player.Wire.Log.Count);
    }

    [Fact]
    public void The_options_ask_for_no_compression()
    {
        var channel = new SoundChannel();

        Assert.Equal("rdpsnd", channel.Name);
        Assert.Equal(0xC0000000, channel.Options);
        Assert.True(channel.Enabled);
    }
}
