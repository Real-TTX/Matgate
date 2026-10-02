using System.Collections.Concurrent;
using Matgate.Models;

namespace Matgate.Services;

// The gateway's own file areas. They live outside the data directory, because this is user content:
// big, backed up differently, and often worth putting on a NAS. Every area is its own subtree, so a
// deployment can mount the whole thing, a single area, or nothing at all:
//
//   <files>/global/                 shared by everyone who is allowed in
//   <files>/connection/<serverId>/  belongs to one saved connection, shared by its users
//   <files>/user/<userId>/          private to one user, the same in every session
//   <files>/session/<sessionId>/    one remote session; also the view guacd is pointed at
//
// The session directory is what makes this work for RDP: guacd serves exactly one directory as the
// redirected drive, so the permitted areas are linked into that one directory. guacd follows the
// links and writes through them. A user can only reach what was linked - and nothing links back up,
// because guacd refuses ".." itself.
public sealed class FileShareService
{
    private sealed record Live(Guid UserId, DateTimeOffset LastSeen);

    // Which session views are known to be in use. Matgate is never told that a remote session ended -
    // the tunnel runs between the browser and Guacamole - so the browser reports in instead, exactly
    // like it does for browser-farm slots. Purely in memory: after a restart the entries come back
    // with the next report, which is why unknown views get a long grace instead of being swept.
    private readonly ConcurrentDictionary<string, Live> _live = new(StringComparer.Ordinal);

    // Generous enough to ride out a reload or a phone that was in the background for a moment.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    // For views nobody has reported on: left over from an earlier process, a crashed browser, or a
    // tab that was closed without the beacon arriving.
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(12);

    private readonly ILogger<FileShareService> _logger;

