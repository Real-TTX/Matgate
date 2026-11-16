using System.Text;
using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

public class Md4Tests
{
    // The test suite of RFC 1320, appendix A.5.
    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043f8582f241db351ce627e153e7f0e4")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Rfc1320TestSuite(string text, string expected)
    {
        Assert.Equal(expected, Hex.Text(Md4.HashData(Encoding.ASCII.GetBytes(text))));
    }

    // The RFC has no message that ends right at the edges of the padding. These are 'a' repeated, hashed with
    // OpenSSL 3.5 (openssl dgst -md4 -provider legacy): 55 bytes are the most that fit into one block with
    // the padding, 56 the first that need a second one.
    [Theory]
    [InlineData(55, "c889c81dd86c4d2e025778944ea02881")]
    [InlineData(56, "d5f9a9e9257077a5f08b0b92f348b0ad")]
    [InlineData(57, "872097e6f78e3b53f890459d03bc6fb7")]
    [InlineData(63, "7ea3da77432d44c323671097d1348fc8")]
    [InlineData(64, "52f5076fabd22680234a3fa9f9dc5732")]
    [InlineData(65, "330e377bf231f3cacfecc2c182fe7e5b")]
    [InlineData(119, "e65dd227ccef97fa1d34d70189120f76")]
    [InlineData(120, "b03ddbd470b47c013e0c7ab2ddd763db")]
    [InlineData(128, "cb4a20a561558e29460190c91dced59f")]
    [InlineData(1000, "5f1bf26a8067c9159b91f1440f7c9e8a")]
    public void BlockBoundaries(int length, string expected)
    {
        Assert.Equal(expected, Hex.Text(Md4.HashData(Encoding.ASCII.GetBytes(new string('a', length)))));
    }

    [Fact]
    public void WritesIntoTheDestinationAndRefusesOneThatIsTooShort()
    {
        var destination = new byte[20];
        Md4.HashData("abc"u8, destination.AsSpan(2));
        Assert.Equal("0000a448017aaf21d8525fc10ae87aa6729d0000", Hex.Text(destination));
        Assert.Throws<ArgumentException>(() => Md4.HashData("abc"u8, new byte[15]));
    }
}
