using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Matgate.Display.Vnc.Tests;

public class SecurityTests
{
    private const int None = VncSecurity.None;
    private const int VncAuth = VncSecurity.VncAuthentication;
    private const int Apple = VncSecurity.AppleRemoteDesktop;

    // What is taken when a server offers several ways in: the strongest - and between ways that are a match
    // for each other, the one that fits what the connection carries.
    [Theory]
    [InlineData(new[] { None, VncAuth }, false, false, VncAuth)]
    [InlineData(new[] { VncAuth, None }, false, true, VncAuth)]
    [InlineData(new[] { None, VncAuth, Apple }, false, false, Apple)]
    [InlineData(new[] { None, VncAuth, Apple }, true, true, Apple)]
    [InlineData(new[] { None, VncAuth, Apple }, false, true, VncAuth)] // a password alone is the VNC password
    [InlineData(new[] { None, Apple }, false, true, Apple)]
    [InlineData(new[] { None, 5, 6, 16, 129 }, true, true, None)]
    [InlineData(new[] { 5, 6, 16, 129, 130 }, true, true, 0)] // nothing Matgate knows
    public void The_outer_list(int[] offered, bool haveUsername, bool havePassword, int expected)
    {
        Assert.Equal(expected, Best(offered, inVeNCrypt: false, haveUsername, havePassword));
    }

    [Theory]
    [InlineData(new[] { 256, 257, 258, 259, 260, 261, 262 }, false, true, 261)] // X509Vnc for a password alone
    [InlineData(new[] { 256, 257, 258, 259, 260, 261, 262 }, true, true, 262)] // X509Plain for a user name
    [InlineData(new[] { 256, 257, 258, 259, 260, 261, 262 }, false, false, 261)]
    [InlineData(new[] { 260, 262 }, false, true, 262)]
    [InlineData(new[] { 260, 2, 1 }, false, true, 260)] // encrypted before anything that is not
    [InlineData(new[] { 258, 2 }, false, true, 2)] // TigerVNC's default: TLS without a certificate cannot be done
    [InlineData(new[] { 257, 258, 259 }, true, true, 0)]
    [InlineData(new[] { 256, 2 }, true, true, 256)] // Plain where there is a user name to sign in with
    [InlineData(new[] { 256, 2 }, false, true, 2)]
    [InlineData(new[] { 256, 1 }, false, false, 256)]
    [InlineData(new[] { 263, 264 }, true, true, 0)] // SASL
    public void The_list_inside_VeNCrypt(int[] offered, bool haveUsername, bool havePassword, int expected)
    {
        Assert.Equal(expected, Best(offered, inVeNCrypt: true, haveUsername, havePassword));
    }

    [Fact]
    public void A_type_counts_the_same_inside_VeNCrypt_and_outside()
    {
        // Once inside VeNCrypt, taking the same type there saves connecting again for the one outside.
        foreach (var type in new[] { None, VncAuth })
        {
            Assert.Equal(VncSecurity.Rank(type, false, false, true), VncSecurity.Rank(type, true, false, true));
        }

        // The types that only exist inside mean nothing outside, and Apple's nothing inside.
        Assert.Equal(0, VncSecurity.Rank(VncSecurity.X509Plain, false, true, true));
        Assert.Equal(0, VncSecurity.Rank(VncSecurity.Plain, false, true, true));
        Assert.Equal(0, VncSecurity.Rank(Apple, true, true, true));
    }

    [Fact]
    public void The_Plain_login_is_two_lengths_and_two_texts()
    {
        var message = VncSecurity.PlainLogin("jörg", "pass word");
        var expected = new Packet().U32(5).U32(9).Bytes(Encoding.UTF8.GetBytes("jörg")).Text("pass word").ToArray();
        Assert.Equal(expected, message);
    }

