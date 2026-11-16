using System.Text;
using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

// The example of MS-NLMP 4.2.4 ("NTLMv2 Authentication") with the common values of 4.2.1, byte for byte as
// the specification prints it. Every value in here is copied from there, none is computed by this code.
public class NtlmSpecificationTests
{
    // 4.2.1 Common Values.
    private const string User = "User";
    private const string UserDom = "Domain";
    private const string Passwd = "Password";
    private const string WorkstationName = "COMPUTER";
    private static readonly byte[] RandomSessionKey = Hex.Bytes("55 55 55 55 55 55 55 55 55 55 55 55 55 55 55 55");
    private static readonly byte[] ClientChallenge = Hex.Bytes("aa aa aa aa aa aa aa aa");
    private static readonly byte[] ServerChallenge = Hex.Bytes("01 23 45 67 89 ab cd ef");

    // 4.2.4.1.1 NTOWFv2() and LMOWFv2().
    private static readonly byte[] ResponseKey = Hex.Bytes("0c 86 8a 40 3b fd 7a 93 a3 00 1e f2 2e f0 2e 3f");

    // 4.2.4.1.2 Session Base Key.
    private static readonly byte[] SessionBaseKey = Hex.Bytes("8d e4 0c ca db c1 4a 82 f1 5c b0 ad 0d e9 5c a3");

    // 4.2.4.1.3 temp.
    private static readonly byte[] Temp = Hex.Bytes("""
        01 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
        aa aa aa aa aa aa aa aa 00 00 00 00
        02 00 0c 00 44 00 6f 00 6d 00 61 00 69 00 6e 00
        01 00 0c 00 53 00 65 00 72 00 76 00 65 00 72 00
        00 00 00 00 00 00 00 00
        """);

    // 4.2.4.2.1 LMv2 Response.
    private static readonly byte[] LmV2Response = Hex.Bytes("86 c3 50 97 ac 9c ec 10 25 54 76 4a 57 cc cc 19 aa aa aa aa aa aa aa aa");

    // 4.2.4.2.2 NTLMv2 Response (the NTProofStr).
    private static readonly byte[] NtProof = Hex.Bytes("68 cd 0a b8 51 e5 1c 96 aa bc 92 7b eb ef 6a 1c");

    // 4.2.4.2.3 Encrypted Session Key.
    private static readonly byte[] EncryptedSessionKey = Hex.Bytes("c5 da d2 54 4f c9 79 90 94 ce 1c e9 0b c9 d0 3e");

    // 4.2.4.3 Messages: the CHALLENGE_MESSAGE.
    private static readonly byte[] ChallengeMessage = Hex.Bytes("""
        4e 54 4c 4d 53 53 50 00 02 00 00 00 0c 00 0c 00
        38 00 00 00 33 82 8a e2 01 23 45 67 89 ab cd ef
        00 00 00 00 00 00 00 00 24 00 24 00 44 00 00 00
        06 00 70 17 00 00 00 0f 53 00 65 00 72 00 76 00
        65 00 72 00 02 00 0c 00 44 00 6f 00 6d 00 61 00
        69 00 6e 00 01 00 0c 00 53 00 65 00 72 00 76 00
        65 00 72 00 00 00 00 00
        """);

    // 4.2.4.3 Messages: the AUTHENTICATE_MESSAGE.
    private static readonly byte[] AuthenticateMessage = Hex.Bytes("""
        4e 54 4c 4d 53 53 50 00 03 00 00 00 18 00 18 00
        6c 00 00 00 54 00 54 00 84 00 00 00 0c 00 0c 00
        48 00 00 00 08 00 08 00 54 00 00 00 10 00 10 00
        5c 00 00 00 10 00 10 00 d8 00 00 00 35 82 88 e2
        05 01 28 0a 00 00 00 0f 44 00 6f 00 6d 00 61 00
        69 00 6e 00 55 00 73 00 65 00 72 00 43 00 4f 00
        4d 00 50 00 55 00 54 00 45 00 52 00 86 c3 50 97
        ac 9c ec 10 25 54 76 4a 57 cc cc 19 aa aa aa aa
        aa aa aa aa 68 cd 0a b8 51 e5 1c 96 aa bc 92 7b
        eb ef 6a 1c 01 01 00 00 00 00 00 00 00 00 00 00
        00 00 00 00 aa aa aa aa aa aa aa aa 00 00 00 00
        02 00 0c 00 44 00 6f 00 6d 00 61 00 69 00 6e 00
        01 00 0c 00 53 00 65 00 72 00 76 00 65 00 72 00
        00 00 00 00 00 00 00 00 c5 da d2 54 4f c9 79 90
        94 ce 1c e9 0b c9 d0 3e
        """);

    // 4.2.4.4 GSS_WrapEx Examples.
    private static readonly byte[] Plaintext = Hex.Bytes("50 00 6c 00 61 00 69 00 6e 00 74 00 65 00 78 00 74 00");
    private static readonly byte[] SealKey = Hex.Bytes("59 f6 00 97 3c c4 96 0a 25 48 0a 7c 19 6e 4c 58");
    private static readonly byte[] SignKey = Hex.Bytes("47 88 dc 86 1b 47 82 f3 5d 43 fd 98 fe 1a 2d 39");
    private static readonly byte[] SealedData = Hex.Bytes("54 e5 01 65 bf 19 36 dc 99 60 20 c1 81 1b 0f 06 fb 5f");
    private static readonly byte[] PlainChecksum = Hex.Bytes("70 35 28 51 f2 56 43 09");
    private static readonly byte[] Signature = Hex.Bytes("01 00 00 00 7f b3 8e c5 c5 5d 49 76 00 00 00 00");

