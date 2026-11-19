using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Matgate.Models;

namespace Matgate.Services;

// A website under an address of its own.
//
// The other way to show a website (WebsiteProxyService) serves it under a path of Matgate's own address,
// /website/<id>/<tab>/proxy/, and rewrites every address in its pages to fit. That works for pages that
// only link to themselves. It cannot work for an application that reads its own address: one that takes
// its route from location.pathname, or sends the browser to "/login" - the path is Matgate's there, not
// the application's, and a page cannot be lied to about where it is.
//
// Here the website gets a host name instead - <label>.web.example.com, from the pattern in
// MATGATE_WEBSITE_HOST -, and everything under that name IS the website: its paths are its own, its
// cookies and its storage are the browser's own for that address, and nothing in its pages has to be
// rewritten. It also keeps the website out of Matgate's origin: its scripts cannot read Matgate's pages.
//
// The label says which user and which connection; a cookie for that host, handed out against a one-time
// ticket from the shell, says that the browser is that user's.
public sealed class WebsiteSiteService(ILogger<WebsiteSiteService> logger)
{
    public const string CookieName = "Matgate-Site";
    // Says that this browser has had its automatic sign-in at the website (see TrySignInAsync).
    public const string SignInCookieName = "Matgate-SignIn";
    public const string Reserved = "/__matgate/";
    public const string EnterPath = Reserved + "enter";
    public const string ScriptPath = Reserved + "site.js";

    private const int LabelLength = 20;
    private const int MaxHtmlBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan TicketLife = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleLife = TimeSpan.FromHours(12);
    private static readonly Encoding Bytes = Encoding.Latin1;

    private static readonly (string Prefix, string Suffix)? Pattern =
        ParsePattern(Environment.GetEnvironmentVariable("MATGATE_WEBSITE_HOST"));

