namespace Matgate.Models;

public sealed class WorkspaceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string RootPath { get; set; } = "";

    public string AccessPasswordHash { get; set; } = "";

    public string SharedNoteFileName { get; set; } = "shared-note.md";

    public bool AllowUploads { get; set; } = true;

    public bool AllowTextExchange { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? PublicAccessExpiresAt { get; set; }

    public Guid? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPrivate => OwnerUserId.HasValue;
}
