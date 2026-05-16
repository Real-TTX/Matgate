using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matgate.Models;

namespace Matgate.Services;

public sealed class GuacamoleLauncher
{
    private static readonly byte[] ZeroIv = new byte[16];

    private readonly IConfiguration _configuration;

    public GuacamoleLauncher(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<GuacamoleLaunchResult> CreateLaunchAsync(
        MatgateUser user,
        ServerEndpoint server,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ServerEndpoint.IsGuacamoleProtocol(server.Protocol))
        {
            return Task.FromResult(GuacamoleLaunchResult.Failed(
                "Dateiverbindungen werden im Matgate-Dateimanager gestartet."));
        }

        var secret = _configuration["Guacamole:JsonSecretKey"]
            ?? Environment.GetEnvironmentVariable("GUACAMOLE_JSON_SECRET_KEY")
            ?? Environment.GetEnvironmentVariable("JSON_SECRET_KEY");

        if (!TryReadHexKey(secret, out var key))
        {
            return Task.FromResult(GuacamoleLaunchResult.Failed(
                "Guacamole JSON auth secret is missing or invalid. Set Guacamole:JsonSecretKey / GUACAMOLE_JSON_SECRET_KEY to 32 hex characters."));
        }

        var connectionName = GuacamoleConfigWriter.ConnectionName(server);
        var payload = BuildJsonPayload(user, server, connectionName);
        var encryptedData = EncryptAndSign(payload, key);
        var publicBasePath = _configuration["Guacamole:PublicBasePath"] ?? "/guacamole";
        var directLaunch = _configuration.GetValue("Guacamole:DirectLaunch", true);

        var url = directLaunch
            ? $"{publicBasePath.TrimEnd('/')}/#/client/{Uri.EscapeDataString(ClientIdentifier(connectionName))}?data={Uri.EscapeDataString(encryptedData)}"
            : $"{publicBasePath.TrimEnd('/')}/#/?data={Uri.EscapeDataString(encryptedData)}";

        return Task.FromResult(GuacamoleLaunchResult.Ok(url, encryptedData, connectionName));
    }

    private string BuildJsonPayload(MatgateUser user, ServerEndpoint server, string connectionName)
    {
        var ttlMinutes = Math.Clamp(_configuration.GetValue("Guacamole:LaunchTtlMinutes", 2), 1, 30);
        var parameters = new Dictionary<string, string>
        {
            ["hostname"] = server.Host,
            ["port"] = server.Port.ToString()
        };

        if (!string.IsNullOrWhiteSpace(server.UserName))
        {
            parameters["username"] = server.UserName;
        }

        if (!string.IsNullOrWhiteSpace(server.Password))
        {
            parameters["password"] = server.Password;
        }

        if (server.Protocol == ServerProtocol.Rdp)
        {
            if (!string.IsNullOrWhiteSpace(server.Domain))
            {
                parameters["domain"] = server.Domain;
            }

            parameters["security"] = "any";
            parameters["ignore-cert"] = server.IgnoreCertificate ? "true" : "false";
            parameters["server-layout"] = string.IsNullOrWhiteSpace(server.KeyboardLayout)
                ? ServerEndpoint.DefaultKeyboardLayout
                : server.KeyboardLayout.Trim();
            parameters["resize-method"] = "reconnect";
            parameters["enable-wallpaper"] = "false";
        }
        else if (server.Protocol == ServerProtocol.Ssh)
        {
            parameters["font-name"] = "monospace";
            parameters["font-size"] = ServerEndpoint.NormalizeTerminalFontSize(server.TerminalFontSize).ToString();
        }

        var payload = new
        {
            username = user.UserName,
            expires = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes).ToUnixTimeMilliseconds(),
            connections = new Dictionary<string, object>
            {
                [connectionName] = new
                {
                    id = server.Id.ToString("N"),
                    protocol = GuacamoleConfigWriter.ProtocolName(server.Protocol),
                    parameters
                }
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string EncryptAndSign(string json, byte[] key)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] signature;

        using (var hmac = new HMACSHA256(key))
        {
            signature = hmac.ComputeHash(jsonBytes);
        }

        var signedPayload = new byte[signature.Length + jsonBytes.Length];
        Buffer.BlockCopy(signature, 0, signedPayload, 0, signature.Length);
        Buffer.BlockCopy(jsonBytes, 0, signedPayload, signature.Length, jsonBytes.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = ZeroIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(signedPayload, 0, signedPayload.Length);
        return Convert.ToBase64String(encrypted);
    }

    private static string ClientIdentifier(string connectionName)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connectionName}\0c\0json"));
    }

    private static bool TryReadHexKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
        {
            return false;
        }

        try
        {
            key = Convert.FromHexString(value);
            return key.Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record GuacamoleLaunchResult(
    bool Success,
    string? Url,
    string? Error,
    string? EncryptedData,
    string? ConnectionName)
{
    public static GuacamoleLaunchResult Ok(string url, string encryptedData, string connectionName)
    {
        return new(true, url, null, encryptedData, connectionName);
    }

    public static GuacamoleLaunchResult Failed(string error) => new(false, null, error, null, null);
}
