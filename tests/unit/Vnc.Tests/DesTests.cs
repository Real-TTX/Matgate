using System.Security.Cryptography;
using Xunit;

namespace Matgate.Display.Vnc.Tests;

public class DesTests
{
    // The worked example every description of DES uses, the first line of the NBS "variable plaintext" table,
    // and the first block of the example in FIPS 81.
    [Theory]
    [InlineData("133457799BBCDFF1", "0123456789ABCDEF", "85E813540F0AB405")]
    [InlineData("0101010101010101", "95F8A5E5DD31D900", "8000000000000000")]
    [InlineData("0123456789ABCDEF", "4E6F772069732074", "3FA40E8A984D4815")]
    [InlineData("0123456789ABCDEF", "68652074696D6520", "6A271787AB8883F9")]
    [InlineData("0123456789ABCDEF", "666F7220616C6C20", "893D51EC4B563B53")]
    public void Encrypts_the_published_examples(string key, string block, string expected)
    {
        var output = new byte[8];
        VncDes.Encrypt(Convert.FromHexString(key), Convert.FromHexString(block), output);
        Assert.Equal(expected, Convert.ToHexString(output));
    }

    // Challenges and the answers a VNC server expects, worked out with the DES of Windows (CNG) and the key's
    // bits mirrored by hand - nothing of Matgate's code was involved.
    [Theory]
    [InlineData("password", "00112233445566778899AABBCCDDEEFF", "B7B9C87777661A7A2299733209BFDFCE")]
    [InlineData("secret", "0123456789ABCDEFFEDCBA9876543210", "2D2A0DD383426C2EBF1186809BBA0F9C")]
    [InlineData("a-much-longer-password", "A5A5A5A5A5A5A5A55A5A5A5A5A5A5A5A", "ED7EFD77730059EAC15C79B6DF477C74")]
    public void Answers_a_challenge_the_way_VNC_wants_it(string password, string challenge, string expected)
    {
        var response = new byte[16];
        VncDes.Respond(System.Text.Encoding.UTF8.GetBytes(password), Convert.FromHexString(challenge), response);
        Assert.Equal(expected, Convert.ToHexString(response));
    }

    [Fact]
    public void Only_the_first_eight_bytes_of_a_password_count()
    {
        var challenge = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var first = new byte[16];
        var second = new byte[16];
        VncDes.Respond("12345678"u8, challenge, first);
        VncDes.Respond("12345678 and whatever follows"u8, challenge, second);
        Assert.Equal(first, second);

        VncDes.Respond("1234567"u8, challenge, second);
        Assert.NotEqual(first, second);
    }

    // Every entry of every table gets its turn here: a thousand random blocks against the DES the platform
    // brings. Where the platform has none (OpenSSL 3 without its legacy provider - the very reason for
    // VncDes), there is nothing to compare with and the examples above have to do.
    [Fact]
    public void Agrees_with_the_platforms_DES_where_there_is_one()
    {
        var random = new Random(4711);
        var compared = 0;
        for (var round = 0; round < 1000; round++)
        {
            var key = new byte[8];
            var block = new byte[8];
            random.NextBytes(key);
            random.NextBytes(block);
            if (DES.IsWeakKey(key) || DES.IsSemiWeakKey(key))
            {
                continue;
            }

            byte[] expected;
            try
            {
                using var des = DES.Create();
                des.Key = key;
                expected = des.EncryptEcb(block, PaddingMode.None);
            }
            catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
            {
                return;
            }

            var output = new byte[8];
            VncDes.Encrypt(key, block, output);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(output));
            compared++;
        }

        Assert.True(compared > 900);
    }
}
