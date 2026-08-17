using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matgate.Services;

// Thin HTTP client for the optional browser-farm sidecar's control API (acquire/release/status).
// If no BaseUrl is configured the farm is considered absent and IsConfigured is false.
public sealed class BrowserFarmClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _baseUrl;
    private readonly string _tokenEnv;
    private readonly string _tokenFile;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public BrowserFarmClient(IConfiguration configuration)
    {
        _baseUrl = (configuration["BrowserFarm:BaseUrl"] ?? "").Trim().TrimEnd('/');
        VncHost = (configuration["BrowserFarm:VncHost"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(VncHost) && Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
        {
            VncHost = uri.Host;
        }

        _tokenEnv = Environment.GetEnvironmentVariable("MATGATE_BROWSER_FARM_TOKEN") ?? "";
        _tokenFile = Environment.GetEnvironmentVariable("MATGATE_BROWSER_FARM_TOKEN_FILE") ?? "";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl);

    // Hostname guacd should connect the VNC session to (the farm's service name on the docker network).
    public string VncHost { get; }

    private string ReadToken()
    {
        if (!string.IsNullOrWhiteSpace(_tokenEnv))
        {
            return _tokenEnv.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_tokenFile))
        {
            try
            {
                return File.ReadAllText(_tokenFile).Trim();
            }
            catch
            {
                return "";
            }
        }

        return "";
    }

    private HttpRequestMessage Build(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        request.Headers.TryAddWithoutValidation("X-Farm-Token", ReadToken());
        if (body is not null)
        {
            // StringContent sets Content-Length; the farm's stdlib http.server reads the body by
            // Content-Length only (it does not decode chunked transfer-encoding).
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        return request;
    }

    public async Task<FarmAcquireResult?> AcquireAsync(string url, string browser, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            using var response = await _http.SendAsync(
                Build(HttpMethod.Post, "/acquire", new { url, browser }), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[browser-farm] acquire failed: HTTP {(int)response.StatusCode} {detail}");
                return null;
            }

            return await response.Content.ReadFromJsonAsync<FarmAcquireResult>(JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[browser-farm] acquire threw: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    public async Task ReleaseAsync(int slot, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            using var response = await _http.SendAsync(
                Build(HttpMethod.Post, "/release", new { slot }), cancellationToken);
        }
        catch
        {
            // Best effort - the farm's own max-age reaper is the backstop.
        }
    }

    public async Task<FarmStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            using var response = await _http.SendAsync(
                Build(HttpMethod.Get, "/status"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<FarmStatus>(JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class FarmAcquireResult
{
    public int Slot { get; set; }
    public int Port { get; set; }
    public string Browser { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class FarmStatus
{
    public int PoolSize { get; set; }
    public int VncBasePort { get; set; }
    public int Busy { get; set; }
    public int Free { get; set; }
    public List<FarmSlotInfo> Slots { get; set; } = [];
}

public sealed class FarmSlotInfo
{
    public int Slot { get; set; }
    public int Port { get; set; }
    public bool Busy { get; set; }
    public string Browser { get; set; } = "";
    public string Url { get; set; } = "";
    [JsonPropertyName("ageSeconds")]
    public int AgeSeconds { get; set; }
}
