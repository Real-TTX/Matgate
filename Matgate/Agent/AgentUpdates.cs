using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Agent;

// The newest agent and the way it gets to the machines: Matgate reads the release (matgate-agent.json:
// version, sha256, size), keeps the exe once it is fetched and checked, and sends it to an agent over the
// connection that agent already trusts. The agent checks it again before swapping.
public sealed class AgentUpdates(IHttpClientFactory http, ILogger<AgentUpdates> logger)
{
    // How long the release's manifest is believed; a test stack sets 0 to see its feed change at once.
    private static readonly TimeSpan ManifestLifetime = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("MATGATE_AGENT_UPDATE_CACHE_SECONDS"), out var seconds) ? seconds : 1800);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentRelease? _latest;
    private DateTimeOffset _checkedAt;

    public sealed record AgentRelease(string Version, string Sha256, long Size);

    // Where the releases are - GitHub's rolling release unless configured otherwise (tests use their own).
    public static string Feed => (Environment.GetEnvironmentVariable("MATGATE_AGENT_UPDATE_URL") is { Length: > 0 } url
        ? url
        : "https://github.com/Real-TTX/Matgate/releases/download/agent-latest/").TrimEnd('/') + "/";

    private static string CacheFolder => Path.Combine(Environment.GetEnvironmentVariable("MATGATE_DATA_DIR") is { Length: > 0 } data ? data : Path.GetTempPath(), "agent-updates");

    public async Task<AgentRelease?> LatestAsync(CancellationToken cancellationToken)
    {
        if (_latest is not null && DateTimeOffset.UtcNow - _checkedAt < ManifestLifetime)
        {
            return _latest;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_latest is not null && DateTimeOffset.UtcNow - _checkedAt < ManifestLifetime)
            {
                return _latest;
            }

            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var manifest = await client.GetFromJsonAsync<JsonObject>(Feed + "matgate-agent.json", cancellationToken);
            var version = manifest?["version"]?.GetValue<string>() ?? "";
            var sha256 = (manifest?["sha256"]?.GetValue<string>() ?? "").ToUpperInvariant();
            var size = manifest?["size"]?.GetValue<long>() ?? 0;
            if (System.Version.TryParse(version, out _) && sha256.Length == 64 && size > 0)
            {
                _latest = new AgentRelease(version, sha256, size);
            }

            _checkedAt = DateTimeOffset.UtcNow;
            return _latest;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogInformation("Agent release not readable: {Message}", exception.Message);
            _checkedAt = DateTimeOffset.UtcNow;
            return _latest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool IsNewer(AgentRelease? release, string running)
    {
        return release is not null
            && System.Version.TryParse(release.Version, out var available)
            && System.Version.TryParse(running, out var current)
            && Normalize(available) > Normalize(current);
    }

    // 0.3.2 and 0.3.2.0 are the same version.
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private async Task<byte[]> PackageAsync(AgentRelease release, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheFolder);
        var path = Path.Combine(CacheFolder, $"matgate-agent-{release.Version}.exe");
        if (File.Exists(path))
        {
            var cached = await File.ReadAllBytesAsync(path, cancellationToken);
            if (Convert.ToHexString(SHA256.HashData(cached)) == release.Sha256)
            {
                return cached;
            }
        }

        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        var bytes = await client.GetByteArrayAsync(Feed + "matgate-agent.exe", cancellationToken);
        if (bytes.Length != release.Size || Convert.ToHexString(SHA256.HashData(bytes)) != release.Sha256)
        {
            // The rolling release may have moved on between manifest and download; read it again next time.
            _latest = null;
            throw new InvalidOperationException("The downloaded agent does not match its release (size or SHA-256) - not sent.");
        }

        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        foreach (var old in Directory.GetFiles(CacheFolder, "matgate-agent-*.exe").Where(file => file != path))
        {
            File.Delete(old);
        }

        return bytes;
    }

    // Sends the release to the agent and tells it to switch. The connection drops when it restarts.
    public async Task PushAsync(AgentLink link, AgentRelease release, CancellationToken cancellationToken)
    {
        var bytes = await PackageAsync(release, cancellationToken);
        logger.LogInformation("Updating agent {Name} from {Current} to {Version}.", link.Hello.Name, link.Hello.Version, release.Version);
        await link.RequestAsync("update.begin", new JsonObject { ["version"] = release.Version, ["sha256"] = release.Sha256, ["size"] = release.Size }, cancellationToken: cancellationToken);
        for (var offset = 0; offset < bytes.Length; offset += AgentWire.ChunkSize)
        {
            var length = Math.Min(AgentWire.ChunkSize, bytes.Length - offset);
            await link.RequestAsync("update.write", null, bytes.AsMemory(offset, length), cancellationToken);
        }

        await link.RequestAsync("update.apply", cancellationToken: cancellationToken);
        link.Dispose();
    }
}

// Every hour: every agent that allows it and has nothing open gets the newest version.
public sealed class AgentAutoUpdater(AgentUpdates updates, AgentClientPool agents, JsonDataStore store, ILogger<AgentAutoUpdater> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RoundAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RoundAsync(CancellationToken cancellationToken)
    {
        var latest = await updates.LatestAsync(cancellationToken);
        if (latest is null)
        {
            return;
        }

        foreach (var server in (await store.GetServersAsync(cancellationToken)).Where(server => server.Protocol == ServerProtocol.Agent && server.IsEnabled))
        {
            try
            {
                var link = await agents.GetAsync(server, cancellationToken);
                var hello = link.Hello;
                if (!hello.AutoUpdate || !hello.CanUpdate || !AgentUpdates.IsNewer(latest, hello.Version))
                {
                    continue;
                }

                // Never in the middle of someone's work: an update restarts the agent and ends its terminals.
                var sessions = await link.RequestAsync("pty.list", cancellationToken: cancellationToken);
                if (sessions.Data?["sessions"] is JsonArray { Count: > 0 })
                {
                    continue;
                }

                await updates.PushAsync(link, latest, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or HttpRequestException)
            {
                logger.LogInformation("Agent {Name} not updated: {Message}", server.Name, exception.Message);
            }
        }
    }
}
