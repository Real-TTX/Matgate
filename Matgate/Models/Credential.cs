namespace Matgate.Models;

// A login kept once and used by as many connections as point at it: change the password here and every one of
// them has the new one. Personal (OwnerUserId set: only that user sees and uses it) or shared (no owner: made by
// an administrator or a server manager, for everyone's connections). The password is stored encrypted, as the
// connections' own are.
public sealed class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string UserName { get; set; } = "";

    public string Password { get; set; } = "";

    public string Domain { get; set; } = "";

    public string Notes { get; set; } = "";

    public Guid? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsShared => OwnerUserId is null;

    // Who may pick it for a connection, and see that it exists: its owner, or everyone when it is shared.
    public bool UsableBy(MatgateUser user) => OwnerUserId is null || OwnerUserId == user.Id;

    // Who may change or delete it: its owner; a shared one, whoever manages the shared connections.
    public bool EditableBy(MatgateUser user) =>
        OwnerUserId is null ? user.IsAdmin || user.CanManageServers : OwnerUserId == user.Id;
}
