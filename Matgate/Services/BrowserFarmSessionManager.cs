using System.Collections.Concurrent;
using System.Text.Json;
using Matgate.Models;

namespace Matgate.Services;

// Tracks live browser-farm sessions Matgate opened, releases them (explicit close, keepalive timeout,
// or startup reconciliation) and keeps a bounded, persisted history for the admin UI.
public sealed class BrowserFarmSessionManager
{
    private readonly BrowserFarmClient _farm;
    private readonly ILogger<BrowserFarmSessionManager> _logger;
    private readonly ConcurrentDictionary<Guid, BrowserFarmSession> _active = new();
    private readonly object _historyGate = new();
    private readonly List<BrowserFarmHistoryEntry> _history = [];
    private readonly string _historyPath;
    private const int MaxHistory = 300;

    // A session with no keepalive for this long is assumed abandoned (tab closed/crashed) and released.
    private static readonly TimeSpan KeepaliveTimeout = TimeSpan.FromSeconds(120);

    public BrowserFarmSessionManager(BrowserFarmClient farm, ILogger<BrowserFarmSessionManager> logger)
    {
        _farm = farm;
        _logger = logger;
        var dataDir = Environment.GetEnvironmentVariable("MATGATE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = "data";
        }

        _historyPath = Path.Combine(dataDir, "browser-farm-history.json");
        LoadHistory();
    }

    public bool IsConfigured => _farm.IsConfigured;

    public IReadOnlyList<BrowserFarmSession> ActiveSessions =>
        _active.Values.OrderByDescending(session => session.StartedAt).ToList();

    public IReadOnlyList<BrowserFarmHistoryEntry> History
    {
        get
        {
            lock (_historyGate)
            {
                return _history.OrderByDescending(entry => entry.EndedAt).ToList();
            }
        }
    }

    public Task<FarmStatus?> GetFarmStatusAsync(CancellationToken cancellationToken) =>
        _farm.GetStatusAsync(cancellationToken);

    // Acquire a farm slot for a website server; returns the session (with the VNC port) or null if the
    // farm is absent / the pool is exhausted.
    public async Task<BrowserFarmSession?> OpenAsync(MatgateUser user, ServerEndpoint server, CancellationToken cancellationToken)
    {
        if (!_farm.IsConfigured)
        {
            return null;
        }

        var browser = server.WebsiteRenderMode == WebsiteRenderMode.FirefoxVnc ? "firefox" : "chromium";
        var url = string.IsNullOrWhiteSpace(server.WebsiteUrl) ? server.Host : server.WebsiteUrl;
        var acquired = await _farm.AcquireAsync(url, browser, cancellationToken);
        if (acquired is null)
        {
            return null;
        }

        var session = new BrowserFarmSession
        {
            Id = Guid.NewGuid(),
            Slot = acquired.Slot,
            Port = acquired.Port,
            Browser = browser,
            Url = url,
            ServerId = server.Id,
            ServerName = server.Name,
            UserId = user.Id,
            UserName = user.UserName,
            StartedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _active[session.Id] = session;
        return session;
    }

    // The ephemeral VNC endpoint Matgate launches guacd against for a farm session.
    public ServerEndpoint BuildVncEndpoint(BrowserFarmSession session, ServerEndpoint source)
    {
        return new ServerEndpoint
        {
            Id = session.Id,
            Name = source.Name,
            Protocol = ServerProtocol.Vnc,
            Host = _farm.VncHost,
            Port = session.Port,
            UserName = "",
            Password = "",
            IconKey = source.IconKey,
            IsEnabled = true,
        };
    }

    public Guid? OwnerOf(Guid sessionId) =>
        _active.TryGetValue(sessionId, out var session) ? session.UserId : null;

    public bool Keepalive(Guid sessionId)
    {
        if (_active.TryGetValue(sessionId, out var session))
        {
            session.LastSeenAt = DateTimeOffset.UtcNow;
            return true;
        }

        return false;
    }

    public Task CloseAsync(Guid sessionId, string reason, CancellationToken cancellationToken) =>
        ReleaseInternalAsync(sessionId, reason, cancellationToken);

    // Called by an admin who force-releases a session from the Browser tab.
    public Task ForceReleaseAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ReleaseInternalAsync(sessionId, "released by admin", cancellationToken);

    public async Task ReapAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - KeepaliveTimeout;
        foreach (var session in _active.Values.Where(session => session.LastSeenAt < cutoff).ToList())
        {
            await ReleaseInternalAsync(session.Id, "timeout", cancellationToken);
        }
    }

    private async Task ReleaseInternalAsync(Guid sessionId, string reason, CancellationToken cancellationToken)
    {
        if (!_active.TryRemove(sessionId, out var session))
        {
            return;
        }

        await _farm.ReleaseAsync(session.Slot, cancellationToken);
        AppendHistory(new BrowserFarmHistoryEntry
        {
            Id = session.Id,
            Browser = session.Browser,
            Url = session.Url,
            ServerName = session.ServerName,
            UserName = session.UserName,
            StartedAt = session.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            Reason = reason,
        });
    }

    private void AppendHistory(BrowserFarmHistoryEntry entry)
    {
        lock (_historyGate)
        {
            _history.Add(entry);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveRange(0, _history.Count - MaxHistory);
            }

            SaveHistoryLocked();
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return;
            }

            var entries = JsonSerializer.Deserialize<List<BrowserFarmHistoryEntry>>(File.ReadAllText(_historyPath));
            if (entries is not null)
            {
                _history.AddRange(entries.TakeLast(MaxHistory));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load browser-farm history from {Path}.", _historyPath);
        }
    }

    private void SaveHistoryLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_historyPath, JsonSerializer.Serialize(_history));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist browser-farm history to {Path}.", _historyPath);
        }
    }
}

// Snapshot passed to the admin "Browser" view.
public sealed record BrowserAdminData(
    FarmStatus? Status,
    IReadOnlyList<BrowserFarmSession> Active,
    IReadOnlyList<BrowserFarmHistoryEntry> History);

public sealed class BrowserFarmSession
{
    public Guid Id { get; set; }
    public int Slot { get; set; }
    public int Port { get; set; }
    public string Browser { get; set; } = "";
    public string Url { get; set; } = "";
    public Guid ServerId { get; set; }
    public string ServerName { get; set; } = "";
    public Guid UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class BrowserFarmHistoryEntry
{
    public Guid Id { get; set; }
    public string Browser { get; set; } = "";
    public string Url { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string UserName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public string Reason { get; set; } = "";
}

// Periodically releases abandoned sessions (keepalive timeout).
public sealed class BrowserFarmReaper(BrowserFarmSessionManager manager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!manager.IsConfigured)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                await manager.ReapAsync(stoppingToken);
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
