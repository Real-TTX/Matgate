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
    private readonly SecretProtector _protector;

    public JsonDataStore(IConfiguration configuration, IHostEnvironment environment, ILogger<JsonDataStore> logger, SecretProtector protector)
    {
        _logger = logger;
        _protector = protector;

        var configured = Environment.GetEnvironmentVariable("MATGATE_DATA_DIR")
            ?? configuration["Matgate:DataDirectory"];
        var configuredWorkspaceRoot = Environment.GetEnvironmentVariable("MATGATE_WORKSPACE_ROOT")
            ?? configuration["Matgate:WorkspaceRoot"];

        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "data")
            : configured);
        WorkspaceRootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredWorkspaceRoot)
            ? Path.Combine(DataDirectory, "workspaces")
            : configuredWorkspaceRoot);

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(WorkspaceRootDirectory);
    }

    public string DataDirectory { get; }

    public string WorkspaceRootDirectory { get; }

    private string UsersPath => Path.Combine(DataDirectory, "users.json");

    private string ServersPath => Path.Combine(DataDirectory, "servers.json");

    private string WorkspacesPath => Path.Combine(DataDirectory, "workspaces.json");

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
            var servers = await ReadListAsync<ServerEndpoint>(ServersPath, cancellationToken);
            foreach (var server in servers)
            {
                server.Password = _protector.Unprotect(server.Password);
            }
            return servers;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkspaceDefinition>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadListAsync<WorkspaceDefinition>(WorkspacesPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureWorkspacePublicAccessDefaultsAsync(TimeSpan defaultDuration, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var workspaces = await ReadListAsync<WorkspaceDefinition>(WorkspacesPath, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var changed = false;

            foreach (var workspace in workspaces.Where(workspace => workspace.PublicAccessExpiresAt is null))
            {
                workspace.PublicAccessExpiresAt = now.Add(defaultDuration);
                workspace.UpdatedAt = now;
                changed = true;
            }

            if (changed)
            {
                await WriteListAsync(WorkspacesPath, workspaces, cancellationToken);
            }
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

    // Sign-in accepts the username OR the (optional) email address in the same field.
    public async Task<MatgateUser?> FindUserByNameOrEmailAsync(string input, CancellationToken cancellationToken = default)
    {
        var users = await GetUsersAsync(cancellationToken);
        var normalized = PasswordHasher.NormalizeUserName(input);
        var byName = users.FirstOrDefault(user => string.Equals(user.UserName, normalized, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        var email = (input ?? "").Trim();
        if (email.Length == 0 || !email.Contains('@'))
        {
            return null;
        }

        return users.FirstOrDefault(user =>
            !string.IsNullOrWhiteSpace(user.Email)
            && string.Equals(user.Email.Trim(), email, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        return (await GetUsersAsync(cancellationToken)).Count > 0;
    }

    public async Task<ServerEndpoint?> FindServerByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return (await GetServersAsync(cancellationToken)).FirstOrDefault(server => server.Id == id);
    }

    public async Task<WorkspaceDefinition?> FindWorkspaceByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return (await GetWorkspacesAsync(cancellationToken)).FirstOrDefault(workspace => workspace.Id == id);
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
            foreach (var server in servers)
            {
                server.Password = _protector.Unprotect(server.Password);
            }
            update(servers);
            foreach (var server in servers)
            {
                server.Password = _protector.Protect(server.Password);
            }
            await WriteListAsync(ServersPath, servers, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateWorkspacesAsync(Action<List<WorkspaceDefinition>> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var workspaces = await ReadListAsync<WorkspaceDefinition>(WorkspacesPath, cancellationToken);
            update(workspaces);
            await WriteListAsync(WorkspacesPath, workspaces, cancellationToken);
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

        // Seeding via environment is for automated deployments only. Without an explicit
        // MATGATE_ADMIN_PASSWORD the first-run setup wizard (/setup) creates the admin account
        // interactively (username + email + password).
        var password = Environment.GetEnvironmentVariable("MATGATE_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation("No users and no MATGATE_ADMIN_PASSWORD set - the setup wizard will run on first visit.");
            return;
        }

        var userName = PasswordHasher.NormalizeUserName(
            Environment.GetEnvironmentVariable("MATGATE_ADMIN_USER") ?? "admin");
        var email = (Environment.GetEnvironmentVariable("MATGATE_ADMIN_EMAIL") ?? "").Trim();

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
                Email = email,
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

        logger.LogWarning("Seed admin user '{UserName}' was created from MATGATE_ADMIN_* environment variables.", userName);
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

    public async Task MigrateServerSecretsAsync(CancellationToken cancellationToken = default)
    {
        if (!_protector.IsEnabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var servers = await ReadListAsync<ServerEndpoint>(ServersPath, cancellationToken);
            var plaintextPresent = servers.Any(server =>
                !string.IsNullOrEmpty(server.Password) && !_protector.IsProtected(server.Password));
            if (!plaintextPresent)
            {
                return;
            }

            // One-time safety backup before rewriting real credentials.
            var backupPath = ServersPath + ".plaintext.bak";
            if (File.Exists(ServersPath) && !File.Exists(backupPath))
            {
                File.Copy(ServersPath, backupPath);
            }

            foreach (var server in servers)
            {
                server.Password = _protector.Protect(server.Password);
            }

            await WriteListAsync(ServersPath, servers, cancellationToken);
            _logger.LogWarning(
                "Encrypted {Count} stored server credential(s) at rest. A one-time plaintext backup was "
                + "written to '{Backup}' - move it off the host or delete it once the app is confirmed working.",
                servers.Count,
                backupPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveLegacyGatewayServersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var servers = await ReadListAsync<ServerEndpoint>(ServersPath, cancellationToken);
            var legacyServerIds = servers
                .Where(server => server.Protocol == ServerProtocol.LegacyBrowser)
                .Select(server => server.Id)
                .ToHashSet();

            if (legacyServerIds.Count == 0)
            {
                return;
            }

            servers.RemoveAll(server => server.Protocol == ServerProtocol.LegacyBrowser);
            await WriteListAsync(ServersPath, servers, cancellationToken);

            var users = await ReadListAsync<MatgateUser>(UsersPath, cancellationToken);
            foreach (var user in users)
            {
                user.FavoriteServerIds ??= [];
                user.ServerAccess.RemoveAll(legacyServerIds.Contains);
                user.FavoriteServerIds.RemoveAll(legacyServerIds.Contains);
            }

            await WriteListAsync(UsersPath, users, cancellationToken);
            _logger.LogInformation("Removed legacy gateway server entries from the data store.");
        }
        finally
        {
            _gate.Release();
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
