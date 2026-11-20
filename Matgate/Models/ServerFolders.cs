namespace Matgate.Models;

// A folder of connections as the pages show it.
public sealed record ServerFolder(string Name, string IconKey, IReadOnlyList<ServerEndpoint> Servers);

// Folders are not stored on their own: a folder is the name the connections in it carry, and it exists as
// long as one of them does. What belongs to the folder rather than to a connection - its name, its icon -
// is therefore kept the same on all of them; the functions here are the one place that says how.
public static class ServerFolders
{
    public const int MaxNameLength = 80;

    // A folder's name as it is stored: no line breaks, nothing around it, not endlessly long.
    public static string CleanName(string? value)
    {
        var name = new string((value ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength].Trim() : name;
    }

    // A folder's name that may name a subfolder: the parts between "/" cleaned, the empty ones dropped -
    // " Lab / / Linux " is "Lab/Linux".
    public static string CleanPath(string? value)
    {
        var parts = CleanName(value).Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return CleanName(string.Join("/", parts));
    }

    public static bool Same(string? left, string? right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    // The folders among these connections, by name. The icon is the first one any of them carries.
    public static List<ServerFolder> List(IEnumerable<ServerEndpoint> servers) =>
    [
        .. servers
            .Where(server => !string.IsNullOrWhiteSpace(server.FolderName))
            .GroupBy(server => server.FolderName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ServerFolder(
                group.First().FolderName.Trim(),
                group.Select(server => ServerEndpoint.NormalizeIconKey(server.FolderIconKey)).FirstOrDefault(key => key.Length > 0) ?? "",
                [.. group.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)])),
    ];

    public static List<string> Names(IEnumerable<ServerEndpoint> servers) => [.. List(servers).Select(folder => folder.Name)];

    // The icon a connection carries for its folder: the one the others in that folder have; alone in it, the
    // one it had there itself (own); and none without a folder.
    public static string IconFor(IEnumerable<ServerEndpoint> servers, Guid self, string? folderName, string? own)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "";
        }

        var others = servers
            .Where(server => server.Id != self && Same(server.FolderName, folderName))
            .Select(server => ServerEndpoint.NormalizeIconKey(server.FolderIconKey))
            .FirstOrDefault(key => key.Length > 0);
        return others ?? ServerEndpoint.NormalizeIconKey(own);
    }
}
