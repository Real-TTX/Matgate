namespace Matgate.Models;

// A group of users, for a company rather than a household: its members get the connections it is given - one by
// one, or a whole folder at a time (with its subfolders, and whatever is filed there later). Made and changed by
// administrators only.
public sealed class UserGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public List<Guid> MemberIds { get; set; } = [];

    public List<Guid> ServerIds { get; set; } = [];

    // Folder names ("Lab" or "Lab/Linux"): a connection filed in one of them, or below it, is the members'.
    public List<string> Folders { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// A folder that exists on its own, not only as the name its connections carry: one an administrator made for
// everyone (no owner) - which is there even while empty, for connections to be filed in - or one a user made
// for themselves.
public sealed class FolderDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string IconKey { get; set; } = "";

    public Guid? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool VisibleTo(MatgateUser user) => OwnerUserId is null || OwnerUserId == user.Id;
}