    private static readonly Regex HeadTag = new(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"<html(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Doctype = new(@"<!doctype[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LinkAttribute = new(@"(?<attribute>\s(?:href|src|action|formaction|poster)\s*=\s*[""'])(?<value>https?://[^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Nonce = new(@"'nonce-([A-Za-z0-9+/_=-]+)'", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CookieDomain = new(@";\s*Domain=[^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CookieSecure = new(@";\s*Secure\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CookieSameSiteNone = new(@";\s*SameSite=None\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> RequestHeadersNotSent = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host", "Content-Length", "Accept-Encoding", "Cookie", "Origin", "Referer",
        // Where Matgate is reached from is nothing the website has to know.
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port", "X-Real-IP", "Via",
    };

    private static readonly HashSet<string> ResponseHeadersNotSent = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Content-Length", "Set-Cookie", "Location", "Content-Security-Policy",
        // Shown in a frame of Matgate's: what forbids that goes. So does what would bind this host name, which
        // is Matgate's, to what the website says about its own.
        "X-Frame-Options", "Content-Security-Policy-Report-Only", "Strict-Transport-Security", "Public-Key-Pins",
        "Alt-Svc", "Clear-Site-Data",
    };

    private readonly ConcurrentDictionary<string, Site> _sites = new(StringComparer.Ordinal);

    // Whether websites can have an address of their own here at all.
    public static bool Available => Pattern is not null;

    // What MATGATE_WEBSITE_HOST says and cannot be used (for the log at start); empty when it is fine or unset.
    public static string PatternProblem
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("MATGATE_WEBSITE_HOST");
            return string.IsNullOrWhiteSpace(configured) || Pattern is not null
                ? ""
                : $"MATGATE_WEBSITE_HOST \"{configured}\" is not a host name with one * in its first part (like *.web.example.com or web-*.example.com); websites open under Matgate's own address.";
        }
    }

    public static string PatternText => Pattern is { } pattern ? pattern.Prefix + "*" + pattern.Suffix : "";

    internal static (string Prefix, string Suffix)? ParsePattern(string? configured)
    {
        var text = (configured ?? "").Trim().ToLowerInvariant();
        var star = text.IndexOf('*');
        var dot = text.IndexOf('.');
        if (star < 0 || star != text.LastIndexOf('*') || dot < 0 || star > dot
            || text.Any(character => character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '*'))
            || text.EndsWith('.') || text.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return (text[..star], text[(star + 1)..]);
    }

    // The label in a host name that is a website's; null for every other host (Matgate's own among them).
    public static string? LabelOf(HostString host)
    {
        if (Pattern is not { } pattern)
        {
            return null;
        }

        var name = host.Host.ToLowerInvariant();
        if (name.Length != pattern.Prefix.Length + LabelLength + pattern.Suffix.Length
            || !name.StartsWith(pattern.Prefix, StringComparison.Ordinal)
            || !name.EndsWith(pattern.Suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var label = name.Substring(pattern.Prefix.Length, LabelLength);
        return label.All(character => character is (>= 'a' and <= 'z') or (>= '2' and <= '7')) ? label : null;
    }

    // The address the browser reaches a website's host under: as it reached Matgate, with the other name.
    public static string PublicOrigin(HttpContext context, string label)
    {
        var pattern = Pattern ?? throw new InvalidOperationException("Websites have no host names here.");
        var port = context.Request.Host.Port is { } number ? ":" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return $"{context.Request.Scheme}://{pattern.Prefix}{label}{pattern.Suffix}{port}";
    }

    public sealed class Site : IDisposable
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _tickets = new(StringComparer.Ordinal);

        internal Site(string label, Guid userId, ServerEndpoint server, Uri origin, string signature)
        {
            Label = label;
            UserId = userId;
            ServerId = server.Id;
            Origin = origin.GetLeftPart(UriPartial.Authority);
            Signature = signature;
            Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(20),
                SslOptions = server.IgnoreCertificate
                    ? new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
                    : new System.Net.Security.SslClientAuthenticationOptions(),
            })
            {
                // A download or an event stream may take as long as it takes; what must not hang is the
                // connecting, and that has its own limit above.
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public string Label { get; }

        public Guid UserId { get; }

        public Guid ServerId { get; }

        // The website's own origin, like "https://10.0.0.1:8443".
        public string Origin { get; }

        // The value of the cookie that lets a browser in.
        public string Secret { get; }

        // Where the shell that shows this website lives: the only window its pages report to.
        public string ShellOrigin { get; set; } = "";

        internal string Signature { get; }

        internal HttpClient Client { get; }

        // The website asked for a name and password (Basic) and the connection has them: they go with every request.
        internal volatile bool Basic;

        // What a browser keeps (in the cookie SignInCookieName) once the stored name and password were tried
        // for it on the sign-in this kind of website has. Each browser gets one try, not one for all of them.
        internal string SignInMark { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

        internal DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;

        // A ticket the shell puts into the address it opens: good once, and not for long.
        public string Ticket()
        {
            foreach (var old in _tickets.Where(entry => entry.Value < DateTimeOffset.UtcNow).ToList())
            {
                _tickets.TryRemove(old.Key, out _);
            }

            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _tickets[ticket] = DateTimeOffset.UtcNow + TicketLife;
            return ticket;
        }

        public bool Redeem(string? ticket) =>
            !string.IsNullOrEmpty(ticket) && _tickets.TryRemove(ticket, out var until) && until >= DateTimeOffset.UtcNow;

        public void Dispose() => Client.Dispose();
    }

    // The website of this connection for this user - the one that is there, or a new one. `origin` is for a
    // second address of the same machine (the same host under another scheme or port).
    public Site Open(Guid userId, ServerEndpoint server, Uri? origin = null)
    {
        var target = origin ?? StartUri(server) ?? throw new InvalidOperationException("Website URL is missing or invalid.");
        var authority = target.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var label = LabelFor(userId, server.Id, authority);
        var signature = string.Join("|", authority, server.IgnoreCertificate ? "1" : "0", server.UserName, server.Password);

        Sweep();
        while (true)
        {
            if (_sites.TryGetValue(label, out var existing))
            {
                if (existing.Signature == signature)
                {
                    existing.LastUsed = DateTimeOffset.UtcNow;
                    return existing;
                }

                // The connection was changed: what was open under the old settings ends.
                if (_sites.TryRemove(new KeyValuePair<string, Site>(label, existing)))
                {
                    existing.Dispose();
                }

                continue;
            }

            var created = new Site(label, userId, server, target, signature);
            if (_sites.TryAdd(label, created))
            {
                return created;
            }

            created.Dispose();
        }
    }

    public bool TryGet(string label, out Site site) => _sites.TryGetValue(label, out site!);

    public void ForgetServer(Guid serverId) => Forget(site => site.ServerId == serverId);

    public void ForgetUser(Guid userId) => Forget(site => site.UserId == userId);

    private void Forget(Func<Site, bool> which)
    {
        foreach (var entry in _sites.Where(entry => which(entry.Value)).ToList())
        {
            if (_sites.TryRemove(entry))
            {
                entry.Value.Dispose();
            }
        }
    }

    private void Sweep() => Forget(site => site.LastUsed + IdleLife < DateTimeOffset.UtcNow);

    public static Uri? StartUri(ServerEndpoint server) =>
        ParseAddress(string.IsNullOrWhiteSpace(server.WebsiteUrl) ? server.Host : server.WebsiteUrl);

    // A web address as someone wrote it - with https in front where no scheme was given, and otherwise
    // untouched: a path keeps its last character, slash or not.
    public static Uri? ParseAddress(string? written)
    {
        var text = (written ?? "").Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && uri.Host.Length > 0
            ? uri
            : null;
    }

    // The same for the same user, connection and address every time: what the browser keeps for a website
    // (its sign-in, its settings) is kept under this name.
    private static string LabelFor(Guid userId, Guid serverId, string authority)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"matgate-site|{userId:N}|{serverId:N}|{authority}"));
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var label = new StringBuilder(LabelLength);
        for (var index = 0; index < LabelLength; index++)
        {
            label.Append(alphabet[hash[index] & 31]);
        }

        return label.ToString();
    }

