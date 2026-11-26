using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matgate.Models;

namespace Matgate.Services;

// What an app gets as its library (GET /app/api/library): the remote desktops and terminals this user may work in -
// RDP, VNC and SSH with at least "Connect" - and their folders. Never a password or a key: the app keeps its own, and
// what is stored here stays here. A connection read-only for this user is left out, since an app that connects on its
// own could not keep it read-only.
public static class AppLibrary
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public sealed record LibraryUser(Guid Id, string UserName, string DisplayName);

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
        bool IsPrivate);

    public sealed record Library(string Revision, LibraryUser User, IReadOnlyList<LibraryFolder> Folders, IReadOnlyList<LibraryConnection> Connections);

    // The protocols an app opens on its own - the ones a relay can carry (one TCP connection to host and port).
    public static bool IsAppProtocol(ServerProtocol protocol) =>
        protocol is ServerProtocol.Rdp or ServerProtocol.Vnc or ServerProtocol.Ssh;

    // May this user open this connection from an app, through the relay too?
    public static bool CanUse(MatgateUser user, ServerEndpoint server) =>
        server.IsEnabled && IsAppProtocol(server.Protocol) && AccessResolver.Level(user, server) >= AccessLevel.Connect;

    public static Library Build(MatgateUser user, IEnumerable<ServerEndpoint> servers)
    {
        var connections = servers
            .Where(server => CanUse(user, server))
            .OrderBy(server => server.FolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(server => new LibraryConnection(
                server.Id,
                server.Name,
                server.Protocol.ToString().ToLowerInvariant(),
                server.Host,
                server.Port,
                server.UserName,
                server.Protocol == ServerProtocol.Rdp ? server.Domain : "",
                ServerFolders.CleanPath(server.FolderName),
                server.Notes,
                AccessResolver.Level(user, server).ToString(),
                server.KeyboardLayout,
                server.IgnoreCertificate,
                !string.IsNullOrEmpty(server.Password) || !string.IsNullOrEmpty(server.PrivateKey),
                server.CredentialName,
                server.IsPrivate))
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

        var libraryUser = new LibraryUser(user.Id, user.UserName, string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName);
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { libraryUser, folders, connections }, JsonOptions))))[..32];
        return new Library(revision, libraryUser, folders, connections);
    }
}
