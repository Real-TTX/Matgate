namespace Matgate.Models;

public sealed class MatgateUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserName { get; set; } = "";

    // Optional contact/login address: sign-in accepts the username OR this email. Unique across
    // users when set; legacy users (created before the field existed) simply have it empty.
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public string GuacamolePassword { get; set; } = "";

    public bool IsAdmin { get; set; }

    public bool CanManageServers { get; set; }

    public bool CanCreateServers { get; set; }

    // Allowed to open ad-hoc "Quick connect" sessions (enter host + credentials, connect without
    // saving a connection). Admins are always allowed.
    public bool CanQuickConnect { get; set; }

    public bool ServerAccessAll { get; set; }

    public string PreferredLanguage { get; set; } = "en";

    public string PreferredTheme { get; set; } = "system";

    public bool RememberLoginByDefault { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public List<Guid> FavoriteServerIds { get; set; } = [];

    // Most-recently-opened connections (most recent first), updated whenever a connection launches.
    // Drives the "Recently used" section on the home page.
    public List<RecentConnectionEntry> RecentConnections { get; set; } = [];

    public List<Guid> ServerAccess { get; set; } = [];

    // Optional per-(user, file-server) restrictions. Absence of a rule = full access.
    public List<FileAccessRule> FileAccessRules { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// One entry in a user's recently-used connection history.
public sealed class RecentConnectionEntry
{
    public Guid ServerId { get; set; }

    public DateTimeOffset UsedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Restricts a user's access to a single file connection (SMB/FTP/SFTP): optionally read-only
// and/or confined to a subfolder (relative to the server's configured file root).
public sealed class FileAccessRule
{
    public Guid ServerId { get; set; }

    public bool ReadOnly { get; set; }

    public string SubPath { get; set; } = "";
}