    // ---- a request for the website ------------------------------------------------------------------------

    public async Task ProxyAsync(HttpContext context, Site site, ServerEndpoint server)
    {
        site.LastUsed = DateTimeOffset.UtcNow;
        var cancellation = context.RequestAborted;

        // As the browser sent it: a path that was escaped stays escaped the same way.
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || raw[0] != '/')
        {
            raw = (context.Request.Path.Value ?? "/") + context.Request.QueryString.Value;
        }

        if (!Uri.TryCreate(site.Origin + raw, UriKind.Absolute, out var target)
            || !string.Equals(target.GetLeftPart(UriPartial.Authority), site.Origin, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await RelayWebSocketAsync(context, site, server, target);
            return;
        }

        var hasCredentials = !string.IsNullOrEmpty(server.UserName) && !string.IsNullOrEmpty(server.Password);
        HttpResponseMessage response;
        try
        {
            var request = BuildRequest(context, site, server, target, hasCredentials);
            response = await site.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);

            // The website wants a name and password the way a browser would ask for in a dialog, and the
            // connection has them: they are sent, here and from now on.
            if (response.StatusCode == HttpStatusCode.Unauthorized
                && hasCredentials
                && !site.Basic
                && request.Content is null
                && !context.Request.Headers.ContainsKey("Authorization")
                && response.Headers.WwwAuthenticate.Any(challenge => challenge.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)))
            {
                site.Basic = true;
                response.Dispose();
                request = BuildRequest(context, site, server, target, hasCredentials);
                response = await site.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Website {Server}: {Method} {Target} failed.", server.Name, context.Request.Method, target);
            await WriteUnreachableAsync(context, server, target, exception);
            return;
        }

        using (response)
        {
            var publicIsHttps = context.Request.IsHttps;
            context.Response.StatusCode = (int)response.StatusCode;

            var nonce = "";
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
                {
                    var policies = header.Value.Select(policy => WithoutFrameAncestors(policy)).Where(policy => policy.Length > 0).ToArray();
                    nonce = policies.Select(policy => Nonce.Match(policy)).FirstOrDefault(match => match.Success)?.Groups[1].Value ?? "";
                    if (policies.Length > 0)
                    {
                        TrySetHeader(context.Response, header.Key, policies);
                    }

                    continue;
                }

                if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var cookie in header.Value)
                    {
                        if (RewriteSetCookie(cookie, publicIsHttps) is { } rewritten)
                        {
                            context.Response.Headers.Append("Set-Cookie", rewritten);
                        }
                    }

