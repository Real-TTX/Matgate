namespace Matgate.Models;

public sealed class MatgateUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserName { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public string GuacamolePassword { get; set; } = "";

    public bool IsAdmin { get; set; }

    public bool CanManageServers { get; set; }

    public bool CanCreateServers { get; set; }

    public bool ServerAccessAll { get; set; }

    public string PreferredLanguage { get; set; } = "en";

    public bool IsEnabled { get; set; } = true;

    public List<Guid> ServerAccess { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
