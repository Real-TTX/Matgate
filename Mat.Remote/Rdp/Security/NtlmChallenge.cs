using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Security;

// The CHALLENGE_MESSAGE of MS-NLMP 2.2.1.2, reduced to what a client needs from it. All of it comes from the
// server and is taken as hostile: every offset and length is checked against the message before it is used.
internal sealed class NtlmChallenge
{
    // Signature, type, TargetNameFields, flags, challenge, reserved, TargetInfoFields. Windows has sent all of
    // that since Vista; the shorter forms of NT and 2000 cannot carry what NTLMv2 with a session key needs.
    private const int HeaderLength = 48;

    private NtlmChallenge(NtlmFlags flags, byte[] serverChallenge, List<(NtlmAvId Id, byte[] Value)> targetInfo)
    {
        Flags = flags;
        ServerChallenge = serverChallenge;
        TargetInfo = targetInfo;
    }

    public NtlmFlags Flags { get; }

    public byte[] ServerChallenge { get; }

    // The AV pairs in the order the server sent them, without the terminating MsvAvEOL.
    public IReadOnlyList<(NtlmAvId Id, byte[] Value)> TargetInfo { get; }

    public byte[]? Find(NtlmAvId id)
    {
        foreach (var pair in TargetInfo)
        {
            if (pair.Id == id)
            {
                return pair.Value;
            }
        }

        return null;
    }

    public static NtlmChallenge Parse(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderLength)
        {
            throw new NtlmException("The server's NTLM challenge is too short.");
        }

        if (!message[..8].SequenceEqual(NtlmClient.Signature) || BinaryPrimitives.ReadUInt32LittleEndian(message[8..]) != 2)
        {
            throw new NtlmException("The server did not answer with an NTLM challenge.");
        }

        var flags = (NtlmFlags)BinaryPrimitives.ReadUInt32LittleEndian(message[20..]);
        var serverChallenge = message.Slice(24, 8).ToArray();
        var targetInfo = new List<(NtlmAvId Id, byte[] Value)>();

        // Without the flag the two fields are to be ignored, whatever they contain.
        if (flags.HasFlag(NtlmFlags.TargetInfo))
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(message[40..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(message[44..]);
            if (offset < HeaderLength || offset > (uint)message.Length || length > message.Length - (int)offset)
            {
                throw new NtlmException("The target information of the server's NTLM challenge lies outside the message.");
            }

            var rest = message.Slice((int)offset, length);
            while (!rest.IsEmpty)
            {
                if (rest.Length < 4)
                {
                    throw new NtlmException("The target information of the server's NTLM challenge is cut off.");
                }

                var id = (NtlmAvId)BinaryPrimitives.ReadUInt16LittleEndian(rest);
                int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(rest[2..]);
                if (id == NtlmAvId.EndOfList)
                {
                    break;
                }

                if (valueLength > rest.Length - 4)
                {
                    throw new NtlmException("The target information of the server's NTLM challenge is cut off.");
                }

                targetInfo.Add((id, rest.Slice(4, valueLength).ToArray()));
                rest = rest[(4 + valueLength)..];
            }
        }

        return new NtlmChallenge(flags, serverChallenge, targetInfo);
    }
}