    // Apple Remote Desktop cannot be tried against a Mac here. This is the server's side of it as the
    // specification describes it, worked out separately: its own key pair, the shared secret, MD5, AES.
    [Theory]
    [InlineData(128, 2, "admin", "secret")]
    [InlineData(128, 5, "", "")]
    [InlineData(128, 2, "jörg", "pässwörd – mit Umlauten")]
    [InlineData(64, 3, "u23456789012345678901234567890123456789012345678901234567890123", "p")] // 63 bytes: just fits
    [InlineData(160, 2, "u234567890123456789012345678901234567890123456789012345678901234", "a password that is longer than sixty-three bytes and so has to be cut off there")]
    public void The_Apple_login_can_be_read_by_a_server(int keySize, int generator, string username, string password)
    {
        var modulus = Modulus(keySize);
        var serverSecret = new BigInteger(RandomNumberGenerator.GetBytes(keySize), isUnsigned: true, isBigEndian: true);
        var serverKey = BigInteger.ModPow(generator, serverSecret, modulus);

        var answer = VncSecurity.AppleLogin(generator, Bytes(modulus, keySize), Bytes(serverKey, keySize), username, password);

        Assert.Equal(128 + keySize, answer.Length);
        var clientKey = new BigInteger(answer.AsSpan(128), isUnsigned: true, isBigEndian: true);
        var shared = BigInteger.ModPow(clientKey, serverSecret, modulus);
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Bytes(shared, keySize));
        var block = aes.DecryptEcb(answer.AsSpan(0, 128), PaddingMode.None);

        Assert.Equal(Cut(username), Field(block.AsSpan(0, 64)));
        Assert.Equal(Cut(password), Field(block.AsSpan(64, 64)));

        // The rest of both fields is noise, and so is Matgate's key: other bytes every time.
        var again = VncSecurity.AppleLogin(generator, Bytes(modulus, keySize), Bytes(serverKey, keySize), username, password);
        Assert.NotEqual(Convert.ToHexString(answer.AsSpan(0, 128)), Convert.ToHexString(again.AsSpan(0, 128)));
        Assert.NotEqual(Convert.ToHexString(answer.AsSpan(128)), Convert.ToHexString(again.AsSpan(128)));
    }

    [Fact]
    public void The_Apple_login_refuses_numbers_that_make_no_key()
    {
        Assert.Throws<VncException>(() => VncSecurity.AppleLogin(2, new byte[128], new byte[128], "a", "b"));
        Assert.Throws<VncException>(() => VncSecurity.AppleLogin(0, Bytes(Modulus(128), 128), new byte[128], "a", "b"));
        Assert.Throws<VncException>(() => VncSecurity.AppleLogin(1, Bytes(Modulus(128), 128), new byte[128], "a", "b"));
    }

    private static int Best(int[] offered, bool inVeNCrypt, bool haveUsername, bool havePassword)
    {
        var best = 0;
        var bestRank = 0;
        foreach (var type in offered)
        {
            var rank = VncSecurity.Rank(type, inVeNCrypt, haveUsername, havePassword);
            if (rank > bestRank)
            {
                best = type;
                bestRank = rank;
            }
        }

        return best;
    }

    // The text a server finds in a field: up to the zero, at most 63 bytes.
    private static string Field(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        Assert.InRange(end, 0, 63);
        return Convert.ToHexString(field[..end]);
    }

    private static string Cut(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 63)));
    }

    private static byte[] Bytes(BigInteger value, int size)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[size];
        bytes.CopyTo(result.AsSpan(size - bytes.Length));
        return result;
    }

    // An odd number that fills `size` bytes. A real server sends a prime; that both sides arrive at the same
    // secret does not depend on it, and that is what is looked at here.
    private static BigInteger Modulus(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        bytes[0] |= 0x80;
        bytes[^1] |= 1;
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }
}

public class ClipboardTests
{
    [Fact]
    public void Text_for_an_old_server_is_Latin_1_with_line_feeds()
    {
        var message = VncClipboard.PlainMessage("Grüße\r\nzweite Zeile\rdritte\n€");
        var expected = new Packet().U8(6).Zeros(3).U32(27).Text("Grüße\nzweite Zeile\ndritte\n?").ToArray();
        Assert.Equal(expected, message);
    }

    [Fact]
    public void Text_from_an_old_server_loses_its_zeros_and_carriage_returns()
    {
        Assert.Equal("Grüße\nzwei\ndrei", VncClipboard.PlainText(Encoding.Latin1.GetBytes("Grüße\r\nzwei\rdrei\0\0")));
        Assert.Equal("", VncClipboard.PlainText([]));
    }

