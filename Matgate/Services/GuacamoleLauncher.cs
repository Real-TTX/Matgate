using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matgate.Models;

namespace Matgate.Services;

public sealed class GuacamoleLauncher
{
    private static readonly byte[] ZeroIv = new byte[16];

    private readonly IConfiguration _configuration;
    private readonly JsonDataStore _dataStore;
    private readonly FileShareService _fileShares;
    private readonly ILogger<GuacamoleLauncher> _logger;

    public GuacamoleLauncher(
        IConfiguration configuration,
        JsonDataStore dataStore,
        FileShareService fileShares,
        ILogger<GuacamoleLauncher> logger)
    {
        _configuration = configuration;
        _dataStore = dataStore;
        _fileShares = fileShares;
        _logger = logger;
    }

    public Task<GuacamoleLaunchResult> CreateLaunchAsync(
        MatgateUser user,
        ServerEndpoint server,
        bool ephemeralServer = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ServerEndpoint.IsWebsiteProtocol(server.Protocol))
        {
            return Task.FromResult(GuacamoleLaunchResult.Failed(
                "Websites are opened directly in Matgate."));
        }

        if (!ServerEndpoint.IsGuacamoleProtocol(server.Protocol))
        {
            return Task.FromResult(GuacamoleLaunchResult.Failed(
                "Dateiverbindungen werden im Matgate-Dateimanager gestartet."));
        }

        var secret = SecretUtil.FirstNonEmpty(
            _configuration["Guacamole:JsonSecretKey"],
            Environment.GetEnvironmentVariable("GUACAMOLE_JSON_SECRET_KEY"),
            Environment.GetEnvironmentVariable("JSON_SECRET_KEY"),
            SecretUtil.ReadSecretFile(
                Environment.GetEnvironmentVariable("MATGATE_GUACAMOLE_JSON_SECRET_KEY_FILE")
                ?? _configuration["Guacamole:JsonSecretKeyFile"]
                ?? "/run/matgate-secrets/guac.key"));

        if (!TryReadHexKey(secret, out var key))
        {
            return Task.FromResult(GuacamoleLaunchResult.Failed(
                "Guacamole JSON auth secret is missing or invalid. Set Guacamole:JsonSecretKey / GUACAMOLE_JSON_SECRET_KEY to 32 hex characters."));
        }

        var connectionName = GuacamoleConfigWriter.ConnectionName(server);
        var sessionId = $"{server.Id:N}-{Guid.NewGuid():N}";
        var payload = BuildJsonPayload(user, server, connectionName, sessionId, ephemeralServer);
        var encryptedData = EncryptAndSign(payload, key);
        var publicBasePath = _configuration["Guacamole:PublicBasePath"] ?? "/guacamole";
        var directLaunch = _configuration.GetValue("Guacamole:DirectLaunch", true);

        var url = directLaunch
            ? $"{publicBasePath.TrimEnd('/')}/#/client/{Uri.EscapeDataString(ClientIdentifier(connectionName))}?data={Uri.EscapeDataString(encryptedData)}"
            : $"{publicBasePath.TrimEnd('/')}/#/?data={Uri.EscapeDataString(encryptedData)}";

        return Task.FromResult(GuacamoleLaunchResult.Ok(url, encryptedData, connectionName, sessionId));
    }

    private string BuildJsonPayload(
        MatgateUser user,
        ServerEndpoint server,
        string connectionName,
        string sessionId,
        bool ephemeralServer)
    {
        var ttlMinutes = Math.Clamp(_configuration.GetValue("Guacamole:LaunchTtlMinutes", 2), 1, 30);
        var parameters = new Dictionary<string, string>
        {
            ["hostname"] = server.Host,
            ["port"] = server.Port.ToString()
        };

        if (server.Protocol is ServerProtocol.Rdp or ServerProtocol.Ssh
            && !string.IsNullOrWhiteSpace(server.UserName))
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

            // Report as "Matgate" instead of the default "Guacamole" so Windows shows the
            // redirected drive as "Matgate on Matgate" rather than "... on Guacamole".
            parameters["client-name"] = "Matgate";

            // Redirect a drive into the session so files can be transferred like real RDP (drag &
            // drop / upload in Matgate). Matgate assembles the directory guacd serves: a scratch
            // folder for this session plus a link to every file area the user may use.
            //
            // If the file areas are not shared with guacd, there is nothing to assemble and the
            // session falls back to a folder guacd makes for itself under its own /tmp. That still
            // gives working file transfer, just without the persistent areas. It has to stay exactly
            // ONE level below /tmp: guacd's create-drive-path is a single non-recursive mkdir(), so
            // a nested path would fail the moment its parent is missing.
            parameters["enable-drive"] = "true";
            parameters["drive-name"] = "Matgate";
            parameters["create-drive-path"] = "true";
            parameters["drive-path"] = _fileShares.CreateSessionView(user, server, sessionId, ephemeralServer)
                ?? $"/tmp/matgate-drive-{sessionId}";
        }
        else if (server.Protocol == ServerProtocol.Vnc)
        {
            // Guacamole's VNC support uses the same shared password field and
            // does not need any extra protocol-specific parameters for the
            // standard outbound connection case.
        }
        else if (server.Protocol == ServerProtocol.Ssh)
        {
            parameters["font-name"] = "monospace";
            parameters["font-size"] = ServerEndpoint.NormalizeTerminalFontSize(server.TerminalFontSize).ToString();

            // File transfer inside the session (the same "Send files" button and drag & drop as RDP).
            // guacd reuses this very SSH connection for SFTP, so there is no second host, no second
            // login and nothing to configure. Files also go straight to the remote filesystem instead
            // of a redirected drive the user would have to pick them up from.
            parameters["enable-sftp"] = "true";
        }

        var payload = new
        {
            username = user.UserName,
            expires = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes).ToUnixTimeMilliseconds(),
            connections = new Dictionary<string, object>
            {
                [connectionName] = new
                {
                    id = sessionId,
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

    // The example key shipped with guacamole-auth-json (and older Matgate defaults). It is public,
    // so a token signed with it is forgeable by anyone — refuse it outright.
    private const string KnownPublicSecret = "0123456789abcdeffedcba9876543210";

    private static bool TryReadHexKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
        {
            return false;
        }

        if (string.Equals(value.Trim(), KnownPublicSecret, StringComparison.OrdinalIgnoreCase))
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
    string? ConnectionName,
    // Names the file areas prepared for this session, so the client can report it as still open.
    string? SessionId)
{
    public static GuacamoleLaunchResult Ok(string url, string encryptedData, string connectionName, string sessionId)
    {
        return new(true, url, null, encryptedData, connectionName, sessionId);
    }

    public static GuacamoleLaunchResult Failed(string error) => new(false, null, error, null, null, null);
}
