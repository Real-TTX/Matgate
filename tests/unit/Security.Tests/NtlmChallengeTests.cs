using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Matgate.Display.Rdp.Security.Tests;

// The CHALLENGE message is the first thing a server - any server, also a hostile one - gets to say. Whatever
// it contains, the client either answers it or stops with an NtlmException; it never trips over an offset.
public class NtlmChallengeTests
{
    private const NtlmFlags Granted =
        NtlmFlags.Key56 | NtlmFlags.KeyExchange | NtlmFlags.Key128 | NtlmFlags.Version | NtlmFlags.TargetInfo |
        NtlmFlags.ExtendedSessionSecurity | NtlmFlags.TargetTypeServer | NtlmFlags.AlwaysSign | NtlmFlags.Ntlm |
        NtlmFlags.Seal | NtlmFlags.Sign | NtlmFlags.RequestTarget | NtlmFlags.Unicode;

    [Fact]
    public void AWellFormedChallengeIsAnswered()
    {
        Assert.NotEmpty(Answer(Challenge(Granted, StandardPairs())));
    }

    [Fact]
    public void EveryTruncationIsRefusedCleanly()
    {
        var challenge = Challenge(Granted, StandardPairs());
        for (var length = 0; length < challenge.Length; length++)
        {
            Assert.Throws<NtlmException>(() => Answer(challenge[..length]));
        }
    }

