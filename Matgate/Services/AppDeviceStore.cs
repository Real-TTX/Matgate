using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matgate.Models;

namespace Matgate.Services;

// The devices apps were allowed on (app-devices.json) and the sign-ins under way. An app signs in the way a
// desktop app does with OAuth: it opens /app/authorize in the browser, the user signs in as always (password, second
// factor, MatIdentity, Entra) and allows the device; Matgate sends a one-time code back to the app's own address on
// this computer (127.0.0.1), and the app trades it - together with the secret only it knows (PKCE) - for its token.
// Every request of the app reads the device by the token's hash, so the list is kept in memory.
public sealed class AppDeviceStore
{
    // Apps that may ask. One for now.
    public static readonly IReadOnlyDictionary<string, string> Apps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["thixgate"] = "ThixGate",
    };

    public const int MaxNameLength = 80;

    public const string TokenPrefix = "mgd_";

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);
    private List<AppDevice> _devices;

    public AppDeviceStore(JsonDataStore store)
        : this(Path.Combine(store.DataDirectory, "app-devices.json"))
    {
    }

    public AppDeviceStore(string path)
    {
        _path = path;
        _devices = File.Exists(path)
            ? JsonSerializer.Deserialize<List<AppDevice>>(File.ReadAllText(path), JsonOptions) ?? []
            : [];
    }

    // A sign-in the user allowed, waiting for the app to trade its code.
    public sealed record PendingCode(Guid UserId, string App, string DeviceName, string RedirectUri, string CodeChallenge, DateTimeOffset Expires);

    // What the app gets for its code.
    public sealed record IssuedToken(AppDevice Device, string Token);

    public IReadOnlyList<AppDevice> ForUser(Guid userId)
    {
        lock (_gate)
        {
            return [.. _devices.Where(device => device.UserId == userId).OrderByDescending(device => device.LastUsedAt ?? device.CreatedAt)];
        }
    }

    // The device of a token, or null.
    public AppDevice? FindByToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(token);
        lock (_gate)
        {
            return _devices.FirstOrDefault(device => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(device.TokenHash), Encoding.ASCII.GetBytes(hash)));
        }
    }

    // The user allowed the device: a one-time code for the app.
    public string CreateCode(Guid userId, string app, string deviceName, string redirectUri, string codeChallenge, DateTimeOffset now)
    {
        foreach (var expired in _codes.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToList())
        {
            _codes.TryRemove(expired, out _);
        }

        var code = Base64Url(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new PendingCode(userId, app, CleanName(deviceName), redirectUri, codeChallenge, now + CodeLifetime);
        return code;
    }

    // The app trades its code (once) and proves with the verifier that it is the one that asked. Null: no such code,
    // used already, run out, another address, or the wrong verifier - the app learns no more than that.
    public PendingCode? RedeemCode(string? code, string? codeVerifier, string? redirectUri, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(code) || !_codes.TryRemove(code, out var pending))
        {
            return null;
        }

        if (pending.Expires <= now
            || !string.Equals(pending.RedirectUri, redirectUri, StringComparison.Ordinal)
            || !PkceMatches(pending.CodeChallenge, codeVerifier))
        {
            return null;
        }

        return pending;
    }

    public IssuedToken AddDevice(PendingCode pending, string address, DateTimeOffset now)
    {
        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new AppDevice
        {
            UserId = pending.UserId,
            App = pending.App,
            Name = pending.DeviceName,
            TokenHash = Hash(token),
            CreatedAt = now,
            LastUsedAt = now,
            LastAddress = address,
        };
        lock (_gate)
        {
            _devices.Add(device);
            Save();
        }

        return new IssuedToken(device, token);
    }

    // Notes the use - at most once a minute per device, so a busy app does not write the file all the time.
    public void Touch(AppDevice device, string address, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (device.LastUsedAt is { } last && now - last < TimeSpan.FromMinutes(1) && device.LastAddress == address)
            {
                return;
            }

            device.LastUsedAt = now;
            device.LastAddress = address;
            Save();
        }
    }

    // Removes a device of this user. False: there is none with this id (or it is someone else's).
    public bool Remove(Guid userId, Guid deviceId)
    {
        lock (_gate)
        {
            var removed = _devices.RemoveAll(device => device.Id == deviceId && device.UserId == userId) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    // Every device of a user (the user was deleted).
    public void RemoveUser(Guid userId)
    {
        lock (_gate)
        {
            if (_devices.RemoveAll(device => device.UserId == userId) > 0)
            {
                Save();
            }
        }
    }

    // The address an app may be sent back to: its own on this computer, plain http on a loopback address with a port
    // (RFC 8252 7.3). Anything else could hand the code to someone else.
    public static bool IsLoopbackRedirect(string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return uri.Host is "127.0.0.1" or "[::1]" or "localhost";
    }

    // S256 only: the challenge is base64url(SHA-256(verifier)); a verifier has 43 to 128 characters (RFC 7636 4.1).
    public static bool PkceMatches(string challenge, string? verifier)
    {
        if (verifier is null || verifier.Length is < 43 or > 128 || verifier.Any(character => !IsUnreserved(character)))
        {
            return false;
        }

        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    public static bool IsValidChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    // A device's name as it is stored: no control characters, nothing around it, not endlessly long.
    public static string CleanName(string? value)
    {
        var name = new string((value ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].Trim();
        }

        return name.Length == 0 ? "?" : name;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsUnreserved(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~';

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_devices, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
