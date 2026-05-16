using System.Security.Cryptography;
using System.Text;

namespace Matgate.Services;

public sealed class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000;
    private const string Algorithm = "pbkdf2-sha256";

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return string.Join(
            ':',
            Algorithm,
            Iterations.ToString(),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        var parts = passwordHash.Split(':');
        if (parts.Length != 4 || parts[0] != Algorithm)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public string GenerateSecret(int bytes = 32)
    {
        var raw = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(raw)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static bool IsValidUserName(string userName)
    {
        if (userName.Length is < 3 or > 64)
        {
            return false;
        }

        return userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@');
    }

    public static string NormalizeUserName(string userName)
    {
        return userName.Trim().ToLowerInvariant();
    }

    public static bool SecureEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }
}
