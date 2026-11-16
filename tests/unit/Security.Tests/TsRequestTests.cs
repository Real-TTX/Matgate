using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

// TSRequest on the wire (MS-CSSP 2.2.1): what the client writes, what it reads - also when an encoder on the
// other side is less strict than DER - and what it makes of bytes that are no TSRequest at all.
public class TsRequestTests
{
    [Fact]
    public void EncodesTheFirstMessageAsDer()
    {
        var encoded = new TsRequest { Version = 6, NegoToken = [1, 2, 3] }.Encode();

        // SEQUENCE { [0] INTEGER 6, [1] SEQUENCE OF SEQUENCE { [0] OCTET STRING 01 02 03 } }
        Assert.Equal("3012 a003 020106 a10b 3009 3007 a005 0403 010203".Replace(" ", ""), Hex.Text(encoded));
    }

    [Fact]
    public void EncodesEveryFieldInItsPlace()
    {
        var encoded = new TsRequest
        {
            Version = 6,
            NegoToken = [0x11],
            AuthInfo = [0x22],
            PubKeyAuth = [0x33],
            ErrorCode = 0xC000006D,
            ClientNonce = [0x55],
        }.Encode();

        // The status is written the way servers write it: as the signed 32-bit number it is, four bytes.
        Assert.Equal(
            "3027 a003 020106 a109 3007 3005 a003 0401 11 a203 0401 22 a303 0401 33 a406 0204 c000006d a503 0401 55".Replace(" ", ""),
            Hex.Text(encoded));
    }

    [Fact]
    public void RoundTrips()
    {
        var random = new Random(1);
        foreach (var size in new[] { 0, 1, 127, 128, 255, 256, 1000, 20_000 })
        {
            var token = new byte[size];
            var authInfo = new byte[size + 3];
            var pubKeyAuth = new byte[size + 7];
            var nonce = new byte[32];
            random.NextBytes(token);
            random.NextBytes(authInfo);
            random.NextBytes(pubKeyAuth);
            random.NextBytes(nonce);

            var original = new TsRequest { Version = 6, NegoToken = token, AuthInfo = authInfo, PubKeyAuth = pubKeyAuth, ErrorCode = 0xC0000234, ClientNonce = nonce };
            var encoded = original.Encode();
            var decoded = TsRequest.Decode(encoded);

            Assert.Equal(6, decoded.Version);
            Assert.Equal(token, decoded.NegoToken);
            Assert.Equal(authInfo, decoded.AuthInfo);
            Assert.Equal(pubKeyAuth, decoded.PubKeyAuth);
            Assert.Equal(0xC0000234u, decoded.ErrorCode);
            Assert.Equal(nonce, decoded.ClientNonce);
            Assert.Equal(encoded.Length, TsRequest.MessageLength(encoded));
        }
    }

    [Fact]
    public void AbsentFieldsStayAbsent()
    {
        var decoded = TsRequest.Decode(new TsRequest { Version = 3 }.Encode());
        Assert.Equal(3, decoded.Version);
        Assert.Null(decoded.NegoToken);
        Assert.Null(decoded.AuthInfo);
        Assert.Null(decoded.PubKeyAuth);
        Assert.Null(decoded.ErrorCode);
        Assert.Null(decoded.ClientNonce);
        Assert.Equal("3005a003020103", Hex.Text(new TsRequest { Version = 3 }.Encode()));
    }

    // Hand-made encodings with lengths in the long form, also where the short form would have done. DER
    // forbids that, BER allows it, and a client that is picky here only loses servers.
    [Theory]
    [InlineData("30 81 05 a0 03 02 01 06")]                 // the message
    [InlineData("30 82 00 05 a0 03 02 01 06")]
    [InlineData("30 83 00 00 05 a0 03 02 01 06")]
    [InlineData("30 84 00 00 00 05 a0 03 02 01 06")]
    [InlineData("30 06 a0 81 03 02 01 06")]                 // the field
    [InlineData("30 07 a0 82 00 03 02 01 06")]
    [InlineData("30 06 a0 04 02 81 01 06")]                 // the number in it
    [InlineData("30 83 00 00 08 a0 82 00 04 02 81 01 06")]  // all three
    public void ReadsLongFormLengths(string hex)
    {
        var decoded = TsRequest.Decode(Hex.Bytes(hex));
        Assert.Equal(6, decoded.Version);
    }

