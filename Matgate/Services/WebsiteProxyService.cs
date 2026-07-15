using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Extensions.Primitives;
using Matgate.Models;

namespace Matgate.Services;

public sealed class WebsiteProxyService
{
    private static readonly Regex HtmlUrlAttributeRegex = new(
        @"(?<attr>\b(?:href|src|action|poster|formaction|xlink:href)\s*=\s*)(?<quote>[""'])(?<value>[^""']*)(\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlTargetTopRegex = new(
        @"(?<attr>\btarget\s*=\s*)(?<quote>[""']?)(?:_top|_parent)(\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlSrcSetRegex = new(
        @"(?<attr>\bsrcset\s*=\s*)(?<quote>[""'])(?<value>[^""']*)(\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CssUrlRegex = new(
        @"url\(\s*(?<quote>[""']?)(?<value>[^""')]+)(\k<quote>)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MetaFrameBlockingRegex = new(
        @"<meta\b[^>]*http-equiv\s*=\s*(?:[""']?)(?:content-security-policy|content-security-policy-report-only|x-frame-options)(?:[""']?)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MetaRefreshRegex = new(
        @"(?<prefix><meta\b[^>]*http-equiv\s*=\s*(?<q1>[""']?)refresh(\k<q1>)[^>]*\bcontent\s*=\s*(?<q2>[""']))(?<content>[^""']*)(?<q2close>\k<q2>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlScriptBlockRegex = new(
        @"(?<open><script\b[^>]*>)(?<script>.*?)(?<close></script>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex JavaScriptModuleSpecifierRegex = new(
        @"(?<prefix>\b(?:import|export)\s+(?:[^;]*?\s+from\s*)?|\bimport\s*\(\s*)(?<quote>[""'])(?<value>/(?!/)[^""']+)(\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CsrfTokenAssignmentRegex = new(
        @"\bCSRFPreventionToken\b\s*[:=]\s*[""'](?<token>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoVncRfbUrlArgumentRegex = new(
        @"(?<prefix>new\s+RFB\s*\(\s*document\.getElementById\((?<quote>[""'])noVNC_container\k<quote>\)\s*,\s*)url\.href(?<suffix>\s*,)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MatgateProxyPathRegex = new(
        @"^/website/[0-9a-f-]{32,36}(?:/[0-9a-f-]{32,36})?/proxy(?:/|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string BootstrapCacheKey = DateTimeOffset.UtcNow
        .ToUnixTimeMilliseconds()
        .ToString(CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions StorageStateJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, WebsiteProxySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<WebsiteProxyService> _logger;

    public sealed record WebsiteStorageSnapshot(
        Dictionary<string, string> LocalStorage,
        Dictionary<string, string> SessionStorage);

    public WebsiteProxyService(ILogger<WebsiteProxyService> logger)
    {
        _logger = logger;
    }

    public async Task HandleProxyAsync(HttpContext context, ServerEndpoint server, Guid? tabId, string? proxyPath, CancellationToken cancellationToken = default)
    {
        var session = GetOrCreateSession(context, server, tabId);
        session.Touch();
        await session.EnsureAuthenticatedAsync(server, cancellationToken);

        var targetUri = session.BuildTargetUri(proxyPath, context.Request.QueryString);
        var browserCookieNames = DescribeCookieNames(context.Request.Headers.TryGetValue("Cookie", out var cookieHeader) ? cookieHeader : default);

        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketProxyAsync(context, server, session, targetUri, proxyPath, browserCookieNames, cancellationToken);
            return;
        }

        _logger.LogDebug("Website proxy {Server} {Method} {ProxyPath} -> {TargetUri}", server.Name, context.Request.Method, proxyPath ?? "", targetUri);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        CopyRequestHeaders(context.Request, request, session, targetUri);

        if (CanHaveRequestBody(context.Request.Method))
        {
            if (IsLoginTicketEndpoint(targetUri) && IsFormUrlEncoded(context.Request.ContentType))
            {
                var form = await context.Request.ReadFormAsync(cancellationToken);
                var normalizedForm = NormalizeLoginForm(form, out var normalization);
                LogLoginAttempt(targetUri, normalizedForm, normalization);
                request.Content = CreateFormContent(normalizedForm);
            }
            else
            {
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentLength.HasValue)
                {
                    request.Content.Headers.ContentLength = context.Request.ContentLength.Value;
                }

                if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                {
                    try
                    {
                        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
                    }
                    catch
                    {
                        // Keep the raw stream without a parsed content type.
                    }
                }
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await session.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Website proxy upstream request failed {Server} {Method} {TargetUri}", server.Name, context.Request.Method, targetUri);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(
                    "The proxied website could not be reached. If it uses a self-signed certificate (e.g. UniFi, Synology, Proxmox over HTTPS), enable \"Ignore certificate\" for this server.",
                    cancellationToken);
            }

            return;
        }

        using (response)
        {
            var responseContent = response.Content;
            var contentType = responseContent?.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var shouldRewrite = IsHtmlLike(contentType) || IsCss(contentType) || IsJavaScript(contentType) || IsLoginTicketEndpoint(targetUri);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = contentType;

            if (shouldRewrite)
            {
                PreventCaching(context.Response);
            }

            if (response.Headers.Location is not null)
            {
                context.Response.Headers["Location"] = RewriteLocation(response.Headers.Location, session, targetUri);
            }

            if (response.Headers.WwwAuthenticate.Count > 0)
            {
                context.Response.Headers["WWW-Authenticate"] = string.Join(", ", response.Headers.WwwAuthenticate.Select(item => item.ToString()));
            }

            CopyPassThroughResponseHeaders(response, responseContent, context.Response, shouldRewrite);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
            {
                var cookies = setCookieValues.ToArray();
                session.MergeResponseCookies(cookies, targetUri);

                foreach (var setCookie in cookies)
                {
                    context.Response.Headers.Append("Set-Cookie", RewriteSetCookie(setCookie, session, context.Request.IsHttps));
                }
            }

            if (responseContent?.Headers.ContentDisposition is not null)
            {
                context.Response.Headers["Content-Disposition"] = responseContent.Headers.ContentDisposition.ToString();
            }

            if (responseContent?.Headers.ContentLanguage.Count > 0)
            {
                context.Response.Headers["Content-Language"] = string.Join(", ", responseContent.Headers.ContentLanguage);
            }

            if (!ShouldWriteResponseBody(context.Request.Method, response.StatusCode))
            {
                return;
            }

            // Anything we don't rewrite (downloads, images, audio/video, JSON, SSE, range requests)
            // is streamed straight through so large media works and nothing is buffered in memory.
            if (!shouldRewrite)
            {
                if (responseContent is null)
                {
                    return;
                }

                if (responseContent.Headers.ContentLength.HasValue)
                {
                    context.Response.ContentLength = responseContent.Headers.ContentLength.Value;
                }

                await using var upstreamStream = await responseContent.ReadAsStreamAsync(cancellationToken);
                await upstreamStream.CopyToAsync(context.Response.Body, cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                return;
            }

            var body = responseContent is null
                ? []
                : await responseContent.ReadAsByteArrayAsync(cancellationToken);

            if (IsLoginTicketEndpoint(targetUri) && response.IsSuccessStatusCode && session.TryCaptureLoginResponse(body, out var loginCookieHeader))
            {
                context.Response.Headers.Append("Set-Cookie", RewriteSetCookie(loginCookieHeader, session, context.Request.IsHttps));
            }

            if (IsHtmlLike(contentType))
            {
                body = RewriteHtml(body, contentType, session, targetUri);
            }
            else if (IsCss(contentType))
            {
                body = RewriteCss(body, contentType, session, targetUri);
            }
            else if (IsJavaScript(contentType))
            {
                body = RewriteJavaScript(body, contentType, session, targetUri);
            }

            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static void CopyPassThroughResponseHeaders(
        HttpResponseMessage response,
        HttpContent? content,
        HttpResponse target,
        bool isRewritten)
    {
        void Copy(string name, IEnumerable<string>? values)
        {
            if (values is null)
            {
                return;
            }

            var array = values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
            if (array.Length > 0)
            {
                target.Headers[name] = array;
            }
        }

        if (response.Headers.TryGetValues("Vary", out var vary))
        {
            Copy("Vary", vary);
        }

        // Caching and range headers only make sense for content we pass through unchanged.
        if (isRewritten)
        {
            return;
        }

        if (response.Headers.TryGetValues("ETag", out var etag))
        {
            Copy("ETag", etag);
        }

        if (response.Headers.TryGetValues("Accept-Ranges", out var acceptRanges))
        {
            Copy("Accept-Ranges", acceptRanges);
        }

        if (response.Headers.CacheControl is not null)
        {
            target.Headers["Cache-Control"] = response.Headers.CacheControl.ToString();
        }

        if (content is not null)
        {
            if (content.Headers.LastModified.HasValue)
            {
                target.Headers["Last-Modified"] = content.Headers.LastModified.Value.ToString("R", CultureInfo.InvariantCulture);
            }

            if (content.Headers.TryGetValues("Content-Range", out var contentRange))
            {
                Copy("Content-Range", contentRange);
            }
        }
    }

    private async Task HandleWebSocketProxyAsync(
        HttpContext context,
        ServerEndpoint server,
        WebsiteProxySession session,
        Uri targetUri,
        string? proxyPath,
        string browserCookieNames,
        CancellationToken cancellationToken)
    {
        if (context.Request.Headers.TryGetValue("Cookie", out var cookies))
        {
            session.MergeBrowserCookies(cookies);
        }

        var websocketUri = CreateWebSocketUri(targetUri);
        using var upstream = new ClientWebSocket();
        ConfigureWebSocketOptions(context.Request, session, targetUri, upstream.Options, server.IgnoreCertificate);

        _logger.LogDebug(
            "Website websocket {Server} {ProxyPath} -> {TargetUri}",
            server.Name,
            proxyPath ?? "",
            websocketUri);

        try
        {
            await upstream.ConnectAsync(websocketUri, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Website websocket upstream connect failed {Server} {ProxyPath} -> {TargetUri}", server.Name, proxyPath ?? "", websocketUri);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("WebSocket upstream connection failed.", cancellationToken);
            return;
        }

        using var browser = await context.WebSockets.AcceptWebSocketAsync(string.IsNullOrWhiteSpace(upstream.SubProtocol) ? null : upstream.SubProtocol);

        try
        {
            await RelayWebSocketsAsync(browser, upstream, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser went away or the server is shutting down.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Website websocket closed with transport error {Server} {ProxyPath}", server.Name, proxyPath ?? "");
        }
    }

    private static Uri CreateWebSocketUri(Uri targetUri)
    {
        var builder = new UriBuilder(targetUri)
        {
            Scheme = string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };

        return builder.Uri;
    }

    private static Uri ToHttpCookieUri(Uri uri)
    {
        if (!uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttps
                : Uri.UriSchemeHttp
        };

        return builder.Uri;
    }

    private static void ConfigureWebSocketOptions(
        HttpRequest request,
        WebsiteProxySession session,
        Uri targetUri,
        ClientWebSocketOptions options,
        bool ignoreCertificate)
    {
        foreach (var protocolHeader in request.Headers.SecWebSocketProtocol)
        {
            if (string.IsNullOrWhiteSpace(protocolHeader))
            {
                continue;
            }

            foreach (var protocol in protocolHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(protocol))
                {
                    options.AddSubProtocol(protocol);
                }
            }
        }

        if (ignoreCertificate)
        {
            options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var cookieHeader = session.GetCookieHeader(targetUri);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            options.Cookies = session.GetCookieContainer(targetUri);
            TrySetWebSocketHeader(options, "Cookie", cookieHeader);
        }

        TrySetWebSocketHeader(options, "Origin", session.TargetOrigin);

        if (request.Headers.TryGetValue("User-Agent", out var userAgent))
        {
            TrySetWebSocketHeader(options, "User-Agent", userAgent.ToString());
        }

        if (request.Headers.TryGetValue("Accept-Language", out var acceptLanguage))
        {
            TrySetWebSocketHeader(options, "Accept-Language", acceptLanguage.ToString());
        }
    }

    private static void TrySetWebSocketHeader(ClientWebSocketOptions options, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            options.SetRequestHeader(name, value);
        }
        catch
        {
            // ClientWebSocket owns a few handshake headers itself.
        }
    }

    private static async Task RelayWebSocketsAsync(WebSocket browser, WebSocket upstream, CancellationToken cancellationToken)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var browserToUpstream = PumpWebSocketAsync(browser, upstream, relayCancellation.Token);
        var upstreamToBrowser = PumpWebSocketAsync(upstream, browser, relayCancellation.Token);

        await Task.WhenAny(browserToUpstream, upstreamToBrowser);
        relayCancellation.Cancel();

        try
        {
            await Task.WhenAll(browserToUpstream, upstreamToBrowser);
        }
        catch (OperationCanceledException)
        {
            // One side closed; the other pump was cancelled.
        }
    }

    private static async Task PumpWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State == WebSocketState.Open || destination.State == WebSocketState.CloseReceived)
                {
                    await destination.CloseOutputAsync(
                        source.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        source.CloseStatusDescription,
                        CancellationToken.None);
                }