                    continue;
                }

                if (header.Key.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase))
                {
                    var mine = context.Request.Scheme + "://" + context.Request.Host;
                    TrySetHeader(context.Response, header.Key, header.Value.Select(value =>
                        value.Equals(site.Origin, StringComparison.OrdinalIgnoreCase) ? mine : value).ToArray());
                    continue;
                }

                if (!ResponseHeadersNotSent.Contains(header.Key))
                {
                    TrySetHeader(context.Response, header.Key, header.Value.ToArray());
                }
            }

            if (response.Headers.Location is { } location)
            {
                context.Response.Headers.Location = MapLocation(context, site, server, target, location);
            }

            if (HttpMethods.IsHead(context.Request.Method)
                || response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified or HttpStatusCode.ResetContent
                || (int)response.StatusCode < 200)
            {
                if (HttpMethods.IsHead(context.Request.Method) && response.Content.Headers.ContentLength is { } headLength)
                {
                    context.Response.ContentLength = headLength;
                }

                return;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            var isDocument = HttpMethods.IsGet(context.Request.Method)
                && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                && response.Content.Headers.ContentLength is null or <= MaxHtmlBytes
                && response.Content.Headers.ContentEncoding.Count == 0;

            await using var upstream = await response.Content.ReadAsStreamAsync(cancellation);
            try
            {
                if (isDocument)
                {
                    await WriteDocumentAsync(context, site, server, target, upstream, nonce, hasCredentials);
                    return;
                }

                if (response.Content.Headers.ContentLength is { } length)
                {
                    context.Response.ContentLength = length;
                }

                await CopyAsync(upstream, context.Response, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The browser went away.
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                // The website stopped sending in the middle; the browser sees the response end.
                logger.LogDebug(exception, "Website {Server}: {Target} ended early.", server.Name, target);
                context.Abort();
            }
        }
    }

    private HttpRequestMessage BuildRequest(HttpContext context, Site site, ServerEndpoint server, Uri target, bool hasCredentials)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var method = context.Request.Method;
        var hasBody = context.Request.ContentLength is > 0
            || context.Request.Headers.ContainsKey("Transfer-Encoding")
            || (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) && !HttpMethods.IsDelete(method)
                && context.Request.ContentLength is null && context.Request.Headers.ContainsKey("Content-Type"));
        if (hasBody)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentLength is { } length)
            {
                request.Content.Headers.ContentLength = length;
            }
        }

        var mine = context.Request.Scheme + "://" + context.Request.Host;
        foreach (var header in context.Request.Headers)
        {
            if (RequestHeadersNotSent.Contains(header.Key) || header.Key.StartsWith(':'))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        // The website sees its own address wherever the browser named this one.
        if (context.Request.Headers.TryGetValue("Origin", out var origin) && origin.Count > 0)
        {
            var value = origin.ToString();
            request.Headers.TryAddWithoutValidation("Origin", value.Equals(mine, StringComparison.OrdinalIgnoreCase) ? site.Origin : value);
        }

        if (context.Request.Headers.TryGetValue("Referer", out var referer)
            && referer.ToString() is { } from
            && from.StartsWith(mine, StringComparison.OrdinalIgnoreCase)
            && (from.Length == mine.Length || from[mine.Length] is '/' or '?' or '#'))
        {
            request.Headers.TryAddWithoutValidation("Referer", site.Origin + from[mine.Length..]);
        }

        if (WebsiteCookies(context.Request) is { Length: > 0 } cookies)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        if (site.Basic && hasCredentials && !request.Headers.Contains("Authorization"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(server.UserName + ":" + server.Password)));
        }

        return request;
    }

    // The browser's cookies for this host, without the one that is Matgate's.
    private static string WebsiteCookies(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Cookie", out var headers))
        {
            return "";
        }

        var kept = new List<string>();
        foreach (var header in headers)
        {
            foreach (var part in (header ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = part.IndexOf('=');
                var name = equals < 0 ? part : part[..equals];
                if (!IsOwnCookie(name))
                {
                    kept.Add(part);
                }
            }
        }

        return string.Join("; ", kept);
    }

    private static bool IsOwnCookie(string name) =>
        name.Trim().Equals(CookieName, StringComparison.Ordinal) || name.Trim().Equals(SignInCookieName, StringComparison.Ordinal);

    private static string? RewriteSetCookie(string cookie, bool publicIsHttps)
    {
        var equals = cookie.IndexOf('=');
        if (equals > 0 && IsOwnCookie(cookie[..equals]))
        {
            // The names a website cannot set here.
            return null;
        }

        // The cookie is for this host and no wider: not for the website's own name (which this is not), and
        // not for a domain that other hosts share.
        cookie = CookieDomain.Replace(cookie, "");
        if (publicIsHttps)
        {
            // Browsers drop SameSite=None without Secure; a website reached over plain HTTP cannot say Secure.
            if (CookieSameSiteNone.IsMatch(cookie) && !CookieSecure.IsMatch(cookie))
            {
                cookie += "; Secure";
            }
        }
        else
        {
            cookie = CookieSecure.Replace(cookie, "");
            cookie = CookieSameSiteNone.Replace(cookie, "; SameSite=Lax");
        }

        return cookie;
    }

    private static string WithoutFrameAncestors(string policy) =>
        string.Join("; ", policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(directive => !directive.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase)));

    private static void TrySetHeader(HttpResponse response, string name, string[] values)
    {
        try
        {
            response.Headers[name] = values;
        }
        catch (InvalidOperationException)
        {
            // A header the web server does not let through (a character it refuses): the page goes without it.
        }
    }

    // Where a redirect leads: within the website it stays a path; to the same machine under another scheme
    // or port (http to https, mostly) it leads to that address's own host name; anywhere else it is left.
    private string MapLocation(HttpContext context, Site site, ServerEndpoint server, Uri target, Uri location)
    {
        var absolute = location.IsAbsoluteUri ? location : new Uri(target, location);
        var authority = absolute.GetLeftPart(UriPartial.Authority);
        var rest = absolute.PathAndQuery + absolute.Fragment;
        if (authority.Equals(site.Origin, StringComparison.OrdinalIgnoreCase))
        {
            return rest;
        }

        return Reaches(site, server, absolute) ? EnterAddress(context, site, server, absolute) : location.OriginalString;
    }

    // Whether this website may lead to that address through Matgate: its own machine under another scheme or
    // port (http to https, mostly) always; any address at all when the tab is a browser.
    public static bool Reaches(Site site, ServerEndpoint server, Uri address) =>
        address.IsAbsoluteUri
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
        && (server.WebsiteBrowse || address.Host.Equals(new Uri(site.Origin).Host, StringComparison.OrdinalIgnoreCase));

    // The address that lets the browser into the website of that address and leads on to the page it names.
    public string EnterAddress(HttpContext context, Site from, ServerEndpoint server, Uri address)
    {
        var site = Open(from.UserId, server, address);
        site.ShellOrigin = from.ShellOrigin;
        return EnterAddress(context, site, address.PathAndQuery + address.Fragment);
    }

    public static string EnterAddress(HttpContext context, Site site, string path) =>
        PublicOrigin(context, site.Label) + EnterPath + "?t=" + site.Ticket() + "&to=" + Uri.EscapeDataString(path);

    private static async Task CopyAsync(Stream upstream, HttpResponse response, CancellationToken cancellation)
    {
        // Sent on as it arrives: an event stream or a slow download must not wait in a buffer here.
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await upstream.ReadAsync(buffer, cancellation)) > 0)
        {
            await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellation);
            await response.Body.FlushAsync(cancellation);
        }
    }

    // A page: the one thing that is changed on its way. It gets a small script of Matgate's (so the shell
    // knows where the page is and its buttons work), and addresses that name the website's real host are
    // turned into paths - the browser could not reach that host.
    private async Task WriteDocumentAsync(HttpContext context, Site site, ServerEndpoint server, Uri target, Stream upstream, string nonce, bool hasCredentials)
    {
        var cancellation = context.RequestAborted;
        using var buffered = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        var complete = true;
        while ((read = await upstream.ReadAsync(buffer, cancellation)) > 0)
        {
            buffered.Write(buffer, 0, read);
            if (buffered.Length > MaxHtmlBytes)
            {
                complete = false;
                break;
            }
        }

        if (!complete)
        {
            // Too large to be a page worth reading: it goes through as it is.
            await context.Response.Body.WriteAsync(buffered.GetBuffer().AsMemory(0, (int)buffered.Length), cancellation);
            await CopyAsync(upstream, context.Response, cancellation);
            return;
        }

        // Read byte for byte: whatever the page's character set, only plain ASCII is looked for and added.
        var html = Bytes.GetString(buffered.GetBuffer(), 0, (int)buffered.Length);
        if (LooksLikeDocument(html))
        {
            if (hasCredentials && await TrySignInAsync(context, site, server, html))
            {
                return;
            }

            html = LinkAttribute.Replace(html, match =>
                match.Groups["value"].Value.StartsWith(site.Origin + "/", StringComparison.OrdinalIgnoreCase)
                    ? match.Groups["attribute"].Value + match.Groups["value"].Value[site.Origin.Length..]
                    : match.Value);
            html = InjectScript(html, nonce);
        }

        var body = Bytes.GetBytes(html);
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, cancellation);
    }

    private static bool LooksLikeDocument(string html)
    {
        var head = html.AsSpan(0, Math.Min(html.Length, 4096));
        return head.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<head", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static string InjectScript(string html, string nonce)
    {
        var tag = $"<script src=\"{ScriptPath}\"{(nonce.Length > 0 ? $" nonce=\"{nonce}\"" : "")}></script>";
        foreach (var anchor in new[] { HeadTag, HtmlTag, Doctype })
        {
            var match = anchor.Match(html);
            if (match.Success)
            {
                return html.Insert(match.Index + match.Length, tag);
            }
        }

        return tag + html;
    }

    // Proxmox (VE and Backup Server) signs in with a ticket that lives in a cookie. With a name and password
    // in the connection the ticket is fetched here and the page is asked for again with it - so the user lands
    // in the application instead of at its sign-in dialog. Once for each browser: the ticket lives in the
    // browser, so a second one (the phone, after the desktop) needs its own - while a browser that had its
    // try is left alone, or whoever signs out there on purpose would be signed in again at once, and a
    // browser that does not keep the ticket would be sent round in a circle.
    private async Task<bool> TrySignInAsync(HttpContext context, Site site, ServerEndpoint server, string html)
    {
        var cookieName = html.Contains("pvemanagerlib.js", StringComparison.OrdinalIgnoreCase) ? "PVEAuthCookie"
            : html.Contains("proxmox-backup-gui.js", StringComparison.OrdinalIgnoreCase) ? "PBSAuthCookie"
            : "";
        if (cookieName.Length == 0
            || context.Request.Cookies.ContainsKey(cookieName)
            || string.Equals(context.Request.Cookies[SignInCookieName], site.SignInMark, StringComparison.Ordinal))
        {
            return false;
        }

        context.Response.Cookies.Append(SignInCookieName, site.SignInMark, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        });
        var names = server.UserName.Contains('@') ? new[] { server.UserName } : new[] { server.UserName + "@pam", server.UserName + "@pve", server.UserName + "@pbs" };
        foreach (var name in names)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, site.Origin + "/api2/json/access/ticket")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = name, ["password"] = server.Password }),
                };
                using var response = await site.Client.SendAsync(request, context.RequestAborted);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
                if (document.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("ticket", out var ticket)
                    && ticket.GetString() is { Length: > 0 } value)
                {
                    // As the application's own sign-in writes it: escaped, for the whole host, readable by its scripts.
                    context.Response.Headers.Append("Set-Cookie",
                        $"{cookieName}={Uri.EscapeDataString(value)}; Path=/; SameSite=Lax{(context.Request.IsHttps ? "; Secure" : "")}");
                    context.Response.Headers.Remove("Content-Type");
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location = (context.Request.Path.Value ?? "/") + context.Request.QueryString.Value;
                    return true;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                logger.LogDebug(exception, "Website {Server}: signing in with the stored name failed.", server.Name);
                return false;
            }
        }

        return false;
    }

    private static async Task WriteUnreachableAsync(HttpContext context, ServerEndpoint server, Uri target, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        var german = Matgate.Web.HtmlViews.Language(context) == "de";
        var certificate = exception.InnerException is System.Security.Authentication.AuthenticationException
            || exception.ToString().Contains("certificate", StringComparison.OrdinalIgnoreCase);
        var title = german ? "Die Website ist nicht erreichbar" : "The website cannot be reached";
        var reason = certificate
            ? (german
                ? "Ihr Zertifikat wird nicht anerkannt. Bei einem selbst ausgestellten Zertifikat (Router, NAS, Proxmox) hilft „Zertifikat ignorieren“ in der Verbindung."
                : "Its certificate is not trusted. For a self-signed certificate (router, NAS, Proxmox) switch on \"Ignore certificate\" in the connection.")
            : (german
                ? "Matgate hat keine Antwort bekommen. Läuft das Gerät, und stimmen Adresse und Port in der Verbindung?"
                : "Matgate got no answer. Is the device running, and are the address and port in the connection right?");
        var again = german ? "Noch einmal versuchen" : "Try again";
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; font: 16px/1.5 system-ui, sans-serif; background: #f4f5f7; color: #1d2330; }
            main { max-width: 34rem; margin: 1.5rem; padding: 1.5rem 1.75rem; background: #fff; border-radius: 14px; box-shadow: 0 6px 24px rgba(20, 30, 50, .08); }
            h1 { margin: 0 0 .5rem; font-size: 1.25rem; } p { margin: .5rem 0; } code { word-break: break-all; color: #55607a; }
            button { margin-top: .75rem; padding: .55rem 1rem; border: 0; border-radius: 10px; background: #2f6fed; color: #fff; font: inherit; cursor: pointer; }
            @media (prefers-color-scheme: dark) { body { background: #14171d; color: #e7eaf0; } main { background: #1d222b; } code { color: #9aa6bf; } }
            </style></head><body><main id="matgate-site-unreachable">
            <h1>{{WebUtility.HtmlEncode(title)}}</h1>
            <p>{{WebUtility.HtmlEncode(reason)}}</p>
            <p><code>{{WebUtility.HtmlEncode(server.Name)}} - {{WebUtility.HtmlEncode(target.GetLeftPart(UriPartial.Path))}}</code></p>
            <button type="button" onclick="location.reload()">{{WebUtility.HtmlEncode(again)}}</button>
            </main></body></html>
            """, context.RequestAborted);
    }

    // ---- web sockets ----------------------------------------------------------------------------------------

    private async Task RelayWebSocketAsync(HttpContext context, Site site, ServerEndpoint server, Uri target)
    {
        var address = new UriBuilder(target) { Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        using var upstream = new ClientWebSocket();
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
        {
            upstream.Options.AddSubProtocol(protocol);
        }

        if (server.IgnoreCertificate)
        {
            upstream.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        void Header(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                try
                {
                    upstream.Options.SetRequestHeader(name, value);
                }
                catch (ArgumentException)
                {
                    // One of the few headers the socket sets itself.
                }
            }
        }

        Header("Cookie", WebsiteCookies(context.Request));
        Header("Origin", site.Origin);
        Header("User-Agent", context.Request.Headers.UserAgent.ToString());
        Header("Accept-Language", context.Request.Headers.AcceptLanguage.ToString());
        if (site.Basic && !string.IsNullOrEmpty(server.UserName))
        {
            Header("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(server.UserName + ":" + server.Password)));
        }

        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            connecting.CancelAfter(TimeSpan.FromSeconds(20));
            await upstream.ConnectAsync(address, connecting.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException or HttpRequestException)
        {
            logger.LogWarning(exception, "Website {Server}: web socket to {Target} failed.", server.Name, address);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var browser = await context.WebSockets.AcceptWebSocketAsync(string.IsNullOrEmpty(upstream.SubProtocol) ? null : upstream.SubProtocol);
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var up = PumpAsync(browser, upstream, relay.Token);
        var down = PumpAsync(upstream, browser, relay.Token);
        await Task.WhenAny(up, down);
        relay.Cancel();
        try
        {
            await Task.WhenAll(up, down);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException)
        {
            // One side closed; the other was told to stop.
        }
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
        {
            var result = await from.ReceiveAsync(buffer.AsMemory(), cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await to.CloseOutputAsync(from.CloseStatus ?? WebSocketCloseStatus.NormalClosure, from.CloseStatusDescription, CancellationToken.None);
                }

                return;
            }

            await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellation);
        }
    }

    // ---- the script every page of a website gets ----------------------------------------------------------------

    // Small on purpose. The page is at its own address, so nothing of it has to be bent - except requests
    // that name the website's real host in full, which the browser could not reach. The rest is for the
    // shell: where the page is and what it is called, and its back, forward and reload buttons.
    public static string Script(Site site, bool browse) => ScriptSource
        .Replace("__BROWSE__", browse ? "true" : "false", StringComparison.Ordinal)
        .Replace("__TARGET__", JsonSerializer.Serialize(site.Origin), StringComparison.Ordinal)
        .Replace("__SHELL__", JsonSerializer.Serialize(site.ShellOrigin), StringComparison.Ordinal);

    private const string ScriptSource = """
        (() => {
            if (window.__matgateSite) { return; }
            window.__matgateSite = true;
            const target = __TARGET__;
            const shell = __SHELL__;
            const browse = __BROWSE__;
            const framed = window.parent !== window;
            const here = location.origin;
            const socketTarget = target.replace(/^http/i, 'ws');
            const socketHere = here.replace(/^http/i, 'ws');

            function swap(value, from, to) {
                if (typeof value !== 'string' || value.length < from.length) { return value; }
                if (value.slice(0, from.length).toLowerCase() !== from.toLowerCase()) { return value; }
                const next = value.charAt(from.length);
                return next === '' || next === '/' || next === '?' || next === '#' ? to + value.slice(from.length) : value;
            }
            function mapped(value) {
                if (value instanceof URL) { return swap(value.href, target, here); }
                return swap(value, target, here);
            }
            function mappedSocket(value) {
                const text = value instanceof URL ? value.href : value;
                return swap(swap(text, socketTarget, socketHere), target, here);
            }

            const nativeFetch = window.fetch;
            if (typeof nativeFetch === 'function') {
                window.fetch = function (input, init) {
                    try {
                        if (typeof Request === 'function' && input instanceof Request) {
                            const address = mapped(input.url);
                            if (address !== input.url) { input = new Request(address, input); }
                        }
                        else {
                            input = mapped(input);
                        }
                    }
                    catch (error) { /* the request goes out as it was written */ }
                    return nativeFetch.call(this, input, init);
                };
            }

            const nativeOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function (method, address) {
                const rest = Array.prototype.slice.call(arguments, 2);
                return nativeOpen.apply(this, [method, mapped(address)].concat(rest));
            };

            function wrap(name, map) {
                const Native = window[name];
                if (typeof Native !== 'function') { return; }
                const Wrapped = function (address, options) {
                    return arguments.length > 1 ? new Native(map(address), options) : new Native(map(address));
                };
                Wrapped.prototype = Native.prototype;
                for (const key of ['CONNECTING', 'OPEN', 'CLOSING', 'CLOSED']) {
                    if (key in Native) { Wrapped[key] = Native[key]; }
                }
                window[name] = Wrapped;
            }
            wrap('WebSocket', mappedSocket);
            wrap('EventSource', mapped);

            // Another address that Matgate leads to as well: any, in a browser tab; otherwise this website's
            // own machine under another scheme or port.
            const machine = new URL(target).hostname.toLowerCase();
            function through(absolute) {
                return /^https?:$/.test(absolute.protocol) && absolute.origin !== here
                    && (browse || absolute.hostname.toLowerCase() === machine);
            }
            function elsewhere(address) {
                try {
                    const absolute = new URL(address, location.href);
                    if (through(absolute)) {
                        return '/__matgate/go?u=' + encodeURIComponent(absolute.href);
                    }
                }
                catch (error) { /* not an address */ }
                return address;
            }

            const nativeWindowOpen = window.open;
            window.open = function (address) {
                const rest = Array.prototype.slice.call(arguments, 1);
                const next = typeof address === 'string' || address instanceof URL ? elsewhere(mapped(address)) : address;
                return nativeWindowOpen.apply(window, [next].concat(rest));
            };

            // Links. One that names the website's real host in full stays in this website. One that leads to
            // another website goes through Matgate in a browser tab, and into a browser tab of its own
            // otherwise - a frame inside Matgate is no place for a page that is not this connection's. And no
            // link replaces Matgate itself (target _top), which a frame may do after a click.
            document.addEventListener('click', event => {
                if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) { return; }
                const link = event.target instanceof Element ? event.target.closest('a[href], area[href]') : null;
                if (!link || link.hasAttribute('download')) { return; }
                let address;
                try { address = new URL(link.href); } catch (error) { return; }
                if (!/^https?:$/.test(address.protocol)) { return; }
                const top = /^_(top|parent)$/i.test(link.target || '');
                if (address.origin === here) {
                    if (top && framed && window.parent === window.top) { link.target = '_self'; }
                    return;
                }
                const blank = !!link.target && !/^_(self|top|parent)$/i.test(link.target);
                if (address.origin.toLowerCase() === target.toLowerCase()) {
                    event.preventDefault();
                    const inside = here + address.pathname + address.search + address.hash;
                    if (blank) { nativeWindowOpen.call(window, inside, link.target); } else { location.href = inside; }
                    return;
                }
                if (through(address)) {
                    event.preventDefault();
                    const next = elsewhere(address.href);
                    if (blank) { nativeWindowOpen.call(window, next, link.target); } else { location.href = next; }
                    return;
                }
                if (framed && !blank) {
                    event.preventDefault();
                    nativeWindowOpen.call(window, address.href, '_blank', 'noopener');
                }
            }, true);
            document.addEventListener('submit', event => {
                const form = event.target;
                if (form instanceof HTMLFormElement && /^_(top|parent)$/i.test(form.target || '') && framed && window.parent === window.top) {
                    form.target = '_self';
                }
            }, true);

            // ---- the shell around this page
            if (!framed || !shell) { return; }
            let told = '';
            function tell() {
                const state = JSON.stringify([location.href, document.title]);
                if (state === told) { return; }
                told = state;
                try {
                    window.parent.postMessage({
                        matgateSite: 1,
                        href: target + location.pathname + location.search + location.hash,
                        path: location.pathname + location.search + location.hash,
                        title: document.title
                    }, shell);
                }
                catch (error) { /* no shell to tell */ }
            }
            for (const name of ['pushState', 'replaceState']) {
                const native = history[name];
                history[name] = function () {
                    const result = native.apply(this, arguments);
                    tell();
                    return result;
                };
            }
            for (const name of ['popstate', 'hashchange', 'DOMContentLoaded', 'load', 'pageshow']) {
                window.addEventListener(name, tell);
            }
            document.addEventListener('DOMContentLoaded', () => {
                const title = document.querySelector('title');
                if (title && typeof MutationObserver === 'function') {
                    new MutationObserver(tell).observe(title, { childList: true, characterData: true, subtree: true });
                }
            });
            // Gone from this page: until the next one says where it is, the shell's buttons have nobody to ask.
            window.addEventListener('pagehide', () => {
                try { window.parent.postMessage({ matgateSite: 1, left: true }, shell); } catch (error) { /* no shell to tell */ }
            });
            window.addEventListener('message', event => {
                if (event.source !== window.parent || event.origin !== shell || !event.data || event.data.matgateSite !== 1) { return; }
                switch (event.data.go) {
                    case 'back': history.back(); break;
                    case 'forward': history.forward(); break;
                    case 'reload': location.reload(); break;
                    case 'to':
                        if (typeof event.data.path === 'string' && event.data.path.charAt(0) === '/' && event.data.path.charAt(1) !== '/') {
                            location.href = here + event.data.path;
                        }
                        break;
                }
            });
            tell();
        })();
        """;
}
