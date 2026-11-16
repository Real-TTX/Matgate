using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace Matgate.Display.Rdp.Security;

// TSRequest (MS-CSSP 2.2.1), the one message CredSSP knows:
//
//   TSRequest ::= SEQUENCE {
//       version     [0] INTEGER,
//       negoTokens  [1] SEQUENCE OF SEQUENCE { negoToken [0] OCTET STRING } OPTIONAL,
//       authInfo    [2] OCTET STRING OPTIONAL,
//       pubKeyAuth  [3] OCTET STRING OPTIONAL,
//       errorCode   [4] INTEGER OPTIONAL,
//       clientNonce [5] OCTET STRING OPTIONAL }
//
// Written as DER. Read with the laxer rules of BER, because nothing says every server's encoder is as strict
// as DER - a longer form of a length does no harm. The parsing is the runtime's (System.Formats.Asn1), which
// checks every length against the data; whatever it rejects leaves here as a CredSspException.
internal sealed class TsRequest
{
    // Far above anything NTLM needs (a few hundred bytes) and still nothing a server can exhaust memory with.
    public const int MaxLength = 64 * 1024;

    public int Version { get; init; }

    // The one token in negoTokens. The list could hold several; no implementation sends more than one, and
    // the decoder refuses a second rather than guess which is meant.
    public byte[]? NegoToken { get; init; }

    public byte[]? AuthInfo { get; init; }

    public byte[]? PubKeyAuth { get; init; }

    // An NTSTATUS. Servers write it as a signed 32-bit number, so 0xC000006D arrives as a negative INTEGER.
    public uint? ErrorCode { get; init; }

    public byte[]? ClientNonce { get; init; }

