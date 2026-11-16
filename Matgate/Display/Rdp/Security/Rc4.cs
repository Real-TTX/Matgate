using System.Security.Cryptography;

namespace Matgate.Display.Rdp.Security;

// RC4. NTLM seals its messages with it and keeps the cipher running across messages, so this is a stream with
// a state rather than a one-shot function. .NET never had RC4 and OpenSSL 3 keeps it in the legacy provider.
public sealed class Rc4 : IDisposable
{
    private readonly byte[] _state = new byte[256];
    private byte _i;
    private byte _j;
    private bool _disposed;

    public Rc4(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 1 or > 256)
        {
            throw new ArgumentException("An RC4 key has between 1 and 256 bytes.", nameof(key));
        }

        for (var i = 0; i < 256; i++)
        {
            _state[i] = (byte)i;
        }

        byte j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (byte)(j + _state[i] + key[i % key.Length]);
            (_state[i], _state[j]) = (_state[j], _state[i]);
        }
    }

    // Encrypting and decrypting are the same operation. Input and output may be the same memory.
    public void Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        // A cleared state would hand the input back unencrypted, so this must not pass silently.
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (output.Length < input.Length)
        {
            throw new ArgumentException("The output is shorter than the input.", nameof(output));
        }

        for (var n = 0; n < input.Length; n++)
        {
            _i++;
            _j = (byte)(_j + _state[_i]);
            (_state[_i], _state[_j]) = (_state[_j], _state[_i]);
            output[n] = (byte)(input[n] ^ _state[(byte)(_state[_i] + _state[_j])]);
        }
    }

    public byte[] Process(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        Process(input, output);
        return output;
    }

    public void Dispose()
    {
        // The state is as good as the key.
        CryptographicOperations.ZeroMemory(_state);
        _i = 0;
        _j = 0;
        _disposed = true;
    }
}
