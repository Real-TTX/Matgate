namespace Matgate.Models;

public sealed class WorkspaceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string RootPath { get; set; } = "";

    // Zeigt die Freigabe auf eine Ablage, die es schon gibt? Dann steht hier deren Kennung, und
    // RootPath ist deren Ordner - nicht ein eigens angelegter. Der Unterschied ist einer von zweien:
    // eine Freigabe mit eigenem Ordner legt darin ein Unterverzeichnis "files" an, eine Freigabe
    // auf eine vorhandene Ablage nimmt deren Ordner, wie er ist. Leer heißt: eigener Ordner.
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
