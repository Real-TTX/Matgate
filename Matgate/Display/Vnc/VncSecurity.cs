using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Matgate.Display.Vnc;

// The ways a server lets a client in ("security types"), which of them Matgate can do, and which it takes
// when a server offers several.
internal static class VncSecurity
{
    public const int None = 1;
    public const int VncAuthentication = 2;
    public const int VeNCrypt = 19;
    public const int AppleRemoteDesktop = 30;

    // VeNCrypt's own types. Its list may also name the plain ones above.
    public const int Plain = 256;
    public const int TlsNone = 257;
    public const int TlsVnc = 258;
    public const int TlsPlain = 259;
    public const int X509None = 260;
    public const int X509Vnc = 261;
    public const int X509Plain = 262;

    public static bool NeedsUsername(int type) => type is Plain or X509Plain or AppleRemoteDesktop;

    public static bool NeedsPassword(int type) => type is not (None or X509None);

    public static bool UsesTls(int type) => type is X509None or X509Vnc or X509Plain;

    // How much a type is wanted - the highest wins, 0 cannot be used at all. Strongest first: encrypted with
    // a certificate, then Apple's login (the password travels encrypted), then the VNC password (a challenge
    // to answer), then none. Between two that are equally strong the one that fits what the connection
    // carries wins: a user name means a login that takes one, a password alone means the VNC password - on a
    // Mac that offers both its own login and a VNC password, a password alone is the VNC password.
    //
    // VeNCrypt's types with "TLS" in their name encrypt without any certificate, which .NET's TLS cannot do:
    // they count as not offered. Plain sends the password as it is; it only goes ahead of the VNC password
    // when there is a user name to sign in with.
    //
    // The plain types count the same inside VeNCrypt as outside: once VeNCrypt has been entered, taking one
    // of them there saves connecting again for the same thing outside.
    public static int Rank(int type, bool inVeNCrypt, bool haveUsername, bool havePassword)
    {
        var passwordOnly = havePassword && !haveUsername;
        return type switch
        {
            X509Plain when inVeNCrypt => haveUsername ? 102 : 100,
            X509Vnc when inVeNCrypt => 101,
            X509None when inVeNCrypt => 90,
            AppleRemoteDesktop when !inVeNCrypt => passwordOnly ? 55 : 80,
            Plain when inVeNCrypt => haveUsername ? 70 : 50,
            VncAuthentication => 60,
            None => 10,
            _ => 0,
        };
    }

    public static string Name(int type) => type switch
    {
        None => "none",
        VncAuthentication => "VNC password",
        5 or 6 or 129 or 130 => "RealVNC's RSA-AES",
        13 or 133 => "RealVNC's two-step RSA-AES",
        16 => "TightVNC's own",
        17 => "UltraVNC's own",
        18 => "TLS",
        VeNCrypt => "VeNCrypt",
        20 => "SASL",
        22 => "xvp",
        AppleRemoteDesktop => "Apple Remote Desktop",
        113 or 114 => "UltraVNC's Windows login",
        Plain => "VeNCrypt Plain",
        TlsNone or TlsVnc or TlsPlain => "VeNCrypt with TLS without a certificate",
        X509None or X509Vnc or X509Plain => "VeNCrypt with a certificate",
        263 or 264 => "VeNCrypt with SASL",
        _ => "unknown",
    };

    public static string Describe(IEnumerable<int> types) => string.Join(", ", types.Select(type => $"{Name(type)} ({type})"));

    // The message of the Plain login: both lengths, then the user name and the password.
    public static byte[] PlainLogin(string username, string password)
    {
        var name = Encoding.UTF8.GetBytes(username);
        var secret = Encoding.UTF8.GetBytes(password);
        var message = new byte[8 + name.Length + secret.Length];
        BinaryPrimitives.WriteUInt32BigEndian(message, (uint)name.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), (uint)secret.Length);
        name.CopyTo(message, 8);
        secret.CopyTo(message, 8 + name.Length);
        return message;
    }

    // Apple Remote Desktop (30). The server sends a generator, a prime and its public key; both sides agree on
    // a secret the Diffie-Hellman way, and the MD5 of that secret is the AES key under which user name and
    // password travel - 64 bytes each, ended by a zero, the rest filled with noise so that the encrypted
    // block says nothing about their length. The answer is that block followed by Matgate's public key.
    public static byte[] AppleLogin(int generator, ReadOnlySpan<byte> prime, ReadOnlySpan<byte> serverKey, string username, string password)
    {
        var size = prime.Length;
        var modulus = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        if (modulus <= 2 || generator < 2)
        {
            throw new VncException("The VNC server sent numbers no key can be agreed on with (Apple Remote Desktop).");
        }

        var secret = new BigInteger(RandomNumberGenerator.GetBytes(size), isUnsigned: true, isBigEndian: true);
        var publicKey = BigInteger.ModPow(generator, secret, modulus);
        var shared = BigInteger.ModPow(new BigInteger(serverKey, isUnsigned: true, isBigEndian: true), secret, modulus);

        var block = RandomNumberGenerator.GetBytes(128);
        PutCredential(block.AsSpan(0, 64), username);
        PutCredential(block.AsSpan(64, 64), password);

        var answer = new byte[128 + size];
        using (var aes = Aes.Create())
        {
            aes.Key = MD5.HashData(FixedSize(shared, size));
            aes.EncryptEcb(block, answer.AsSpan(0, 128), PaddingMode.None);
        }

        FixedSize(publicKey, size).CopyTo(answer.AsSpan(128));
        return answer;
    }

    private static void PutCredential(Span<byte> field, string text)
    {
        var encoded = Encoding.UTF8.GetBytes(text);
        var length = Math.Min(encoded.Length, field.Length - 1);
        encoded.AsSpan(0, length).CopyTo(field);
        field[length] = 0;
    }

    // A number as the protocol carries it: most significant byte first, as long as the prime.
    private static byte[] FixedSize(BigInteger value, int size)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[size];
        bytes.CopyTo(result.AsSpan(size - bytes.Length));
        return result;
    }
}
