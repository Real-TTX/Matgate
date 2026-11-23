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

    // Before 0.41.0 a group's connections and folders were kept here. They are moved into entries (grants.json)
    // once, then cleared; what a group may do is now there, with a level.
    public List<Guid> ServerIds { get; set; } = [];

    public List<string> Folders { get; set; } = [];

    // Groups of a sign-in service this group stands for, as "<provider>:<value>" ("matidentity:Family",
    // "entra:3f2a…" or the name Entra sends). Whoever signs in through that service is put into this group when
    // the service names one of them, and taken out again when it no longer does - so with any of these set, the
    // service decides the membership of its users; local users stay as they are added here.
    public List<string> ExternalGroups { get; set; } = [];

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

    // Nothing given above this folder (on a parent folder or on all connections) reaches what lies in it - only
    // what is given on this folder itself and below.
    public bool InheritanceBroken { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool VisibleTo(MatgateUser user) => OwnerUserId is null || OwnerUserId == user.Id;
}
