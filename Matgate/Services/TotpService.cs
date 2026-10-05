using System.Security.Cryptography;
using System.Text;

namespace Matgate.Services;

// The second factor per RFC 6238: a six-digit code that changes every 30 seconds and is built from
// a shared secret and the time of day. The authenticator app computes the same thing - nothing is
// transmitted in the process.
//
// Two things that are easy to get wrong and are deliberately handled differently here:
//
//  * Clocks drift apart. So not only the current time step is checked but also the one before and
//    the one after - just under a minute of leeway. No more: every further step widens the window
//    in which an intercepted code is still valid.
//  * A code is valid for 30 seconds - long enough to type it a second time. Someone reading it
//    along could use it themselves within that time. So the account remembers the most recently
//    used time step, and anything not newer than that is rejected.
public static class TotpService
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const int Window = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // 20 bytes = 160 bits, the length RFC 4226 specifies for HMAC-SHA1.
    public static string NewSecret()
    {
        return ToBase32(RandomNumberGenerator.GetBytes(20));
    }

    // What the authenticator app scans. The issuer appears twice - once as a path, once as a
    // parameter - because the apps disagree about which one they read.
    public static string Uri(string issuer, string account, string secret)
    {
        var label = System.Uri.EscapeDataString(issuer) + ":" + System.Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}"
            + $"?secret={secret}"
            + $"&issuer={System.Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    // Checks a code and returns the time step it belonged to. The caller writes that step into the
    // account; next time it has to be strictly greater.
    public static bool Verify(string? secret, string? code, long lastUsedStep, out long usedStep)
    {
        usedStep = 0;
        var clean = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != Digits || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        byte[] key;
        try
        {
            key = FromBase32(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -Window; offset <= Window; offset++)
        {
            var step = now + offset;
            if (step <= lastUsedStep)
            {
                // Already used - or older than the last one used. Neither counts.
                continue;
            }

            if (FixedTimeEquals(Compute(key, step), clean))
            {
                usedStep = step;
                return true;
            }
        }

        return false;
    }

    // Recovery codes for the case where the phone is gone. Ten of them, 10 characters each from the
    // Base32 alphabet - that is 50 bits and therefore not guessable.
    public static IReadOnlyList<string> NewRecoveryCodes(int count = 10)
    {
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var chars = new char[10];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = Base32Alphabet[RandomNumberGenerator.GetInt32(Base32Alphabet.Length)];
            }

            codes.Add(new string(chars, 0, 5) + "-" + new string(chars, 5, 5));
        }

        return codes;
    }

    // Only the hash is stored, never the code itself. SHA-256 is enough here where it would not be for
    // a password: a recovery code is not something a person made up but 50 random bits - no dictionary
    // helps there, and a slow algorithm would only slow down signing in.
    public static string HashRecoveryCode(string code)
    {
        var clean = Normalise(code);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clean))).ToLowerInvariant();
    }

    public static string Normalise(string code)
    {
        return new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static string Compute(byte[] key, long step)
    {
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xff);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);

        return (value % 1_000_000).ToString("D" + Digits);
    }

    // Character-by-character comparison with constant running time: anyone able to measure how long a
    // comparison takes could otherwise guess a code digit by digit.
    private static bool FixedTimeEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a),
            Encoding.ASCII.GetBytes(b));
    }

    private static string ToBase32(byte[] data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        var clean = text.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException("Not a Base32 character: " + c);
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