                return;
            }

            if (result.Count > 0)
            {
                await destination.SendAsync(
                    buffer.AsMemory(0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    cancellationToken);
            }
        }
    }

    public string BuildBootstrapScript(HttpContext context, ServerEndpoint server, Guid? tabId)
    {
        var session = GetOrCreateSession(context, server, tabId);
        session.Touch();

        var normalizedUrl = ServerEndpoint.NormalizeWebsiteUrl(server.WebsiteUrl, server.Host);
        var baseUri = string.IsNullOrWhiteSpace(normalizedUrl)
            ? null
            : new Uri(normalizedUrl, UriKind.Absolute);
        var proxyPrefix = session.ProxyPrefix;
        var targetOrigin = baseUri is null ? "" : baseUri.GetLeftPart(UriPartial.Authority);
        var hasTabId = tabId.HasValue && tabId.Value != Guid.Empty;
        var stateSyncUrl = session.StateSyncUrl;
        var storageStateJson = JsonSerializer.Serialize(session.GetStorageSnapshot(), StorageStateJsonOptions);

        return $$"""
            (() => {
                const proxyPrefix = {{JsonSerializer.Serialize(proxyPrefix)}};
                const targetOrigin = {{JsonSerializer.Serialize(targetOrigin)}};
                const stateSyncUrl = {{JsonSerializer.Serialize(stateSyncUrl)}};
                const currentOrigin = window.location.origin;
                const tabId = window.name || '';
                const routeHasTabId = {{JsonSerializer.Serialize(hasTabId)}};
                const originalFetch = window.fetch.bind(window);
                const originalOpen = XMLHttpRequest.prototype.open;
                const originalSend = XMLHttpRequest.prototype.send;
                const originalPushState = history.pushState.bind(history);
                const originalReplaceState = history.replaceState.bind(history);
                const originalWindowOpen = window.open.bind(window);
                const originalLocationAssign = window.location.assign.bind(window.location);
                const originalLocationReplace = window.location.replace.bind(window.location);
                const originalSetAttribute = Element.prototype.setAttribute;
                const originalSetAttributeNS = Element.prototype.setAttributeNS;
                const originalInsertAdjacentHTML = Element.prototype.insertAdjacentHTML;
                const originalDocumentWrite = document.write.bind(document);
                const originalDocumentWriteln = document.writeln.bind(document);
                const originalWebSocket = window.WebSocket;
                const originalEventSource = window.EventSource;
                const originalWorker = window.Worker;
                const locationProto = Object.getPrototypeOf(window.location);
                const bootstrapScript = document.currentScript;
                const bootstrapScriptUrl = bootstrapScript instanceof HTMLScriptElement ? bootstrapScript.src : '';
                const innerHTMLDescriptor = Object.getOwnPropertyDescriptor(Element.prototype, 'innerHTML');
                const outerHTMLDescriptor = Object.getOwnPropertyDescriptor(Element.prototype, 'outerHTML');
                const cookieDescriptor = Object.getOwnPropertyDescriptor(Document.prototype, 'cookie');
                const proxyCookiePath = proxyPrefix.replace(/\/$/, '');
                const initialStorageState = {{storageStateJson}};
                const storagePrototype = window.Storage && window.Storage.prototype;
                const nativeLocalStorage = window.localStorage;
                const nativeSessionStorage = window.sessionStorage;
                const storageState = {
                    localStorage: new Map(Object.entries(initialStorageState.localStorage || {})),
                    sessionStorage: new Map(Object.entries(initialStorageState.sessionStorage || {}))
                };
                let storageSyncTimer = null;
                let storageSyncInFlight = false;
                let storageSyncSuppressed = false;

                if (!routeHasTabId) {
                    const effectiveTabId = tabId || (crypto && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`);
                    const migratedHref = window.location.href.replace(/(\/website\/[^/]+)\/proxy\//i, `$1/${effectiveTabId}/proxy/`);

                    if (migratedHref !== window.location.href) {
                        window.location.replace(migratedHref);
                        return;
                    }
                }

                function rewriteUrl(value) {
                    if (typeof value !== 'string' || !value) {
                        return value;
                    }

                    if (value === bootstrapScriptUrl) {
                        return value;
                    }

                    const lower = value.trim().toLowerCase();
                    if (lower.startsWith('data:') || lower.startsWith('javascript:') || lower.startsWith('mailto:') || lower.startsWith('blob:')) {
                        return value;
                    }

                    function isMatgateProxyPath(pathname) {
                        return /^\/website\/[0-9a-f-]{32,36}(?:\/[0-9a-f-]{32,36})?\/proxy(?:\/|$)/i.test(pathname || '');
                    }

                    if (value.startsWith(proxyPrefix)) {
                        return value;
                    }

                    if (isMatgateProxyPath(value.trim())) {
                        return value;
                    }

                    if (value.startsWith('/')) {
                        return proxyPrefix + value.slice(1);
                    }

                    try {
                        const absolute = new URL(value);
                        const origin = absolute.origin;
                        if (origin === currentOrigin && isMatgateProxyPath(absolute.pathname)) {
                            return value;
                        }

                        if (origin === targetOrigin || (origin === currentOrigin && !absolute.pathname.startsWith(proxyPrefix))) {
                            return proxyPrefix + absolute.pathname.replace(/^\/+/, '') + absolute.search + absolute.hash;
                        }
                    }
                    catch {
                        // Keep the original value.
                    }

                    return value;
                }

                function toWebSocketProxyUrl(value) {
                    const absolute = new URL(value, currentOrigin);
                    absolute.protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
                    return absolute.toString();
                }

                function rewriteWebSocketUrl(value) {
                    if (typeof value !== 'string' || !value) {
                        return value;
                    }

                    try {
                        const absolute = new URL(value, currentOrigin);
                        const originAsHttp = absolute.origin
                            .replace(/^wss:/i, 'https:')
                            .replace(/^ws:/i, 'http:');

                        if (originAsHttp === targetOrigin) {
                            return toWebSocketProxyUrl(proxyPrefix + absolute.pathname.replace(/^\/+/, '') + absolute.search + absolute.hash);
                        }

                        if (absolute.host === window.location.host && isMatgateProxyPath(absolute.pathname)) {
                            return toWebSocketProxyUrl(absolute.pathname + absolute.search + absolute.hash);
                        }

                        if (absolute.host === window.location.host && !absolute.pathname.startsWith(proxyPrefix)) {
                            return toWebSocketProxyUrl(proxyPrefix + absolute.pathname.replace(/^\/+/, '') + absolute.search + absolute.hash);
                        }
                    }
                    catch {
                        // Fall back to the regular URL rewriter below.
                    }

                    const rewritten = rewriteUrl(value);
                    try {
                        const absolute = new URL(rewritten, currentOrigin);
                        if (absolute.host === window.location.host && isMatgateProxyPath(absolute.pathname)) {
                            return toWebSocketProxyUrl(absolute.pathname + absolute.search + absolute.hash);
                        }
                    }
                    catch {
                        // Keep the rewritten value.
                    }

                    return rewritten;
                }

                function rewriteSrcSet(value) {
                    if (typeof value !== 'string' || !value) {
                        return value;
                    }

                    return value.split(',').map(item => {
                        const trimmed = item.trim();
                        if (!trimmed) {
                            return trimmed;
                        }

                        const firstSpace = trimmed.search(/\s/);
                        const url = firstSpace < 0 ? trimmed : trimmed.slice(0, firstSpace);
                        const descriptor = firstSpace < 0 ? '' : trimmed.slice(firstSpace);
                        return `${rewriteUrl(url)}${descriptor}`;
                    }).join(', ');
                }

                function rewriteMarkup(value) {
                    if (typeof value !== 'string' || !value) {
                        return value;
                    }

                    value = value.replace(/(\b(?:href|src|action|poster|formaction|xlink:href)\s*=\s*)(["'])([^"']*)(\2)/gi, (_, prefix, quote, rawValue) => {
                        return `${prefix}${quote}${rewriteUrl(rawValue)}${quote}`;
                    });

                    value = value.replace(/(\bsrcset\s*=\s*)(["'])([^"']*)(\2)/gi, (_, prefix, quote, rawValue) => {
                        return `${prefix}${quote}${rewriteSrcSet(rawValue)}${quote}`;
                    });

                    value = value.replace(/(\btarget\s*=\s*)(["']?)(?:_top|_parent)(\2)/gi, (_, prefix, quote) => {
                        return `${prefix}${quote}_self${quote}`;
                    });

                    return value;
                }

                function rewriteCookie(value) {
                    if (typeof value !== 'string' || !value) {
                        return value;
                    }

                    let cookie = value.replace(/;\s*Domain=[^;]+/gi, '');

                    if (/;\s*Path=[^;]+/i.test(cookie)) {
                        cookie = cookie.replace(/(;\s*Path=)[^;]+/i, `$1${proxyCookiePath}`);
                    }
                    else {
                        cookie += `; Path=${proxyCookiePath}`;
                    }

                    if (currentOrigin.startsWith('http://')) {
                        cookie = cookie.replace(/;\s*Secure\b/gi, '');
                        cookie = cookie.replace(/;\s*SameSite=None\b/gi, '; SameSite=Lax');
                    }

                    return cookie;
                }

                function isLoginTicketUrl(value) {
                    return typeof value === 'string' && value.toLowerCase().includes('/access/ticket');
                }

                function applyLoginCookieFromResponseText(text) {
                    if (typeof text !== 'string' || !text) {
                        return false;
                    }

                    try {
                        const parsed = JSON.parse(text);
                        const data = parsed && typeof parsed === 'object' && parsed.data && typeof parsed.data === 'object'
                            ? parsed.data
                            : parsed;
                        const ticket = data && typeof data.ticket === 'string' ? data.ticket : '';

                        if (!ticket) {
                            return false;
                        }

                        document.cookie = rewriteCookie(`PVEAuthCookie=${ticket}; Path=/; SameSite=Lax`);
                        return true;
                    }
                    catch {
                        return false;
                    }
                }

                function storageEntries(kind) {
                    return storageState[kind] || storageState.localStorage;
                }

                function storageLength(kind) {
                    return storageEntries(kind).size;
                }

                function storageKey(kind, index) {
                    const keys = Array.from(storageEntries(kind).keys());
                    const numericIndex = Number(index);
                    if (!Number.isInteger(numericIndex) || numericIndex < 0 || numericIndex >= keys.length) {
                        return null;
                    }

                    return keys[numericIndex] ?? null;
                }

                function storageKind(storage) {
                    if (storage === nativeLocalStorage || storage === window.localStorage) {
                        return 'localStorage';
                    }

                    if (storage === nativeSessionStorage || storage === window.sessionStorage) {
                        return 'sessionStorage';
                    }

                    return '';
                }

                function getStorageValue(kind, key) {
                    const entries = storageEntries(kind);
                    const normalizedKey = String(key);
                    return entries.has(normalizedKey) ? entries.get(normalizedKey) : null;
                }

                function setStorageValue(kind, key, value, notify = true) {
                    storageEntries(kind).set(String(key), value == null ? '' : String(value));
                    if (notify) {
                        scheduleStorageSync();
                    }
                }

                function removeStorageValue(kind, key, notify = true) {
                    storageEntries(kind).delete(String(key));
                    if (notify) {
                        scheduleStorageSync();
                    }
                }

                function clearStorage(kind, notify = true) {
                    storageEntries(kind).clear();
                    if (notify) {
                        scheduleStorageSync();
                    }
                }

                function storageSnapshot() {
                    return {
                        localStorage: Object.fromEntries(storageEntries('localStorage')),
                        sessionStorage: Object.fromEntries(storageEntries('sessionStorage'))
                    };
                }

                function applyStorageSnapshot(snapshot) {
                    const nextLocal = snapshot && typeof snapshot === 'object' && snapshot.localStorage && typeof snapshot.localStorage === 'object'
                        ? snapshot.localStorage
                        : {};
                    const nextSession = snapshot && typeof snapshot === 'object' && snapshot.sessionStorage && typeof snapshot.sessionStorage === 'object'
                        ? snapshot.sessionStorage
                        : {};

                    storageSyncSuppressed = true;
                    try {
                        storageState.localStorage.clear();
                        storageState.sessionStorage.clear();
                        for (const [key, value] of Object.entries(nextLocal)) {
                            storageState.localStorage.set(String(key), value == null ? '' : String(value));
                        }
                        for (const [key, value] of Object.entries(nextSession)) {
                            storageState.sessionStorage.set(String(key), value == null ? '' : String(value));
                        }
                    }
                    finally {
                        storageSyncSuppressed = false;
                    }
                }

                function scheduleStorageSync() {
                    if (storageSyncSuppressed) {
                        return;
                    }

                    if (storageSyncTimer) {
                        clearTimeout(storageSyncTimer);
                    }

                    storageSyncTimer = setTimeout(flushStorageSync, 250);
                }

                function flushStorageSync() {
                    if (storageSyncSuppressed || storageSyncInFlight) {
                        return;
                    }

                    storageSyncInFlight = true;
                    const payload = JSON.stringify(storageSnapshot());

                    try {
                        if (navigator.sendBeacon) {
                            const sent = navigator.sendBeacon(stateSyncUrl, new Blob([payload], { type: 'application/json' }));
                            if (sent) {
                                storageSyncInFlight = false;
                                return;
                            }
                        }
                    }
                    catch {
                        // Use fetch fallback below.
                    }

                    originalFetch(stateSyncUrl, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: payload,
                        credentials: 'same-origin',
                        keepalive: true
                    }).catch(() => {
                        // Ignore background sync failures.
                    }).finally(() => {
                        storageSyncInFlight = false;
                    });
                }

                function createVirtualStorage(kind) {
                    const target = Object.create(Storage.prototype);

                    return new Proxy(target, {
                        get(_target, prop, receiver) {
                            switch (prop) {
                                case 'getItem':
                                    return key => getStorageValue(kind, key);
                                case 'setItem':
                                    return (key, value) => setStorageValue(kind, key, value);
                                case 'removeItem':
                                    return key => removeStorageValue(kind, key);
                                case 'clear':
                                    return () => clearStorage(kind);
                                case 'key':
                                    return index => storageKey(kind, index);
                                case 'length':
                                    return storageLength(kind);
                                case Symbol.toStringTag:
                                    return 'Storage';
                                default:
                                    break;
                            }

                            if (typeof prop === 'string') {
                                return getStorageValue(kind, prop);
                            }

                            return Reflect.get(_target, prop, receiver);
                        },
                        set(_target, prop, value) {
                            if (typeof prop === 'string') {
                                setStorageValue(kind, prop, value);
                                return true;
                            }

                            return false;
                        },
                        deleteProperty(_target, prop) {
                            if (typeof prop === 'string') {
                                removeStorageValue(kind, prop);
                                return true;
                            }

                            return false;
                        },
                        has(_target, prop) {
                            if (typeof prop === 'string') {
                                return storageEntries(kind).has(prop);
                            }

                            return Reflect.has(_target, prop);
                        },
                        ownKeys() {
                            return [...Array.from(storageEntries(kind).keys()), 'length'];
                        },
                        getOwnPropertyDescriptor(_target, prop) {
                            if (typeof prop === 'string' && storageEntries(kind).has(prop)) {
                                return {
                                    configurable: true,
                                    enumerable: true,
                                    writable: true,
                                    value: getStorageValue(kind, prop)
                                };
                            }

                            if (prop === 'length') {
                                return {
                                    configurable: true,
                                    enumerable: false,
                                    writable: false,
                                    value: storageLength(kind)
                                };
                            }

                            return undefined;
                        }
                    });
                }

                const virtualLocalStorage = createVirtualStorage('localStorage');
                const virtualSessionStorage = createVirtualStorage('sessionStorage');

                try {
                    Object.defineProperty(window, 'localStorage', {
                        configurable: true,
                        enumerable: true,
                        get: () => virtualLocalStorage
                    });
                    Object.defineProperty(window, 'sessionStorage', {
                        configurable: true,
                        enumerable: true,
                        get: () => virtualSessionStorage
                    });
                }
                catch {
                    if (storagePrototype) {
                        const originalGetItem = storagePrototype.getItem;
                        const originalSetItem = storagePrototype.setItem;
                        const originalRemoveItem = storagePrototype.removeItem;
                        const originalClear = storagePrototype.clear;
                        const originalKey = storagePrototype.key;
                        const originalLength = Object.getOwnPropertyDescriptor(storagePrototype, 'length');

                        try {
                            Object.defineProperty(storagePrototype, 'getItem', {
                                configurable: true,
                                enumerable: false,
                                value(key) {
                                    const kind = storageKind(this);
                                    return kind ? getStorageValue(kind, key) : originalGetItem.call(this, key);
                                }
                            });
                            Object.defineProperty(storagePrototype, 'setItem', {
                                configurable: true,
                                enumerable: false,
                                value(key, value) {
                                    const kind = storageKind(this);
                                    if (kind) {
                                        setStorageValue(kind, key, value);
                                        return undefined;
                                    }

                                    return originalSetItem.call(this, key, value);
                                }
                            });
                            Object.defineProperty(storagePrototype, 'removeItem', {
                                configurable: true,
                                enumerable: false,
                                value(key) {
                                    const kind = storageKind(this);
                                    if (kind) {
                                        removeStorageValue(kind, key);
                                        return undefined;
                                    }

                                    return originalRemoveItem.call(this, key);
                                }
                            });
                            Object.defineProperty(storagePrototype, 'clear', {
                                configurable: true,
                                enumerable: false,
                                value() {
                                    const kind = storageKind(this);
                                    if (kind) {
                                        clearStorage(kind);
                                        return undefined;
                                    }

                                    return originalClear.call(this);
                                }
                            });
                            Object.defineProperty(storagePrototype, 'key', {
                                configurable: true,
                                enumerable: false,
                                value(index) {
                                    const kind = storageKind(this);
                                    return kind ? storageKey(kind, index) : originalKey.call(this, index);
                                }
                            });
                            if (originalLength) {
                                Object.defineProperty(storagePrototype, 'length', {
                                    configurable: true,
                                    enumerable: originalLength.enumerable,
                                    get() {
                                        const kind = storageKind(this);
                                        if (kind) {
                                            return storageLength(kind);
                                        }

                                        return originalLength.get ? originalLength.get.call(this) : 0;
                                    }
                                });
                            }
                        }
                        catch {
                            // Ignore storage patch failures; some browsers may lock the prototype.
                        }
                    }
                }

                applyStorageSnapshot(initialStorageState);
                window.addEventListener('pagehide', flushStorageSync);
                window.addEventListener('beforeunload', flushStorageSync);

                if (cookieDescriptor && typeof cookieDescriptor.set === 'function') {
                    Object.defineProperty(Document.prototype, 'cookie', {
                        configurable: true,
                        enumerable: cookieDescriptor.enumerable,
                        get: cookieDescriptor.get ? function() { return cookieDescriptor.get.call(this); } : undefined,
                        set(value) {
                            cookieDescriptor.set.call(this, rewriteCookie(value));
                        }
                    });
                }

                function rewriteAttribute(name, value) {
                    if (typeof name !== 'string') {
                        return value;
                    }

                    switch (name.toLowerCase()) {
                        case 'href':
                        case 'src':
                        case 'action':
                        case 'poster':
                        case 'formaction':
                        case 'xlink:href':
                            return rewriteUrl(value);
                        case 'srcset':
                            return rewriteSrcSet(value);
                        case 'target':
                            return typeof value === 'string' && /^(?:_top|_parent)$/i.test(value) ? '_self' : value;
                        default:
                            return value;
                    }
                }

                function rewriteElementAttributes(element) {
                    if (!(element instanceof Element)) {
                        return;
                    }

                    if (element === bootstrapScript) {
                        return;
                    }

                    for (const attr of Array.from(element.attributes || [])) {
                        const next = rewriteAttribute(attr.name, attr.value);
                        if (next !== attr.value) {
                            originalSetAttribute.call(element, attr.name, next);
                        }
                    }
                }

                function rewriteElementTree(root) {
                    if (!(root instanceof Element)) {
                        return;
                    }

                    rewriteElementAttributes(root);
                    for (const element of root.querySelectorAll('*')) {
                        rewriteElementAttributes(element);
                    }
                }

                function patchUrlProperty(proto, propertyName, transform) {
                    if (!proto) {
                        return;
                    }

                    const descriptor = Object.getOwnPropertyDescriptor(proto, propertyName);
                    if (!descriptor || typeof descriptor.set !== 'function') {
                        return;
                    }

                    Object.defineProperty(proto, propertyName, {
                        configurable: true,
                        enumerable: descriptor.enumerable,
                        get: descriptor.get ? function() { return descriptor.get.call(this); } : undefined,
                        set(value) {
                            descriptor.set.call(this, transform(value));
                        }
                    });
                }

                function patchSourceUrl(proto, propertyName) {
                    patchUrlProperty(proto, propertyName, rewriteUrl);
                }

                function patchMarkupSetter(proto, propertyName) {
                    if (!proto) {
                        return;
                    }

                    const descriptor = Object.getOwnPropertyDescriptor(proto, propertyName);
                    if (!descriptor || typeof descriptor.set !== 'function') {
                        return;
                    }

                    Object.defineProperty(proto, propertyName, {
                        configurable: true,
                        enumerable: descriptor.enumerable,
                        get: descriptor.get ? function() { return descriptor.get.call(this); } : undefined,
                        set(value) {
                            descriptor.set.call(this, typeof value === 'string' ? rewriteMarkup(value) : value);
                        }
                    });
                }

                function patchConstructor(original, transform) {
                    if (typeof original !== 'function') {
                        return original;
                    }

                    const proxy = function(url, ...rest) {
                        return new original(transform(url), ...rest);
                    };

                    proxy.prototype = original.prototype;
                    Object.setPrototypeOf(proxy, original);
                    try {
                        Object.assign(proxy, original);
                    }
                    catch {
                        // Ignore readonly static members.
                    }

                    return proxy;
                }

                function notifyParent() {
                    try {
                        if (tabId && window.parent && window.parent !== window && typeof window.parent.MatgateWebsiteLocationChanged === 'function') {
                            window.parent.MatgateWebsiteLocationChanged(tabId, window.location.href);
                        }
                    }
                    catch {
                        // Keep the local page functional even if the parent is unavailable.
                    }
                }

                window.__matgateProxyPrefix = proxyPrefix;
                window.__matgateRewriteUrl = rewriteUrl;
                window.__matgateRewriteWebSocketUrl = rewriteWebSocketUrl;

                patchSourceUrl(HTMLAnchorElement && HTMLAnchorElement.prototype, 'href');
                patchSourceUrl(HTMLAreaElement && HTMLAreaElement.prototype, 'href');
                patchSourceUrl(HTMLImageElement && HTMLImageElement.prototype, 'src');
                patchSourceUrl(HTMLIFrameElement && HTMLIFrameElement.prototype, 'src');
                patchSourceUrl(HTMLScriptElement && HTMLScriptElement.prototype, 'src');
                patchSourceUrl(HTMLLinkElement && HTMLLinkElement.prototype, 'href');
                patchSourceUrl(HTMLSourceElement && HTMLSourceElement.prototype, 'src');
                patchSourceUrl(HTMLVideoElement && HTMLVideoElement.prototype, 'src');
                patchSourceUrl(HTMLAudioElement && HTMLAudioElement.prototype, 'src');
                patchSourceUrl(HTMLTrackElement && HTMLTrackElement.prototype, 'src');
                patchSourceUrl(HTMLInputElement && HTMLInputElement.prototype, 'src');
                patchSourceUrl(HTMLFormElement && HTMLFormElement.prototype, 'action');
                patchSourceUrl(HTMLButtonElement && HTMLButtonElement.prototype, 'formAction');
                patchSourceUrl(HTMLObjectElement && HTMLObjectElement.prototype, 'data');
                patchSourceUrl(HTMLEmbedElement && HTMLEmbedElement.prototype, 'src');
                patchSourceUrl(SVGUseElement && SVGUseElement.prototype, 'href');
                patchSourceUrl(SVGUseElement && SVGUseElement.prototype, 'xlinkHref');
                patchSourceUrl(locationProto, 'href');
                patchMarkupSetter(Element.prototype, 'innerHTML');
                patchMarkupSetter(Element.prototype, 'outerHTML');

                Element.prototype.setAttribute = function(name, value) {
                    return originalSetAttribute.call(this, name, rewriteAttribute(name, value));
                };

                Element.prototype.setAttributeNS = function(namespace, name, value) {
                    return originalSetAttributeNS.call(this, namespace, name, rewriteAttribute(name, value));
                };

                Element.prototype.insertAdjacentHTML = function(position, html) {
                    return originalInsertAdjacentHTML.call(this, position, typeof html === 'string' ? rewriteMarkup(html) : html);
                };

                document.write = function(...parts) {
                    return originalDocumentWrite(...parts.map(part => typeof part === 'string' ? rewriteMarkup(part) : part));
                };

                document.writeln = function(...parts) {
                    return originalDocumentWriteln(...parts.map(part => typeof part === 'string' ? rewriteMarkup(part) : part));
                };

                window.WebSocket = patchConstructor(originalWebSocket, rewriteWebSocketUrl);
                window.EventSource = patchConstructor(originalEventSource, rewriteUrl);
                window.Worker = patchConstructor(originalWorker, rewriteUrl);

                // Neutralize service workers. Every proxied site shares the Matgate origin, so a site's
                // service worker would install on Matgate itself, run without these URL-rewriting shims,
                // and could hijack unrelated pages. Present the API but make registration fail cleanly so
                // apps fall back to their no-service-worker path.
                try {
                    if (navigator.serviceWorker) {
                        const swNoop = () => {};
                        const swReject = () => Promise.reject(new DOMException('Service workers are disabled behind the Matgate proxy.', 'SecurityError'));
                        const swStub = {
                            register: swReject,
                            getRegistration: () => Promise.resolve(undefined),
                            getRegistrations: () => Promise.resolve([]),
                            ready: new Promise(() => {}),
                            controller: null,
                            oncontrollerchange: null,
                            onmessage: null,
                            startMessages: swNoop,
                            addEventListener: swNoop,
                            removeEventListener: swNoop,
                            dispatchEvent: () => false
                        };
                        Object.defineProperty(navigator, 'serviceWorker', {
                            configurable: true,
                            get: () => swStub
                        });
                    }
                }
                catch {
                    // Some browsers expose navigator.serviceWorker as non-configurable; ignore.
                }

                // Namespace IndexedDB per proxied target. Databases live on the shared Matgate origin, so
                // without a per-session prefix every proxied site would see and clobber the others' data.
                try {
                    const idbFactory = window.indexedDB;
                    if (idbFactory && typeof idbFactory.open === 'function') {
                        const idbPrefix = '__mg' + proxyCookiePath.replace(/[^a-z0-9]+/gi, '_') + '__';
                        const addPrefix = dbName => (typeof dbName === 'string' && dbName.indexOf(idbPrefix) !== 0) ? idbPrefix + dbName : dbName;
                        const stripPrefix = dbName => (typeof dbName === 'string' && dbName.indexOf(idbPrefix) === 0) ? dbName.slice(idbPrefix.length) : dbName;
                        const nativeIdbOpen = idbFactory.open.bind(idbFactory);
                        idbFactory.open = function(dbName, ...rest) { return nativeIdbOpen(addPrefix(dbName), ...rest); };
                        if (typeof idbFactory.deleteDatabase === 'function') {
                            const nativeIdbDelete = idbFactory.deleteDatabase.bind(idbFactory);
                            idbFactory.deleteDatabase = function(dbName, ...rest) { return nativeIdbDelete(addPrefix(dbName), ...rest); };
                        }
                        if (typeof idbFactory.databases === 'function') {
                            const nativeIdbDatabases = idbFactory.databases.bind(idbFactory);
                            idbFactory.databases = function() {
                                return nativeIdbDatabases().then(list => (list || [])
                                    .filter(info => info && typeof info.name === 'string' && info.name.indexOf(idbPrefix) === 0)
                                    .map(info => Object.assign({}, info, { name: stripPrefix(info.name) })));
                            };
                        }
                    }
                }
                catch {
                    // Ignore IndexedDB shim failures; fall back to native (shared) behaviour.
                }

                if (document.documentElement) {
                    rewriteElementTree(document.documentElement);
                }

                const observer = new MutationObserver(mutations => {
                    for (const mutation of mutations) {
                        if (mutation.type === 'childList') {
                            for (const node of mutation.addedNodes) {
                                if (node instanceof Element) {
                                    rewriteElementTree(node);
                                }
                            }
                        }
                        else if (mutation.type === 'attributes' && mutation.target instanceof Element) {
                            const next = rewriteAttribute(mutation.attributeName || '', mutation.target.getAttribute(mutation.attributeName || '') || '');
                            if (mutation.attributeName && next !== mutation.target.getAttribute(mutation.attributeName)) {
                                originalSetAttribute.call(mutation.target, mutation.attributeName, next);
                            }
                        }
                    }
                });

                observer.observe(document.documentElement, {
                    attributes: true,
                    childList: true,
                    subtree: true,
                    attributeFilter: ['href', 'src', 'srcset', 'action', 'poster', 'formaction', 'xlink:href', 'target', 'data']
                });

                window.fetch = function(input, init) {
                    let requestUrl = '';
                    if (typeof input === 'string') {
                        requestUrl = rewriteUrl(input);
                        return originalFetch(requestUrl, init).then(response => {
                            if (isLoginTicketUrl(requestUrl) && response && response.ok) {
                                response.clone().text().then(text => applyLoginCookieFromResponseText(text)).catch(() => {});
                            }

                            return response;
                        });
                    }

                    if (input instanceof Request) {
                        requestUrl = rewriteUrl(input.url);
                        return originalFetch(new Request(requestUrl, input)).then(response => {
                            if (isLoginTicketUrl(requestUrl) && response && response.ok) {
                                response.clone().text().then(text => applyLoginCookieFromResponseText(text)).catch(() => {});
                            }

                            return response;
                        });
                    }

                    return originalFetch(input, init).then(response => {
                        if (response && response.ok && input && typeof input.url === 'string' && isLoginTicketUrl(rewriteUrl(input.url))) {
                            response.clone().text().then(text => applyLoginCookieFromResponseText(text)).catch(() => {});
                        }

                        return response;
                    });
                };

                XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                    const rewrittenUrl = rewriteUrl(url);
                    this.__matgateUrl = rewrittenUrl;
                    return originalOpen.call(this, method, rewrittenUrl, ...rest);
                };

                XMLHttpRequest.prototype.send = function(body) {
                    if (!this.__matgateLoginCookieHooked) {
                        this.__matgateLoginCookieHooked = true;
                        this.addEventListener('load', () => {
                            try {
                                if (isLoginTicketUrl(this.__matgateUrl) && this.status >= 200 && this.status < 300) {
                                    applyLoginCookieFromResponseText(this.responseText || '');
                                }
                            }
                            catch {
                                // Ignore cookie hook failures.
                            }
                        }, { once: true });
                    }

                    return originalSend.call(this, body);
                };

                history.pushState = function(state, title, url) {
                    const result = originalPushState(state, title, typeof url === 'string' ? rewriteUrl(url) : url);
                    notifyParent();
                    return result;
                };

                history.replaceState = function(state, title, url) {
                    const result = originalReplaceState(state, title, typeof url === 'string' ? rewriteUrl(url) : url);
                    notifyParent();
                    return result;
                };

                window.open = function(url, target, features) {
                    return originalWindowOpen(typeof url === 'string' ? rewriteUrl(url) : url, target, features);
                };

                try {
                    window.location.assign = function(url) {
                        return originalLocationAssign(typeof url === 'string' ? rewriteUrl(url) : url);
                    };
                }
                catch {
                    // Ignore read-only navigation hooks.
                }

                try {
                    window.location.replace = function(url) {
                        return originalLocationReplace(typeof url === 'string' ? rewriteUrl(url) : url);
                    };
                }
                catch {
                    // Ignore read-only navigation hooks.
                }

                window.addEventListener('load', notifyParent);
                window.addEventListener('hashchange', notifyParent);
                window.addEventListener('popstate', notifyParent);
                notifyParent();
            })();
            """;
    }

    public async Task<IResult> HandleWebsiteStateAsync(HttpContext context, ServerEndpoint server, Guid? tabId, CancellationToken cancellationToken = default)
    {
        var session = GetOrCreateSession(context, server, tabId);
        session.Touch();

        if (HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Json(session.GetStorageSnapshot(), StorageStateJsonOptions);
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            var snapshot = await context.Request.ReadFromJsonAsync<WebsiteStorageSnapshot>(StorageStateJsonOptions, cancellationToken);
            if (snapshot is not null)
            {
                session.ReplaceStorageSnapshot(snapshot);
            }

            return Results.Ok(new { status = "ok" });
        }

        return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    public void ForgetServer(Guid serverId)
    {
        foreach (var entry in _sessions.Where(entry =>
                     entry.Key.Contains($":{serverId:N}:", StringComparison.OrdinalIgnoreCase)
                     || entry.Key.EndsWith($":{serverId:N}", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_sessions.TryRemove(entry.Key, out var session))
            {
                session.Dispose();
            }
        }
    }

    private WebsiteProxySession GetOrCreateSession(HttpContext context, ServerEndpoint server, Guid? tabId)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var normalizedTabId = tabId.GetValueOrDefault();
        var key = $"{userId}:{server.Id:N}:{normalizedTabId:N}";
        var signature = BuildSignature(server);

        while (true)
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                if (existing.Signature == signature && existing.IsHealthy)
                {
                    return existing;
                }

                if (_sessions.TryRemove(key, out var removed))
                {
                    removed.Dispose();
                }

                continue;
            }

            var created = new WebsiteProxySession(server, signature, normalizedTabId);
            if (_sessions.TryAdd(key, created))
            {
                return created;
            }

            created.Dispose();
        }
    }

    private static string BuildSignature(ServerEndpoint server)
    {
        return string.Join("|",
            ServerEndpoint.NormalizeWebsiteUrl(server.WebsiteUrl, server.Host),
            server.UserName,
            server.Password,
            server.IgnoreCertificate ? "1" : "0");
    }

    private static string BuildProxyPrefix(Guid serverId, Guid? tabId)
    {
        return tabId.HasValue && tabId.Value != Guid.Empty
            ? $"/website/{serverId:D}/{tabId.Value:D}/proxy/"
            : $"/website/{serverId:D}/proxy/";
    }

    private void CopyRequestHeaders(HttpRequest request, HttpRequestMessage message, WebsiteProxySession session, Uri targetUri)
    {
        foreach (var header in request.Headers)
        {
            if (IsHopByHopHeader(header.Key)
                || string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(header.Key, "CSRFPreventionToken", StringComparison.OrdinalIgnoreCase))
            {
                session.TryCaptureCsrfToken(header.Value.ToString());
                continue;
            }

            if (string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                session.MergeBrowserCookies(header.Value);
                continue;
            }

            if (string.Equals(header.Key, "Origin", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.TryAddWithoutValidation(header.Key, session.TargetOrigin);
                continue;
            }

            if (string.Equals(header.Key, "Referer", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Referrer = targetUri;
                continue;
            }

            message.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (message.Headers.Referrer is null)
        {
            message.Headers.Referrer = targetUri;
        }

        if (string.Equals(request.Method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Method, HttpMethods.Put, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Method, HttpMethods.Patch, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Method, HttpMethods.Delete, StringComparison.OrdinalIgnoreCase))
        {
            // Ensure exactly one Origin header. The header loop above already adds one when the browser
            // sends Origin (it does on POST); adding a second here produces a duplicate Origin, which
            // nginx (e.g. Synology DSM) rejects with 400 Bad Request.
            message.Headers.Remove("Origin");
            message.Headers.TryAddWithoutValidation("Origin", session.TargetOrigin);
        }

        // CSRFPreventionToken is a Proxmox concept (api2/*). Never attach it to other targets (e.g. Synology),
        // where a stray/oversized header value would be rejected by the upstream with 400.
        var shouldSendCsrfToken = session.HasCsrfToken
            && IsWriteMethod(request.Method)
            && !IsLoginTicketEndpoint(targetUri)
            && targetUri.AbsolutePath.StartsWith("/api2/", StringComparison.OrdinalIgnoreCase);

        if (shouldSendCsrfToken)
        {
            message.Headers.TryAddWithoutValidation("CSRFPreventionToken", session.CsrfToken);
        }

        if (IsWriteMethod(request.Method) && targetUri.AbsolutePath.StartsWith("/api2/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Website proxy write auth {Method} {TargetUri} csrf={HasCsrfToken}",
                request.Method,
                targetUri,
                shouldSendCsrfToken ? "yes" : "no");
        }

        session.ApplyCookies(message, targetUri);
    }

    private static bool IsMatgateCookie(string cookieName)
    {
        return cookieName.Equals("Matgate.Auth", StringComparison.OrdinalIgnoreCase)
            || cookieName.Equals("Matgate.Language", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanHaveRequestBody(string method)
    {
        return !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method);
    }

    private static bool IsWriteMethod(string method)
    {
        return HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);
    }

    private static bool ShouldWriteResponseBody(string method, HttpStatusCode statusCode)
    {
        if (HttpMethods.IsHead(method))
        {
            return false;
        }

        return statusCode is not HttpStatusCode.NoContent
            and not HttpStatusCode.NotModified
            and not HttpStatusCode.ResetContent;
    }

    private static bool IsHopByHopHeader(string headerName)
    {
        return headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("TE", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Host", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHtmlLike(string contentType)
    {
        return contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFormUrlEncoded(string? contentType)
    {
        return !string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoginTicketEndpoint(Uri targetUri)
    {
        return targetUri.AbsolutePath.EndsWith("/api2/json/access/ticket", StringComparison.OrdinalIgnoreCase)
            || targetUri.AbsolutePath.EndsWith("/api2/extjs/access/ticket", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCss(string contentType)
    {
        return contentType.StartsWith("text/css", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJavaScript(string contentType)
    {
        return contentType.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/x-javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConsoleEndpoint(Uri targetUri)
    {
        return targetUri.AbsolutePath.Contains("/termproxy", StringComparison.OrdinalIgnoreCase)
            || targetUri.AbsolutePath.Contains("/vncproxy", StringComparison.OrdinalIgnoreCase)
            || targetUri.AbsolutePath.Contains("/vncwebsocket", StringComparison.OrdinalIgnoreCase);
    }

    private byte[] RewriteHtml(byte[] body, string contentType, WebsiteProxySession session, Uri responseUri)
    {
        var encoding = ResolveEncoding(contentType);
        var html = encoding.GetString(body);

        session.TryCaptureCsrfToken(html);
        html = MetaFrameBlockingRegex.Replace(html, "");
        html = MetaRefreshRegex.Replace(html, m =>
        {
            var content = m.Groups["content"].Value;
            var urlIndex = content.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
            if (urlIndex < 0)
            {
                return m.Value;
            }

            var head = content[..(urlIndex + 4)];
            var url = content[(urlIndex + 4)..].Trim().Trim('\'', '"');
            return $"{m.Groups["prefix"].Value}{head}{RewriteUrl(url, session, responseUri)}{m.Groups["q2close"].Value}";
        });
        html = HtmlTargetTopRegex.Replace(html, m => $"{m.Groups["attr"].Value}{m.Groups["quote"].Value}_self{m.Groups["quote"].Value}");
        html = HtmlSrcSetRegex.Replace(html, m => $"{m.Groups["attr"].Value}{m.Groups["quote"].Value}{RewriteSrcSet(m.Groups["value"].Value, session, responseUri)}{m.Groups["quote"].Value}");
        html = HtmlUrlAttributeRegex.Replace(html, m => $"{m.Groups["attr"].Value}{m.Groups["quote"].Value}{RewriteUrl(m.Groups["value"].Value, session, responseUri)}{m.Groups["quote"].Value}");
        html = HtmlScriptBlockRegex.Replace(html, m => $"{m.Groups["open"].Value}{RewriteJavaScriptText(m.Groups["script"].Value, session, responseUri)}{m.Groups["close"].Value}");
        html = InjectBootstrapScript(html, session.BootstrapScriptUrl);

        return encoding.GetBytes(html);
    }

    private byte[] RewriteCss(byte[] body, string contentType, WebsiteProxySession session, Uri responseUri)
    {
        var encoding = ResolveEncoding(contentType);
        var css = encoding.GetString(body);
        css = CssUrlRegex.Replace(css, m => $"url({m.Groups["quote"].Value}{RewriteUrl(m.Groups["value"].Value, session, responseUri)}{m.Groups["quote"].Value})");
        return encoding.GetBytes(css);
    }

    private byte[] RewriteJavaScript(byte[] body, string contentType, WebsiteProxySession session, Uri responseUri)
    {
        var encoding = ResolveEncoding(contentType);
        var script = encoding.GetString(body);
        session.TryCaptureCsrfToken(script);
        script = RewriteJavaScriptText(script, session, responseUri);

        return encoding.GetBytes(script);
    }

    private string RewriteJavaScriptText(string script, WebsiteProxySession session, Uri responseUri)
    {
        var proxiedWebSocketBase = $"((location.protocol === 'https:') ? 'wss://' : 'ws://') + location.host + {JsonSerializer.Serialize(session.ProxyPrefix)}";
        script = script.Replace(
            "socketURL = protocol + location.hostname + ((location.port) ? (':' + location.port) : '') + '/api2/json' + url + '/vncwebsocket?port=' + port + '&vncticket=' + encodeURIComponent(ticket);",
            $"socketURL = {proxiedWebSocketBase} + 'api2/json' + url + '/vncwebsocket?port=' + port + '&vncticket=' + encodeURIComponent(ticket);",
            StringComparison.Ordinal);
        script = script.Replace(
            "new WebSocket(socketURL, 'binary')",
            "new WebSocket(window.__matgateRewriteWebSocketUrl ? window.__matgateRewriteWebSocketUrl(socketURL) : socketURL, 'binary')",
            StringComparison.Ordinal);
        script = NoVncRfbUrlArgumentRegex.Replace(script, m =>
            $"{m.Groups["prefix"].Value}(window.__matgateRewriteWebSocketUrl ? window.__matgateRewriteWebSocketUrl(url.href) : url.href){m.Groups["suffix"].Value}");
        script = JavaScriptModuleSpecifierRegex.Replace(script, m =>
        {
            var rewrittenUrl = RewriteUrl(m.Groups["value"].Value, session, responseUri);
            return $"{m.Groups["prefix"].Value}{m.Groups["quote"].Value}{rewrittenUrl}{m.Groups["quote"].Value}";
        });

        return script;
    }

    private string RewriteSrcSet(string value, WebsiteProxySession session, Uri responseUri)
    {
        var items = value
            .Split(',')
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item =>
            {
                var firstSpace = item.IndexOfAny([' ', '\t']);
                var url = firstSpace < 0 ? item : item[..firstSpace];
                var descriptor = firstSpace < 0 ? "" : item[firstSpace..];
                return $"{RewriteUrl(url, session, responseUri)}{descriptor}";
            });

        return string.Join(", ", items);
    }

    private string RewriteUrl(string? value, WebsiteProxySession session, Uri responseUri)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return value ?? "";
        }

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return value ?? "";
        }

        if (trimmed.StartsWith(session.ProxyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value ?? "";
        }

        if (MatgateProxyPathRegex.IsMatch(trimmed))
        {
            return value ?? "";
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return session.ProxyPrefix + trimmed.TrimStart('/');
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            if (MatgateProxyPathRegex.IsMatch(absolute.AbsolutePath))
            {
                return value ?? "";
            }

            if (string.Equals(absolute.GetLeftPart(UriPartial.Authority), session.TargetOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return session.ProxyPrefix + absolute.PathAndQuery.TrimStart('/') + absolute.Fragment;
            }

            return value ?? "";
        }

        if (Uri.TryCreate(responseUri, trimmed, out var resolved)
            && string.Equals(resolved.GetLeftPart(UriPartial.Authority), session.TargetOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return session.ProxyPrefix + resolved.PathAndQuery.TrimStart('/') + resolved.Fragment;
        }

        return value ?? "";
    }

    private string RewriteLocation(Uri location, WebsiteProxySession session, Uri responseUri)
    {
        var value = location.IsAbsoluteUri
            ? location.ToString()
            : new Uri(responseUri, location).ToString();

        return RewriteUrl(value, session, responseUri);
    }

    private static string RewriteSetCookie(string value, WebsiteProxySession session, bool isHttps)
    {
        var cookie = Regex.Replace(value, @"(?i);\s*Domain=[^;]+", "");
        var proxyPath = session.ProxyPrefix.TrimEnd('/');

        if (Regex.IsMatch(cookie, @"(?i);\s*Path=[^;]+"))
        {
            cookie = Regex.Replace(cookie, @"(?i)(;\s*Path=)[^;]+", $"$1{proxyPath}");
        }
        else
        {
            cookie += $"; Path={proxyPath}";
        }

        if (!isHttps)
        {
            cookie = Regex.Replace(cookie, @"(?i)(;\s*Secure\b)", "");
            cookie = Regex.Replace(cookie, @"(?i)(;\s*SameSite=None\b)", "; SameSite=Lax");
        }

        return cookie;
    }

    public static void PreventCaching(HttpResponse response)
    {
        response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
    }

    private static string DescribeCookieNames(StringValues cookieHeaders)
    {
        var names = new List<string>();
        foreach (var header in cookieHeaders)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equalsIndex = part.IndexOf('=');
                if (equalsIndex <= 0)
                {
                    continue;
                }

                var name = part[..equalsIndex].Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string InjectBootstrapScript(string html, string scriptUrl)
    {
        var scriptTag = $"""<script src="{WebUtility.HtmlEncode(scriptUrl)}"></script>""";
        var scriptIndex = html.IndexOf("proxmoxlib.js", StringComparison.OrdinalIgnoreCase);
        if (scriptIndex >= 0)
        {
            scriptIndex = html.LastIndexOf("<script", scriptIndex, StringComparison.OrdinalIgnoreCase);
        }

        if (scriptIndex < 0)
        {
            scriptIndex = html.IndexOf("pvemanagerlib.js", StringComparison.OrdinalIgnoreCase);
            if (scriptIndex >= 0)
            {
                scriptIndex = html.LastIndexOf("<script", scriptIndex, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (scriptIndex < 0)
        {
            scriptIndex = html.IndexOf("<script type=\"text/javascript\" src=", StringComparison.OrdinalIgnoreCase);
        }
        if (scriptIndex < 0)
        {
            scriptIndex = html.IndexOf("<script src=", StringComparison.OrdinalIgnoreCase);
        }

        if (scriptIndex >= 0)
        {
            return html.Insert(scriptIndex, scriptTag);
        }

        var headIndex = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headIndex >= 0)
        {
            return html.Insert(headIndex, scriptTag);
        }

        var bodyIndex = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex >= 0)
        {
            return html.Insert(bodyIndex, scriptTag);
        }

        return scriptTag + html;
    }

    private static Encoding ResolveEncoding(string contentType)
    {
        try
        {
            var parsed = MediaTypeHeaderValue.Parse(contentType);
            if (!string.IsNullOrWhiteSpace(parsed.CharSet))
            {
                return Encoding.GetEncoding(parsed.CharSet);
            }
        }
        catch
        {
            // Use UTF-8 fallback.
        }

        return Encoding.UTF8;
    }

    private static HttpContent CreateFormContent(IEnumerable<KeyValuePair<string, string>> values)
    {
        var formValues = new List<KeyValuePair<string, string>>();
        foreach (var field in values)
        {
            formValues.Add(field);
        }

        return new FormUrlEncodedContent(formValues);
    }

    private static Dictionary<string, string> NormalizeLoginForm(IFormCollection form, out string normalization)
    {
        normalization = "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form)
        {
            values[field.Key] = field.Value.ToString();
        }

        if (!values.TryGetValue("username", out var username) || string.IsNullOrWhiteSpace(username))
        {
            return values;
        }

        var atIndex = username.LastIndexOf('@');
        if (atIndex <= 0 || atIndex >= username.Length - 1)
        {
            return values;
        }

        var userPart = username[..atIndex];
        var realmPart = username[(atIndex + 1)..];
        if (string.IsNullOrWhiteSpace(userPart) || string.IsNullOrWhiteSpace(realmPart))
        {
            return values;
        }

        values["username"] = userPart;
        values["realm"] = realmPart;
        normalization = $"{username} -> {userPart}@{realmPart}";
        return values;
    }

    private void LogLoginAttempt(Uri targetUri, IReadOnlyDictionary<string, string> form, string normalization)
    {
        // Never log credential values. Only the (non-sensitive) field names, at debug level.
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var fields = string.Join(", ", form.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        var realmNormalized = !string.IsNullOrEmpty(normalization);

        _logger.LogDebug(
            "Website login attempt {TargetUri} fields=[{Fields}] realmSplit={RealmNormalized}",
            targetUri,
            fields,
            realmNormalized);
    }

    private sealed class WebsiteProxySession : IDisposable
    {
        private readonly SemaphoreSlim _authGate = new(1, 1);
        private readonly object _cookieLock = new();
        private readonly object _storageLock = new();
        private readonly Dictionary<string, StoredCookie> _cookies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _localStorage = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sessionStorage = new(StringComparer.Ordinal);
        private string? _csrfToken;
        private bool _authenticated;

        public WebsiteProxySession(ServerEndpoint server, string signature, Guid tabId)
        {
            Signature = signature;
            ProxyPrefix = BuildProxyPrefix(server.Id, tabId == Guid.Empty ? null : tabId);
            var scriptVersion = $"{server.UpdatedAt.ToUnixTimeMilliseconds()}-{BootstrapCacheKey}";
            BootstrapScriptUrl = tabId == Guid.Empty
                ? $"/website/{server.Id:D}/bootstrap.js?v={scriptVersion}"
                : $"/website/{server.Id:D}/{tabId:D}/bootstrap.js?v={scriptVersion}";
            StateSyncUrl = tabId == Guid.Empty
                ? $"/website/{server.Id:D}/state?v={scriptVersion}"
                : $"/website/{server.Id:D}/{tabId:D}/state?v={scriptVersion}";

            var normalized = ServerEndpoint.NormalizeWebsiteUrl(server.WebsiteUrl, server.Host);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("Website URL is missing or invalid.");
            }

            BaseUri = baseUri;
            TargetOrigin = baseUri.GetLeftPart(UriPartial.Authority);

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                ServerCertificateCustomValidationCallback = server.IgnoreCertificate
                    ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    : null
            };

            Handler = handler;
            Client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        public string Signature { get; }

        public string ProxyPrefix { get; }

        public Uri BaseUri { get; }

        public string TargetOrigin { get; }

        public HttpClient Client { get; }

        private HttpClientHandler Handler { get; }

        public string BootstrapScriptUrl { get; }

        public string StateSyncUrl { get; }

        public DateTimeOffset LastAccessUtc { get; private set; } = DateTimeOffset.UtcNow;

        public bool IsHealthy => Client != null;

        public bool HasCsrfToken => !string.IsNullOrWhiteSpace(_csrfToken);

        public string? CsrfToken => _csrfToken;

        public bool TryCaptureCsrfToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var candidate = value.Trim();
            var match = CsrfTokenAssignmentRegex.Match(candidate);
            if (match.Success)
            {
                candidate = match.Groups["token"].Value.Trim();
            }

            // This is called with whole HTML/JS bodies, so guard hard: a real Proxmox CSRF token is a short
            // single word (e.g. "HHMMSS:base64") with no whitespace. Anything long or containing whitespace/
            // control chars is arbitrary content and must never be stored - otherwise the whole JS blob gets
            // sent back as a CSRF header and the upstream (e.g. Synology nginx) rejects it with 400.
            if (string.IsNullOrWhiteSpace(candidate)
                || candidate.Length > 128
                || candidate.Equals("null", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("undefined", StringComparison.OrdinalIgnoreCase)
                || candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return false;
            }

            _csrfToken = candidate;
            return true;
        }

        public WebsiteStorageSnapshot GetStorageSnapshot()
        {
            lock (_storageLock)
            {
                return new WebsiteStorageSnapshot(
                    new Dictionary<string, string>(_localStorage, StringComparer.Ordinal),
                    new Dictionary<string, string>(_sessionStorage, StringComparer.Ordinal));
            }
        }

        public void ReplaceStorageSnapshot(WebsiteStorageSnapshot snapshot)
        {
            lock (_storageLock)
            {
                _localStorage.Clear();
                _sessionStorage.Clear();

                foreach (var entry in snapshot.LocalStorage)
                {
                    _localStorage[entry.Key] = entry.Value ?? "";
                }

                foreach (var entry in snapshot.SessionStorage)
                {
                    _sessionStorage[entry.Key] = entry.Value ?? "";
                }
            }
        }

        public void Touch()
        {
            LastAccessUtc = DateTimeOffset.UtcNow;
        }

        public async Task EnsureAuthenticatedAsync(ServerEndpoint server, CancellationToken cancellationToken)
        {
            if (_authenticated || string.IsNullOrWhiteSpace(server.UserName) || string.IsNullOrWhiteSpace(server.Password))
            {
                return;
            }

            await _authGate.WaitAsync(cancellationToken);
            try
            {
                if (_authenticated || string.IsNullOrWhiteSpace(server.UserName) || string.IsNullOrWhiteSpace(server.Password))
                {
                    return;
                }

                await AuthenticateAsync(server, cancellationToken);
            }
            finally
            {
                _authGate.Release();
            }
        }

        public Uri BuildTargetUri(string? proxyPath, QueryString queryString)
        {
            var relativePath = (proxyPath ?? "").TrimStart('/');
            var target = string.IsNullOrWhiteSpace(relativePath)
                ? BaseUri
                : new Uri(BaseUri, relativePath);

            if (!queryString.HasValue)
            {
                return target;
            }

            var builder = new UriBuilder(target)
            {
                Query = queryString.Value.TrimStart('?')
            };

            return builder.Uri;
        }

        public void MergeBrowserCookies(IEnumerable<string> cookieHeaders)
        {
            lock (_cookieLock)
            {
                foreach (var header in cookieHeaders)
                {
                    if (string.IsNullOrWhiteSpace(header))
                    {
                        continue;
                    }

                    foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var equalsIndex = part.IndexOf('=');
                        if (equalsIndex <= 0)
                        {
                            continue;
                        }

                        var name = part[..equalsIndex].Trim();
                        var value = part[(equalsIndex + 1)..].Trim();
                        if (string.IsNullOrWhiteSpace(name) || IsMatgateCookie(name))
                        {
                            continue;
                        }

                        StoreCookie(new StoredCookie(
                            name,
                            value,
                            "/",
                            BaseUri.Host,
                            string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                            null,
                            StoredCookieSource.Browser));
                    }
                }
            }
        }

        public void MergeResponseCookies(IEnumerable<string> setCookieHeaders, Uri responseUri)
        {
            lock (_cookieLock)
            {
                foreach (var header in setCookieHeaders)
                {
                    if (string.IsNullOrWhiteSpace(header))
                    {
                        continue;
                    }

                    if (TryParseSetCookie(header, responseUri, out var cookie))
                    {
                        if (cookie is null)
                        {
                            continue;
                        }

                        StoreCookie(cookie.Value with { Source = StoredCookieSource.Upstream });
                    }
                }
            }
        }

        public bool TryCaptureLoginResponse(byte[] body, out string loginCookieHeader)
        {
            loginCookieHeader = "";

            if (body.Length == 0)
            {
                return false;
            }

            var payload = Encoding.UTF8.GetString(body);
            if (!TryExtractTicket(payload, out var ticket, out var csrfToken))
            {
                return false;
            }

            lock (_cookieLock)
            {
                StoreCookie(new StoredCookie(
                    "PVEAuthCookie",
                    ticket,
                    "/",
                    BaseUri.Host,
                    string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                    null,
                    StoredCookieSource.Upstream));
            }

            _csrfToken = string.IsNullOrWhiteSpace(csrfToken) ? null : csrfToken;
            _authenticated = true;
            loginCookieHeader = $"PVEAuthCookie={ticket}; Path=/";
            return true;
        }

        public void ApplyCookies(HttpRequestMessage request, Uri targetUri)
        {
            var cookieHeader = GetCookieHeader(targetUri);
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return;
            }

            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        public string GetCookieHeader(Uri targetUri)
        {
            lock (_cookieLock)
            {
                return BuildCookieHeader(targetUri);
            }
        }

        public CookieContainer GetCookieContainer(Uri targetUri)
        {
            var container = new CookieContainer();
            var cookieUri = ToHttpCookieUri(targetUri);
            var now = DateTimeOffset.UtcNow;
            var path = string.IsNullOrWhiteSpace(cookieUri.AbsolutePath) ? "/" : cookieUri.AbsolutePath;

            lock (_cookieLock)
            {
                foreach (var storedCookie in _cookies.Values.Where(cookie => cookie.IsValidFor(cookieUri, path, now)))
                {
                    var cookie = new Cookie(storedCookie.Name, storedCookie.Value, storedCookie.Path)
                    {
                        Secure = storedCookie.Secure
                    };
                    if (storedCookie.ExpiresUtc.HasValue)
                    {
                        cookie.Expires = storedCookie.ExpiresUtc.Value.UtcDateTime;
                    }

                    container.Add(cookieUri, cookie);
                }
            }

            return container;
        }

        public string DescribeCookies()
        {
            lock (_cookieLock)
            {
                return string.Join(", ", _cookies.Values
                    .OrderBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(cookie => cookie.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(cookie => $"{cookie.Name}@{cookie.Path}"));
            }
        }

        public string DescribeCookiesFor(Uri targetUri)
        {
            lock (_cookieLock)
            {
                var now = DateTimeOffset.UtcNow;
                var path = string.IsNullOrWhiteSpace(targetUri.AbsolutePath) ? "/" : targetUri.AbsolutePath;
                return string.Join(", ", _cookies.Values
                    .Where(cookie => cookie.IsValidFor(targetUri, path, now))
                    .OrderByDescending(cookie => cookie.Path.Length)
                    .ThenBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(cookie => $"{cookie.Name}@{cookie.Path}"));
            }
        }

        private async Task AuthenticateAsync(ServerEndpoint server, CancellationToken cancellationToken)
        {
            var usernameCandidates = GetLoginUsernames(server.UserName);
            var loginUri = new Uri(BaseUri, "api2/json/access/ticket");

            foreach (var username in usernameCandidates)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, loginUri)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["username"] = username,
                        ["password"] = server.Password
                    })
                };
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!TryExtractTicket(payload, out var ticket, out var csrfToken))
                {
                    continue;
                }

                lock (_cookieLock)
                {
                    StoreCookie(new StoredCookie(
                        "PVEAuthCookie",
                        ticket,
                        "/",
                        BaseUri.Host,
                        string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                        null,
                        StoredCookieSource.Upstream));
                }

                _csrfToken = string.IsNullOrWhiteSpace(csrfToken) ? null : csrfToken;
                _authenticated = true;
                return;
            }
        }

        private void StoreCookie(StoredCookie cookie)
        {
            var key = CookieKey(cookie.Name, cookie.Path, cookie.Domain);
            if (_cookies.TryGetValue(key, out var existing))
            {
                if (existing.Source == StoredCookieSource.Upstream && cookie.Source == StoredCookieSource.Browser)
                {
                    return;
                }
            }

            _cookies[key] = cookie;
        }

        private string BuildCookieHeader(Uri targetUri)
        {
            var now = DateTimeOffset.UtcNow;
            var path = string.IsNullOrWhiteSpace(targetUri.AbsolutePath) ? "/" : targetUri.AbsolutePath;

            var cookies = _cookies.Values
                .Where(cookie => cookie.IsValidFor(targetUri, path, now))
                .OrderByDescending(cookie => cookie.Path.Length)
                .ThenBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                .Select(cookie => $"{cookie.Name}={cookie.Value}");

            return string.Join("; ", cookies);
        }

        private static string CookieKey(string name, string path, string? domain)
        {
            return $"{domain ?? ""}|{path}|{name}";
        }

        private static bool TryParseSetCookie(string header, Uri responseUri, out StoredCookie? cookie)
        {
            cookie = null;

            var segments = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            var first = segments[0];
            var equalsIndex = first.IndexOf('=');
            if (equalsIndex <= 0)
            {
                return false;
            }

            var name = first[..equalsIndex].Trim();
            var value = first[(equalsIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var path = "/";
            string? domain = responseUri.Host;
            var secure = string.Equals(responseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            DateTimeOffset? expiresUtc = null;

            foreach (var rawAttribute in segments.Skip(1))
            {
                var attribute = rawAttribute.Trim();
                if (attribute.Length == 0)
                {
                    continue;
                }

                var attributeEqualsIndex = attribute.IndexOf('=');
                var attributeName = (attributeEqualsIndex < 0 ? attribute : attribute[..attributeEqualsIndex]).Trim();
                var attributeValue = attributeEqualsIndex < 0 ? "" : attribute[(attributeEqualsIndex + 1)..].Trim();

                if (attributeName.Equals("Path", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attributeValue))
                {
                    path = attributeValue;
                }
                else if (attributeName.Equals("Domain", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attributeValue))
                {
                    domain = attributeValue.TrimStart('.').ToLowerInvariant();
                }
                else if (attributeName.Equals("Secure", StringComparison.OrdinalIgnoreCase))
                {
                    secure = true;
                }
                else if (attributeName.Equals("Expires", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(attributeValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedExpires))
                {
                    expiresUtc = parsedExpires.ToUniversalTime();
                }
                else if (attributeName.Equals("Max-Age", StringComparison.OrdinalIgnoreCase) && int.TryParse(attributeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge))
                {
                    expiresUtc = maxAge <= 0 ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddSeconds(maxAge);
                }
            }

            if (expiresUtc.HasValue && expiresUtc.Value <= DateTimeOffset.UtcNow)
            {
                return true;
            }

            cookie = new StoredCookie(name, value, NormalizeCookiePath(path), domain, secure, expiresUtc, StoredCookieSource.Upstream);
            return true;
        }

        private static string NormalizeCookiePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            var normalized = path.Trim();
            return normalized.StartsWith('/') ? normalized : $"/{normalized}";
        }

        private readonly record struct StoredCookie(
            string Name,
            string Value,
            string Path,
            string? Domain,
            bool Secure,
            DateTimeOffset? ExpiresUtc,
            StoredCookieSource Source)
        {
            public bool IsValidFor(Uri targetUri, string targetPath, DateTimeOffset now)
            {
                if (ExpiresUtc.HasValue && ExpiresUtc.Value <= now)
                {
                    return false;
                }

                if (Secure && !string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!MatchesDomain(targetUri.Host, Domain))
                {
                    return false;
                }

                return MatchesPath(targetPath, Path);
            }

            private static bool MatchesDomain(string host, string? domain)
            {
                if (string.IsNullOrWhiteSpace(domain))
                {
                    return true;
                }

                if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
            }

            private static bool MatchesPath(string targetPath, string cookiePath)
            {
                if (string.IsNullOrWhiteSpace(cookiePath) || cookiePath == "/")
                {
                    return true;
                }

                if (!targetPath.StartsWith(cookiePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (targetPath.Length == cookiePath.Length)
                {
                    return true;
                }

                if (cookiePath.EndsWith("/", StringComparison.Ordinal))
                {
                    return true;
                }

                return targetPath[cookiePath.Length] == '/';
            }
        }

        private enum StoredCookieSource
        {
            Browser = 0,
            Upstream = 1
        }

        private static IEnumerable<string> GetLoginUsernames(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                yield break;
            }

            yield return userName;

            if (userName.Contains('@', StringComparison.Ordinal))
            {
                yield break;
            }

            yield return $"{userName}@pam";
        }

        private static bool TryExtractTicket(string payload, out string ticket, out string csrfToken)
        {
            ticket = "";
            csrfToken = "";

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var data = root.TryGetProperty("data", out var nestedData) ? nestedData : root;

                if (data.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (data.TryGetProperty("ticket", out var ticketProperty))
                {
                    ticket = ticketProperty.GetString() ?? "";
                }

                if (data.TryGetProperty("CSRFPreventionToken", out var csrfProperty))
                {
                    csrfToken = csrfProperty.GetString() ?? "";
                }

                return !string.IsNullOrWhiteSpace(ticket);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _authGate.Dispose();
            Client.Dispose();
            Handler.Dispose();
        }
    }
}
