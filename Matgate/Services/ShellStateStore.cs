using System.Collections.Concurrent;
using System.Text.Json;

namespace Matgate.Services;

// A user's open tabs as the shell last had them: which connections and pages, in which order, which one in
// front. A browser keeps that for itself; for a user whose sessions outlive the browser (Account ->
// Session) it is kept here as well, so a reload, a browser that forgot everything or another device starts
// with the same tabs - and each tab finds its session still running.
//
// The three values are what the shell stores under its own names, as text: this store neither reads nor
// changes them, it only hands them back. One small file per user.
public sealed class ShellStateStore
{
    public const int MaxBytes = 256 * 1024;

    private readonly string _folder;
    private readonly ConcurrentDictionary<Guid, ShellState?> _cache = new();
    private readonly object _gate = new();

    public ShellStateStore(JsonDataStore store)
    {
        _folder = Path.Combine(store.DataDirectory, "shell-state");
    }

    public sealed record ShellState(string? Workspace, string? Shell, string? Order);

    public ShellState? Get(Guid userId)
    {
        return _cache.GetOrAdd(userId, id =>
        {
            try
            {
                var path = PathOf(id);
                return File.Exists(path) ? JsonSerializer.Deserialize<ShellState>(File.ReadAllText(path)) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A file that cannot be read is no list of tabs: the browser's own applies.
                return null;
            }
        });
    }

    public void Set(Guid userId, ShellState state)
    {
        _cache[userId] = state;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                var path = PathOf(userId);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(state));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Kept in memory until the next restart; a browser that reloads still finds it.
            }
        }
    }

    public void Forget(Guid userId)
    {
        _cache[userId] = null;
        lock (_gate)
        {
            try
            {
                File.Delete(PathOf(userId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string PathOf(Guid userId) => Path.Combine(_folder, userId.ToString("N") + ".json");
}