    [Theory]
    [InlineData("4e544c4d53535000", 1u)]
    [InlineData("4e544c4d53535000", 3u)]
    [InlineData("4e544c4d53535001", 2u)]
    [InlineData("4e544c4d20202000", 2u)]
    public void SomethingThatIsNotAChallengeIsRefused(string signature, uint type)
    {
        var challenge = Challenge(Granted, StandardPairs());
        Hex.Bytes(signature).CopyTo(challenge, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(challenge.AsSpan(8), type);
        Assert.Throws<NtlmException>(() => Answer(challenge));
    }

    [Theory]
    [InlineData(0u)]            // into the signature
    [InlineData(47u)]           // into the header
    [InlineData(1_000u)]        // behind the end
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0x80000000u)]   // negative, were it read as a signed number
    [InlineData(0xFFFFFFFFu)]
    public void TargetInformationOutsideTheMessageIsRefused(uint offset)
    {
        var challenge = Challenge(Granted, StandardPairs());
        BinaryPrimitives.WriteUInt32LittleEndian(challenge.AsSpan(44), offset);
        Assert.Throws<NtlmException>(() => Answer(challenge));
    }

    [Fact]
    public void TargetInformationLongerThanTheMessageIsRefused()
    {
        var challenge = Challenge(Granted, StandardPairs());
        BinaryPrimitives.WriteUInt16LittleEndian(challenge.AsSpan(40), 0xFFFF);
        Assert.Throws<NtlmException>(() => Answer(challenge));

        // One byte too many is as wrong as sixty thousand.
        var pairs = StandardPairs();
        challenge = Challenge(Granted, pairs);
        BinaryPrimitives.WriteUInt16LittleEndian(challenge.AsSpan(40), (ushort)(pairs.Length + 1));
        Assert.Throws<NtlmException>(() => Answer(challenge));
    }

    [Fact]
    public void AnAvPairThatRunsPastTheEndIsRefused()
    {
        // The last pair claims 0x200 bytes where there are four.
        var pairs = Pairs((NtlmAvId.NetBiosDomainName, Name("DOMAIN")), (NtlmAvId.NetBiosComputerName, Name("SERVER")));
        byte[] broken = [.. pairs[..^4], 0x03, 0x00, 0x00, 0x02, 1, 2, 3, 4];
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted, broken)));

        // Fewer than four bytes left cannot be a pair at all.
        byte[] stray = [.. pairs[..^4], 0x03, 0x00, 0x00];
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted, stray)));
    }

    [Fact]
    public void AListWithoutItsTerminatorIsStillAnswered()
    {
        // Careless, but nothing is out of bounds: the pairs simply end where the field ends.
        var authenticate = Answer(Challenge(Granted, StandardPairs()[..^4]));

        // The response carries a proper list all the same: all five pairs of the server, the client's three,
        // and behind them the terminator and the four zero bytes that close the structure.
        Assert.Equal(8, PairsOf(authenticate).Count);
        Assert.Equal(new byte[8], Field(authenticate, 20)[^8..]);
    }

    [Theory]
    [InlineData((uint)NtlmFlags.Unicode)]
    [InlineData((uint)NtlmFlags.ExtendedSessionSecurity)]
    [InlineData((uint)NtlmFlags.Key128)]
    [InlineData((uint)NtlmFlags.Seal)]
    [InlineData((uint)NtlmFlags.Sign)]
    public void AServerThatWithholdsWhatTheSessionNeedsIsRefused(uint withheld)
    {
        var exception = Assert.Throws<NtlmException>(() => Answer(Challenge(Granted & ~(NtlmFlags)withheld, StandardPairs())));
        Assert.Contains(((NtlmFlags)withheld).ToString(), exception.Message);
    }

    [Theory]
    [InlineData((uint)NtlmFlags.Key56)]
    [InlineData((uint)NtlmFlags.AlwaysSign)]
    [InlineData((uint)NtlmFlags.Ntlm)]
    [InlineData((uint)NtlmFlags.Version)]
    [InlineData((uint)NtlmFlags.RequestTarget)]
    [InlineData((uint)NtlmFlags.TargetTypeServer)]
    public void WhatTheSessionDoesNotDependOnMayBeMissing(uint withheld)
    {
        Assert.NotEmpty(Answer(Challenge(Granted & ~(NtlmFlags)withheld, StandardPairs())));
    }

    [Fact]
    public void AChallengeThatDoesNotNameItsServerIsRefused()
    {
        var time = (NtlmAvId.Timestamp, new byte[8]);
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted, Pairs((NtlmAvId.NetBiosComputerName, Name("SERVER")), time))));
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted, Pairs((NtlmAvId.NetBiosDomainName, Name("DOMAIN")), time))));
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted, Pairs())));

        // Without the flag the target information does not count, whatever the fields say.
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted & ~NtlmFlags.TargetInfo, StandardPairs())));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(9, 4)]
    [InlineData(8, 2)]
    [InlineData(8, 8)]
    public void AMalformedTimeOrFlagsValueIsRefused(int timeLength, int flagsLength)
    {
        // A time has eight bytes and the flags have four.
        var pairs = Pairs(
            (NtlmAvId.NetBiosDomainName, Name("DOMAIN")),
            (NtlmAvId.NetBiosComputerName, Name("SERVER")),
            (NtlmAvId.Timestamp, new byte[timeLength]),
            (NtlmAvId.Flags, new byte[flagsLength]));
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted, pairs)));
    }

    [Fact]
    public void MoreTargetInformationThanAResponseCanHoldIsRefused()
    {
        // A response has a 16-bit length; sixty thousand bytes of pairs plus the client's own do not fit.
        var pairs = Pairs(
            (NtlmAvId.NetBiosDomainName, Name("DOMAIN")),
            (NtlmAvId.NetBiosComputerName, Name("SERVER")),
            (NtlmAvId.DnsTreeName, new byte[60_000]),
            (NtlmAvId.DnsDomainName, new byte[5_400]));
        Assert.Throws<NtlmException>(() => Answer(Challenge(Granted, pairs)));
    }

    [Fact]
    public void TheServersFlagsPairIsKeptAndGetsTheIntegrityBit()
    {
        // 0x1 says the account is constrained; the client adds 0x2 for its integrity code to the same pair.
        var pairs = Pairs(
            (NtlmAvId.NetBiosDomainName, Name("DOMAIN")),
            (NtlmAvId.NetBiosComputerName, Name("SERVER")),
            (NtlmAvId.Flags, [1, 0, 0, 0]),
            (NtlmAvId.Timestamp, new byte[8]));
        var sent = PairsOf(Answer(Challenge(Granted, pairs)));

        Assert.Equal(
            [NtlmAvId.NetBiosDomainName, NtlmAvId.NetBiosComputerName, NtlmAvId.Flags, NtlmAvId.Timestamp, NtlmAvId.ChannelBindings, NtlmAvId.TargetName],
            sent.Select(pair => pair.Id));
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, sent.Single(pair => pair.Id == NtlmAvId.Flags).Value);
    }

    [Fact]
    public void ChannelBindingsAndTargetNameAreTheClientsToSay()
    {
        // A server has no business sending these two; if it does, only the client's own go back.
        var pairs = Pairs(
            (NtlmAvId.NetBiosDomainName, Name("DOMAIN")),
            (NtlmAvId.NetBiosComputerName, Name("SERVER")),
            (NtlmAvId.TargetName, Name("cifs/elsewhere")),
            (NtlmAvId.ChannelBindings, Enumerable.Repeat((byte)0xEE, 16).ToArray()),
            (NtlmAvId.Timestamp, new byte[8]));
        var sent = PairsOf(Answer(Challenge(Granted, pairs)));

        Assert.Equal("TERMSRV/server.example", Encoding.Unicode.GetString(sent.Single(pair => pair.Id == NtlmAvId.TargetName).Value));
        Assert.Equal(new byte[16], sent.Single(pair => pair.Id == NtlmAvId.ChannelBindings).Value);
    }

    // Twenty thousand challenges with random damage: bytes flipped, cut short, grown. The client answers or
    // refuses with its own exception; anything else - an index out of range above all - fails the test.
    [Fact]
    public void RandomDamageNeverGetsPastTheChecks()
    {
        var random = new Random(20261116);
        var valid = Challenge(Granted, StandardPairs());
        var answered = 0;
        var refused = 0;
        for (var round = 0; round < 20_000; round++)
        {
            var damaged = (byte[])valid.Clone();
            switch (random.Next(4))
            {
                case 0:
                    damaged = damaged[..random.Next(damaged.Length)];
                    break;
                case 1:
                    damaged = [.. damaged, .. new byte[random.Next(1, 64)]];
                    break;
                default:
                    break;
            }

            for (var flips = random.Next(1, 6); flips > 0 && damaged.Length > 0; flips--)
            {
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            }

            try
            {
                Answer(damaged);
                answered++;
            }
            catch (NtlmException)
            {
                refused++;
            }
        }

        // Both must have happened, or the damage was not the kind that tests anything.
        Assert.True(answered > 500, $"only {answered} damaged challenges were answered");
        Assert.True(refused > 5_000, $"only {refused} damaged challenges were refused");
    }

    private static NtlmClient NewClient() => new("tester", "", "secret", "WORKSTATION", "TERMSRV/server.example");

    private static byte[] Answer(byte[] challenge)
    {
        using var client = NewClient();
        client.CreateNegotiate();
        return client.CreateAuthenticate(challenge);
    }

    // What a Windows server sends: its names and its time.
    private static byte[] StandardPairs() => Pairs(
        (NtlmAvId.NetBiosDomainName, Name("DOMAIN")),
        (NtlmAvId.NetBiosComputerName, Name("SERVER")),
        (NtlmAvId.DnsDomainName, Name("domain.example")),
        (NtlmAvId.DnsComputerName, Name("server.domain.example")),
        (NtlmAvId.Timestamp, Hex.Bytes("0090d336b734c301")));

    private static byte[] Name(string name) => Encoding.Unicode.GetBytes(name);

    // AV pairs with their terminator.
    private static byte[] Pairs(params (NtlmAvId Id, byte[] Value)[] pairs)
    {
        var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        foreach (var (id, value) in pairs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)id);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)value.Length);
            stream.Write(header);
            stream.Write(value);
        }

        stream.Write([0, 0, 0, 0]);
        return stream.ToArray();
    }

    // A CHALLENGE_MESSAGE as in MS-NLMP 2.2.1.2: header, version, target name, target information.
    private static byte[] Challenge(NtlmFlags flags, byte[] targetInfo)
    {
        var targetName = Name("SERVER");
        var message = new byte[56 + targetName.Length + targetInfo.Length];
        "NTLMSSP\0"u8.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(12), (ushort)targetName.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), (ushort)targetName.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), (uint)flags);
        Hex.Bytes("0123456789abcdef").CopyTo(message, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(40), (ushort)targetInfo.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(42), (ushort)targetInfo.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(44), (uint)(56 + targetName.Length));
        Hex.Bytes("0a00f4650000000f").CopyTo(message, 48);
        targetName.CopyTo(message, 56);
        targetInfo.CopyTo(message, 56 + targetName.Length);
        return message;
    }

    private static byte[] Field(byte[] message, int field)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(field));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(field + 4));
        return message.AsSpan(offset, length).ToArray();
    }

    // The AV pairs inside the NT response of an AUTHENTICATE message, without the terminator.
    private static List<(NtlmAvId Id, byte[] Value)> PairsOf(byte[] authenticate)
    {
        var result = new List<(NtlmAvId Id, byte[] Value)>();
        var rest = Field(authenticate, 20).AsSpan(16 + 28);
        while (true)
        {
            var id = (NtlmAvId)BinaryPrimitives.ReadUInt16LittleEndian(rest);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(rest[2..]);
            if (id == NtlmAvId.EndOfList)
            {
                return result;
            }

            result.Add((id, rest.Slice(4, length).ToArray()));
            rest = rest[(4 + length)..];
        }
    }
}