    public FileShareService(IConfiguration configuration, IHostEnvironment environment, ILogger<FileShareService> logger)
    {
        _logger = logger;

        var configured = Environment.GetEnvironmentVariable("MATGATE_FILES_DIR")
            ?? configuration["Matgate:FilesDirectory"];

        RootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "files")
            : configured);

        Directory.CreateDirectory(GlobalDirectory);
        Directory.CreateDirectory(Path.Combine(RootDirectory, "connection"));
        Directory.CreateDirectory(Path.Combine(RootDirectory, "user"));
        Directory.CreateDirectory(SessionRootDirectory);

        // The areas only work if guacd sees this very tree, which means it has to be a mount rather
        // than a folder that happens to exist in this container. Nothing else would tell us apart an
        // install whose compose file was updated from one that only pulled a new image - and getting
        // that wrong would hand guacd a path it cannot see, leaving a drive that looks fine and
        // silently swallows every transfer.
        AreasAvailable = IsMountPoint(RootDirectory);

        if (AreasAvailable)
        {
            logger.LogInformation("File areas ready under {Path}.", RootDirectory);
        }
        else
        {
            logger.LogInformation(
                "Nothing is mounted at {Path}, so remote sessions get a scratch folder only - no "
                + "Global, Connection or User area. Mount that folder into matgate and into guacd to "
                + "turn them on.",
                RootDirectory);
        }
    }

    public string RootDirectory { get; }

    // False when this tree is not shared with guacd; sessions then fall back to a scratch folder.
    public bool AreasAvailable { get; }

    public string GlobalDirectory => Path.Combine(RootDirectory, "global");

    public string SessionRootDirectory => Path.Combine(RootDirectory, "session");

    public string ConnectionDirectory(Guid serverId) =>
        Path.Combine(RootDirectory, "connection", serverId.ToString("N"));

    public string PersonalDirectory(Guid userId) =>
        Path.Combine(RootDirectory, "user", userId.ToString("N"));

    // --- The areas as file connections ------------------------------------------------------------
    //
    // The file manager already knows how to browse a connection; the areas just need to look like one.
    // Their ids are derived from what they are rather than stored, so they survive restarts and never
    // collide with a real server's id.

    public static Guid GlobalAreaId { get; } = AreaId("global");

    public static Guid PersonalAreaId(Guid userId) => AreaId("user:" + userId.ToString("N"));

    public static Guid ConnectionAreaId(Guid serverId) => AreaId("connection:" + serverId.ToString("N"));

    // Every area this user may open, as something the file manager understands. The caller passes the
    // connections the user already has access to - a connection's area is reachable exactly while its
    // connection is.
    // The names are the ones a session sees as folders on its redirected drive: Global, User and
    // Connection. Saying "Eigene Dateien" here and "User" there described the same folder twice, and
    // only one of the two names was on the drive - so they are the same word everywhere, in both
    // languages. A connection's area is written as a path below Connection, which also sorts the way
    // it reads.
    public IReadOnlyList<ServerEndpoint> ListAreas(MatgateUser user, IReadOnlyList<ServerEndpoint> accessibleServers)
    {
        var permissions = user.FileShare ?? new FileSharePermissions();
        var areas = new List<ServerEndpoint>();

        if (permissions.Global)
        {
            areas.Add(AreaEndpoint(user, GlobalAreaId, "Global", GlobalDirectory));
        }

        if (permissions.Personal)
        {
            areas.Add(AreaEndpoint(user, PersonalAreaId(user.Id), "User", PersonalDirectory(user.Id)));
        }

        if (permissions.Connection)
        {
            foreach (var server in accessibleServers.OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                areas.Add(AreaEndpoint(
                    user,
                    ConnectionAreaId(server.Id),
                    // A slash in the connection's own name would read as another level that is not
                    // there; it becomes a dash so the first slash stays the one Matgate put in.
                    "Connection/" + (server.Name ?? "").Replace('/', '-'),
                    ConnectionDirectory(server.Id)));
            }
        }

        return areas;
    }

    // Turns one of those ids back into something the file manager can open - but only if the user is
    // allowed that area. Returns null for anything else, so an id that is not an area simply falls
    // through to the normal server lookup.
    public ServerEndpoint? ResolveArea(MatgateUser user, Guid id, IReadOnlyList<ServerEndpoint> accessibleServers)
    {
        return ListAreas(user, accessibleServers).FirstOrDefault(area => area.Id == id);
    }

    private ServerEndpoint AreaEndpoint(MatgateUser user, Guid id, string name, string directory)
    {
        Directory.CreateDirectory(directory);
        OpenForGuacd(directory);

        return new ServerEndpoint
        {
            Id = id,
            Name = name,
            Protocol = ServerProtocol.Local,
            FileRootPath = directory,
            IsEnabled = true,
            // Owned by the caller, so the ordinary access check lets exactly them in.
            OwnerUserId = user.Id,
        };
    }

    private static Guid AreaId(string key)
    {
        // A name-based id, so the same area is always the same id without having to store it.
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("matgate.file-area." + key));
        return new Guid(hash.AsSpan(0, 16));
    }

    public string SessionDirectory(string sessionId) =>
        Path.Combine(SessionRootDirectory, SafeName(sessionId));

    // Builds the directory a remote session gets as its drive: a real "Session" folder for this
    // sitting, plus one link per area the user is allowed to use. Returns the path, or null when it
    // could not be prepared - the caller then simply connects without a drive.
    public SessionView? CreateSessionView(MatgateUser user, ServerEndpoint server, string sessionId, bool ephemeralServer)
    {
        if (!AreasAvailable)
        {
            return null;
        }

        try
        {
            var view = SessionDirectory(sessionId);
            Directory.CreateDirectory(view);
            OpenForGuacd(view);

            // Counts as live from the moment it exists, so the sweeper cannot take it away in the
            // window before the browser has reported in for the first time.
            Keepalive(sessionId, user.Id);

            // Always present: the scratch area of this one session.
            var scratch = Path.Combine(view, "Session");
            Directory.CreateDirectory(scratch);
            OpenForGuacd(scratch);

            // Ad-hoc connections get NOTHING but this scratch folder. Their target host is typed in
            // by the user at connect time, so linking a persistent area into one would let anyone
            // who may quick-connect mount the shared store into a machine of their choosing.
            if (ephemeralServer)
            {
                return new SessionView(view, false);
            }

            var permissions = user.FileShare ?? new FileSharePermissions();

            if (permissions.Global)
            {
                Link(view, "Global", GlobalDirectory);
            }

            if (permissions.Connection)
            {
                Link(view, "Connection", ConnectionDirectory(server.Id));
            }

            if (permissions.Personal)
            {
                Link(view, "User", PersonalDirectory(user.Id));
            }

            // Whether anything was actually linked decides more than the view itself: with the areas
            // in the drive, guacd's own Download folder has a better alternative and is turned off.
            return new SessionView(
                view,
                permissions.Global || permissions.Connection || permissions.Personal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not prepare the file areas for {Server}; the session starts without a drive.",
                server.Name);
            return null;
        }
    }

    // The browser reports that this session is still open. Also re-registers a view this process has
    // never seen, so a restart does not turn every live session into an orphan.
    public void Keepalive(string sessionId, Guid userId)
    {
        var key = SafeName(sessionId);
        _live.AddOrUpdate(
            key,
            _ => new Live(userId, DateTimeOffset.UtcNow),
            (_, existing) => existing.UserId == userId
                ? existing with { LastSeen = DateTimeOffset.UtcNow }
                : existing);
    }

    // Only the owner may end a session, and a view this process knows nothing about is never removed
    // on request - otherwise a guessed id would delete someone else's files.
    public bool Close(string sessionId, Guid userId)
    {
        var key = SafeName(sessionId);
        if (!_live.TryGetValue(key, out var live) || live.UserId != userId)
        {
            return false;
        }

        _live.TryRemove(key, out _);
        RemoveSessionView(key);
        return true;
    }

    public void RemoveSessionView(string sessionId)
    {
        try
        {
            var view = SessionDirectory(sessionId);
            if (Directory.Exists(view))
            {
                DeleteView(view);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the session file area {Session}.", sessionId);
        }
    }

    // Drops views nobody is using any more.
    //
    // Deliberately NOT based on how recently files were touched: a session can sit open all day with
    // all the work happening in a linked area, and deleting its view would leave guacd holding a path
    // that no longer exists - the drive stays visible in the session while every transfer silently
    // fails. So a view goes only when the browser stopped reporting it, or when nobody has reported
    // it for long enough that it cannot belong to a live tab any more.
    public int RemoveStaleSessionViews()
    {
        var removed = 0;

        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var directory in Directory.EnumerateDirectories(SessionRootDirectory))
            {
                try
                {
                    var name = Path.GetFileName(directory);
                    var stale = _live.TryGetValue(name, out var live)
                        ? now - live.LastSeen > IdleTimeout
                        : now - Directory.GetLastWriteTimeUtc(directory) > OrphanGrace;

                    if (!stale)
                    {
                        continue;
                    }

                    DeleteView(directory);
                    _live.TryRemove(name, out _);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not remove the stale session file area {Path}.", directory);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not sweep the session file areas.");
        }

        return removed;
    }

    // One area failing must not cost the session its whole drive, so this never throws: the area is
    // simply missing from the view, which is the same thing as "not permitted" and therefore safe.
    private void Link(string view, string name, string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            OpenForGuacd(target);

            // Clear whatever sits here first. Directory.Exists() FOLLOWS links, so a dangling one
            // would report as absent and the create below would then throw - and an entry that was
            // left behind must never be mistaken for a granted permission.
            RemoveLink(Path.Combine(view, name));

            // Relative, so it resolves to the same place in every container that mounts this tree.
            // A symlink stores the literal target string and guacd resolves it in its own namespace.
            var relative = Path.GetRelativePath(view, target).Replace('\\', '/');
            Directory.CreateSymbolicLink(Path.Combine(view, name), relative);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add the {Area} area to a session; it stays hidden.", name);
        }
    }

    // The areas that are linked into a view. Named explicitly so removal can unlink them by hand.
    private static readonly string[] LinkedAreas = ["Global", "Connection", "User"];

    // Removing a view must never reach through a link into the shared areas. Current .NET unlinks
    // rather than descending, but being wrong here would wipe everyone's files at once - so the
    // links are taken out by hand first instead of relying on that guarantee.
    private void DeleteView(string view)
    {
        foreach (var name in LinkedAreas)
        {
            try
            {
                RemoveLink(Path.Combine(view, name));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not unlink {Area} before removing a session view.", name);
            }
        }

        Directory.Delete(view, recursive: true);
    }

    private static void RemoveLink(string link)
    {
        // LinkTarget reads the link itself without following it, and is null when there is no entry
        // at all - unlike Exists(), which follows and therefore reports a dangling link as absent,
        // and unlike ResolveLinkTarget(), which throws when nothing is there.
        var entry = new FileInfo(link);
        if (entry.LinkTarget is not null || entry.Exists)
        {
            // A link is a file entry even when it points at a directory, so this removes the link
            // itself and never touches what it points at.
            entry.Delete();
        }
        else if (Directory.Exists(link))
        {
            Directory.Delete(link, recursive: true);
        }
    }

    // guacd runs as its own non-root user while Matgate runs as root, so anything guacd has to write
    // in must be opened up first. Plain 0755 would leave every area read-only for the session.
    private static void OpenForGuacd(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
    }

    // True when the path is its own mount, i.e. a volume the compose file put there rather than an
    // ordinary folder inside this container. Linux containers only - which is where guacd lives.
    private static bool IsMountPoint(string path)
    {
        try
        {
            if (!Directory.Exists(path) || !File.Exists("/proc/self/mountinfo"))
            {
                return false;
            }

            var normalized = path.TrimEnd('/');
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                // Fields are space separated; the 5th is the mount point inside this container.
                var fields = line.Split(' ');
                if (fields.Length > 4 && fields[4] == normalized)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    // Session ids are generated by Matgate, but this path ends up in a protocol parameter - so it is
    // pinned to characters that cannot walk out of the directory.
    private static string SafeName(string sessionId)
    {
        var cleaned = new string((sessionId ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-').ToArray());
        return cleaned.Length == 0 ? Guid.NewGuid().ToString("N") : cleaned;
    }
}

// Removes session views nobody reports on any more. Delay-first, so a restart does not sweep live
// sessions before their browsers have had a chance to report in again.
public sealed class FileShareReaper(FileShareService fileShares) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                fileShares.RemoveStaleSessionViews();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep the reaper alive across transient errors.
            }
        }
    }
}

// The directory a session is pointed at, and whether any of the gateway's own areas ended up in it.
public sealed record SessionView(string Path, bool HasAreas);