    // The flags of the AUTHENTICATE_MESSAGE above (35 82 88 e2), which are the negotiated ones.
    private const NtlmFlags NegotiatedFlags = (NtlmFlags)0xE2888235;

    [Fact]
    public void NtOwfV2()
    {
        Assert.Equal(ResponseKey, NtlmClient.NtOwfV2(Passwd, User, UserDom));
    }

    [Fact]
    public void TempIsWhatTheResponseCarriesBehindTheProof()
    {
        // The specification's "temp" is in the message it prints, right behind the 16 bytes of the proof.
        Assert.Equal(Temp, AuthenticateMessage.AsSpan(0x84 + 16, 0x54 - 16).ToArray());
    }

    [Fact]
    public void NtProofStr()
    {
        Assert.Equal(NtProof, NtlmClient.NtProof(ResponseKey, ServerChallenge, Temp));
    }

    [Fact]
    public void SessionBaseKeyFromTheProof()
    {
        Assert.Equal(SessionBaseKey, NtlmClient.SessionBaseKey(ResponseKey, NtProof));
    }

    [Fact]
    public void LmV2ResponseFromBothChallenges()
    {
        Assert.Equal(LmV2Response, NtlmClient.LmV2Response(ResponseKey, ServerChallenge, ClientChallenge));
    }

    [Fact]
    public void EncryptedSessionKeyIsTheRandomKeyUnderTheSessionBaseKey()
    {
        using var cipher = new Rc4(SessionBaseKey);
        Assert.Equal(EncryptedSessionKey, cipher.Process(RandomSessionKey));
    }

    [Fact]
    public void AuthenticateMessageByteForByte()
    {
        using var client = SpecificationClient();
        client.CreateNegotiate();
        var message = client.CreateAuthenticate(ChallengeMessage);
        Assert.Equal(Hex.Text(AuthenticateMessage), Hex.Text(message));
    }

    [Fact]
    public void SealKeyAndSignKey()
    {
        Assert.Equal(SealKey, NtlmSession.DeriveKey(RandomSessionKey, "session key to client-to-server sealing key magic constant"));
        Assert.Equal(SignKey, NtlmSession.DeriveKey(RandomSessionKey, "session key to client-to-server signing key magic constant"));
    }

    [Fact]
    public void GssWrapExOfTheClientAfterTheHandshake()
    {
        using var client = SpecificationClient();
        client.CreateNegotiate();
        client.CreateAuthenticate(ChallengeMessage);

        // EncryptMessage puts the signature in front of the sealed data.
        Assert.Equal(Hex.Text([.. Signature, .. SealedData]), Hex.Text(client.Encrypt(Plaintext)));
    }

    [Fact]
    public void GssWrapExFromTheSessionKeyAlone()
    {
        using var session = new NtlmSession(RandomSessionKey, NegotiatedFlags);
        Assert.Equal(Hex.Text([.. Signature, .. SealedData]), Hex.Text(session.Encrypt(Plaintext)));
    }

    // The specification also prints the checksum before RC4 goes over it. That is the whole signature when no
    // key exchange was negotiated (MS-NLMP 3.4.4.2), and the sealed data is the same either way.
    [Fact]
    public void ChecksumStaysPlainWithoutKeyExchange()
    {
        using var session = new NtlmSession(RandomSessionKey, NegotiatedFlags & ~NtlmFlags.KeyExchange);
        byte[] expected = [1, 0, 0, 0, .. PlainChecksum, 0, 0, 0, 0, .. SealedData];
        Assert.Equal(Hex.Text(expected), Hex.Text(session.Encrypt(Plaintext)));
    }

    [Fact]
    public void TheServerSideOfTheSessionUnwrapsTheExample()
    {
        using var server = new NtlmSession(RandomSessionKey, NegotiatedFlags, asServer: true);
        Assert.Equal(Plaintext, server.Decrypt([.. Signature, .. SealedData]));
    }

    // The client of the example: Windows XP (5.1.2600), which knew neither channel bindings nor the target
    // name, talking to a server that sends no time - so the client's own clock, set to zero, goes in.
    private static NtlmClient SpecificationClient() => new(User, UserDom, Passwd, WorkstationName, "TERMSRV/Server", new NtlmClientOptions
    {
        ClientChallenge = ClientChallenge,
        ExportedSessionKey = RandomSessionKey,
        TimeProvider = new FixedTime(DateTimeOffset.FromFileTime(0)),
        OperatingSystemVersion = (5, 1, 2600),
        ExtendedProtection = false,
    });

    [Fact]
    public void TheValuesOfTheExampleAreWhatTheyClaimToBe()
    {
        // Guards the transcription itself: the strings of 4.2.1 as the specification spells them in UTF-16.
        Assert.Equal(Hex.Bytes("55 00 73 00 65 00 72 00"), Encoding.Unicode.GetBytes(User));
        Assert.Equal(Hex.Bytes("44 00 6f 00 6d 00 61 00 69 00 6e 00"), Encoding.Unicode.GetBytes(UserDom));
        Assert.Equal(Hex.Bytes("50 00 61 00 73 00 73 00 77 00 6f 00 72 00 64 00"), Encoding.Unicode.GetBytes(Passwd));
        Assert.Equal(Hex.Bytes("43 00 4f 00 4d 00 50 00 55 00 54 00 45 00 52 00"), Encoding.Unicode.GetBytes(WorkstationName));
        Assert.Equal(Plaintext, Encoding.Unicode.GetBytes("Plaintext"));
        Assert.Equal(Hex.Bytes("33 82 8a e2"), ChallengeMessage.AsSpan(20, 4).ToArray());
    }
}