    [Fact]
    public void ReadsAHandMadeMessageWithLongFormLengthsThroughout()
    {
        // SEQUENCE (82 01 1d: 285 bytes) {
        //   [0] (81 03) INTEGER 4
        //   [1] (82 01 0e: 270) SEQUENCE (82 01 0a: 266) { SEQUENCE (82 01 06: 262) {
        //         [0] (82 01 02: 258) OCTET STRING (81 ff: 255 bytes) } }
        //   [3] (81 02) OCTET STRING, empty }
        var token = Enumerable.Range(0, 255).Select(i => (byte)i).ToArray();
        byte[] message =
        [
            0x30, 0x82, 0x01, 0x1d,
            0xa0, 0x81, 0x03, 0x02, 0x01, 0x04,
            0xa1, 0x82, 0x01, 0x0e, 0x30, 0x82, 0x01, 0x0a, 0x30, 0x82, 0x01, 0x06, 0xa0, 0x82, 0x01, 0x02, 0x04, 0x81, 0xff, .. token,
            0xa3, 0x81, 0x02, 0x04, 0x00,
        ];

        var decoded = TsRequest.Decode(message);
        Assert.Equal(4, decoded.Version);
        Assert.Equal(token, decoded.NegoToken);
        Assert.NotNull(decoded.PubKeyAuth);
        Assert.Empty(decoded.PubKeyAuth);
        Assert.Null(decoded.AuthInfo);
        Assert.Equal(message.Length, TsRequest.MessageLength(message));
    }

    // 0xC000006D as servers encode it - the signed 32-bit number it is, negative, in four bytes - and as an
    // encoder that takes it for unsigned would: positive, with a leading zero.
    [Theory]
    [InlineData("300d a003 020106 a406 0204 c000006d")]
    [InlineData("300e a003 020106 a407 0205 00c000006d")]
    public void ReadsTheStatusInEitherEncoding(string hex)
    {
        Assert.Equal(0xC000006Du, TsRequest.Decode(Hex.Bytes(hex)).ErrorCode);
    }

    [Fact]
    public void IgnoresFieldsOfALaterVersion()
    {
        // [6] with something in it, behind the fields this client knows.
        var decoded = TsRequest.Decode(Hex.Bytes("300f a003 020106 a303 0401 33 a603 0401 77"));
        Assert.Equal(6, decoded.Version);
        Assert.Equal(new byte[] { 0x33 }, decoded.PubKeyAuth);
    }

    [Theory]
    [InlineData("")]                                              // nothing
    [InlineData("30")]                                            // a tag and no length
    [InlineData("3000")]                                          // no version
    [InlineData("3005 a003 020106 00")]                           // a byte behind the message
    [InlineData("3105 a003 020106")]                              // a SET
    [InlineData("3005 a003 040106")]                              // the version is no INTEGER
    [InlineData("3005 8003 020106")]                              // the tag of the version is not constructed
    [InlineData("3005 0203 020106")]                              // no context tag at all
    [InlineData("300a a003 020106 a003 020106")]                  // the version twice
    [InlineData("300a a003 020106 a303 020133")]                  // pubKeyAuth is no OCTET STRING
    [InlineData("3006 a004 0204 7fff")]                           // a number that claims four bytes and has two
    [InlineData("300d a003 020106 a406 0203 010000 00")]          // a byte behind the status, inside its field
    [InlineData("3012 a003 020106 a40b 0209 010000000000000000")] // a status of nine bytes
    [InlineData("3011 a003 020106 a40a 0208 00000000c000006d")]   // a number padded with zeros, which no rule allows
    [InlineData("3009 a007 0205 0100000000")]                     // a version that does not fit 32 bits
    [InlineData("300b a003 020106 a104 3002 3000")]               // a token entry without a token
    [InlineData("3012 a003 020106 a10b 3009 3007 a005 0403 0102")] // the token shorter than its length
    [InlineData("3080 a003 020106 0000 00")]                      // no stated length, and a byte behind the end
    public void RefusesWhatIsNotATsRequest(string hex)
    {
        Assert.Throws<CredSspException>(() => TsRequest.Decode(Hex.Bytes(hex)));
    }

    [Fact]
    public void RefusesMoreThanOneToken()
    {
        // negoTokens with two entries, each an empty token. Nobody sends that, and guessing which one is
        // meant would be worse than saying so.
        var exception = Assert.Throws<CredSspException>(() => TsRequest.Decode(Hex.Bytes("3015 a003 020106 a10e 300c 3004 a002 0400 3004 a002 0400")));
        Assert.Contains("more than one", exception.Message);

        // One such entry is fine.
        var token = TsRequest.Decode(Hex.Bytes("300f a003 020106 a108 3006 3004 a002 0400")).NegoToken;
        Assert.NotNull(token);
        Assert.Empty(token);
    }

    [Fact]
    public void EveryTruncationIsRefusedCleanly()
    {
        var encoded = new TsRequest { Version = 6, NegoToken = new byte[300], PubKeyAuth = new byte[200], ClientNonce = new byte[32] }.Encode();
        for (var length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<CredSspException>(() => TsRequest.Decode(encoded.AsSpan(0, length)));
        }
    }

