using System.Text.Json.Serialization;

namespace Matgate.Models;

// How much someone may do with a connection. Each level includes the ones below it.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessLevel
{
    None = 0,

    // Open it, but change nothing: a remote desktop or a terminal only to watch, files and databases only to read.
    ReadOnly = 1,

    // Open it and work in it.
    Connect = 2,

    // Also change the connection itself (address, login, folder).
    Edit = 3,

    // Also decide who else may use it.
    Manage = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessTargetKind
{
    // Every shared connection - the top of the tree, above all folders.
    All,
    Folder,
    Server,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessPrincipalKind
{
    User,
    Group,
}

// One entry of "who may do what where": a user or a group gets a level on one connection, on a folder (and
// with it everything below it, unless a folder further down stops inheriting) or on all shared connections.
// There is no "deny": what someone may do is the highest level any entry that reaches them gives.
public sealed class AccessGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public AccessTargetKind TargetKind { get; set; }

    // The folder's or the connection's id; empty for All.
    public Guid TargetId { get; set; }

    public AccessPrincipalKind PrincipalKind { get; set; }

    public Guid PrincipalId { get; set; }

    public AccessLevel Level { get; set; } = AccessLevel.Connect;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Targets(AccessTargetKind kind, Guid id) => TargetKind == kind && (kind == AccessTargetKind.All || TargetId == id);
}
