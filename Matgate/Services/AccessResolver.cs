using Matgate.Models;

namespace Matgate.Services;

// What reaches one user: the entries given to them and to their groups, and the shared folders (by path) the
// entries on folders are attached to. Filled in when users are read (JsonDataStore.GetUsersAsync); never stored.
public sealed class UserAccess
{
    public static readonly UserAccess Empty = new([], new Dictionary<string, FolderDefinition>());

    public UserAccess(IReadOnlyList<ReachingGrant> grants, IReadOnlyDictionary<string, FolderDefinition> folders)
    {
        Grants = grants;
        Folders = folders;
    }

    public IReadOnlyList<ReachingGrant> Grants { get; }

    // The shared folders by AccessResolver.FolderKey.
    public IReadOnlyDictionary<string, FolderDefinition> Folders { get; }
}

// An entry together with the group it came through (null: given to the user directly).
public sealed record ReachingGrant(AccessGrant Grant, UserGroup? Group);

public enum AccessReasonKind
{
    Administrator,
    Owner,
    ServerManager,
    Grant,
}

// One reason why someone may do something: "Connect, through the group IT, given on the folder Servers".
public sealed record AccessReason(
    AccessLevel Level,
    AccessReasonKind Kind,
    UserGroup? Group = null,
    AccessTargetKind Target = AccessTargetKind.All,
    string FolderPath = "",
    bool Inherited = false);

// The one place that says how far someone may go with a connection. Entries on the connection itself count,
// then those on its folder, on the folder above that and so on up to "all connections" - unless a folder on the
// way stops inheriting: then nothing above it reaches what lies in it. The highest level wins; there is no deny.
public static class AccessResolver
{
    public static string FolderKey(string? path) => ServerFolders.CleanPath(path).ToLowerInvariant();

    // "Lab/Linux/Web" -> "Lab/Linux/Web", "Lab/Linux", "Lab".
    public static IEnumerable<string> PathAndParents(string? path)
    {
        var parts = ServerFolders.CleanPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var count = parts.Length; count > 0; count--)
        {
            yield return string.Join("/", parts.Take(count));
        }
    }

    public static AccessLevel Level(MatgateUser user, ServerEndpoint server) => Highest(Reasons(user, server));

    public static bool IsReadOnly(MatgateUser user, ServerEndpoint server) => Level(user, server) == AccessLevel.ReadOnly;

    // Every reason this user has for this connection; empty when they may not use it at all.
    public static List<AccessReason> Reasons(MatgateUser user, ServerEndpoint server)
    {
        var reasons = new List<AccessReason>();
        if (user.IsAdmin)
        {
            reasons.Add(new AccessReason(AccessLevel.Manage, AccessReasonKind.Administrator));
        }

        if (server.OwnerUserId == user.Id)
        {
            reasons.Add(new AccessReason(AccessLevel.Manage, AccessReasonKind.Owner));
        }

        // Someone else's own connection: theirs alone (and the administrators').
        if (server.OwnerUserId is not null)
        {
            return reasons;
        }

        if (user.CanManageServers)
        {
            reasons.Add(new AccessReason(AccessLevel.Edit, AccessReasonKind.ServerManager));
        }

        var access = user.Access;
        foreach (var reaching in access.Grants.Where(reaching => reaching.Grant.Targets(AccessTargetKind.Server, server.Id)))
        {
            reasons.Add(new AccessReason(reaching.Grant.Level, AccessReasonKind.Grant, reaching.Group, AccessTargetKind.Server));
        }

        reasons.AddRange(FromFolders(access, server.FolderName, inheritedFromStart: true));
        return reasons;
    }

    // What reaches everything filed in this folder (the folder's own entries and what it inherits), for the
    // folder's page. Administrators and server managers are not part of it: they are listed apart.
    public static List<AccessReason> FolderReasons(MatgateUser user, string folderPath)
    {
        var reasons = new List<AccessReason>();
        if (user.IsAdmin)
        {
            reasons.Add(new AccessReason(AccessLevel.Manage, AccessReasonKind.Administrator));
        }

        if (user.CanManageServers)
        {
            reasons.Add(new AccessReason(AccessLevel.Edit, AccessReasonKind.ServerManager));
        }

        reasons.AddRange(FromFolders(user.Access, folderPath, inheritedFromStart: false));
        return reasons;
    }

    // The entries on "all connections" only.
    public static List<AccessReason> AllReasons(MatgateUser user)
    {
        var reasons = new List<AccessReason>();
        if (user.IsAdmin)
        {
            reasons.Add(new AccessReason(AccessLevel.Manage, AccessReasonKind.Administrator));
        }

        if (user.CanManageServers)
        {
            reasons.Add(new AccessReason(AccessLevel.Edit, AccessReasonKind.ServerManager));
        }

        reasons.AddRange(user.Access.Grants
            .Where(reaching => reaching.Grant.TargetKind == AccessTargetKind.All)
            .Select(reaching => new AccessReason(reaching.Grant.Level, AccessReasonKind.Grant, reaching.Group, AccessTargetKind.All)));
        return reasons;
    }

    // May this user decide who may use this folder (and what lies in it)?
    public static bool CanManageFolder(MatgateUser user, string folderPath) =>
        user.IsAdmin || Highest(FromFolders(user.Access, folderPath, inheritedFromStart: false)) >= AccessLevel.Manage;

    public static bool CanManageAll(MatgateUser user) => user.IsAdmin || Highest(AllReasons(user)) >= AccessLevel.Manage;

    public static AccessLevel Highest(IEnumerable<AccessReason> reasons) =>
        reasons.Select(reason => reason.Level).DefaultIfEmpty(AccessLevel.None).Max();

    private static IEnumerable<AccessReason> FromFolders(UserAccess access, string? folderPath, bool inheritedFromStart)
    {
        var first = true;
        foreach (var path in PathAndParents(folderPath))
        {
            var inherited = inheritedFromStart || !first;
            first = false;
            if (!access.Folders.TryGetValue(FolderKey(path), out var folder))
            {
                continue;
            }

            foreach (var reaching in access.Grants.Where(reaching => reaching.Grant.Targets(AccessTargetKind.Folder, folder.Id)))
            {
                yield return new AccessReason(reaching.Grant.Level, AccessReasonKind.Grant, reaching.Group, AccessTargetKind.Folder, folder.Name, inherited);
            }

            if (folder.InheritanceBroken)
            {
                yield break;
            }
        }

        foreach (var reaching in access.Grants.Where(reaching => reaching.Grant.TargetKind == AccessTargetKind.All))
        {
            yield return new AccessReason(reaching.Grant.Level, AccessReasonKind.Grant, reaching.Group, AccessTargetKind.All, "", true);
        }
    }
}
