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
    private readonly ILogger<GuacamoleLauncher> _logger;

    public GuacamoleLauncher(IConfiguration configuration, JsonDataStore dataStore, ILogger<GuacamoleLauncher> logger)
    {
        _configuration = configuration;
        _dataStore = dataStore;
        _logger = logger;
    }

    public Task<GuacamoleLaunchResult> CreateLaunchAsync(
        MatgateUser user,
        ServerEndpoint server,
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
        var payload = BuildJsonPayload(user, server, connectionName, sessionId);
        var encryptedData = EncryptAndSign(payload, key);
        var publicBasePath = _configuration["Guacamole:PublicBasePath"] ?? "/guacamole";
        var directLaunch = _configuration.GetValue("Guacamole:DirectLaunch", true);

        var url = directLaunch
            ? $"{publicBasePath.TrimEnd('/')}/#/client/{Uri.EscapeDataString(ClientIdentifier(connectionName))}?data={Uri.EscapeDataString(encryptedData)}"
            : $"{publicBasePath.TrimEnd('/')}/#/?data={Uri.EscapeDataString(encryptedData)}";

        return Task.FromResult(GuacamoleLaunchResult.Ok(url, encryptedData, connectionName));
    }

    private string BuildJsonPayload(MatgateUser user, ServerEndpoint server, string connectionName, string sessionId)
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

            // Redirect a shared drive into the session so files can be transferred like real RDP
            // (drag & drop / upload in Matgate). guacd stores the drive under drive-path.
            parameters["enable-drive"] = "true";
            parameters["drive-name"] = "Matgate";
            parameters["create-drive-path"] = "true";
            parameters["drive-path"] = PrepareDrivePath(server);
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

    // Decides where guacd keeps the folder it redirects into the RDP session as the "Matgate" drive.
    //
    // Two modes, so file transfer works with and without extra setup:
    //
    //  * Shared folder (what the shipped compose files do): ./data/guac-drives is mounted into guacd
    //    as /drive and into matgate as <data>/guac-drives. Transferred files then live on the host,
    //    survive restarts and are part of a normal backup. guacd runs as uid 1000 and cannot create
    //    anything inside that root-owned mount itself, so matgate - which sees the same folder -
    //    creates the per-connection folder up front and makes it writable for guacd.
    //
    //  * Scratch folder (default, no configuration, no volume): a folder directly under /tmp inside
    //    the guacd container, which is world-writable, so guacd can create it on its own. Deliberately
    //    ONE level deep - guacd's create-drive-path is a single non-recursive mkdir(), so a nested
    //    path would fail with ENOENT the moment the parent is missing. Nothing on the host has to be
    //    prepared; the files live in the guacd container's writable layer, so they outlive a restart
    //    and are discarded when that container is recreated (image update, compose down/up).
    // Which one is used is decided by the deployment itself, without a setting to get wrong: the
    // shared folder is used exactly when <data>/guac-drives is a real mount, because that is the same
    // volume the compose file hands to guacd as /drive. Nothing mounted there, nothing to share -
    // then the scratch folder it is. Remove the mount later and it simply falls back.
    //
    // Which mode was picked is logged on every connect: when a transfer silently does nothing, guacd
    // only ever writes a single line about it, so the gateway log is the one place that can answer
    // "where did my files go".
    private string PrepareDrivePath(ServerEndpoint server)
    {
        var id = server.Id.ToString("N");
        var root = SharedDriveRoot();

        if (!IsMountPoint(root))
        {
            WarnAboutAbandonedSharedDrive(root);
            _logger.LogInformation(
                "RDP drive for {Server}: scratch folder inside the guacd container, because nothing is "
                + "mounted at {Path}. Transfer works either way; mount a folder there (and into guacd "
                + "as /drive) to keep transferred files on the host.",
                server.Name,
                root);
            return $"/tmp/matgate-drive-{id}";
        }

        EnsureSharedDriveDirectory(root, server);
        _logger.LogInformation(
            "RDP drive for {Server}: shared folder {HostPath}, which guacd sees as /drive/{Id}.",
            server.Name,
            Path.Combine(root, id),
            id);
        return $"/drive/{id}";
    }

    // True when the path is its own mount, i.e. a volume the compose file put there rather than an
    // ordinary folder inside the data directory. Linux containers only - which is where guacd lives.
    private bool IsMountPoint(string path)
    {
        try
        {
            if (!Directory.Exists(path) || !File.Exists("/proc/self/mountinfo"))
            {
                return false;
            }

            var normalized = path.TrimEnd('/');
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                // Fields are space separated; the 5th is the mount point inside this container.
                var fields = line.Split(' ');
                if (fields.Length > 4 && fields[4] == normalized)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine whether {Path} is a mount.", path);
        }

        return false;
    }

    // A guac-drives folder with files in it but no mount is a reliable sign that this deployment used
    // to keep transferred files on the host and lost the volume somewhere - worth saying out loud,
    // because the transfer itself keeps working and nothing else would hint at it.
    private void WarnAboutAbandonedSharedDrive(string root)
    {
        try
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                _logger.LogWarning(
                    "{Path} still holds files but is not a mount, so new transfers go to a scratch "
                    + "folder inside guacd instead. Mount the folder there again to keep using it.",
                    root);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check the shared drive folder.");
        }
    }

    private string SharedDriveRoot() => Path.Combine(_dataStore.DataDirectory, "guac-drives");

    // Prepares matgate's own side of the shared drive folder (see PrepareDrivePath).
    private void EnsureSharedDriveDirectory(string root, ServerEndpoint server)
    {
        try
        {
            var path = Path.Combine(root, server.Id.ToString("N"));
            Directory.CreateDirectory(path);

            if (!OperatingSystem.IsWindows())
            {
                // guacd runs as uid 1000 and matgate as root, so plain 0755 would leave the folder
                // read-only for guacd - uploads would silently never arrive. Only the per-connection
                // folder is opened up; the root keeps its own mode, guacd just has to traverse it.
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not prepare the redirected drive folder for {Server}; RDP file transfer may not work.",
                server.Name);
        }
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
    string? ConnectionName)
{
    public static GuacamoleLaunchResult Ok(string url, string encryptedData, string connectionName)
    {
        return new(true, url, null, encryptedData, connectionName);
    }

    public static GuacamoleLaunchResult Failed(string error) => new(false, null, error, null, null);
}