    [Fact]
    public void The_caps_message_names_text_and_every_action()
    {
        // ClientCutText, a length of minus eight, the flags, and one size - zero - for the one format.
        var expected = new Packet().U8(6).Zeros(3).S32(-8).U32(0x1F000001).U32(0).ToArray();
        Assert.Equal(expected, VncClipboard.CapsMessage());
    }

    [Fact]
    public void An_action_is_its_flags_and_nothing_else()
    {
        var expected = new Packet().U8(6).Zeros(3).S32(-4).U32(0x08000001).ToArray();
        Assert.Equal(expected, VncClipboard.ActionMessage(VncClipboard.Notify | VncClipboard.Text));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("Zeile eins\nZeile zwei\r\nZeile drei\rvier – mit ‚Sonderzeichen‘ und 🙂")]
    public void A_provide_message_carries_the_text_as_the_extension_wants_it(string text)
    {
        var message = VncClipboard.ProvideMessage(text)!;
        Assert.Equal(6, message[0]);
        var length = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4));
        Assert.Equal(-(message.Length - 8), length);
        Assert.Equal(VncClipboard.Provide | VncClipboard.Text, BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(8)));

        // Unpacked the way a server does it: the size, then that many bytes - UTF-8, CR LF, a zero at the end.
        using var zlib = new ZLibStream(new MemoryStream(message, 12, message.Length - 12), CompressionMode.Decompress);
        var header = new byte[4];
        zlib.ReadExactly(header);
        var data = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        zlib.ReadExactly(data);
        var expected = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n") + "\0";
        Assert.Equal(expected, Encoding.UTF8.GetString(data));

        // And back again through the client's own reading: line feeds, no zero.
        Assert.Equal(text.Replace("\r\n", "\n").Replace('\r', '\n'), VncClipboard.ProvidedText(VncClipboard.Provide | VncClipboard.Text, message.AsSpan(12).ToArray()));
    }

    [Fact]
    public void A_provide_from_a_server_that_does_not_close_its_stream()
    {
        // TigerVNC flushes its zlib stream and leaves it open; the text is all there nevertheless.
        using var packer = new Packer();
        var text = Encoding.UTF8.GetBytes("von TigerVNC\r\nzweite Zeile\0");
        var packed = packer.Pack(new Packet().U32((uint)text.Length).Bytes(text).ToArray());
        Assert.Equal("von TigerVNC\nzweite Zeile", VncClipboard.ProvidedText(VncClipboard.Provide | VncClipboard.Text, packed));
    }

    [Fact]
    public void A_provide_that_is_broken_gives_no_text()
    {
        using var packer = new Packer();
        var text = Encoding.UTF8.GetBytes("text\0");
        var good = packer.Pack(new Packet().U32((uint)text.Length).Bytes(text).ToArray());
        const uint Flags = VncClipboard.Provide | VncClipboard.Text;

        // No text among the formats; nothing at all; noise; a size that the data does not keep; one that is absurd.
        Assert.Null(VncClipboard.ProvidedText(VncClipboard.Provide | 2, good));
        Assert.Null(VncClipboard.ProvidedText(Flags, []));
        Assert.Null(VncClipboard.ProvidedText(Flags, [1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Null(VncClipboard.ProvidedText(Flags, good.AsSpan(0, good.Length / 2).ToArray()));

        using var second = new Packer();
        Assert.Null(VncClipboard.ProvidedText(Flags, second.Pack(new Packet().U32(1000).Bytes(text).ToArray())));

        using var third = new Packer();
        Assert.Null(VncClipboard.ProvidedText(Flags, third.Pack(new Packet().U32(0xFFFFFFFF).Bytes(text).ToArray())));
    }

    [Fact]
    public void Text_that_is_too_long_is_not_sent()
    {
        var huge = new string('x', VncClipboard.MaxTextBytes + 1);
        Assert.Null(VncClipboard.PlainMessage(huge));
        Assert.Null(VncClipboard.ProvideMessage(huge));
        Assert.NotNull(VncClipboard.PlainMessage(new string('x', VncClipboard.MaxTextBytes)));
    }
}
