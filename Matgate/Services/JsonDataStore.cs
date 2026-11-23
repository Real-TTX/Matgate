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

    private string CredentialsPath => Path.Combine(DataDirectory, "credentials.json");

    private string GroupsPath => Path.Combine(DataDirectory, "groups.json");

    private string FoldersPath => Path.Combine(DataDirectory, "folders.json");

    public async Task<IReadOnlyList<UserGroup>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadListAsync<UserGroup>(GroupsPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateGroupsAsync(Action<List<UserGroup>> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var groups = await ReadListAsync<UserGroup>(GroupsPath, cancellationToken);
            update(groups);
            await WriteListAsync(GroupsPath, groups, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GrantsPath => Path.Combine(DataDirectory, "grants.json");

    public async Task<IReadOnlyList<AccessGrant>> GetGrantsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadListAsync<AccessGrant>(GrantsPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateGrantsAsync(Action<List<AccessGrant>> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var grants = await ReadListAsync<AccessGrant>(GrantsPath, cancellationToken);
            update(grants);
            await WriteListAsync(GrantsPath, grants, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The shared folder of each of these paths, made where there is none yet (a folder that so far existed only
    // as the name its connections carry): something lasting for an entry to point to. By AccessResolver.FolderKey.
    public async Task<Dictionary<string, FolderDefinition>> EnsureSharedFoldersAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var folders = await ReadListAsync<FolderDefinition>(FoldersPath, cancellationToken);
            var index = SharedFolderIndex(folders);
            var added = false;
            foreach (var path in paths.Select(ServerFolders.CleanPath).Where(path => path.Length > 0))
            {
                if (!index.ContainsKey(AccessResolver.FolderKey(path)))
                {
                    var folder = new FolderDefinition { Name = path };
                    folders.Add(folder);
                    index[AccessResolver.FolderKey(path)] = folder;
                    added = true;
                }
            }

            if (added)
            {
                await WriteListAsync(FoldersPath, folders, cancellationToken);
            }

            return index;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The shared folders (no owner) by their path, for AccessResolver. Two of the same path: the first one.
    private static Dictionary<string, FolderDefinition> SharedFolderIndex(IEnumerable<FolderDefinition> folders)
    {
        var index = new Dictionary<string, FolderDefinition>();
        foreach (var folder in folders.Where(folder => folder.OwnerUserId is null))
        {
            index.TryAdd(AccessResolver.FolderKey(folder.Name), folder);
        }

        return index;
    }

    // Before 0.41.0 access was kept in three places: on the user (all shared connections, single connections,
    // a read-only tick per file connection) and on the group (single connections, folders by name). All of it
    // becomes entries in grants.json - once, when that file does not exist yet. Folders a group was given by
    // name get a folder of their own first (folders.json), so that the entry has something lasting to point to:
    // renaming the folder later no longer loses it.
    public async Task MigrateAccessAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(GrantsPath))
            {
                return;
            }

            var grants = new List<AccessGrant>();
            var users = await ReadListAsync<MatgateUser>(UsersPath, cancellationToken);
            var groups = await ReadListAsync<UserGroup>(GroupsPath, cancellationToken);
            var folders = await ReadListAsync<FolderDefinition>(FoldersPath, cancellationToken);
            var servers = await ReadListAsync<ServerEndpoint>(ServersPath, cancellationToken);
            var shared = servers.Where(server => server.OwnerUserId is null).Select(server => server.Id).ToHashSet();

            foreach (var user in users.Where(user => !user.IsAdmin))
            {
                var readOnly = (user.FileAccessRules ?? []).Where(rule => rule.ReadOnly).Select(rule => rule.ServerId).ToHashSet();
                // "All connections" with a read-only tick somewhere cannot be one entry: an entry on all connections
                // would give Connect to the read-only one as well (the highest level wins). Then it becomes one
                // entry per connection there is now.
                var everyOne = user.ServerAccessAll && readOnly.Count == 0;
                if (everyOne)
                {
                    grants.Add(new AccessGrant { TargetKind = AccessTargetKind.All, PrincipalKind = AccessPrincipalKind.User, PrincipalId = user.Id, Level = AccessLevel.Connect });
                }

                var single = user.ServerAccessAll ? (everyOne ? [] : shared.ToList()) : user.ServerAccess.Where(shared.Contains).Distinct().ToList();
                foreach (var serverId in single)
                {
                    grants.Add(new AccessGrant
                    {
                        TargetKind = AccessTargetKind.Server,
                        TargetId = serverId,
                        PrincipalKind = AccessPrincipalKind.User,
                        PrincipalId = user.Id,
                        Level = readOnly.Contains(serverId) ? AccessLevel.ReadOnly : AccessLevel.Connect,
                    });
                }
            }

            foreach (var group in groups)
            {
                foreach (var serverId in group.ServerIds.Where(shared.Contains).Distinct())
                {
                    grants.Add(new AccessGrant { TargetKind = AccessTargetKind.Server, TargetId = serverId, PrincipalKind = AccessPrincipalKind.Group, PrincipalId = group.Id, Level = AccessLevel.Connect });
                }

                foreach (var path in group.Folders.Select(ServerFolders.CleanPath).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var folder = folders.FirstOrDefault(candidate => candidate.OwnerUserId is null && ServerFolders.Same(candidate.Name, path));
                    if (folder is null)
                    {
                        folder = new FolderDefinition { Name = path };
                        folders.Add(folder);
                    }

                    grants.Add(new AccessGrant { TargetKind = AccessTargetKind.Folder, TargetId = folder.Id, PrincipalKind = AccessPrincipalKind.Group, PrincipalId = group.Id, Level = AccessLevel.Connect });
                }
            }

            await WriteListAsync(FoldersPath, folders, cancellationToken);
            await WriteListAsync(GrantsPath, grants, cancellationToken);
            foreach (var user in users)
            {
                user.ServerAccessAll = false;
                user.ServerAccess = [];
                // The file rules (read-only, a subfolder) stay as they are: they hold for this user whatever
                // their groups give them.
            }

            foreach (var group in groups)
            {
                group.ServerIds = [];
                group.Folders = [];
            }

            await WriteListAsync(UsersPath, users, cancellationToken);
            await WriteListAsync(GroupsPath, groups, cancellationToken);
            _logger.LogInformation("Moved access into grants.json: {Count} entries.", grants.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FolderDefinition>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadListAsync<FolderDefinition>(FoldersPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateFoldersAsync(Action<List<FolderDefinition>> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var folders = await ReadListAsync<FolderDefinition>(FoldersPath, cancellationToken);
            update(folders);
            await WriteListAsync(FoldersPath, folders, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The stored logins (see Credential), passwords decrypted.
    public async Task<IReadOnlyList<Credential>> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCredentialsAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateCredentialsAsync(Action<List<Credential>> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var credentials = await ReadCredentialsAsync(cancellationToken);
            update(credentials);
            foreach (var credential in credentials)
            {
                credential.Password = _protector.Protect(credential.Password);
                credential.PrivateKey = _protector.Protect(credential.PrivateKey);
            }

            await WriteListAsync(CredentialsPath, credentials, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<Credential>> ReadCredentialsAsync(CancellationToken cancellationToken)
    {
        var credentials = await ReadListAsync<Credential>(CredentialsPath, cancellationToken);
        foreach (var credential in credentials)
        {
            credential.Password = _protector.Unprotect(credential.Password);
            credential.PrivateKey = _protector.Unprotect(credential.PrivateKey);
        }

        return credentials;
    }

    private string DefaultsPath => Path.Combine(DataDirectory, "defaults.json");

    // Not a list but a single set of defaults - hence its own read and write paths: a file holding a
    // one-element array would be a riddle for whoever opens it.
    public async Task<AppDefaults> GetDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(DefaultsPath))
            {
                return new AppDefaults();
            }

            await using var stream = File.OpenRead(DefaultsPath);
            return await JsonSerializer.DeserializeAsync<AppDefaults>(stream, JsonOptions, cancellationToken)
                ?? new AppDefaults();
        }
        catch (JsonException exception)
        {
            // A broken file must not hold up signing in: the built-in defaults apply instead, and it goes
            // into the log.
            _logger.LogWarning(exception, "defaults.json is not readable - built-in defaults apply.");
            return new AppDefaults();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateDefaultsAsync(Action<AppDefaults> update, CancellationToken cancellationToken = default)
    {
        var current = await GetDefaultsAsync(cancellationToken);
        update(current);
        current.UpdatedAt = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var tempPath = DefaultsPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, current, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, DefaultsPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MatgateUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadListAsync<MatgateUser>(UsersPath, cancellationToken);
            foreach (var user in users)
            {
                // The second factor's secret is as good as a password: whoever holds it can compute every code.
                // It is therefore stored encrypted in the file, like the connections' passwords.
                user.TotpSecret = _protector.Unprotect(user.TotpSecret);
            }

            // What reaches each of them - given to them, or to a group they are in - for every check of who may
            // use what (AccessResolver). Writing users (UpdateUsersAsync) reads them on its own and never carries this.
            var groups = await ReadListAsync<UserGroup>(GroupsPath, cancellationToken);
            var grants = await ReadListAsync<AccessGrant>(GrantsPath, cancellationToken);
            var folders = SharedFolderIndex(await ReadListAsync<FolderDefinition>(FoldersPath, cancellationToken));
            foreach (var user in users)
            {
                var memberOf = groups.Where(group => group.MemberIds.Contains(user.Id)).ToList();
                user.GroupNames.AddRange(memberOf.Select(group => group.Name));
                var reaching = new List<ReachingGrant>();
                foreach (var grant in grants)
                {
                    if (grant.PrincipalKind == AccessPrincipalKind.User && grant.PrincipalId == user.Id)
                    {
                        reaching.Add(new ReachingGrant(grant, null));
                    }
                    else if (grant.PrincipalKind == AccessPrincipalKind.Group && memberOf.FirstOrDefault(group => group.Id == grant.PrincipalId) is { } group)
                    {
                        reaching.Add(new ReachingGrant(grant, group));
                    }
                }

                user.Access = new UserAccess(reaching, folders);
            }

            return users;
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
                server.PrivateKey = _protector.Unprotect(server.PrivateKey);
            }

            // A connection that uses stored credentials gets their login here, where every use of a
            // connection reads it - RDP, VNC, SSH, files, databases. What is written back (UpdateServersAsync)
            // is read on its own and never carries this.
            if (servers.Any(server => server.CredentialId is not null))
            {
                var credentials = (await ReadCredentialsAsync(cancellationToken)).ToDictionary(credential => credential.Id);
                foreach (var server in servers.Where(server => server.CredentialId is not null))
                {
                    if (credentials.TryGetValue(server.CredentialId!.Value, out var credential))
                    {
                        server.UserName = credential.UserName;
                        server.Password = credential.Password;
                        server.Domain = credential.Domain;
                        server.PrivateKey = credential.PrivateKey;
                        server.CredentialName = credential.Name;
                    }
                }
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
            foreach (var user in users)
            {
                user.TotpSecret = _protector.Unprotect(user.TotpSecret);
            }

            update(users);
            foreach (var user in users)
            {
                user.TotpSecret = _protector.Protect(user.TotpSecret);
            }

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
                server.PrivateKey = _protector.Unprotect(server.PrivateKey);
            }
            update(servers);
            foreach (var server in servers)
            {
                server.Password = _protector.Protect(server.Password);
                server.PrivateKey = _protector.Protect(server.PrivateKey);
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
                CanQuickConnect = true,
                FileShare = new FileSharePermissions { Global = true, Connection = true, Personal = true },
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
