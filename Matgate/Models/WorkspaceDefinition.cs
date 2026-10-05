namespace Matgate.Models;

public sealed class WorkspaceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string RootPath { get; set; } = "";

    // Does this share point at a place that already exists? Then its id is here and RootPath is that
    // place's folder - not one created for the share. The difference matters: a share with its own
    // folder creates a "files" subdirectory inside it, a share on an existing place takes that folder
    // as it is. Empty means: own folder.
    public Guid? AreaId { get; set; }

    public bool SharesExistingPlace => AreaId.HasValue;

    public string AccessPasswordHash { get; set; } = "";

    public bool AllowUploads { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? PublicAccessExpiresAt { get; set; }

    public Guid? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPrivate => OwnerUserId.HasValue;
}
