using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Matgate.Services;

public sealed class NetworkToolsService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly ILogger<NetworkToolsService> _logger;

    public NetworkToolsService(ILogger<NetworkToolsService> logger)
    {
        _logger = logger;
    }

    public Task PingAsync(HttpContext context, string host, int count, int timeoutMs, int intervalMs, CancellationToken cancellationToken)
    {
        return WriteTextAsync(context, async writer =>
        {
            var normalizedHost = NormalizeHost(host);
            if (string.IsNullOrWhiteSpace(normalizedHost))
            {
                await WriteLineAsync(writer, "Error: host is missing.", cancellationToken);
                TrySetStatusCode(context, StatusCodes.Status400BadRequest);
                return;
            }

            count = Math.Clamp(count, 1, 30);
            timeoutMs = Math.Clamp(timeoutMs, 100, 60_000);
            intervalMs = Math.Clamp(intervalMs, 0, 60_000);

            await WriteLineAsync(writer, $"PING {normalizedHost}", cancellationToken);

            IReadOnlyList<IPAddress> addresses = Array.Empty<IPAddress>();
            try
            {
                addresses = await Dns.GetHostAddressesAsync(normalizedHost, cancellationToken);
                if (addresses.Count > 0)
                {
                    await WriteLineAsync(writer, $"resolved: {string.Join(", ", addresses.Select(address => address.ToString()))}", cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteLineAsync(writer, $"lookup warning: {ex.Message}", cancellationToken);
            }

            var buffer = Encoding.ASCII.GetBytes("matgate-network-tools");
            var options = new PingOptions(64, true);
            var replies = new List<long>(count);
            using var ping = new Ping();

            for (var i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var reply = await ping.SendPingAsync(normalizedHost, timeoutMs, buffer, options);
                    if (reply.Status == IPStatus.Success)
                    {
                        replies.Add(reply.RoundtripTime);
                        await WriteLineAsync(
                            writer,
                            $"{i,2}: reply from {reply.Address} time={reply.RoundtripTime} ms ttl={reply.Options?.Ttl ?? 0}",
                            cancellationToken);
                    }
                    else
                    {
                        await WriteLineAsync(writer, $"{i,2}: {reply.Status}", cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await WriteLineAsync(writer, $"{i,2}: error {ex.Message}", cancellationToken);
                    _logger.LogWarning(ex, "Ping failed for {Host}", normalizedHost);
                }

                if (i < count && intervalMs > 0)
                {
                    await Task.Delay(intervalMs, cancellationToken);
                }
            }

            var packetLoss = 100.0 - (replies.Count * 100.0 / count);
            await WriteLineAsync(writer, $"--- summary ---", cancellationToken);
            await WriteLineAsync(writer, $"{count} sent, {replies.Count} received, {packetLoss:0.#}% loss", cancellationToken);
            if (replies.Count > 0)
            {
                var min = replies.Min();
                var max = replies.Max();
                var avg = replies.Average();
                await WriteLineAsync(writer, $"rtt min/avg/max = {min:0}/{avg:0.0}/{max:0} ms", cancellationToken);
            }
            else
            {
                await WriteLineAsync(writer, "rtt unavailable", cancellationToken);
            }
        }, cancellationToken);
    }

    public Task LookupAsync(HttpContext context, string host, CancellationToken cancellationToken)
    {
        return WriteTextAsync(context, async writer =>
        {
            var normalizedHost = NormalizeHost(host);
            if (string.IsNullOrWhiteSpace(normalizedHost))
            {
                await WriteLineAsync(writer, "Error: host is missing.", cancellationToken);
                TrySetStatusCode(context, StatusCodes.Status400BadRequest);
                return;
            }

            await WriteLineAsync(writer, $"LOOKUP {normalizedHost}", cancellationToken);

            try
            {
                var entry = await Dns.GetHostEntryAsync(normalizedHost, cancellationToken);
                await WriteLineAsync(writer, $"canonical: {entry.HostName}", cancellationToken);
                await WriteLineAsync(writer, "addresses:", cancellationToken);
                foreach (var address in entry.AddressList.Distinct())
                {
                    await WriteLineAsync(writer, $" - {address} ({address.AddressFamily})", cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TrySetStatusCode(context, StatusCodes.Status400BadRequest);
                await WriteLineAsync(writer, $"error: {ex.Message}", cancellationToken);
                _logger.LogWarning(ex, "Lookup failed for {Host}", normalizedHost);
            }
        }, cancellationToken);
    }

    public Task PortCheckAsync(HttpContext context, string host, string ports, int timeoutMs, CancellationToken cancellationToken)
    {
        return WriteTextAsync(context, async writer =>
        {
            var normalizedHost = NormalizeHost(host);
            if (string.IsNullOrWhiteSpace(normalizedHost))
            {
                await WriteLineAsync(writer, "Error: host is missing.", cancellationToken);
                TrySetStatusCode(context, StatusCodes.Status400BadRequest);
                return;
            }

            var portList = ParsePorts(ports).ToList();
            if (portList.Count == 0)
            {
                await WriteLineAsync(writer, "Error: port list is empty.", cancellationToken);
                TrySetStatusCode(context, StatusCodes.Status400BadRequest);
                return;
            }

            timeoutMs = Math.Clamp(timeoutMs, 100, 60_000);
            await WriteLineAsync(writer, $"PORT CHECK {normalizedHost}", cancellationToken);

            foreach (var port in portList.Distinct().OrderBy(port => port))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                try
                {
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync(normalizedHost, port);
                    var finishedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, cancellationToken));
                    if (finishedTask != connectTask)
                    {
                        await WriteLineAsync(writer, $"{port}: timeout after {timeoutMs} ms", cancellationToken);
                        continue;
                    }

                    await connectTask;
                    sw.Stop();
                    await WriteLineAsync(writer, $"{port}: open ({sw.ElapsedMilliseconds} ms)", cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw.Stop();
                    await WriteLineAsync(writer, $"{port}: closed ({ex.Message})", cancellationToken);
                }
            }
        }, cancellationToken);
    }

    public Task DownloadAsync(HttpContext context, string url, CancellationToken cancellationToken)
    {
        return WriteTextAsync(context, async writer =>
        {
            var normalizedUrl = NormalizeDownloadUri(url);
            if (normalizedUrl is null)
            {
                await WriteLineAsync(writer, "Error: URL is missing or invalid.", cancellationToken);
                TrySetStatusCode(context, StatusCodes.Status400BadRequest);
                return;
            }

            await WriteLineAsync(writer, $"DOWNLOAD {normalizedUrl}", cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl)
            {
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            try
            {
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await WriteLineAsync(writer, $"status: {(int)response.StatusCode} {response.ReasonPhrase}", cancellationToken);
                await WriteLineAsync(writer, $"content-type: {response.Content.Headers.ContentType?.ToString() ?? "-"}", cancellationToken);
                await WriteLineAsync(writer, $"content-length: {response.Content.Headers.ContentLength?.ToString() ?? "-"}", cancellationToken);

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[64 * 1024];
                var totalBytes = 0L;
                var started = Stopwatch.GetTimestamp();
                var lastReport = Stopwatch.GetTimestamp();
                var reportInterval = TimeSpan.FromMilliseconds(500);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await body.ReadAsync(buffer, cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    totalBytes += read;
                    var now = Stopwatch.GetTimestamp();
                    if (TimeSpan.FromSeconds((now - lastReport) / (double)Stopwatch.Frequency) >= reportInterval)
                    {
                        var elapsed = TimeSpan.FromSeconds((now - started) / (double)Stopwatch.Frequency);
                        var speed = elapsed.TotalSeconds <= 0 ? 0 : totalBytes / elapsed.TotalSeconds;
                        await WriteLineAsync(writer, $"downloaded: {FormatBytes(totalBytes)} @ {FormatBytes((long)speed)}/s", cancellationToken);
                        lastReport = now;
                    }
                }

                var totalElapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
                var averageSpeed = totalElapsed.TotalSeconds <= 0 ? 0 : totalBytes / totalElapsed.TotalSeconds;
                await WriteLineAsync(writer, $"finished: {FormatBytes(totalBytes)} in {totalElapsed.TotalSeconds:0.0}s", cancellationToken);
                await WriteLineAsync(writer, $"average speed: {FormatBytes((long)averageSpeed)}/s", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TrySetStatusCode(context, StatusCodes.Status400BadRequest);
                await WriteLineAsync(writer, $"error: {ex.Message}", cancellationToken);
                _logger.LogWarning(ex, "Download failed for {Url}", normalizedUrl);
            }
        }, cancellationToken);
    }

    private async Task WriteTextAsync(HttpContext context, Func<StreamWriter, Task> action, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(cancellationToken);

        var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true);

        try
        {
            await action(writer);
            await writer.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller disconnected or explicitly canceled the request.
        }
        catch (Exception ex)
        {
            TrySetStatusCode(context, StatusCodes.Status500InternalServerError);
            await WriteLineAsync(writer, $"error: {ex.Message}", cancellationToken);
            _logger.LogError(ex, "Network tool execution failed.");
        }
    }

    private static async Task WriteLineAsync(StreamWriter writer, string text, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(text.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static void TrySetStatusCode(HttpContext context, int statusCode)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = statusCode;
        }
    }

    private static string NormalizeHost(string? value)
    {
        var cleaned = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "";
        }

        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var absolute))
        {
            return absolute.Host;
        }

        if (Uri.TryCreate($"http://{cleaned}", UriKind.Absolute, out var prefixed))
        {
            return prefixed.Host;
        }

        return cleaned;
    }

    private static Uri? NormalizeDownloadUri(string? value)
    {
        var cleaned = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (!cleaned.Contains("://", StringComparison.Ordinal))
        {
            cleaned = $"http://{cleaned}";
        }

        return Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static IEnumerable<int> ParsePorts(string? value)
    {
        var parts = (value ?? "")
            .Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (int.TryParse(part, out var port) && port is >= 1 and <= 65535)
            {
                yield return port;
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(0, value);
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.0} {units[unit]}";
    }
}
