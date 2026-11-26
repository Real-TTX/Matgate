using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matgate.Models;

namespace Matgate.Services;

// What an app gets as its library (GET /app/api/library): the connections this user may work in with the app's own
// engine - RDP, VNC, SSH, files (SFTP, FTP, SMB, WebDAV), websites and databases (SQL Server, MySQL, PostgreSQL) with at
// least "Connect" - and their folders, plus the remote desktops (RDP, VNC) they may only
// watch: those open as a session on Matgate (GET /app/api/screen), which keeps them read-only. Never a password or a
// key: the app keeps its own, and what is stored here stays here.
//
// Writing back (POST/PUT/DELETE /app/api/connections): the same rules as the connection form - create with "Create
// connections" (private) or "Manage connections" (shared), change with "Edit", delete with "Manage" - and only what
// a connection IS (name, address, user, folder, notes, keyboard): never a password, a key, a tunnel or a permission.
public static class AppLibrary
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public sealed record LibraryUser(Guid Id, string UserName, string DisplayName, bool CanCreatePrivate, bool CanCreateShared);

    public sealed record LibraryFolder(string Path, string Name, string Parent);

    public sealed record LibraryConnection(
        Guid Id,
        string Name,
        string Protocol,
        string Host,
        int Port,
        string UserName,
        string Domain,
        string Folder,
        string Notes,
        string Level,
        string KeyboardLayout,
        bool IgnoreCertificate,
        bool HasStoredLogin,
        string CredentialName,
        bool IsPrivate,
        bool CanDelete,
        string Url = "",
        string RootPath = "",
        string Database = "");

    public sealed record Library(string Revision, LibraryUser User, IReadOnlyList<LibraryFolder> Folders, IReadOnlyList<LibraryConnection> Connections);

    // The protocols an app opens on its own, with its own engine and login.
    public static bool IsAppProtocol(ServerProtocol protocol) =>
        protocol is ServerProtocol.Rdp or ServerProtocol.Vnc or ServerProtocol.Ssh
            or ServerProtocol.Sftp or ServerProtocol.Ftp or ServerProtocol.Smb or ServerProtocol.WebDav
            or ServerProtocol.Website
            or ServerProtocol.MsSql or ServerProtocol.MySql or ServerProtocol.Postgres;

    // The protocols the relay carries (one TCP connection to host and port): all of them but FTP, which opens further
    // connections for its data.
    public static bool IsRelayProtocol(ServerProtocol protocol) => IsAppProtocol(protocol) && protocol != ServerProtocol.Ftp;

    // Where the relay of a connection goes: host and port - for a website those of its address.
    public static (string Host, int Port) RelayTarget(ServerEndpoint server)
    {
        if (server.Protocol == ServerProtocol.Website && Uri.TryCreate(server.WebsiteUrl, UriKind.Absolute, out var url))
        {
            return (url.Host, url.Port);
        }

        return (server.Host, server.Port);
    }

    // The protocols Matgate runs a session of for an app (GET /app/api/screen).
    public static bool IsScreenProtocol(ServerProtocol protocol) =>
        protocol is ServerProtocol.Rdp or ServerProtocol.Vnc;

    // May this user open this connection from an app with the app's own engine - through the relay?
    public static bool CanUse(MatgateUser user, ServerEndpoint server) =>
        server.IsEnabled && IsAppProtocol(server.Protocol) && AccessResolver.Level(user, server) >= AccessLevel.Connect;

    // May this user see this connection as a session on Matgate? Watching ("read only") is enough: Matgate passes
    // no key and no pointer on then.
    public static bool CanWatch(MatgateUser user, ServerEndpoint server) =>
        server.IsEnabled && IsScreenProtocol(server.Protocol) && AccessResolver.Level(user, server) >= AccessLevel.ReadOnly;

    public static bool CanCreate(MatgateUser user, bool shared) =>
        shared ? user.IsAdmin || user.CanManageServers : user.IsAdmin || user.CanCreateServers;

    public static bool CanEdit(MatgateUser user, ServerEndpoint server) =>
        IsAppProtocol(server.Protocol) && AccessResolver.Level(user, server) >= AccessLevel.Edit;

    // As in the connection form: "Manage" on it, or a server manager for a shared one.
    public static bool CanDelete(MatgateUser user, ServerEndpoint server) =>
        IsAppProtocol(server.Protocol)
        && (AccessResolver.Level(user, server) >= AccessLevel.Manage || (server.OwnerUserId is null && user.CanManageServers));

    public static Library Build(MatgateUser user, IEnumerable<ServerEndpoint> servers)
    {
        var connections = servers
            .Where(server => CanUse(user, server) || CanWatch(user, server))
            .OrderBy(server => server.FolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(server => Item(user, server))
            .ToList();

        // Every folder on the way to a connection, so "Lab/Linux" brings "Lab" along.
        var folders = connections
            .SelectMany(connection => AccessResolver.PathAndParents(connection.Folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var cut = path.LastIndexOf('/');
                return new LibraryFolder(path, cut < 0 ? path : path[(cut + 1)..], cut < 0 ? "" : path[..cut]);
            })
            .ToList();

        var libraryUser = new LibraryUser(
            user.Id,
            user.UserName,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName,
            CanCreate(user, shared: false),
            CanCreate(user, shared: true));
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { libraryUser, folders, connections }, JsonOptions))))[..32];
        return new Library(revision, libraryUser, folders, connections);
    }

    public static LibraryConnection Item(MatgateUser user, ServerEndpoint server) => new(
        server.Id,
        server.Name,
        server.Protocol.ToString().ToLowerInvariant(),
        server.Host,
        server.Port,
        server.UserName,
        server.Protocol is ServerProtocol.Rdp or ServerProtocol.Smb ? server.Domain : "",
        ServerFolders.CleanPath(server.FolderName),
        server.Notes,
        AccessResolver.Level(user, server).ToString(),
        server.KeyboardLayout,
        server.IgnoreCertificate,
        !string.IsNullOrEmpty(server.Password) || !string.IsNullOrEmpty(server.PrivateKey),
        server.CredentialName,
        server.IsPrivate,
        CanDelete(user, server),
        server.Protocol == ServerProtocol.Website ? server.WebsiteUrl : "",
        server.Protocol is ServerProtocol.Sftp or ServerProtocol.Ftp or ServerProtocol.Smb or ServerProtocol.WebDav ? server.FileRootPath : "",
        server.Protocol is ServerProtocol.MsSql or ServerProtocol.MySql or ServerProtocol.Postgres ? server.DatabaseName : "");

    // ---- writing back --------------------------------------------------------------------------------------

    // What an app sends to create or change a connection. Null: as it is (a new one: the default).
    public sealed record ConnectionChange(
        string? Name,
        string? Protocol,
        string? Host,
        int? Port,
        string? UserName,
        string? Domain,
        string? Folder,
        string? Notes,
        string? KeyboardLayout,
        bool? IgnoreCertificate,
        bool? Shared,
        string? Url = null,
        string? RootPath = null,
        string? Database = null);

    // A field that is not right: its name and a code the app turns into its own words.
    public sealed record ChangeProblem(string Field, string Error);

    public const int MaxNameLength = 200;
    public const int MaxHostLength = 253;
    public const int MaxTextLength = 200;
    public const int MaxNotesLength = 4000;

    public static ServerProtocol? ParseProtocol(string? protocol) => (protocol ?? "").Trim().ToLowerInvariant() switch
    {
        "rdp" => ServerProtocol.Rdp,
        "vnc" => ServerProtocol.Vnc,
        "ssh" => ServerProtocol.Ssh,
        "sftp" => ServerProtocol.Sftp,
        "ftp" => ServerProtocol.Ftp,
        "smb" => ServerProtocol.Smb,
        "webdav" => ServerProtocol.WebDav,
        "website" => ServerProtocol.Website,
        "mssql" => ServerProtocol.MsSql,
        "mysql" => ServerProtocol.MySql,
        "postgres" => ServerProtocol.Postgres,
        _ => null,
    };

    public static int DefaultPort(ServerProtocol protocol) => protocol switch
    {
        ServerProtocol.Ssh or ServerProtocol.Sftp => 22,
        ServerProtocol.Vnc => 5900,
        ServerProtocol.Ftp => 21,
        ServerProtocol.Smb => 445,
        ServerProtocol.WebDav => 443,
        ServerProtocol.Website => 0,
        ServerProtocol.MsSql => 1433,
        ServerProtocol.MySql => 3306,
        ServerProtocol.Postgres => 5432,
        _ => 3389,
    };

    // What is wrong with a change - for a new connection (existing: null) or one that is there.
    public static ChangeProblem? Check(ConnectionChange change, ServerEndpoint? existing)
    {
        if (existing is null && ParseProtocol(change.Protocol) is null)
        {
            return new ChangeProblem("protocol", "protocol");
        }

        if (existing is not null && change.Protocol is not null && ParseProtocol(change.Protocol) != existing.Protocol)
        {
            return new ChangeProblem("protocol", "protocol_fixed");
        }

        var name = (change.Name ?? existing?.Name ?? "").Trim();
        if (name.Length == 0)
        {
            return new ChangeProblem("name", "required");
        }

        if (name.Length > MaxNameLength)
        {
            return new ChangeProblem("name", "too_long");
        }

        var protocol = existing?.Protocol ?? ParseProtocol(change.Protocol);
        if (protocol == ServerProtocol.Website)
        {
            var url = ServerEndpoint.CleanWebsiteUrl(change.Url, existing?.WebsiteUrl ?? "");
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            {
                return new ChangeProblem("url", url.Length == 0 && (change.Url ?? "").Trim().Length == 0 ? "required" : "invalid");
            }
        }

        if ((change.RootPath ?? "").Length > MaxTextLength || (change.Database ?? "").Length > MaxTextLength)
        {
            return new ChangeProblem(change.RootPath is { Length: > MaxTextLength } ? "rootPath" : "database", "too_long");
        }

        var host = (change.Host ?? existing?.Host ?? "").Trim();
        if (protocol == ServerProtocol.Website && host.Length == 0)
        {
            host = new Uri(ServerEndpoint.CleanWebsiteUrl(change.Url, existing?.WebsiteUrl ?? "")).Host;
        }

        if (host.Length == 0)
        {
            return new ChangeProblem("host", "required");
        }

        if (host.Length > MaxHostLength || host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return new ChangeProblem("host", "invalid");
        }

        if (change.Port is { } port && port is < 0 or > 65535)
        {
            return new ChangeProblem("port", "invalid");
        }

        if ((change.UserName ?? "").Length > MaxTextLength || (change.Domain ?? "").Length > MaxTextLength)
        {
            return new ChangeProblem("userName", "too_long");
        }

        if ((change.Folder ?? "").Length > MaxTextLength)
        {
            return new ChangeProblem("folder", "too_long");
        }

        return (change.Notes ?? "").Length > MaxNotesLength ? new ChangeProblem("notes", "too_long") : null;
    }

    // A new connection of this user (Check first; CanCreate decides whether it may be shared).
    public static ServerEndpoint Create(ConnectionChange change, MatgateUser user, DateTimeOffset now)
    {
        var protocol = ParseProtocol(change.Protocol) ?? ServerProtocol.Rdp;
        var server = new ServerEndpoint
        {
            Id = Guid.NewGuid(),
            Protocol = protocol,
            Port = DefaultPort(protocol),
            KeyboardLayout = ServerEndpoint.DefaultKeyboardLayout,
            IgnoreCertificate = true,
            IsEnabled = true,
            OwnerUserId = change.Shared == true ? null : user.Id,
            CreatedAt = now,
        };
        Apply(server, change, now);
        return server;
    }

    // Takes over what the change names; nothing else of the connection moves (password, key, tunnel, owner, ...).
    public static void Apply(ServerEndpoint server, ConnectionChange change, DateTimeOffset now)
    {
        if (change.Name is not null)
        {
            server.Name = change.Name.Trim();
        }

        if (change.Host is not null)
        {
            server.Host = change.Host.Trim();
        }

        if (change.Port is { } port)
        {
            server.Port = port == 0 ? DefaultPort(server.Protocol) : port;
        }

        // A connection with stored credentials takes its user from them (as in the connection form).
        if (server.CredentialId is null)
        {
            if (change.UserName is not null)
            {
                server.UserName = change.UserName.Trim();
            }

            if (change.Domain is not null)
            {
                server.Domain = server.Protocol is ServerProtocol.Rdp or ServerProtocol.Smb ? change.Domain.Trim() : "";
            }
        }

        if (server.Protocol == ServerProtocol.Website && change.Url is not null)
        {
            server.WebsiteUrl = ServerEndpoint.CleanWebsiteUrl(change.Url, server.WebsiteUrl);
            server.Port = 0;
            if (change.Host is null && Uri.TryCreate(server.WebsiteUrl, UriKind.Absolute, out var url))
            {
                server.Host = url.Host;
            }
        }

        if (server.Protocol is ServerProtocol.Sftp or ServerProtocol.Ftp or ServerProtocol.Smb && change.RootPath is not null)
        {
            server.FileRootPath = change.RootPath.Trim();
        }

        if (server.Protocol is ServerProtocol.MsSql or ServerProtocol.MySql or ServerProtocol.Postgres && change.Database is not null)
        {
            server.DatabaseName = change.Database.Trim();
        }

        if (change.Folder is not null)
        {
            server.FolderName = ServerFolders.CleanPath(change.Folder);
        }

        if (change.Notes is not null)
        {
            server.Notes = change.Notes.Trim();
        }

        if (change.KeyboardLayout is { } layout)
        {
            var cleaned = layout.Trim().ToLowerInvariant();
            if (cleaned.Length is > 0 and <= 64 && cleaned.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                server.KeyboardLayout = cleaned;
            }
        }

        if (change.IgnoreCertificate is { } ignore)
        {
            server.IgnoreCertificate = ignore;
        }

        server.UpdatedAt = now;
    }
}