    [Fact]
    public void AMessageBeyondTheLimitIsRefusedBeforeItIsParsed()
    {
        Assert.Throws<CredSspException>(() => TsRequest.Decode(new byte[TsRequest.MaxLength + 1]));
    }

    // Garbage of every kind: pure noise, and valid messages with random damage. Whatever comes in, the
    // decoder returns a message or throws CredSspException - nothing else ever reaches the caller.
    [Fact]
    public void RandomBytesAndRandomDamageNeverGetPastTheDecoder()
    {
        var random = new Random(20261116);
        var valid = new TsRequest { Version = 6, NegoToken = new byte[120], PubKeyAuth = new byte[48], ErrorCode = 0xC000006D, ClientNonce = new byte[32] }.Encode();
        var decoded = 0;
        var refused = 0;
        for (var round = 0; round < 30_000; round++)
        {
            byte[] bytes;
            if (round % 3 == 0)
            {
                bytes = new byte[random.Next(0, 64)];
                random.NextBytes(bytes);
                if (bytes.Length > 0 && round % 2 == 0)
                {
                    bytes[0] = 0x30;
                }
            }
            else
            {
                bytes = (byte[])valid.Clone();
                if (random.Next(4) == 0)
                {
                    bytes = bytes[..random.Next(bytes.Length)];
                }

                for (var flips = random.Next(1, 4); flips > 0 && bytes.Length > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }
            }

            try
            {
                TsRequest.Decode(bytes);
                decoded++;
            }
            catch (CredSspException)
            {
                refused++;
            }

            try
            {
                var length = TsRequest.MessageLength(bytes);
                Assert.InRange(length, 0, TsRequest.MaxLength);
            }
            catch (CredSspException)
            {
            }
        }

        Assert.True(decoded > 1_000, $"only {decoded} damaged messages were still readable");
        Assert.True(refused > 10_000, $"only {refused} damaged messages were refused");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("30", 0)]
    [InlineData("3000", 2)]
    [InlineData("3005", 7)]
    [InlineData("307f", 129)]
    [InlineData("3081", 0)]
    [InlineData("308180", 131)]
    [InlineData("3082", 0)]
    [InlineData("308201", 0)]
    [InlineData("30820102", 262)]
    [InlineData("30820102ffff", 262)]
    [InlineData("3083", 0)]
    [InlineData("30830001", 0)]
    [InlineData("3083000102", 263)]
    [InlineData("308400000102", 264)]
    [InlineData("30840000ff", 0)]
    [InlineData("3082fffc", 65536)]
    public void MessageLengthFromTheFirstBytes(string start, int expected)
    {
        Assert.Equal(expected, TsRequest.MessageLength(Hex.Bytes(start)));
        Assert.Equal(expected, CredSspClient.MessageLength(Hex.Bytes(start)));
    }

    [Theory]
    [InlineData("00")]              // not a sequence
    [InlineData("a003020106")]      // a field without the message around it
    [InlineData("160301")]          // a TLS record
    [InlineData("0300000b")]        // an RDP packet
    [InlineData("3080")]            // no stated end
    [InlineData("3085")]            // a length of five bytes
    [InlineData("30ff")]
    [InlineData("3082fffd")]        // one byte over the limit
    [InlineData("308400010000")]
    [InlineData("3084ffffffff")]
    public void MessageLengthRefusesWhatCannotBeATsRequest(string start)
    {
        Assert.Throws<CredSspException>(() => TsRequest.MessageLength(Hex.Bytes(start)));
    }

    [Fact]
    public void PasswordCredentials()
    {
        var encoded = TsRequest.EncodePasswordCredentials("D", "u", "p\0w\0"u8);

        // TSCredentials { credType [0] INTEGER 1, credentials [1] OCTET STRING {
        //   TSPasswordCreds { domainName [0] OCTET STRING "D", userName [1] OCTET STRING "u", password [2] OCTET STRING "pw" } } }
        // with the strings in UTF-16 and no terminators.
        Assert.Equal(
            "301f a003 020101 a118 0416 3014 a004 0402 4400 a104 0402 7500 a206 0404 70007700".Replace(" ", ""),
            Hex.Text(encoded));
    }

    [Fact]
    public void PasswordCredentialsWithoutADomainAndWithALongPassword()
    {
        var password = new byte[400];
        var encoded = TsRequest.EncodePasswordCredentials("", "tester@corp.example", password);

        // An empty domain is an empty OCTET STRING, not a missing field.
        Assert.Contains("a0020400", Hex.Text(encoded));
        Assert.Equal(encoded.Length, TsRequest.MessageLength(encoded));
    }
}
