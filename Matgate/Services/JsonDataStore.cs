using System.Text.Json;
using Matgate.Models;

namespace Matgate.Services;

public sealed class JsonDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonDataStore> _logger;

    public JsonDataStore(IConfiguration configuration, IHostEnvironment environment, ILogger<JsonDataStore> logger)
    {
        _logger = logger;

        var configured = Environment.GetEnvironmentVariable("MATGATE_DATA_DIR")
            ?? configuration["Matgate:DataDirectory"];

        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "data")
            : configured);

        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }

    private string UsersPath => Path.Combine(DataDirectory, "users.json");

    private string ServersPath => Path.Combine(DataDirectory, "servers.json");

    public async Task<IReadOnlyList<MatgateUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadListAsync<MatgateUser>(UsersPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ServerEndpoint>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadListAsync<ServerEndpoint>(ServersPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MatgateUser?> FindUserByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return (await GetUsersAsync(cancellationToken)).FirstOrDefault(user => user.Id == id);
    }

    public async Task<MatgateUser?> FindUserByNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        var normalized = PasswordHasher.NormalizeUserName(userName);
        return (await GetUsersAsync(cancellationToken))
            .FirstOrDefault(user => string.Equals(user.UserName, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ServerEndpoint?> FindServerByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return (await GetServersAsync(cancellationToken)).FirstOrDefault(server => server.Id == id);
    }

    public async Task UpdateUsersAsync(Action<List<MatgateUser>> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadListAsync<MatgateUser>(UsersPath, cancellationToken);
            update(users);
            await WriteListAsync(UsersPath, users, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateServersAsync(Action<List<ServerEndpoint>> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var servers = await ReadListAsync<ServerEndpoint>(ServersPath, cancellationToken);
            update(servers);
            await WriteListAsync(ServersPath, servers, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureSeedAdminAsync(PasswordHasher hasher, ILogger logger, CancellationToken cancellationToken = default)
    {
        var users = await GetUsersAsync(cancellationToken);
        if (users.Count > 0)
        {
            return;
        }

        var userName = PasswordHasher.NormalizeUserName(
            Environment.GetEnvironmentVariable("MATGATE_ADMIN_USER") ?? "admin");
        var password = Environment.GetEnvironmentVariable("MATGATE_ADMIN_PASSWORD") ?? "change-me-now";

        await UpdateUsersAsync(current =>
        {
            if (current.Count > 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            current.Add(new MatgateUser
            {
                UserName = userName,
                DisplayName = "Administrator",
                PasswordHash = hasher.Hash(password),
                GuacamolePassword = hasher.GenerateSecret(),
                IsAdmin = true,
                CanManageServers = true,
                CanCreateServers = true,
                PreferredLanguage = "en",
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }, cancellationToken);

        logger.LogWarning(
            "Seed admin user '{UserName}' was created. Change the default password immediately if MATGATE_ADMIN_PASSWORD was not set.",
            userName);
    }

    public async Task EnsureGuacamoleSecretsAsync(PasswordHasher hasher, CancellationToken cancellationToken = default)
    {
        var changed = false;
        await UpdateUsersAsync(users =>
        {
            foreach (var user in users.Where(user => string.IsNullOrWhiteSpace(user.GuacamolePassword)))
            {
                user.GuacamolePassword = hasher.GenerateSecret();
                user.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }, cancellationToken);

        if (changed)
        {
            _logger.LogInformation("Generated missing Guacamole bridge passwords for existing users.");
        }
    }

    public async Task EnsureBrowserGatewayEndpointAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!BrowserGatewayEnabled(configuration))
        {
            return;
        }

        var host = ConfigValue(configuration, "MATGATE_BROWSER_HOST", "Matgate:BrowserHost", "browser");
        var name = ConfigValue(configuration, "MATGATE_BROWSER_NAME", "Matgate:BrowserName", "Lokaler Browser");
        var password = ConfigValue(configuration, "MATGATE_BROWSER_VNC_PASSWORD", "Matgate:BrowserVncPassword", "matgate1");
        var port = int.TryParse(
            ConfigValue(configuration, "MATGATE_BROWSER_PORT", "Matgate:BrowserPort", "5900"),
            out var parsedPort)
            && parsedPort is >= 1 and <= 65535
                ? parsedPort
                : 5900;

        var created = false;
        await UpdateServersAsync(servers =>
        {
            if (servers.Any(server => server.Protocol == ServerProtocol.Browser))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            servers.Add(new ServerEndpoint
            {
                Name = name,
                Protocol = ServerProtocol.Browser,
                Host = host,
                Port = port,
                Password = password,
                IsEnabled = true,
                Notes = "Chromium im Matgate-Docker-Netz",
                CreatedAt = now,
                UpdatedAt = now
            });
            created = true;
        }, cancellationToken);

        if (created)
        {
            logger.LogInformation("Seeded local browser gateway endpoint '{Name}' at {Host}:{Port}.", name, host, port);
        }
    }

    private static async Task<List<T>> ReadListAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task WriteListAsync<T>(string path, List<T> values, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, values, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static bool BrowserGatewayEnabled(IConfiguration configuration)
    {
        var value = Environment.GetEnvironmentVariable("MATGATE_BROWSER_GATEWAY_ENABLED")
            ?? configuration["Matgate:BrowserGatewayEnabled"];

        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
    }

    private static string ConfigValue(
        IConfiguration configuration,
        string environmentVariable,
        string configurationKey,
        string fallback)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable)
            ?? configuration[configurationKey];

        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
