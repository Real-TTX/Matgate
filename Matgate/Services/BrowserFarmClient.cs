using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matgate.Services;

// Thin HTTP client for the optional browser-farm sidecar's control API (acquire/release/status).
// If no BaseUrl is configured the farm is considered absent and IsConfigured is false.
// Where the farm is comes from Administration -> Browser farm (Use, at start and on saving); until that was
// saved once, from BrowserFarm__BaseUrl / BrowserFarm__VncHost / MATGATE_BROWSER_FARM_TOKEN[_FILE].
public sealed class BrowserFarmClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private volatile string _baseUrl;
    private volatile string _vncHost;
    private volatile string _token = "";
    private readonly string _tokenEnv;
    private readonly string _tokenFile;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public BrowserFarmClient(IConfiguration configuration)
    {
        EnvironmentBaseUrl = (configuration["BrowserFarm:BaseUrl"] ?? "").Trim().TrimEnd('/');
        EnvironmentVncHost = (configuration["BrowserFarm:VncHost"] ?? "").Trim();
        _baseUrl = EnvironmentBaseUrl;
        _vncHost = DeriveVncHost(EnvironmentBaseUrl, EnvironmentVncHost);
        _tokenEnv = Environment.GetEnvironmentVariable("MATGATE_BROWSER_FARM_TOKEN") ?? "";
        _tokenFile = Environment.GetEnvironmentVariable("MATGATE_BROWSER_FARM_TOKEN_FILE") ?? "";
    }

    public string EnvironmentBaseUrl { get; }

    public string EnvironmentVncHost { get; }

    public string BaseUrl => _baseUrl;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl);

    // Hostname the VNC session is opened to (the farm's service name on the docker network).
    public string VncHost => _vncHost;

    // From the administration: the farm's address ("" = no farm), the host for VNC ("" = the address's host)
    // and the token ("" = the one from the environment, if any).
    public void Use(string baseUrl, string vncHost, string token)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        _vncHost = DeriveVncHost(_baseUrl, (vncHost ?? "").Trim());
        _token = (token ?? "").Trim();
    }

    private static string DeriveVncHost(string baseUrl, string vncHost) =>
        vncHost.Length > 0 ? vncHost : Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";

    // "Check the connection": the status of a farm at an address not saved yet. Null + the reason when it fails.
    public async Task<(FarmStatus? Status, string Error)> TryStatusAsync(string baseUrl, string token, CancellationToken cancellationToken)
    {
        baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, "address");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/status");
            request.Headers.TryAddWithoutValidation("X-Farm-Token", string.IsNullOrWhiteSpace(token) ? ReadToken() : token.Trim());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (null, "HTTP " + (int)response.StatusCode);
            }

            return (await response.Content.ReadFromJsonAsync<FarmStatus>(JsonOptions, timeout.Token), "");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (null, exception is TaskCanceledException ? "timeout" : exception.Message);
        }
    }

    private string ReadToken()
    {
        if (!string.IsNullOrWhiteSpace(_token))
        {
            return _token;
        }

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

    public async Task<FarmAcquireResult?> AcquireAsync(string url, string browser, string? geometry, bool toolbar, double scale, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            using var response = await _http.SendAsync(
                Build(HttpMethod.Post, "/acquire", new { url, browser, geometry, toolbar, scale }), cancellationToken);
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

    public async Task ConfigureAsync(int poolSize, string geometry, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            using var response = await _http.SendAsync(
                Build(HttpMethod.Post, "/configure", new { poolSize, geometry }), cancellationToken);
        }
        catch
        {
            // Best effort - reconciled again on the next reaper tick.
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
    public string Geometry { get; set; } = "";
}

public sealed class FarmStatus
{
    public int PoolSize { get; set; }
    public int VncBasePort { get; set; }
    public string Geometry { get; set; } = "";
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
    public string Geometry { get; set; } = "";
    [JsonPropertyName("ageSeconds")]
    public int AgeSeconds { get; set; }
}
