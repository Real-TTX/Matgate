namespace Matgate.Models;

public sealed record WorkspaceActivityEntry(
    DateTimeOffset Timestamp,
    string Actor,
    string Mode,
    string Action,
    string Path,
    string Details,
    string SessionId);
