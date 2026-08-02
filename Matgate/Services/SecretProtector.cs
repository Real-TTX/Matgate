using System.Security.Cryptography;
using System.Text;

namespace Matgate.Services;

/// <summary>
/// Encrypts sensitive values (device passwords) at rest with AES-256-GCM. The key is derived from
/// MATGATE_SECRET_KEY, which is provided from outside the data volume (e.g. an .env file), so a stolen
/// data directory / backup does not reveal the credentials. Values are self-describing via a marker
/// prefix, so legacy plaintext is read transparently and migrated on the next write.
/// </summary>
public sealed class SecretProtector
{
    private const string Marker = "enc:1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _key;
    private readonly ILogger<SecretProtector> _logger;

    public SecretProtector(IConfiguration configuration, ILogger<SecretProtector> logger)
    {
        _logger = logger;

        var raw = SecretUtil.FirstNonEmpty(
            Environment.GetEnvironmentVariable("MATGATE_SECRET_KEY"),
            configuration["Matgate:SecretKey"],
            SecretUtil.ReadSecretFile(Environment.GetEnvironmentVariable("MATGATE_SECRET_KEY_FILE")));

        if (!string.IsNullOrWhiteSpace(raw))
        {
            // Accept any operator-provided string; hash to a stable 32-byte AES key.
            _key = SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim()));
        }
        else
        {
            _logger.LogWarning(
                "MATGATE_SECRET_KEY is not set - stored device credentials stay in PLAINTEXT at rest. "
                + "Set a random value (e.g. `openssl rand -hex 32`) in .env to encrypt them.");
        }
    }

    public bool IsEnabled => _key is not null;

    public bool IsProtected(string? value)
        => value is not null && value.StartsWith(Marker, StringComparison.Ordinal);

    public string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || _key is null || IsProtected(plaintext))
        {
            return plaintext ?? "";
        }

        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, data, cipher, tag);
        }

        var combined = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, combined, NonceSize + TagSize, cipher.Length);
        return Marker + Convert.ToBase64String(combined);
    }

    public string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsProtected(value))
        {
            return value ?? ""; // legacy plaintext or empty
        }

        if (_key is null)
        {
            _logger.LogError("Found an encrypted secret but MATGATE_SECRET_KEY is not set; leaving it encrypted.");
            return value; // preserve so a later write does not destroy it
        }

        try
        {
            var combined = Convert.FromBase64String(value[Marker.Length..]);
            var nonce = combined[..NonceSize];
            var tag = combined[NonceSize..(NonceSize + TagSize)];
            var cipher = combined[(NonceSize + TagSize)..];
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // Wrong/rotated key: fail closed by returning the original (still-encrypted) value so the
            // connection fails visibly instead of silently overwriting the credential with garbage.
            _logger.LogError(ex, "Failed to decrypt a stored secret - is MATGATE_SECRET_KEY correct?");
            return value;
        }
    }
}
