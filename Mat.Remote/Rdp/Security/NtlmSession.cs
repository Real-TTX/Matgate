using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Matgate.Display.Rdp.Security;

// Signing and sealing after the NTLM handshake, with extended session security (MS-NLMP 3.4): one signing key
// and one RC4 stream per direction, both derived from the exported session key. The ciphers are not restarted
// between messages, and each direction counts its own messages.
internal sealed class NtlmSession : IDisposable
{
    public const int SignatureLength = 16;

    private readonly byte[] _sendSigningKey;
    private readonly byte[] _receiveSigningKey;
    private readonly Rc4 _sendCipher;
    private readonly Rc4 _receiveCipher;
    private readonly bool _encryptChecksum;
    private uint _sendSequence;
    private uint _receiveSequence;
    private bool _disposed;

    // asServer swaps the directions: that is the other end of the same session, which the tests play.
    public NtlmSession(ReadOnlySpan<byte> exportedSessionKey, NtlmFlags negotiated, bool asServer = false)
    {
        if (exportedSessionKey.Length != 16)
        {
            throw new ArgumentException("The exported session key has 16 bytes.", nameof(exportedSessionKey));
        }

        if (!negotiated.HasFlag(NtlmFlags.ExtendedSessionSecurity))
        {
            throw new NtlmException("The server does not offer NTLM's extended session security.");
        }

        // With a key exchange the checksum in the signature is sealed as well (MS-NLMP 3.4.4.2).
        _encryptChecksum = negotiated.HasFlag(NtlmFlags.KeyExchange);

        var sealingLength = negotiated.HasFlag(NtlmFlags.Key128) ? 16 : negotiated.HasFlag(NtlmFlags.Key56) ? 7 : 5;
        var clientSigningKey = DeriveKey(exportedSessionKey, "session key to client-to-server signing key magic constant");
        var serverSigningKey = DeriveKey(exportedSessionKey, "session key to server-to-client signing key magic constant");
        var clientSealingKey = DeriveKey(exportedSessionKey[..sealingLength], "session key to client-to-server sealing key magic constant");
        var serverSealingKey = DeriveKey(exportedSessionKey[..sealingLength], "session key to server-to-client sealing key magic constant");

        _sendSigningKey = asServer ? serverSigningKey : clientSigningKey;
        _receiveSigningKey = asServer ? clientSigningKey : serverSigningKey;
        _sendCipher = new Rc4(asServer ? serverSealingKey : clientSealingKey);
        _receiveCipher = new Rc4(asServer ? clientSealingKey : serverSealingKey);
        CryptographicOperations.ZeroMemory(clientSealingKey);
        CryptographicOperations.ZeroMemory(serverSealingKey);
    }

    // What SSPI's EncryptMessage returns for NTLM: the 16 bytes of signature, then the sealed message.
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var output = new byte[SignatureLength + plaintext.Length];
        _sendCipher.Process(plaintext, output.AsSpan(SignatureLength));
        WriteSignature(output, _sendSigningKey, _sendCipher, _sendSequence, plaintext);
        _sendSequence++;
        return output;
    }

    // The reverse, for a message of the other side: checks the signature and returns the plain message.
    public byte[] Decrypt(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.Length < SignatureLength)
        {
            throw new NtlmException("A sealed NTLM message is shorter than its signature.");
        }

        var plaintext = _receiveCipher.Process(message[SignatureLength..]);
        Span<byte> expected = stackalloc byte[SignatureLength];
        WriteSignature(expected, _receiveSigningKey, _receiveCipher, _receiveSequence, plaintext);
        if (!CryptographicOperations.FixedTimeEquals(expected, message[..SignatureLength]))
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new NtlmException("The signature of a sealed NTLM message does not match.");
        }

        _receiveSequence++;
        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_sendSigningKey);
        CryptographicOperations.ZeroMemory(_receiveSigningKey);
        _sendCipher.Dispose();
        _receiveCipher.Dispose();
    }

    // NTLMSSP_MESSAGE_SIGNATURE for extended session security: version 1, the first eight bytes of
    // HMAC_MD5(signing key, sequence number + message), the sequence number. The cipher has to be the one
    // that just processed the message - the checksum takes the next eight bytes of its stream.
    private void WriteSignature(Span<byte> signature, byte[] signingKey, Rc4 cipher, uint sequence, ReadOnlySpan<byte> message)
    {
        Span<byte> sequenceBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sequenceBytes, sequence);
        Span<byte> mac = stackalloc byte[16];
        using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, signingKey))
        {
            hmac.AppendData(sequenceBytes);
            hmac.AppendData(message);
            hmac.GetHashAndReset(mac);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(signature, 1);
        if (_encryptChecksum)
        {
            cipher.Process(mac[..8], signature.Slice(4, 8));
        }
        else
        {
            mac[..8].CopyTo(signature[4..]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(signature[12..], sequence);
    }

    // SIGNKEY and SEALKEY of MS-NLMP 3.4.5: MD5 over the key and a constant, whose terminating zero counts.
    internal static byte[] DeriveKey(ReadOnlySpan<byte> key, string constant)
    {
        Span<byte> input = stackalloc byte[key.Length + constant.Length + 1];
        key.CopyTo(input);
        Encoding.ASCII.GetBytes(constant, input[key.Length..]);
        input[^1] = 0;
        var derived = MD5.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        return derived;
    }
}
