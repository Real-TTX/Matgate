using System.Collections.Concurrent;
using Matgate.Models;

namespace Matgate.Services;

// Holds short-lived, in-memory "Quick connect" endpoints. They are NEVER persisted: the user enters
// host + credentials, connects immediately, and the endpoint lives only in memory (this process),
// owned by its creator, until it expires. This lets ad-hoc connections reuse every id-based flow
// (Guacamole launch, file manager, website proxy) without writing credentials to disk.
public sealed class EphemeralServerStore
{
    private sealed record Entry(Guid UserId, ServerEndpoint Server, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);
    private const int MaxPerUser = 25;

    // Registers an ad-hoc endpoint for the user and returns it (with a fresh id + owner set).
    public ServerEndpoint Register(MatgateUser user, ServerEndpoint server)
    {
        Prune();

        server.Id = Guid.NewGuid();
        server.OwnerUserId = user.Id;
        server.IsEnabled = true;

        // Keep memory bounded: drop this user's oldest entries beyond the cap before adding a new one.
        var mine = _entries.Values
            .Where(entry => entry.UserId == user.Id)
            .OrderBy(entry => entry.ExpiresAt)
            .ToList();
        for (var i = 0; i <= mine.Count - MaxPerUser; i++)
        {
            _entries.TryRemove(mine[i].Server.Id, out _);
        }

        _entries[server.Id] = new Entry(user.Id, server, DateTimeOffset.UtcNow.Add(Ttl));
        return server;
    }

    // Returns the endpoint only if it exists, belongs to this user, and has not expired.
    public ServerEndpoint? TryResolve(Guid id, Guid userId)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        if (entry.UserId != userId || entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(id, out _);
            return null;
        }

        return entry.Server;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(kvp.Key, out _);
            }
        }
    }
}