    public byte[] Encode()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(Context(0)))
            {
                writer.WriteInteger(Version);
            }

            if (NegoToken is not null)
            {
                using (writer.PushSequence(Context(1)))
                using (writer.PushSequence())
                using (writer.PushSequence())
                {
                    WriteOctets(writer, 0, NegoToken);
                }
            }

            WriteOctets(writer, 2, AuthInfo);
            WriteOctets(writer, 3, PubKeyAuth);
            if (ErrorCode is { } status)
            {
                using (writer.PushSequence(Context(4)))
                {
                    writer.WriteInteger(unchecked((int)status));
                }
            }

            WriteOctets(writer, 5, ClientNonce);
        }

        return writer.Encode();
    }

    public static TsRequest Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaxLength)
        {
            throw new CredSspException("The server sent a CredSSP message that is far too long.");
        }

        try
        {
            var reader = new AsnReader(message.ToArray(), AsnEncodingRules.BER);
            var fields = reader.ReadSequence();
            reader.ThrowIfNotEmpty();

            int? version = null;
            byte[]? negoToken = null;
            byte[]? authInfo = null;
            byte[]? pubKeyAuth = null;
            uint? errorCode = null;
            byte[]? clientNonce = null;
            var seen = 0;
            while (fields.HasData)
            {
                var tag = fields.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || !tag.IsConstructed)
                {
                    throw new CredSspException("The server sent a message that is not valid CredSSP.");
                }

                var field = fields.ReadSequence(tag);
                if (tag.TagValue > 5)
                {
                    // A field of a later version of the protocol: nothing this client acts on.
                    continue;
                }

                if ((seen & (1 << tag.TagValue)) != 0)
                {
                    throw new CredSspException("The server sent a CredSSP message with the same field twice.");
                }

                seen |= 1 << tag.TagValue;
                switch (tag.TagValue)
                {
                    case 0:
                        if (!field.TryReadInt32(out var number))
                        {
                            throw new CredSspException("The server sent a CredSSP message with an impossible version.");
                        }

                        version = number;
                        break;
                    case 1:
                        negoToken = ReadNegoToken(field);
                        break;
                    case 2:
                        authInfo = field.ReadOctetString();
                        break;
                    case 3:
                        pubKeyAuth = field.ReadOctetString();
                        break;
                    case 4:
                        // Four bytes with the top bit set (negative) or five with a leading zero - both are the same status.
                        if (!field.TryReadInt64(out var status))
                        {
                            throw new CredSspException("The server sent a CredSSP message with an impossible status.");
                        }

                        errorCode = unchecked((uint)status);
                        break;
                    default:
                        clientNonce = field.ReadOctetString();
                        break;
                }

                field.ThrowIfNotEmpty();
            }

            if (version is null)
            {
                throw new CredSspException("The server sent a CredSSP message without a version.");
            }

            return new TsRequest
            {
                Version = version.Value,
                NegoToken = negoToken,
                AuthInfo = authInfo,
                PubKeyAuth = pubKeyAuth,
                ErrorCode = errorCode,
                ClientNonce = clientNonce,
            };
        }
        catch (AsnContentException exception)
        {
            throw new CredSspException("The server sent a message that is not valid CredSSP.", innerException: exception);
        }
    }

    // How long the TSRequest beginning with these bytes is in total, or 0 when the bytes do not yet show it.
    public static int MessageLength(ReadOnlySpan<byte> start)
    {
        if (start.IsEmpty)
        {
            return 0;
        }

        if (start[0] != 0x30)
        {
            throw new CredSspException("The server sent something that is not a CredSSP message.");
        }

        if (start.Length < 2)
        {
            return 0;
        }

        int first = start[1];
        if (first < 0x80)
        {
            return 2 + first;
        }

        // 0x80 alone would be a message without a stated end, which nobody sends and nobody could size.
        var lengthBytes = first & 0x7F;
        if (lengthBytes is 0 or > 4)
        {
            throw new CredSspException("The server sent a CredSSP message with an impossible length.");
        }

        if (start.Length < 2 + lengthBytes)
        {
            return 0;
        }

        long length = 0;
        for (var i = 0; i < lengthBytes; i++)
        {
            length = (length << 8) | start[2 + i];
        }

        var total = 2 + lengthBytes + length;
        if (total > MaxLength)
        {
            throw new CredSspException("The server sent a CredSSP message that is far too long.");
        }

        return (int)total;
    }

    // TSCredentials with a TSPasswordCreds inside (MS-CSSP 2.2.1.2): the credentials the server logs the user
    // on with. Strings are UTF-16 without a terminator. The caller clears the result once it is encrypted.
    public static byte[] EncodePasswordCredentials(string domain, string user, ReadOnlySpan<byte> password)
    {
        const int passwordCredentials = 1;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        byte[]? inner = null;
        try
        {
            using (writer.PushSequence())
            {
                WriteOctets(writer, 0, Encoding.Unicode.GetBytes(domain));
                WriteOctets(writer, 1, Encoding.Unicode.GetBytes(user));
                WriteOctets(writer, 2, password);
            }

            inner = writer.Encode();
            writer.Reset();
            using (writer.PushSequence())
            {
                using (writer.PushSequence(Context(0)))
                {
                    writer.WriteInteger(passwordCredentials);
                }

                WriteOctets(writer, 1, inner);
            }

            return writer.Encode();
        }
        finally
        {
            // Reset wipes the writer's own buffer, which held the password too.
            writer.Reset();
            if (inner is not null)
            {
                CryptographicOperations.ZeroMemory(inner);
            }
        }
    }

    private static byte[] ReadNegoToken(AsnReader field)
    {
        var tokens = field.ReadSequence();
        var token = tokens.ReadSequence();
        var content = token.ReadSequence(Context(0));
        var value = content.ReadOctetString();
        content.ThrowIfNotEmpty();
        token.ThrowIfNotEmpty();
        if (tokens.HasData)
        {
            throw new CredSspException("The server sent a CredSSP message with more than one authentication token.");
        }

        return value;
    }

    private static void WriteOctets(AsnWriter writer, int number, ReadOnlySpan<byte> value)
    {
        using (writer.PushSequence(Context(number)))
        {
            writer.WriteOctetString(value);
        }
    }

    private static void WriteOctets(AsnWriter writer, int number, byte[]? value)
    {
        if (value is not null)
        {
            WriteOctets(writer, number, value.AsSpan());
        }
    }

    // The fields are explicitly tagged: a constructed context-specific wrapper around the value itself.
    private static Asn1Tag Context(int number) => new(TagClass.ContextSpecific, number, isConstructed: true);
}
