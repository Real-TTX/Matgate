using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

public class Rc4Tests
{
    // All of RFC 6229: fourteen keys from 40 to 256 bits, each with 16 bytes of key stream at eighteen
    // offsets up to 4096. TestData/rfc6229-rc4.txt is the RFC's tables reduced to "key" and "offset" lines.
    [Fact]
    public void Rfc6229KeyStreams()
    {
        var keys = 0;
        var rows = 0;
        byte[] stream = [];
        foreach (var line in ReadVectors())
        {
            var parts = line.Split(' ');
            if (parts[0] == "key")
            {
                using var cipher = new Rc4(Hex.Bytes(parts[1]));
                stream = cipher.Process(new byte[4112]);
                keys++;
                continue;
            }

            var offset = int.Parse(parts[0]);
            Assert.Equal(parts[1], Hex.Text(stream.AsSpan(offset, 16)));
            rows++;
        }

        Assert.Equal(14, keys);
        Assert.Equal(252, rows);
    }

    // NTLM seals one message after the other with the same cipher, so pieces must add up to the whole.
    [Fact]
    public void TheStreamContinuesAcrossCalls()
    {
        var key = Hex.Bytes("0102030405060708090a0b0c0d0e0f10");
        var data = new byte[1000];
        new Random(7).NextBytes(data);

        using var whole = new Rc4(key);
        var expected = whole.Process(data);

        using var pieces = new Rc4(key);
        var actual = new byte[data.Length];
        var position = 0;
        foreach (var size in new[] { 1, 15, 16, 0, 255, 256, 457 })
        {
            pieces.Process(data.AsSpan(position, size), actual.AsSpan(position));
            position += size;
        }

        Assert.Equal(data.Length, position);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecryptsWhatItEncryptedAndWorksInPlace()
    {
        var key = Hex.Bytes("ebb46227c6cc8b37641910833222772a");
        var plaintext = "Network Level Authentication"u8.ToArray();
        var buffer = (byte[])plaintext.Clone();

        using (var encrypt = new Rc4(key))
        {
            encrypt.Process(buffer, buffer);
        }

        Assert.NotEqual(plaintext, buffer);
        using var decrypt = new Rc4(key);
        Assert.Equal(plaintext, decrypt.Process(buffer));
    }

    [Fact]
    public void RefusesImpossibleKeysAndUseAfterDispose()
    {
        Assert.Throws<ArgumentException>(() => new Rc4([]));
        Assert.Throws<ArgumentException>(() => new Rc4(new byte[257]));

        // A cleared state would return the input unchanged - plain text where a cipher text is expected.
        var cipher = new Rc4([1, 2, 3, 4, 5]);
        cipher.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cipher.Process(new byte[4]));
    }

    private static IEnumerable<string> ReadVectors()
    {
        using var resource = typeof(Rc4Tests).Assembly.GetManifestResourceStream("rfc6229-rc4.txt")
            ?? throw new InvalidOperationException("The RFC 6229 vectors are not embedded in the test assembly.");
        using var reader = new StreamReader(resource);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }
}
