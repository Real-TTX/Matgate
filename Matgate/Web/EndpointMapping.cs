using System.Buffers.Binary;
using System.Security.Claims;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Readers;
using Matgate.Models;
using Matgate.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Matgate.Web;

public static class EndpointMapping
{
    private const string FaviconSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
          <defs>
            <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0%" stop-color="#176b5b"/>
              <stop offset="100%" stop-color="#2b5876"/>
            </linearGradient>
          </defs>
          <rect width="64" height="64" rx="14" fill="url(#g)"/>
          <path d="M19 23c0-7 5-12 13-12s13 5 13 12v18h-7V23c0-3.8-2.2-6-6-6s-6 2.2-6 6v18h-7z" fill="#fff" opacity=".9"/>
          <path d="M18 40h28v8H18z" fill="#fff"/>
        <path d="M26 40h12v4H26z" fill="#176b5b"/>
        </svg>
        """;

    private const string PwaManifestJson = """
        {
          "name": "Matgate",
          "short_name": "Matgate",
          "description": "Gateway for your home network",
          "id": "/",
          "start_url": "/",
          "scope": "/",
          "display": "fullscreen",
          "display_override": ["fullscreen", "standalone", "minimal-ui"],
          "orientation": "any",
          "theme_color": "#176b5b",
          "background_color": "#f4f6f4",
          "icons": [
            {
              "src": "/icon-192.png",
              "sizes": "192x192",
              "type": "image/png",
              "purpose": "any maskable"
            },
            {
              "src": "/icon-512.png",
              "sizes": "512x512",
              "type": "image/png",
              "purpose": "any maskable"
            }
          ]
        }
        """;

    private const string ServiceWorkerJs = """
        const CACHE_NAME = 'matgate-pwa-v1';

        self.addEventListener('install', event => {
          self.skipWaiting();
        });

        self.addEventListener('activate', event => {
          event.waitUntil(self.clients.claim());
        });

        self.addEventListener('fetch', event => {
          const request = event.request;
          if (request.method !== 'GET') {
            return;
          }

          const url = new URL(request.url);
          if (url.origin !== self.location.origin) {
            return;
          }

          event.respondWith(fetch(request));
        });
        """;

    private static readonly uint[] PngCrcTable = CreatePngCrcTable();
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] PwaIcon180 = CreatePwaIconPng(180);
    private static readonly byte[] PwaIcon192 = CreatePwaIconPng(192);
    private static readonly byte[] PwaIcon512 = CreatePwaIconPng(512);

    private static byte[] CreatePwaIconPng(int size)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            var ny = size <= 1 ? 0d : (double)y / (size - 1);
            for (var x = 0; x < size; x++)
            {
                var nx = size <= 1 ? 0d : (double)x / (size - 1);
                var t = Math.Clamp((nx * 0.66) + (ny * 0.34), 0d, 1d);
                var r = Lerp(0x17, 0x2b, t);
                var g = Lerp(0x6b, 0x58, t);
                var b = Lerp(0x5b, 0x76, t);
                var dx = nx - 0.42;
                var dy = ny - 0.30;
                var glow = Math.Max(0d, 1d - Math.Sqrt((dx * dx) + (dy * dy)) * 1.85) * 18d;
                SetPixel(pixels, size, x, y, ClampToByte(r + glow), ClampToByte(g + glow), ClampToByte(b + glow), 255);
            }
        }

        FillRect(pixels, size, (int)(size * 0.21), (int)(size * 0.20), (int)(size * 0.79), (int)(size * 0.30), 246, 250, 248, 255);
        FillRect(pixels, size, (int)(size * 0.23), (int)(size * 0.20), (int)(size * 0.34), (int)(size * 0.74), 246, 250, 248, 255);
        FillRect(pixels, size, (int)(size * 0.66), (int)(size * 0.20), (int)(size * 0.77), (int)(size * 0.74), 246, 250, 248, 255);
        FillRect(pixels, size, (int)(size * 0.19), (int)(size * 0.72), (int)(size * 0.81), (int)(size * 0.82), 246, 250, 248, 255);
        FillRect(pixels, size, (int)(size * 0.44), (int)(size * 0.35), (int)(size * 0.56), (int)(size * 0.72), 231, 238, 235, 255);
        FillRect(pixels, size, (int)(size * 0.41), (int)(size * 0.52), (int)(size * 0.59), (int)(size * 0.60), 246, 250, 248, 255);

        return EncodePng(size, size, pixels);
    }

    private static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Buffer.BlockCopy(rgba, y * stride, raw, (y * (stride + 1)) + 1, stride);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        using var png = new MemoryStream();
        png.Write(PngSignature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed.ToArray());
        WritePngChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        stream.Write(lengthBytes);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        if (!data.IsEmpty)
        {
            stream.Write(data);
        }

        var crc = ComputePngCrc(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, crc);
        stream.Write(lengthBytes);
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> typeBytes, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes)
        {
            crc = PngCrcTable[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
        }

        foreach (var value in data)
        {
            crc = PngCrcTable[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreatePngCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var crc = (uint)index;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            table[index] = crc;
        }

        return table;
    }

    private static void FillRect(byte[] pixels, int size, int left, int top, int right, int bottom, byte r, byte g, byte b, byte a)
    {
        var minX = Math.Clamp(left, 0, size);
        var minY = Math.Clamp(top, 0, size);
        var maxX = Math.Clamp(right, 0, size);
        var maxY = Math.Clamp(bottom, 0, size);

        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                SetPixel(pixels, size, x, y, r, g, b, a);
            }
        }
    }

    private static void SetPixel(byte[] pixels, int size, int x, int y, byte r, byte g, byte b, byte a)
    {
        var index = ((y * size) + x) * 4;
        pixels[index] = r;
        pixels[index + 1] = g;
        pixels[index + 2] = b;
        pixels[index + 3] = a;
    }

    private static byte ClampToByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static double Lerp(byte start, byte end, double t)
    {
        return start + ((end - start) * Math.Clamp(t, 0d, 1d));
    }

    public static void MapMatgateEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapMethods("/favicon.svg", new[] { "GET", "HEAD" }, () => Results.Text(FaviconSvg, "image/svg+xml"));
        app.MapMethods("/favicon.ico", new[] { "GET", "HEAD" }, () => Results.Text(FaviconSvg, "image/svg+xml"));
        app.MapMethods("/pwa-icon.svg", new[] { "GET", "HEAD" }, () => Results.Text(FaviconSvg, "image/svg+xml"));
        app.MapMethods("/icon-180.png", new[] { "GET", "HEAD" }, () => Results.File(PwaIcon180, "image/png"));
        app.MapMethods("/icon-192.png", new[] { "GET", "HEAD" }, () => Results.File(PwaIcon192, "image/png"));
        app.MapMethods("/icon-512.png", new[] { "GET", "HEAD" }, () => Results.File(PwaIcon512, "image/png"));
        app.MapMethods("/apple-touch-icon.png", new[] { "GET", "HEAD" }, () => Results.File(PwaIcon180, "image/png"));
        app.MapMethods("/manifest.webmanifest", new[] { "GET", "HEAD" }, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            return Results.Text(PwaManifestJson, "application/manifest+json");
        });
        app.MapMethods("/sw.js", new[] { "GET", "HEAD" }, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            return Results.Text(ServiceWorkerJs, "application/javascript");
        });
        app.MapGet("/api/ping", () => Results.Ok(new { serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }))
            .RequireAuthorization();
        app.MapGet("/language/{language}", SetLanguage);

        app.MapGet("/login", (HttpContext context, HtmlViews views) =>
        {
            var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
            return context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect(returnUrl)
                : Results.Content(views.Login(context), "text/html");
        });

        app.MapPost("/login", SignInAsync);
        app.MapPost("/logout", SignOutAsync).RequireAuthorization();
        app.MapGet("/", HomeAsync).RequireAuthorization();
        app.MapGet("/forbidden", ForbiddenAsync).RequireAuthorization();
        app.MapGet("/connect/{id:guid}", ConnectAsync).RequireAuthorization();
        app.MapGet("/website/{id:guid}", WebsiteAsync).RequireAuthorization();
        app.MapGet("/website/{id:guid}/bootstrap.js", WebsiteBootstrapAsync).RequireAuthorization();
        app.MapGet("/website/{id:guid}/{tabId:guid}/bootstrap.js", WebsiteBootstrapAsync).RequireAuthorization();
        app.MapMethods("/website/{id:guid}/state", new[] { "GET", "POST" }, WebsiteStateAsync).RequireAuthorization();
        app.MapMethods("/website/{id:guid}/{tabId:guid}/state", new[] { "GET", "POST" }, WebsiteStateAsync).RequireAuthorization();
        app.MapMethods("/website/{id:guid}/proxy", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, WebsiteProxyAsync).RequireAuthorization();
        app.MapMethods("/website/{id:guid}/proxy/{**path}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, WebsiteProxyAsync).RequireAuthorization();
        app.MapMethods("/website/{id:guid}/{tabId:guid}/proxy", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, WebsiteProxyAsync).RequireAuthorization();
        app.MapMethods("/website/{id:guid}/{tabId:guid}/proxy/{**path}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, WebsiteProxyAsync).RequireAuthorization();
        app.MapGet("/sessions", SessionsAsync).RequireAuthorization();
        app.MapGet("/workspaces", WorkspacesAsync).RequireAuthorization();
        app.MapGet("/workspaces/new", NewWorkspaceAsync).RequireAuthorization();
        app.MapPost("/workspaces", CreateWorkspaceAsync).RequireAuthorization();
        app.MapGet("/workspaces/{id:guid}", WorkspaceDetailAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/update", UpdateWorkspaceAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/extend", WorkspaceExtendAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/delete", DeleteWorkspaceAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/upload", WorkspaceUploadAsync).RequireAuthorization();
        app.MapGet("/workspaces/{id:guid}/download", WorkspaceDownloadAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/mkdir", WorkspaceCreateDirectoryAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/create-file", WorkspaceCreateFileAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/delete-entry", WorkspaceDeleteEntryAsync).RequireAuthorization();
        app.MapPost("/workspaces/{id:guid}/note", WorkspaceSaveNoteAsync).RequireAuthorization();
        app.MapGet("/workspace/{id:guid}", WorkspacePublicAsync);
        app.MapPost("/workspace/{id:guid}/unlock", WorkspaceUnlockAsync);
        app.MapPost("/workspace/{id:guid}/upload", WorkspacePublicUploadAsync);
        app.MapGet("/workspace/{id:guid}/download", WorkspacePublicDownloadAsync);
        app.MapPost("/workspace/{id:guid}/mkdir", WorkspacePublicCreateDirectoryAsync);
        app.MapPost("/workspace/{id:guid}/create-file", WorkspacePublicCreateFileAsync);
        app.MapPost("/workspace/{id:guid}/delete-entry", WorkspacePublicDeleteEntryAsync);
        app.MapPost("/workspace/{id:guid}/note", WorkspacePublicSaveNoteAsync);

        app.MapGet("/w/{id:guid}", WorkspacePublicAsync);
        app.MapPost("/w/{id:guid}/unlock", WorkspaceUnlockAsync);
        app.MapPost("/w/{id:guid}/upload", WorkspacePublicUploadAsync);
        app.MapGet("/w/{id:guid}/download", WorkspacePublicDownloadAsync);
        app.MapPost("/w/{id:guid}/mkdir", WorkspacePublicCreateDirectoryAsync);
        app.MapPost("/w/{id:guid}/create-file", WorkspacePublicCreateFileAsync);
        app.MapPost("/w/{id:guid}/delete-entry", WorkspacePublicDeleteEntryAsync);
        app.MapPost("/w/{id:guid}/note", WorkspacePublicSaveNoteAsync);
        app.MapGet("/tools", ToolsAsync).RequireAuthorization();
        app.MapGet("/account", AccountAsync).RequireAuthorization();
        app.MapGet("/about", AboutAsync).RequireAuthorization();
        app.MapPost("/account", UpdateAccountAsync).RequireAuthorization();
        app.MapPost("/account/favorites/{id:guid}/toggle", ToggleFavoriteServerAsync).RequireAuthorization();
        app.MapPost("/api/tools/ping", ToolsPingAsync).RequireAuthorization();
        app.MapPost("/api/tools/lookup", ToolsLookupAsync).RequireAuthorization();
        app.MapPost("/api/tools/port-check", ToolsPortCheckAsync).RequireAuthorization();
        app.MapPost("/api/tools/download", ToolsDownloadAsync).RequireAuthorization();
        app.MapPost("/api/connections/{id:guid}/launch", LaunchConnectionAsync).RequireAuthorization();
        app.MapGet("/files/{id:guid}/view", FileViewerAsync).RequireAuthorization();
        app.MapGet("/api/files/{id:guid}/list", ListFilesAsync).RequireAuthorization();
        app.MapGet("/api/files/{id:guid}/download", DownloadFileAsync).RequireAuthorization();
        app.MapGet("/api/files/{id:guid}/view", ViewFileAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/zip", CreateZipAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/upload", UploadFileAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/create-file", CreateFileAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/extract", ExtractArchiveAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/mkdir", CreateDirectoryAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/copy", CopyFilesAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/move", MoveFilesAsync).RequireAuthorization();
        app.MapPost("/api/files/{id:guid}/delete", DeleteFilesAsync).RequireAuthorization();
        app.MapDelete("/api/files/{id:guid}", DeleteFileAsync).RequireAuthorization();

        app.MapGet("/admin/users", UsersAsync).RequireAuthorization();
        app.MapGet("/admin/users/new", NewUserAsync).RequireAuthorization();
        app.MapPost("/admin/users", CreateUserAsync).RequireAuthorization();
        app.MapGet("/admin/users/{id:guid}", UserDetailAsync).RequireAuthorization();
        app.MapPost("/admin/users/{id:guid}/update", UpdateUserAsync).RequireAuthorization();
        app.MapPost("/admin/users/{id:guid}/access", UpdateUserAccessAsync).RequireAuthorization();
        app.MapPost("/admin/users/{id:guid}/password", ResetUserPasswordAsync).RequireAuthorization();
        app.MapPost("/admin/users/{id:guid}/delete", DeleteUserAsync).RequireAuthorization();

        app.MapGet("/admin/servers", ServersAsync).RequireAuthorization();
        app.MapGet("/admin/servers/new", NewServerAsync).RequireAuthorization();
        app.MapPost("/admin/servers", CreateServerAsync).RequireAuthorization();
        app.MapGet("/admin/servers/{id:guid}", ServerDetailAsync).RequireAuthorization();
        app.MapPost("/admin/servers/{id:guid}", UpdateServerAsync).RequireAuthorization();
        app.MapPost("/admin/servers/{id:guid}/delete", DeleteServerAsync).RequireAuthorization();
    }

    private static async Task<IResult> SetLanguage(string language, HttpContext context, JsonDataStore store)
    {
        var selectedLanguage = NormalizeLanguage(language);
        context.Response.Cookies.Append(
            HtmlViews.LanguageCookie,
            selectedLanguage,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });

        var currentUser = await CurrentUserAsync(context, store);
        if (currentUser is not null)
        {
            MatgateUser? updatedUser = null;
            await store.UpdateUsersAsync(users =>
            {
                updatedUser = users.FirstOrDefault(candidate => candidate.Id == currentUser.Id);
                if (updatedUser is null)
                {
                    return;
                }

                updatedUser.PreferredLanguage = selectedLanguage;
                updatedUser.UpdatedAt = DateTimeOffset.UtcNow;
            }, context.RequestAborted);

            if (updatedUser is not null)
            {
                var csrf = context.User.FindFirstValue("csrf") ?? "";
                if (!string.IsNullOrWhiteSpace(csrf))
                {
                    var persistent = ShouldPersistAuthentication(context);
                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        BuildPrincipal(updatedUser, csrf, selectedLanguage, updatedUser.PreferredTheme, persistent),
                        BuildAuthProperties(persistent));
                }
            }
        }

        var returnUrl = context.Request.Query["returnUrl"].ToString();
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith("/", StringComparison.Ordinal)
            || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            returnUrl = "/";
        }

        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> SignInAsync(
        HttpContext context,
        JsonDataStore store,
        PasswordHasher hasher,
        HtmlViews views)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var userName = form["username"].ToString();
        var password = form["password"].ToString();
        var user = await store.FindUserByNameAsync(userName, context.RequestAborted);

        if (user is null || !user.IsEnabled || !hasher.Verify(password, user.PasswordHash))
        {
            return Results.Content(
                views.Login(context, HtmlViews.Translate(context, "Username or password is invalid.")),
                "text/html");
        }

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            BuildPrincipal(user, hasher.GenerateSecret(24), user.PreferredLanguage, user.PreferredTheme, true),
            BuildAuthProperties(true));

        var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
        AppendRememberLoginCookie(context, true);
        context.Response.Cookies.Append(
            HtmlViews.ThemeCookie,
            NormalizeTheme(user.PreferredTheme),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });

        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var user = await CurrentUserAsync(context, store);
        if (!ValidateCsrf(context, form))
        {
            return Results.Content(views.Message(
                context,
                user,
                HtmlViews.Translate(context, "Invalid request"),
                HtmlViews.Translate(context, "The form has expired.")), "text/html");
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }

    private static async Task<IResult> HomeAsync(
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var allServers = await store.GetServersAsync(context.RequestAborted);
        var servers = allServers
            .Where(server => server.IsEnabled && CanAccessServer(user, server))
            .OrderBy(server => server.OwnerUserId is null ? 0 : 1)
            .ThenBy(server => server.FolderName)
            .ThenBy(server => server.Name)
            .ToList();

        var allWorkspaces = await workspaceService.GetWorkspacesAsync(context.RequestAborted);
        var visibleWorkspaces = VisibleWorkspacesForUser(user, allWorkspaces);

        Guid? openServerId = null;
        if (Guid.TryParse(context.Request.Query["open"], out var requestedOpen)
            && servers.Any(server => server.Id == requestedOpen))
        {
            openServerId = requestedOpen;
        }

        return Results.Content(
            views.SessionsWorkspace(context, user, servers, visibleWorkspaces, openServerId),
            "text/html");
    }

    private static IReadOnlyList<WorkspaceDefinition> VisibleWorkspacesForUser(
        MatgateUser user,
        IReadOnlyList<WorkspaceDefinition> workspaces)
    {
        return user.IsAdmin
            ? workspaces.OrderBy(workspace => workspace.Name).ToList()
            : workspaces.Where(workspace => workspace.OwnerUserId == user.Id).OrderBy(workspace => workspace.Name).ToList();
    }

    private static async Task<IResult> ForbiddenAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await CurrentUserAsync(context, store);
        return Results.Content(
            views.Message(
                context,
                user,
                HtmlViews.Translate(context, "No access"),
                HtmlViews.Translate(context, "Your user is not allowed to perform this action.")),
            "text/html");
    }

    private static async Task<IResult> ConnectAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server))
        {
            return Results.Content(views.Message(
                context,
                user,
                HtmlViews.Translate(context, "No access"),
                HtmlViews.Translate(context, "This server is not shared with you.")), "text/html");
        }

        return Results.Redirect($"/?open={id}");
    }

    private static async Task<IResult> WebsiteAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server))
        {
            return Results.Redirect("/forbidden");
        }

        return Results.Redirect($"/?open={id}");
    }

    private static async Task<IResult> WebsiteBootstrapAsync(
        Guid id,
        Guid? tabId,
        HttpContext context,
        JsonDataStore store,
        WebsiteProxyService proxy)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Matgate.Web.EndpointMapping")
            .LogDebug("Website bootstrap requested for {ServerId}", id);
        var access = await RequireWebsiteServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        WebsiteProxyService.PreventCaching(context.Response);
        return Results.Text(proxy.BuildBootstrapScript(context, access.Server!, tabId), "application/javascript");
    }

    private static async Task<IResult> WebsiteStateAsync(
        Guid id,
        Guid? tabId,
        HttpContext context,
        JsonDataStore store,
        WebsiteProxyService proxy)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Matgate.Web.EndpointMapping")
            .LogDebug("Website state requested for {ServerId}", id);
        var access = await RequireWebsiteServerAsync(id, context, store);
        if (access.Result is not null)
        {
            await access.Result.ExecuteAsync(context);
            return Results.Empty;
        }

        return await proxy.HandleWebsiteStateAsync(context, access.Server!, tabId, context.RequestAborted);
    }

    private static async Task<IResult> WebsiteProxyAsync(
        Guid id,
        Guid? tabId,
        string? path,
        HttpContext context,
        JsonDataStore store,
        WebsiteProxyService proxy)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Matgate.Web.EndpointMapping")
            .LogDebug("Website proxy requested for {ServerId} path {Path}", id, path ?? "");
        var access = await RequireWebsiteServerAsync(id, context, store);
        if (access.Result is not null)
        {
            await access.Result.ExecuteAsync(context);
            return Results.Empty;
        }

        await proxy.HandleProxyAsync(context, access.Server!, tabId, path, context.RequestAborted);
        return Results.Empty;
    }

    private static async Task<IResult> SessionsAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var open = context.Request.Query["open"].ToString();
        return string.IsNullOrWhiteSpace(open)
            ? Results.Redirect("/")
            : Results.Redirect($"/?open={Uri.EscapeDataString(open)}");
    }

    private static async Task<IResult> WorkspacesAsync(
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var allWorkspaces = await workspaceService.GetWorkspacesAsync(context.RequestAborted);
        var visibleWorkspaces = VisibleWorkspacesForUser(user, allWorkspaces);

        var openIdValue = context.Request.Query["open"].ToString();
        WorkspaceDefinition? selectedWorkspace = null;
        if (Guid.TryParse(openIdValue, out var openId))
        {
            selectedWorkspace = visibleWorkspaces.FirstOrDefault(workspace => workspace.Id == openId);
        }

        selectedWorkspace ??= visibleWorkspaces.FirstOrDefault();

        FileGatewayListResult? listing = null;
        string sharedText = "";
        IReadOnlyList<WorkspacePresenceSnapshot> sessions = [];
        string publicUrl = "";
        if (selectedWorkspace is not null)
        {
            var path = context.Request.Query["path"].ToString();
            listing = await workspaceService.ListFilesAsync(selectedWorkspace, path, context.RequestAborted);
            sharedText = await workspaceService.ReadSharedTextAsync(selectedWorkspace, context.RequestAborted);
            sessions = workspaceService.GetPresenceSnapshot(selectedWorkspace.Id);
            workspaceService.TouchPresence(selectedWorkspace, context, user, "Admin");
            await workspaceService.RecordActivityAsync(
                selectedWorkspace,
                context,
                user,
                "Opened workspace",
                path,
                "",
                "Admin",
                context.RequestAborted);
            var activityEntries = await workspaceService.GetActivityAsync(selectedWorkspace, 200, context.RequestAborted);
            publicUrl = BuildWorkspacePublicUrl(context, selectedWorkspace.Id);
            return Results.Content(
                views.Workspaces(
                    context,
                    user,
                    visibleWorkspaces,
                    selectedWorkspace,
                    listing,
                    sharedText,
                    sessions,
                    activityEntries,
                    selectedWorkspace is not null && (user.IsAdmin || selectedWorkspace.OwnerUserId == user.Id),
                    publicUrl,
                    store.WorkspaceRootDirectory),
                "text/html");
        }

        return Results.Content(
            views.Workspaces(
                context,
                user,
                visibleWorkspaces,
                selectedWorkspace,
                listing,
                sharedText,
                sessions,
                [],
                false,
                publicUrl,
                store.WorkspaceRootDirectory),
            "text/html");
    }

    private static async Task<IResult> NewWorkspaceAsync(
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        return Results.Content(
            views.WorkspaceCreate(context, user, store.WorkspaceRootDirectory),
            "text/html");
    }

    private static async Task<IResult> CreateWorkspaceAsync(
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var name = Clean(form["name"].ToString(), "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.Content(views.Message(
                context,
                user,
                HtmlViews.Translate(context, "Invalid request"),
                HtmlViews.Translate(context, "A workspace needs a name.")), "text/html");
        }

        WorkspaceDefinition created = new()
        {
            Name = name,
            Description = Clean(form["description"].ToString(), ""),
            RootPath = Clean(form["rootPath"].ToString(), ""),
            SharedNoteFileName = Clean(form["sharedNoteFileName"].ToString(), "shared-note.md"),
            AllowUploads = IsChecked(form, "allowUploads"),
            AllowTextExchange = IsChecked(form, "allowTextExchange"),
            IsEnabled = IsChecked(form, "isEnabled"),
            PublicAccessExpiresAt = DateTimeOffset.UtcNow.AddHours(ParseWorkspaceValidityHours(form, 24)),
            OwnerUserId = user.Id
        };

        var password = form["password"].ToString();
        if (!string.IsNullOrWhiteSpace(password))
        {
            workspaceService.SetAccessPassword(created, password);
        }

        await workspaceService.UpdateWorkspacesAsync(workspaces =>
        {
            workspaces.Add(created);
        }, context.RequestAborted);

        workspaceService.GetWorkspaceRoot(created);
        await workspaceService.RecordActivityAsync(
            created,
            context,
            user,
            "Created workspace",
            "/",
            created.Description,
            "Admin",
            context.RequestAborted);
        return Results.Redirect($"/workspaces/{created.Id}");
    }

    private static async Task<IResult> WorkspaceDetailAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var workspace = access.Workspace!;
        var listing = await workspaceService.ListFilesAsync(workspace, context.Request.Query["path"].ToString(), context.RequestAborted);
        var sharedText = await workspaceService.ReadSharedTextAsync(workspace, context.RequestAborted);
        var sessions = workspaceService.GetPresenceSnapshot(workspace.Id);
        workspaceService.TouchPresence(workspace, context, user, "Admin");
        await workspaceService.RecordActivityAsync(
            workspace,
            context,
            user,
            "Opened workspace",
            context.Request.Query["path"].ToString(),
            "",
            "Admin",
            context.RequestAborted);
        var activityEntries = await workspaceService.GetActivityAsync(workspace, 200, context.RequestAborted);

        return Results.Content(
            views.WorkspaceDetail(
                context,
                user,
                workspace,
                listing,
                sharedText,
                sessions,
                activityEntries,
                true,
                BuildWorkspacePublicUrl(context, workspace.Id),
                store.WorkspaceRootDirectory),
            "text/html");
    }

    private static async Task<IResult> UpdateWorkspaceAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var name = Clean(form["name"].ToString(), "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.Content(views.Message(
                context,
                user,
                HtmlViews.Translate(context, "Invalid request"),
                HtmlViews.Translate(context, "A workspace needs a name.")), "text/html");
        }

        await workspaceService.UpdateWorkspacesAsync(workspaces =>
        {
            var stored = workspaces.FirstOrDefault(workspace => workspace.Id == id);
            if (stored is null)
            {
                return;
            }

            stored.Name = name;
            stored.Description = Clean(form["description"].ToString(), "");
            stored.RootPath = Clean(form["rootPath"].ToString(), "");
            stored.SharedNoteFileName = Clean(form["sharedNoteFileName"].ToString(), "shared-note.md");
            stored.AllowUploads = IsChecked(form, "allowUploads");
            stored.AllowTextExchange = IsChecked(form, "allowTextExchange");
            stored.IsEnabled = IsChecked(form, "isEnabled");

            if (IsChecked(form, "clearPassword"))
            {
                workspaceService.ClearAccessPassword(stored);
            }
            else
            {
                var password = form["password"].ToString();
                if (!string.IsNullOrWhiteSpace(password))
                {
                    workspaceService.SetAccessPassword(stored, password);
                }
            }

            stored.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        await workspaceService.RecordActivityAsync(
            access.Workspace!,
            context,
            user,
            "Updated workspace settings",
            "/",
            name,
            "Admin",
            context.RequestAborted);
        return Results.Redirect($"/workspaces/{id}");
    }

    private static async Task<IResult> WorkspaceExtendAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var hours = ParseWorkspaceValidityHours(form, 24);
        await workspaceService.UpdateWorkspacesAsync(workspaces =>
        {
            var stored = workspaces.FirstOrDefault(workspace => workspace.Id == id);
            if (stored is null)
            {
                return;
            }

            workspaceService.ExtendPublicAccess(stored, TimeSpan.FromHours(hours));
            stored.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        await workspaceService.RecordActivityAsync(
            access.Workspace!,
            context,
            user,
            "Extended public access",
            "/",
            $"+{hours:N0} hours",
            "Admin",
            context.RequestAborted);
        return Results.Redirect(WorkspaceTabRedirect(context, $"/workspaces/{id}", form, "settings"));
    }

    private static async Task<IResult> DeleteWorkspaceAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var workspace = access.Workspace!;
        var rootPath = workspaceService.GetWorkspaceRoot(workspace);
        await workspaceService.UpdateWorkspacesAsync(workspaces =>
        {
            workspaces.RemoveAll(candidate => candidate.Id == id);
        }, context.RequestAborted);

        var managedRoot = store.WorkspaceRootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? store.WorkspaceRootDirectory
            : store.WorkspaceRootDirectory + Path.DirectorySeparatorChar;
        if ((string.Equals(rootPath, store.WorkspaceRootDirectory, StringComparison.OrdinalIgnoreCase)
                || rootPath.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            && Directory.Exists(rootPath))
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch
            {
                // Ignore filesystem cleanup errors here; metadata removal already succeeded.
            }
        }

        await workspaceService.RecordActivityAsync(
            workspace,
            context,
            user,
            "Deleted workspace",
            "/",
            rootPath,
            "Admin",
            context.RequestAborted);
        return Results.Redirect("/workspaces");
    }

    private static async Task<IResult> WorkspaceUploadAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var path = form["path"].ToString();
        foreach (var file in form.Files)
        {
            if (file.Length <= 0)
            {
                continue;
            }

            await using var stream = file.OpenReadStream();
            await workspaceService.UploadAsync(access.Workspace!, path, stream, file.FileName, context.RequestAborted);
            await workspaceService.RecordActivityAsync(
                access.Workspace!,
                context,
                user,
                "Uploaded file",
                string.IsNullOrWhiteSpace(path) || path == "/" ? $"/{file.FileName}" : $"{path.TrimEnd('/')}/{file.FileName}",
                $"{file.Length:N0} bytes",
                "Admin",
                context.RequestAborted);
        }

        return Results.Redirect($"/workspaces/{id}?path={Uri.EscapeDataString(path)}");
    }

    private static async Task<IResult> WorkspaceDownloadAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        try
        {
            var download = await workspaceService.DownloadAsync(access.Workspace!, path, context.RequestAborted);
            await workspaceService.RecordActivityAsync(
                access.Workspace!,
                context,
                user,
                "Downloaded file",
                path ?? "/",
                download.FileName,
                "Admin",
                context.RequestAborted);
            return Results.File(download.Content, download.ContentType, download.FileName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> WorkspaceCreateDirectoryAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var path = form["path"].ToString();
        var name = Clean(form["name"].ToString(), "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        await workspaceService.CreateDirectoryAsync(access.Workspace!, path, name, context.RequestAborted);
        await workspaceService.RecordActivityAsync(
            access.Workspace!,
            context,
            user,
            "Created folder",
            string.IsNullOrWhiteSpace(path) || path == "/" ? $"/{name}" : $"{path.TrimEnd('/')}/{name}",
            "",
            "Admin",
            context.RequestAborted);
        return Results.Redirect($"/workspaces/{id}?path={Uri.EscapeDataString(path)}");
    }

    private static async Task<IResult> WorkspaceCreateFileAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var path = form["path"].ToString();
        var name = Clean(form["name"].ToString(), "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        await workspaceService.CreateFileAsync(access.Workspace!, path, name, context.RequestAborted);
        await workspaceService.RecordActivityAsync(
            access.Workspace!,
            context,
            user,
            "Created file",
            string.IsNullOrWhiteSpace(path) || path == "/" ? $"/{name}" : $"{path.TrimEnd('/')}/{name}",
            "",
            "Admin",
            context.RequestAborted);
        return Results.Redirect($"/workspaces/{id}?path={Uri.EscapeDataString(path)}");
    }

    private static async Task<IResult> WorkspaceDeleteEntryAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var path = form["path"].ToString();
        await workspaceService.DeleteAsync(access.Workspace!, path, context.RequestAborted);
        await workspaceService.RecordActivityAsync(
            access.Workspace!,
            context,
            user,
            "Deleted entry",
            path,
            "",
            "Admin",
            context.RequestAborted);
        var redirectPath = string.IsNullOrWhiteSpace(path) ? "" : Uri.EscapeDataString(ParentVirtualPath(path));
        return Results.Redirect($"/workspaces/{id}{(string.IsNullOrWhiteSpace(redirectPath) ? "" : $"?path={redirectPath}")}");
    }

    private static async Task<IResult> WorkspaceSaveNoteAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var access = await RequireWorkspaceAdminAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var text = form["text"].ToString();
        await workspaceService.WriteSharedTextAsync(access.Workspace!, text, context.RequestAborted);
        await workspaceService.RecordActivityAsync(
            access.Workspace!,
            context,
            user,
            "Updated shared text",
            "/shared-note",
            $"{text.Length:N0} characters",
            "Admin",
            context.RequestAborted);
        return Results.Redirect(WorkspaceTabRedirect(context, $"/workspaces/{id}", form, "text"));
    }

    private static async Task<IResult> WorkspacePublicAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Expired)
        {
            return Results.Content(views.WorkspaceExpired(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        if (access.PasswordRequired)
        {
            return Results.Content(views.WorkspacePassword(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        var workspace = access.Workspace!;
        var listing = await workspaceService.ListFilesAsync(workspace, path, context.RequestAborted);
        var sharedText = await workspaceService.ReadSharedTextAsync(workspace, context.RequestAborted);
        workspaceService.TouchPresence(workspace, context, access.User, "Public");
        await workspaceService.RecordActivityAsync(
            workspace,
            context,
            access.User,
            "Opened workspace",
            path,
            "",
            "Public",
            context.RequestAborted);
        var activityEntries = await workspaceService.GetActivityAsync(workspace, 200, context.RequestAborted);
        return Results.Content(
            views.WorkspacePublic(
                context,
                workspace,
                listing,
                sharedText,
                workspaceService.GetPresenceSnapshot(workspace.Id),
                access.PublicUrl,
                false,
                activityEntries),
            "text/html");
    }

    private static async Task<IResult> WorkspaceUnlockAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Expired)
        {
            return Results.Content(views.WorkspaceExpired(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        var workspace = access.Workspace!;
        if (!workspaceService.HasAccessPassword(workspace))
        {
            return Results.Redirect($"/workspace/{id}");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var password = form["password"].ToString();
        if (!workspaceService.VerifyAccessPassword(workspace, password))
        {
            return Results.Content(
                views.WorkspacePassword(context, workspace, access.PublicUrl, HtmlViews.Translate(context, "Username or password is invalid.")),
                "text/html");
        }

        workspaceService.IssueAccessGrant(context, workspace);
        await workspaceService.RecordActivityAsync(
            workspace,
            context,
            access.User,
            "Unlocked workspace",
            "/",
            "Password accepted",
            "Public",
            context.RequestAborted);
        return Results.Redirect($"/workspace/{id}");
    }

    private static async Task<IResult> WorkspacePublicUploadAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Expired)
        {
            return Results.Content(views.WorkspaceExpired(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        if (access.PasswordRequired)
        {
            return Results.Content(views.WorkspacePassword(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        var workspace = access.Workspace!;
        if (!workspace.AllowUploads)
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Uploads are disabled for this workspace.") });
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var path = form["path"].ToString();
        foreach (var file in form.Files)
        {
            if (file.Length <= 0)
            {
                continue;
            }

            await using var stream = file.OpenReadStream();
            await workspaceService.UploadAsync(workspace, path, stream, file.FileName, context.RequestAborted);
            await workspaceService.RecordActivityAsync(
                workspace,
                context,
                access.User,
                "Uploaded file",
                string.IsNullOrWhiteSpace(path) || path == "/" ? $"/{file.FileName}" : $"{path.TrimEnd('/')}/{file.FileName}",
                $"{file.Length:N0} bytes",
                "Public",
                context.RequestAborted);
        }

        return Results.Redirect($"/workspace/{id}?path={Uri.EscapeDataString(path)}");
    }

    private static async Task<IResult> WorkspacePublicDownloadAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Expired)
        {
            return Results.Content(views.WorkspaceExpired(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        if (access.PasswordRequired)
        {
            return Results.Redirect($"/workspace/{id}");
        }

        try
        {
            var download = await workspaceService.DownloadAsync(access.Workspace!, path, context.RequestAborted);
            await workspaceService.RecordActivityAsync(
                access.Workspace!,
                context,
                access.User,
                "Downloaded file",
                path ?? "/",
                download.FileName,
                "Public",
                context.RequestAborted);
            return Results.File(download.Content, download.ContentType, download.FileName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> WorkspacePublicCreateDirectoryAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Expired)
        {
            return Results.Content(views.WorkspaceExpired(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        if (access.PasswordRequired)
        {
            return Results.Content(views.WorkspacePassword(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        return Results.Forbid();
    }

    private static async Task<IResult> WorkspacePublicCreateFileAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Expired)
        {
            return Results.Content(views.WorkspaceExpired(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        if (access.PasswordRequired)
        {
            return Results.Content(views.WorkspacePassword(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        return Results.Forbid();
    }

    private static async Task<IResult> WorkspacePublicDeleteEntryAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Expired)
        {
            return Results.Content(views.WorkspaceExpired(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        if (access.PasswordRequired)
        {
            return Results.Content(views.WorkspacePassword(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        return Results.Forbid();
    }

    private static async Task<IResult> WorkspacePublicSaveNoteAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views,
        WorkspaceService workspaceService)
    {
        var access = await RequireWorkspacePublicAsync(id, context, store, workspaceService);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.PasswordRequired)
        {
            return Results.Content(views.WorkspacePassword(context, access.Workspace!, access.PublicUrl), "text/html");
        }

        var workspace = access.Workspace!;
        if (!workspace.AllowTextExchange)
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Text exchange is disabled for this workspace.") });
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var text = form["text"].ToString();
        await workspaceService.WriteSharedTextAsync(workspace, text, context.RequestAborted);
        await workspaceService.RecordActivityAsync(
            workspace,
            context,
            access.User,
            "Updated shared text",
            "/shared-note",
            $"{text.Length:N0} characters",
            "Public",
            context.RequestAborted);
        return Results.Redirect(WorkspaceTabRedirect(context, $"/workspace/{id}", form, "text"));
    }

    private static string WorkspaceTabRedirect(HttpContext context, string baseUrl, IFormCollection form, string defaultTab)
    {
        var tab = Clean(form["tab"].ToString(), "");
        if (string.IsNullOrWhiteSpace(tab))
        {
            tab = Clean(context.Request.Query["tab"].ToString(), "");
        }

        tab = tab.ToLowerInvariant();
        if (tab is not ("files" or "text" or "log" or "info" or "settings"))
        {
            tab = defaultTab;
        }

        var path = Clean(form["path"].ToString(), "");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Clean(context.Request.Query["path"].ToString(), "");
        }

        var url = $"{baseUrl}?tab={Uri.EscapeDataString(tab)}";
        if (!string.IsNullOrWhiteSpace(path))
        {
            url += $"&path={Uri.EscapeDataString(path)}";
        }

        return EmbedAwareRedirect(context, url);
    }

    private static int ParseWorkspaceValidityHours(IFormCollection form, int fallback)
    {
        if (int.TryParse(form["publicAccessHours"].ToString(), out var hours))
        {
            return Math.Clamp(hours, 1, 24 * 365 * 10);
        }

        return fallback;
    }

    private static async Task<IResult> LaunchConnectionAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        GuacamoleConfigWriter configWriter,
        GuacamoleLauncher launcher)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server))
        {
            return Results.NotFound(new { error = HtmlViews.Translate(context, "This server is not shared with you.") });
        }

        await configWriter.SynchronizeAsync(context.RequestAborted);
        var launch = await launcher.CreateLaunchAsync(user, server, context.RequestAborted);
        if (!launch.Success || string.IsNullOrWhiteSpace(launch.Url))
        {
            return Results.BadRequest(new { error = launch.Error ?? HtmlViews.Translate(context, "The connection could not be started.") });
        }

        return Results.Json(new
        {
            server = new
            {
                id = server.Id,
                name = server.Name,
                protocol = server.Protocol.ToString().ToUpperInvariant(),
                target = ServerEndpoint.IsWebsiteProtocol(server.Protocol)
                    ? (string.IsNullOrWhiteSpace(server.WebsiteUrl) ? server.Host : server.WebsiteUrl)
                    : $"{server.Host}:{server.Port}"
            },
            encryptedData = launch.EncryptedData,
            connectionName = launch.ConnectionName
        });
    }

    private static async Task<IResult> ListFilesAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        try
        {
            return Results.Json(await files.ListAsync(access.Server!, path, context.RequestAborted));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ExtractionException or NotSupportedException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> FileViewerAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files,
        HtmlViews views)
    {
        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var user = await CurrentUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var embedded = IsTruthyQuery(context.Request.Query["embedded"]) || IsTruthyQuery(context.Request.Query["dialog"]);
        try
        {
            var fileInfo = await files.GetFileInfoAsync(access.Server!, path, context.RequestAborted);
            return Results.Content(
                views.FileViewer(context, user, access.Server!, fileInfo, path ?? "/", embedded),
                "text/html");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            if (embedded)
            {
                return Results.Content(
                    views.FileViewerError(
                        context,
                        access.Server!,
                        path ?? "/",
                        HtmlViews.Translate(context, "File access failed"),
                        ex.Message),
                    "text/html");
            }

            return Results.Content(views.Message(
                context,
                user,
                HtmlViews.Translate(context, "File access failed"),
                ex.Message), "text/html");
        }
    }

    private static async Task<IResult> DownloadFileAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        try
        {
            var download = await files.DownloadAsync(access.Server!, path, context.RequestAborted);
            return Results.File(download.Content, download.ContentType, download.FileName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ViewFileAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        try
        {
            var fileInfo = await files.GetFileInfoAsync(access.Server!, path, context.RequestAborted);
            var range = ResolveByteRange(context.Request.Headers.Range.ToString(), fileInfo.Length);
            if (!range.IsSatisfiable)
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.Headers["Content-Range"] = $"bytes */{fileInfo.Length}";
                return Results.Empty;
            }

            if (NeedsViewerSandbox(fileInfo.ContentType))
            {
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
            }

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.Headers["Content-Disposition"] = InlineContentDisposition(fileInfo.FileName);
            context.Response.ContentType = fileInfo.ContentType;
            context.Response.ContentLength = range.Length;
            context.Response.StatusCode = range.IsPartial
                ? StatusCodes.Status206PartialContent
                : StatusCodes.Status200OK;

            if (range.IsPartial)
            {
                context.Response.Headers["Content-Range"] = $"bytes {range.Start}-{range.End}/{fileInfo.Length}";
            }

            await files.CopyRangeAsync(
                access.Server!,
                path,
                context.Response.Body,
                range.Start,
                range.Length,
                context.RequestAborted);

            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            return Results.Empty;
        }
        catch (Exception ex) when (context.Response.HasStarted && ex is InvalidOperationException or IOException or InvalidDataException)
        {
            return Results.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> CreateZipAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var request = await context.Request.ReadFromJsonAsync<FileZipCreateRequest>(cancellationToken: context.RequestAborted);
        var paths = CleanPathList(request?.Paths);
        if (paths.Count == 0)
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "No file selected.") });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.DestinationPath) || string.IsNullOrWhiteSpace(request.ArchiveName))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var destinationPath = NormalizeVirtualPathForEndpoint(request.DestinationPath);
        var archiveName = NormalizeZipArchiveFileName(request.ArchiveName);
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");

        try
        {
            await using (var tempWrite = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(tempWrite, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var selectedPath in paths)
                    {
                        await AddPathToZipAsync(access.Server!, files, archive, selectedPath, FileNameFromVirtualPath(selectedPath), context.RequestAborted);
                    }
                }

                await tempWrite.FlushAsync(context.RequestAborted);
            }

            await using var tempRead = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            await files.UploadAsync(access.Server!, destinationPath, tempRead, archiveName, context.RequestAborted);
            return Results.Ok(new { ok = true, fileName = archiveName });
        }
        catch (OperationCanceledException)
        {
            return Results.Empty;
        }
        catch (Exception ex) when (context.Response.HasStarted && ex is InvalidOperationException or IOException or InvalidDataException)
        {
            return Results.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<IResult> UploadFileAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly)
        {
            bodySizeFeature.MaxRequestBodySize = null;
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (BadHttpRequestException ex) when (ex.Message.Contains("Request body too large", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "The uploaded file is too large.") });
        }
        var uploadedFiles = form.Files.GetFiles("file");
        if (uploadedFiles.Count == 0 || uploadedFiles.All(file => file.Length == 0))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "No file selected.") });
        }

        try
        {
            var unzip = IsTruthy(form["unzip"].ToString());
            foreach (var file in uploadedFiles.Where(file => file.Length > 0))
            {
                await using var stream = file.OpenReadStream();
                if (unzip && FileArchiveFormats.IsArchiveFileName(file.FileName))
                {
                    await ExtractArchiveUploadAsync(
                        access.Server!,
                        files,
                        form["path"].ToString(),
                        stream,
                        context.RequestAborted);
                    continue;
                }

                await files.UploadAsync(
                    access.Server!,
                    form["path"].ToString(),
                    stream,
                    file.FileName,
                    context.RequestAborted);
            }

            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> CreateFileAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var request = await context.Request.ReadFromJsonAsync<FileCreateRequest>(cancellationToken: context.RequestAborted);
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "File name is missing.") });
        }

        try
        {
            await files.CreateFileAsync(
                access.Server!,
                request.Path,
                request.Name,
                context.RequestAborted);
            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ExtractArchiveAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var request = await context.Request.ReadFromJsonAsync<FileArchiveExtractRequest>(cancellationToken: context.RequestAborted);
        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        if (!FileArchiveFormats.IsArchiveFileName(request.Path))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "This file is not a supported archive.") });
        }

        try
        {
            await ExtractArchiveFromServerAsync(
                access.Server!,
                files,
                request.Path,
                string.IsNullOrWhiteSpace(request.DestinationPath)
                    ? ParentVirtualPathForEndpoint(request.Path)
                    : request.DestinationPath,
                context.RequestAborted);

            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ExtractionException or NotSupportedException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> CreateDirectoryAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var request = await context.Request.ReadFromJsonAsync<FileDirectoryRequest>(cancellationToken: context.RequestAborted);
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Folder name is missing.") });
        }

        try
        {
            await files.CreateDirectoryAsync(
                access.Server!,
                request.Path,
                request.Name,
                context.RequestAborted);
            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> CopyFilesAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var request = await context.Request.ReadFromJsonAsync<FileBatchTransferRequest>(cancellationToken: context.RequestAborted);
        var paths = CleanPathList(request?.Paths);
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(request?.DestinationPath))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        try
        {
            foreach (var selectedPath in paths)
            {
                await CopyPathAsync(
                    access.Server!,
                    files,
                    selectedPath,
                    CombineVirtualPathForEndpoint(request.DestinationPath, FileNameFromVirtualPath(selectedPath)),
                    context.RequestAborted);
            }

            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> MoveFilesAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var request = await context.Request.ReadFromJsonAsync<FileBatchTransferRequest>(cancellationToken: context.RequestAborted);
        var paths = CleanPathList(request?.Paths);
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(request?.DestinationPath))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        try
        {
            foreach (var selectedPath in paths)
            {
                var destinationPath = CombineVirtualPathForEndpoint(request.DestinationPath, FileNameFromVirtualPath(selectedPath));
                if (string.Equals(NormalizeVirtualPathForEndpoint(selectedPath), destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (destinationPath.StartsWith(NormalizeVirtualPathForEndpoint(selectedPath) + "/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Ein Ordner kann nicht in sich selbst verschoben werden.");
                }

                await CopyPathAsync(
                    access.Server!,
                    files,
                    selectedPath,
                    destinationPath,
                    context.RequestAborted);
                await files.DeleteAsync(access.Server!, selectedPath, context.RequestAborted);
            }

            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DeleteFilesAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var request = await context.Request.ReadFromJsonAsync<FileBatchRequest>(cancellationToken: context.RequestAborted);
        var paths = CleanPathList(request?.Paths);
        if (paths.Count == 0)
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "No file selected.") });
        }

        try
        {
            foreach (var selectedPath in paths)
            {
                await files.DeleteAsync(access.Server!, selectedPath, context.RequestAborted);
            }

            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DeleteFileAsync(
        Guid id,
        string? path,
        HttpContext context,
        JsonDataStore store,
        IFileGatewayService files)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var access = await RequireFileServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        try
        {
            await files.DeleteAsync(access.Server!, path, context.RequestAborted);
            return Results.Ok(new { ok = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> UsersAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireAdminAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        return Results.Content(views.Users(context, user, await store.GetUsersAsync(context.RequestAborted)), "text/html");
    }

    private static async Task<IResult> NewUserAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireAdminAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        return Results.Content(views.UserCreate(context, user), "text/html");
    }

    private static async Task<IResult> CreateUserAsync(
        HttpContext context,
        JsonDataStore store,
        PasswordHasher hasher,
        GuacamoleConfigWriter configWriter,
        HtmlViews views)
    {
        var currentUser = await RequireAdminAsync(context, store);
        if (currentUser is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, currentUser, views);
        }

        var userName = PasswordHasher.NormalizeUserName(form["username"].ToString());
        var password = form["password"].ToString();
        if (!PasswordHasher.IsValidUserName(userName) || password.Length < 10)
        {
            return Results.Content(views.Message(
                context,
                currentUser,
                HtmlViews.Translate(context, "Invalid data"),
                HtmlViews.Translate(context, "Username or password is invalid.")), "text/html");
        }

        var exists = false;
        await store.UpdateUsersAsync(users =>
        {
            exists = users.Any(user => string.Equals(user.UserName, userName, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            users.Add(new MatgateUser
            {
                UserName = userName,
                DisplayName = Clean(form["displayName"].ToString(), userName),
                PasswordHash = hasher.Hash(password),
                GuacamolePassword = hasher.GenerateSecret(),
                IsAdmin = IsChecked(form, "isAdmin"),
                CanManageServers = IsChecked(form, "canManageServers") || IsChecked(form, "isAdmin"),
                CanCreateServers = IsChecked(form, "canCreateServers") || IsChecked(form, "isAdmin"),
                PreferredLanguage = NormalizeLanguage(form["preferredLanguage"].ToString()),
                PreferredTheme = NormalizeTheme(form["preferredTheme"].ToString()),
                RememberLoginByDefault = true,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }, context.RequestAborted);

        if (exists)
        {
            return Results.Content(views.Message(
                context,
                currentUser,
                HtmlViews.Translate(context, "User already exists"),
                HtmlViews.Translate(context, "This username is already taken.")), "text/html");
        }

        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, "/admin/users"));
    }

    private static async Task<IResult> UserDetailAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views)
    {
        var currentUser = await RequireAdminAsync(context, store);
        if (currentUser is null)
        {
            return Results.Redirect("/forbidden");
        }

        var editedUser = await store.FindUserByIdAsync(id, context.RequestAborted);
        if (editedUser is null)
        {
            return Results.NotFound();
        }

        return Results.Content(
            views.UserDetail(
                context,
                currentUser,
                editedUser,
                (await store.GetServersAsync(context.RequestAborted)).Where(server => server.OwnerUserId is null).ToList()),
            "text/html");
    }

    private static async Task<IResult> UpdateUserAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        GuacamoleConfigWriter configWriter,
        HtmlViews views)
    {
        var currentUser = await RequireAdminAsync(context, store);
        if (currentUser is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, currentUser, views);
        }

        var removingLastAdmin = false;
        MatgateUser? updatedSelfUser = null;
        await store.UpdateUsersAsync(users =>
        {
            var user = users.FirstOrDefault(candidate => candidate.Id == id);
            if (user is null)
            {
                return;
            }

            var nextIsAdmin = IsChecked(form, "isAdmin");
            removingLastAdmin = user.IsAdmin
                && !nextIsAdmin
                && users.Count(candidate => candidate.IsAdmin && candidate.IsEnabled) <= 1;

            if (removingLastAdmin)
            {
                return;
            }

            user.DisplayName = Clean(form["displayName"].ToString(), user.UserName);
            user.IsEnabled = IsChecked(form, "isEnabled");
            user.IsAdmin = nextIsAdmin;
            user.CanManageServers = IsChecked(form, "canManageServers") || user.IsAdmin;
            user.CanCreateServers = IsChecked(form, "canCreateServers") || user.IsAdmin;
            user.PreferredLanguage = NormalizeLanguage(form["preferredLanguage"].ToString());
            user.PreferredTheme = NormalizeTheme(form["preferredTheme"].ToString());
            user.RememberLoginByDefault = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            if (user.Id == currentUser.Id)
            {
                updatedSelfUser = user;
            }
        }, context.RequestAborted);

        if (removingLastAdmin)
        {
            return Results.Content(views.Message(
                context,
                currentUser,
                HtmlViews.Translate(context, "Not saved"),
                HtmlViews.Translate(context, "The last active administrator cannot be removed.")), "text/html");
        }

        if (updatedSelfUser is not null)
        {
            var csrf = context.User.FindFirstValue("csrf") ?? "";
            if (!string.IsNullOrWhiteSpace(csrf))
            {
                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    BuildPrincipal(updatedSelfUser, csrf, updatedSelfUser.PreferredLanguage, updatedSelfUser.PreferredTheme, updatedSelfUser.RememberLoginByDefault),
                    BuildAuthProperties(updatedSelfUser.RememberLoginByDefault));
            }

            AppendRememberLoginCookie(context, true);
            context.Response.Cookies.Append(
                HtmlViews.ThemeCookie,
                NormalizeTheme(updatedSelfUser.PreferredTheme),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps
                });
        }

        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, $"/admin/users/{id}"));
    }

    private static async Task<IResult> AccountAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var servers = (await store.GetServersAsync(context.RequestAborted))
            .Where(server => server.IsEnabled && CanAccessServer(user, server))
            .ToList();
        return Results.Content(views.Account(context, user, servers), "text/html");
    }

    private static async Task<IResult> AboutAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        return Results.Content(views.About(context, user), "text/html");
    }

    private static async Task<IResult> ToolsAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        return Results.Content(views.Tools(context, user), "text/html");
    }

    private static async Task ToolsPingAsync(
        HttpContext context,
        JsonDataStore store,
        NetworkToolsService tools)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid request", context.RequestAborted);
            return;
        }

        await tools.PingAsync(
            context,
            form["host"].ToString(),
            ParseInt(form, "count", 4),
            ParseInt(form, "timeoutMs", 1000),
            ParseInt(form, "intervalMs", 1000),
            context.RequestAborted);
    }

    private static async Task ToolsLookupAsync(
        HttpContext context,
        JsonDataStore store,
        NetworkToolsService tools)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid request", context.RequestAborted);
            return;
        }

        await tools.LookupAsync(context, form["host"].ToString(), context.RequestAborted);
    }

    private static async Task ToolsPortCheckAsync(
        HttpContext context,
        JsonDataStore store,
        NetworkToolsService tools)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid request", context.RequestAborted);
            return;
        }

        await tools.PortCheckAsync(
            context,
            form["host"].ToString(),
            form["ports"].ToString(),
            ParseInt(form, "timeoutMs", 1000),
            context.RequestAborted);
    }

    private static async Task ToolsDownloadAsync(
        HttpContext context,
        JsonDataStore store,
        NetworkToolsService tools)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid request", context.RequestAborted);
            return;
        }

        await tools.DownloadAsync(context, form["url"].ToString(), context.RequestAborted);
    }

    private static async Task<IResult> UpdateAccountAsync(
        HttpContext context,
        JsonDataStore store,
        HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        MatgateUser? updatedUser = null;
        await store.UpdateUsersAsync(users =>
        {
            var current = users.FirstOrDefault(candidate => candidate.Id == user.Id);
            if (current is null)
            {
                return;
            }

            current.DisplayName = Clean(form["displayName"].ToString(), current.DisplayName);
            current.PreferredLanguage = NormalizeLanguage(form["preferredLanguage"].ToString());
            current.PreferredTheme = NormalizeTheme(form["preferredTheme"].ToString());
            current.RememberLoginByDefault = true;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            updatedUser = current;
        }, context.RequestAborted);

        if (updatedUser is not null)
        {
            var csrf = context.User.FindFirstValue("csrf") ?? "";
            if (!string.IsNullOrWhiteSpace(csrf))
            {
                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    BuildPrincipal(updatedUser, csrf, updatedUser.PreferredLanguage, updatedUser.PreferredTheme, updatedUser.RememberLoginByDefault),
                    BuildAuthProperties(updatedUser.RememberLoginByDefault));
            }

            AppendRememberLoginCookie(context, true);
            context.Response.Cookies.Append(
                HtmlViews.ThemeCookie,
                NormalizeTheme(updatedUser.PreferredTheme),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps
                });
        }

        return Results.Redirect(EmbedAwareRedirect(context, "/account"));
    }

    private static async Task<IResult> ToggleFavoriteServerAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server))
        {
            return Results.Redirect(EmbedAwareRedirect(context, NormalizeReturnUrl(form["returnUrl"].ToString())));
        }

        await store.UpdateUsersAsync(users =>
        {
            var current = users.FirstOrDefault(candidate => candidate.Id == user.Id);
            if (current is null)
            {
                return;
            }

            current.FavoriteServerIds ??= [];
            if (current.FavoriteServerIds.Contains(id))
            {
                current.FavoriteServerIds.RemoveAll(favoriteId => favoriteId == id);
            }
            else
            {
                current.FavoriteServerIds.Add(id);
            }

            current.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, NormalizeReturnUrl(form["returnUrl"].ToString())));
    }

    private static async Task<IResult> UpdateUserAccessAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        GuacamoleConfigWriter configWriter,
        HtmlViews views)
    {
        var currentUser = await RequireAdminAsync(context, store);
        if (currentUser is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, currentUser, views);
        }

        var allServers = IsChecked(form, "allServers");
        var managedServerIds = (await store.GetServersAsync(context.RequestAborted))
            .Where(server => server.OwnerUserId is null)
            .Select(server => server.Id)
            .ToHashSet();

        var serverIds = form["serverIds"]
            .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
            .Where(value => value != Guid.Empty && managedServerIds.Contains(value))
            .Distinct()
            .ToList();

        await store.UpdateUsersAsync(users =>
        {
            var user = users.FirstOrDefault(candidate => candidate.Id == id);
            if (user is null || user.IsAdmin)
            {
                return;
            }

            user.ServerAccessAll = allServers;
            user.ServerAccess = serverIds;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, $"/admin/users/{id}"));
    }

    private static async Task<IResult> ResetUserPasswordAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        PasswordHasher hasher,
        GuacamoleConfigWriter configWriter,
        HtmlViews views)
    {
        var currentUser = await RequireAdminAsync(context, store);
        if (currentUser is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, currentUser, views);
        }

        var password = form["password"].ToString();
        if (password.Length < 10)
        {
            return Results.Content(views.Message(
                context,
                currentUser,
                HtmlViews.Translate(context, "Password too short"),
                HtmlViews.Translate(context, "The password must be at least 10 characters long.")), "text/html");
        }

        await store.UpdateUsersAsync(users =>
        {
            var user = users.FirstOrDefault(candidate => candidate.Id == id);
            if (user is null)
            {
                return;
            }

            user.PasswordHash = hasher.Hash(password);
            user.GuacamolePassword = hasher.GenerateSecret();
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, $"/admin/users/{id}"));
    }

    private static async Task<IResult> DeleteUserAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        GuacamoleConfigWriter configWriter,
        HtmlViews views)
    {
        var currentUser = await RequireAdminAsync(context, store);
        if (currentUser is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, currentUser, views);
        }

        if (currentUser.Id == id)
        {
            return Results.Content(views.Message(
                context,
                currentUser,
                HtmlViews.Translate(context, "Not deleted"),
                HtmlViews.Translate(context, "You cannot delete your own user.")), "text/html");
        }

        await store.UpdateUsersAsync(users =>
        {
            users.RemoveAll(user => user.Id == id);
        }, context.RequestAborted);

        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, "/admin/users"));
    }

    private static async Task<IResult> ServersAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var servers = (await store.GetServersAsync(context.RequestAborted))
            .Where(server => CanEditServer(user, server))
            .OrderBy(server => server.OwnerUserId is null ? 0 : 1)
            .ThenBy(server => server.FolderName)
            .ThenBy(server => server.Name)
            .ToList();
        var users = await store.GetUsersAsync(context.RequestAborted);

        return Results.Content(
            views.Servers(context, user, servers, users),
            "text/html");
    }

    private static async Task<IResult> NewServerAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        return Results.Content(views.ServerCreate(context, user), "text/html");
    }

    private static async Task<IResult> CreateServerAsync(
        HttpContext context,
        JsonDataStore store,
        GuacamoleConfigWriter configWriter,
        HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var server = ReadServerForm(form, user, null);
        var requiresWebsiteUrl = server.Protocol == ServerProtocol.Website;
        var hasRequiredTarget = requiresWebsiteUrl
            ? !string.IsNullOrWhiteSpace(server.WebsiteUrl)
            : !string.IsNullOrWhiteSpace(server.Host);

        if (string.IsNullOrWhiteSpace(server.Name) || !hasRequiredTarget)
        {
            return Results.Content(views.Message(
                context,
                user,
                HtmlViews.Translate(context, "Invalid data"),
                HtmlViews.Translate(context, requiresWebsiteUrl ? "Name and website URL are required." : "Name and host are required.")), "text/html");
        }

        await store.UpdateServersAsync(servers => servers.Add(server), context.RequestAborted);
        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, "/admin/servers"));
    }

    private static async Task<IResult> ServerDetailAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null)
        {
            return Results.NotFound();
        }

        if (!CanEditServer(user, server))
        {
            return Results.Redirect("/forbidden");
        }

        return Results.Content(views.ServerDetail(context, user, server, await store.GetUsersAsync(context.RequestAborted)), "text/html");
    }

    private static async Task<IResult> UpdateServerAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        GuacamoleConfigWriter configWriter,
        HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var currentServer = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (currentServer is null)
        {
            return Results.NotFound();
        }

        if (!CanEditServer(user, currentServer))
        {
            return Results.Redirect("/forbidden");
        }

        var updated = ReadServerForm(form, user, currentServer);
        var hasRequiredTarget = updated.Protocol == ServerProtocol.Website
            ? !string.IsNullOrWhiteSpace(updated.WebsiteUrl)
            : !string.IsNullOrWhiteSpace(updated.Host);
        if (string.IsNullOrWhiteSpace(updated.Name) || !hasRequiredTarget)
        {
            return Results.Content(views.Message(
                context,
                user,
                HtmlViews.Translate(context, "Invalid data"),
                HtmlViews.Translate(context, updated.Protocol == ServerProtocol.Website ? "Name and website URL are required." : "Name and host are required.")), "text/html");
        }

        await store.UpdateServersAsync(servers =>
        {
            var storedServer = servers.FirstOrDefault(server => server.Id == id);
            if (storedServer is null)
            {
                return;
            }

            storedServer.Name = updated.Name;
            storedServer.Protocol = updated.Protocol;
            storedServer.IconKey = updated.IconKey;
            storedServer.FolderName = updated.FolderName;
            storedServer.FolderIconKey = updated.FolderIconKey;
            storedServer.Host = updated.Host;
            storedServer.Port = updated.Port;
            storedServer.WebsiteUrl = updated.WebsiteUrl;
            storedServer.UserName = updated.UserName;
            storedServer.Domain = updated.Domain;
            storedServer.FileRootPath = updated.FileRootPath;
            storedServer.KeyboardLayout = updated.KeyboardLayout;
            storedServer.TerminalFontSize = updated.TerminalFontSize;
            storedServer.IgnoreCertificate = updated.IgnoreCertificate;
            storedServer.IsEnabled = updated.IsEnabled;
            storedServer.Notes = updated.Notes;
            storedServer.Password = IsChecked(form, "clearPassword")
                ? ""
                : string.IsNullOrWhiteSpace(updated.Password) ? storedServer.Password : updated.Password;
            storedServer.OwnerUserId = updated.OwnerUserId;
            storedServer.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, $"/admin/servers/{id}"));
    }

    private static async Task<IResult> DeleteServerAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        GuacamoleConfigWriter configWriter,
        WebsiteProxyService websiteProxy,
        HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var existing = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (existing is null)
        {
            return Results.NotFound();
        }

        if (!CanEditServer(user, existing))
        {
            return Results.Redirect("/forbidden");
        }

        await store.UpdateServersAsync(servers => servers.RemoveAll(server => server.Id == id), context.RequestAborted);
        await store.UpdateUsersAsync(users =>
        {
            foreach (var editedUser in users)
            {
                editedUser.FavoriteServerIds ??= [];
                editedUser.ServerAccess.Remove(id);
                editedUser.FavoriteServerIds.RemoveAll(favoriteId => favoriteId == id);
            }
        }, context.RequestAborted);

        websiteProxy.ForgetServer(id);
        await configWriter.SynchronizeAsync(context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, "/admin/servers"));
    }

    private static async Task<MatgateUser?> RequireUserAsync(HttpContext context, JsonDataStore store)
    {
        var user = await CurrentUserAsync(context, store);
        if (user is not null)
        {
            return user;
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return null;
    }

    private static async Task<MatgateUser?> RequireAdminAsync(HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        return user?.IsAdmin == true ? user : null;
    }

    private static async Task<MatgateUser?> RequireServerManagerAsync(HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        return user is { IsAdmin: true } || user is { CanManageServers: true } || user is { CanCreateServers: true } ? user : null;
    }

    private static async Task<FileServerAccess> RequireFileServerAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return new FileServerAccess(null, Results.Unauthorized());
        }

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server))
        {
            return new FileServerAccess(null, Results.NotFound(new { error = HtmlViews.Translate(context, "This server is not shared with you.") }));
        }

        if (!ServerEndpoint.IsFileProtocol(server.Protocol))
        {
            return new FileServerAccess(null, Results.BadRequest(new { error = HtmlViews.Translate(context, "This server is not a file connection.") }));
        }

        return new FileServerAccess(server, null);
    }

    private static async Task<FileServerAccess> RequireWebsiteServerAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return new FileServerAccess(null, Results.Unauthorized());
        }

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server))
        {
            return new FileServerAccess(null, Results.NotFound(new { error = HtmlViews.Translate(context, "This server is not shared with you.") }));
        }

        if (!ServerEndpoint.IsWebsiteProtocol(server.Protocol))
        {
            return new FileServerAccess(null, Results.BadRequest(new { error = HtmlViews.Translate(context, "This server is not a website connection.") }));
        }

        return new FileServerAccess(server, null);
    }

    private static async Task<WorkspaceAccess> RequireWorkspaceAdminAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        WorkspaceService workspaceService)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return new WorkspaceAccess(null, Results.Redirect("/login"));
        }

        var workspace = await workspaceService.FindWorkspaceByIdAsync(id, context.RequestAborted);
        if (workspace is null || !workspace.IsEnabled)
        {
            return new WorkspaceAccess(null, Results.NotFound());
        }

        if (!(user.IsAdmin || workspace.OwnerUserId == user.Id))
        {
            return new WorkspaceAccess(null, Results.Redirect("/forbidden"));
        }

        return new WorkspaceAccess(workspace, null);
    }

    private static async Task<WorkspacePublicAccess> RequireWorkspacePublicAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        WorkspaceService workspaceService)
    {
        var user = await CurrentUserAsync(context, store);
        var workspace = await workspaceService.FindWorkspaceByIdAsync(id, context.RequestAborted);
        if (workspace is null || !workspace.IsEnabled)
        {
            return new WorkspacePublicAccess(null, user, false, "", false, Results.NotFound());
        }

        var publicUrl = BuildWorkspacePublicUrl(context, workspace.Id);
        var isOwnerOrAdmin = user is { } currentUser && (currentUser.IsAdmin || workspace.OwnerUserId == currentUser.Id);
        var expired = !isOwnerOrAdmin && workspaceService.HasPublicAccessExpired(workspace);
        var hasAccessGrant = workspaceService.HasValidAccessGrant(context, workspace);
        var passwordRequired = !isOwnerOrAdmin && !expired && workspaceService.HasAccessPassword(workspace) && !hasAccessGrant;
        return new WorkspacePublicAccess(workspace, user, passwordRequired, publicUrl, expired, null);
    }

    private static async Task<MatgateUser?> CurrentUserAsync(HttpContext context, JsonDataStore store)
    {
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var id))
        {
            return null;
        }

        var user = await store.FindUserByIdAsync(id, context.RequestAborted);
        return user is { IsEnabled: true } ? user : null;
    }

    private static ClaimsPrincipal BuildPrincipal(MatgateUser user, string csrf, string preferredLanguage, string preferredTheme, bool persistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new("csrf", csrf),
            new("lang", NormalizeLanguage(preferredLanguage)),
            new("theme", NormalizeTheme(preferredTheme)),
            new("persistent", persistent ? "1" : "0")
        };

        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static AuthenticationProperties BuildAuthProperties(bool persistent)
    {
        return new AuthenticationProperties
        {
            AllowRefresh = true,
            IsPersistent = persistent,
            ExpiresUtc = persistent ? DateTimeOffset.UtcNow.AddDays(30) : null
        };
    }

    private static void AppendRememberLoginCookie(HttpContext context, bool remember)
    {
        context.Response.Cookies.Append(
            HtmlViews.RememberLoginCookie,
            remember ? "1" : "0",
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });
    }

    private static bool ShouldPersistAuthentication(HttpContext context)
    {
        var persistentClaim = context.User.FindFirstValue("persistent");
        if (persistentClaim == "1")
        {
            return true;
        }

        if (persistentClaim == "0")
        {
            return false;
        }

        if (context.Request.Cookies.TryGetValue(HtmlViews.RememberLoginCookie, out var rememberCookie))
        {
            return string.Equals(rememberCookie, "1", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool CanAccessServer(MatgateUser user, ServerEndpoint server)
    {
        if (user.IsAdmin)
        {
            return true;
        }

        if (server.OwnerUserId == user.Id)
        {
            return true;
        }

        if (server.OwnerUserId is not null)
        {
            return false;
        }

        return user.CanManageServers || user.ServerAccessAll || user.ServerAccess.Contains(server.Id);
    }

    private static bool CanEditServer(MatgateUser user, ServerEndpoint server)
    {
        if (user.IsAdmin)
        {
            return true;
        }

        if (server.OwnerUserId == user.Id)
        {
            return true;
        }

        return server.OwnerUserId is null && user.CanManageServers;
    }

    private static bool ValidateCsrf(HttpContext context, IFormCollection form)
    {
        return PasswordHasher.SecureEquals(
            context.User.FindFirstValue("csrf"),
            form["_csrf"].ToString());
    }

    private static bool ValidateCsrfHeader(HttpContext context)
    {
        return PasswordHasher.SecureEquals(
            context.User.FindFirstValue("csrf"),
            context.Request.Headers["X-Matgate-Csrf"].ToString());
    }

    private static string BuildWorkspacePublicUrl(HttpContext context, Guid id)
    {
        var pathBase = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "";
        return $"{context.Request.Scheme}://{context.Request.Host}{pathBase}/workspace/{id}";
    }

    private static string NormalizeWorkspacePath(string? value)
    {
        var cleaned = (value ?? "").Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "/";
        }

        while (cleaned.Contains("//", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("//", "/", StringComparison.Ordinal);
        }

        if (!cleaned.StartsWith('/'))
        {
            cleaned = "/" + cleaned.TrimStart('/');
        }

        if (cleaned.Length > 1 && cleaned.EndsWith('/'))
        {
            cleaned = cleaned.TrimEnd('/');
        }

        return cleaned;
    }

    private static string ParentVirtualPath(string path)
    {
        var normalized = NormalizeWorkspacePath(path);
        if (normalized == "/")
        {
            return "/";
        }

        var trimmed = normalized.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static IResult BadRequest(HttpContext context, MatgateUser? user, HtmlViews views)
    {
        return Results.Content(views.Message(
            context,
            user,
            HtmlViews.Translate(context, "Invalid request"),
            HtmlViews.Translate(context, "The form has expired.")), "text/html");
    }

    private static ServerEndpoint ReadServerForm(IFormCollection form, MatgateUser currentUser, ServerEndpoint? existing)
    {
        var protocol = Enum.TryParse<ServerProtocol>(form["protocol"].ToString(), ignoreCase: true, out var parsed)
            ? parsed
            : existing?.Protocol ?? ServerProtocol.Rdp;
        if (protocol == ServerProtocol.LegacyBrowser)
        {
            protocol = ServerProtocol.Rdp;
        }
        var canManageGlobal = currentUser.IsAdmin || currentUser.CanManageServers;
        var canCreatePrivate = currentUser.IsAdmin || currentUser.CanCreateServers;
        var requestedPrivate = string.Equals(form["scope"].ToString(), "private", StringComparison.OrdinalIgnoreCase);
        var isPrivate = existing?.OwnerUserId is not null ? true : requestedPrivate;

        if (canManageGlobal && canCreatePrivate)
        {
            isPrivate = requestedPrivate;
        }
        else if (canManageGlobal && !canCreatePrivate)
        {
            isPrivate = false;
        }
        else if (!canManageGlobal && canCreatePrivate)
        {
            isPrivate = true;
        }
        else if (!canManageGlobal && !canCreatePrivate)
        {
            isPrivate = existing?.OwnerUserId is not null;
        }

        var defaultPort = protocol switch
        {
            ServerProtocol.Ssh => 22,
            ServerProtocol.Vnc => 5900,
            ServerProtocol.Sftp => 22,
            ServerProtocol.Ftp => 21,
            ServerProtocol.Smb => 445,
            _ => 3389
        };
        var port = protocol == ServerProtocol.Website
            ? existing?.Port ?? 0
            : int.TryParse(form["port"].ToString(), out var parsedPort) && parsedPort is >= 1 and <= 65535
                ? parsedPort
                : existing?.Port ?? defaultPort;
        var terminalFontSize = int.TryParse(form["terminalFontSize"].ToString(), out var parsedTerminalFontSize)
            ? parsedTerminalFontSize
            : existing?.TerminalFontSize ?? ServerEndpoint.DefaultTerminalFontSize;
        var now = DateTimeOffset.UtcNow;
        var websiteUrl = protocol == ServerProtocol.Website
            ? ServerEndpoint.NormalizeWebsiteUrl(form["websiteUrl"].ToString(), existing?.WebsiteUrl ?? existing?.Host ?? "")
            : existing?.WebsiteUrl ?? "";

        return new ServerEndpoint
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = Clean(form["name"].ToString(), existing?.Name ?? ""),
            Protocol = protocol,
            IconKey = ServerEndpoint.NormalizeIconKey(form["iconKey"].ToString()),
            FolderName = Clean(form["folderName"].ToString(), ""),
            FolderIconKey = string.IsNullOrWhiteSpace(Clean(form["folderName"].ToString(), ""))
                ? ""
                : ServerEndpoint.NormalizeIconKey(form["folderIconKey"].ToString()),
            Host = Clean(form["host"].ToString(), existing?.Host ?? ""),
            Port = port,
            WebsiteUrl = websiteUrl,
            UserName = Clean(form["targetUserName"].ToString(), ""),
            Password = form["targetPassword"].ToString(),
            Domain = Clean(form["domain"].ToString(), ""),
            FileRootPath = Clean(form["fileRootPath"].ToString(), ""),
            KeyboardLayout = CleanKeyboardLayout(
                form["keyboardLayout"].ToString(),
                existing?.KeyboardLayout ?? ServerEndpoint.DefaultKeyboardLayout),
            TerminalFontSize = ServerEndpoint.NormalizeTerminalFontSize(terminalFontSize),
            IgnoreCertificate = IsChecked(form, "ignoreCertificate"),
            IsEnabled = IsChecked(form, "isEnabled"),
            Notes = Clean(form["notes"].ToString(), ""),
            OwnerUserId = isPrivate ? (existing?.OwnerUserId ?? currentUser.Id) : null,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
    }

    private sealed record WorkspaceAccess(WorkspaceDefinition? Workspace, IResult? Result);

    private sealed record WorkspacePublicAccess(
        WorkspaceDefinition? Workspace,
        MatgateUser? User,
        bool PasswordRequired,
        string PublicUrl,
        bool Expired,
        IResult? Result);

    private static bool IsChecked(IFormCollection form, string key)
    {
        return form.ContainsKey(key);
    }

    private static string Clean(string? value, string fallback)
    {
        var cleaned = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static int ParseInt(IFormCollection form, string key, int fallback)
    {
        return int.TryParse(form[key].ToString(), out var value) ? value : fallback;
    }

    private static string NormalizeLanguage(string? language)
    {
        return string.Equals((language ?? "").Trim(), "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
    }

    private static string NormalizeTheme(string? theme)
    {
        var normalized = (theme ?? "").Trim().ToLowerInvariant();
        return normalized is "light" or "dark" or "system" ? normalized : "system";
    }

    private static string NormalizeReturnUrl(string? value)
    {
        var cleaned = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)
            || !cleaned.StartsWith("/", StringComparison.Ordinal)
            || cleaned.StartsWith("//", StringComparison.Ordinal))
        {
            return "/";
        }

        return cleaned;
    }

    private static string EmbedAwareRedirect(HttpContext context, string url)
    {
        if (!IsTruthyQuery(context.Request.Query["embed"]) || string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("//", StringComparison.Ordinal))
        {
            return url;
        }

        if (url.Contains("embed=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var hashIndex = url.IndexOf('#');
        var path = hashIndex >= 0 ? url[..hashIndex] : url;
        var fragment = hashIndex >= 0 ? url[hashIndex..] : "";
        return $"{path}{(path.Contains('?') ? '&' : '?')}embed=1{fragment}";
    }

    private static string CleanKeyboardLayout(string? value, string fallback)
    {
        var cleaned = Clean(value, string.IsNullOrWhiteSpace(fallback)
            ? ServerEndpoint.DefaultKeyboardLayout
            : fallback).ToLowerInvariant();

        return cleaned.Length <= 64 && cleaned.All(IsKeyboardLayoutCharacter)
            ? cleaned
            : ServerEndpoint.DefaultKeyboardLayout;
    }

    private static bool IsKeyboardLayoutCharacter(char value)
    {
        return value is >= 'a' and <= 'z'
            || value is >= '0' and <= '9'
            || value == '-';
    }

    private static async Task AddPathToZipAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        ZipArchive archive,
        string sourcePath,
        string zipPath,
        CancellationToken cancellationToken)
    {
        if (await TryListFilesAsync(server, files, sourcePath, cancellationToken) is { } directory)
        {
            var directoryEntryName = NormalizeZipEntryPath(zipPath, isDirectory: true);
            if (!string.IsNullOrWhiteSpace(directoryEntryName))
            {
                archive.CreateEntry(directoryEntryName, CompressionLevel.NoCompression);
            }

            foreach (var entry in directory.Entries)
            {
                await AddPathToZipAsync(
                    server,
                    files,
                    archive,
                    entry.Path,
                    CombineZipEntryPath(zipPath, entry.Name),
                    cancellationToken);
            }

            return;
        }

        var fileInfo = await files.GetFileInfoAsync(server, sourcePath, cancellationToken);
        var zipEntry = archive.CreateEntry(NormalizeZipEntryPath(zipPath, isDirectory: false), CompressionLevel.Fastest);
        await using var zipStream = zipEntry.Open();
        await files.CopyRangeAsync(server, sourcePath, zipStream, 0, fileInfo.Length, cancellationToken);
    }

    private static async Task CopyPathAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        sourcePath = NormalizeVirtualPathForEndpoint(sourcePath);
        destinationPath = NormalizeVirtualPathForEndpoint(destinationPath);
        if (sourcePath == "/" || destinationPath == "/")
        {
            throw new InvalidOperationException("Der Root-Pfad kann nicht fuer diese Aktion verwendet werden.");
        }

        if (await TryListFilesAsync(server, files, sourcePath, cancellationToken) is { } directory)
        {
            await EnsureDirectoryPathAsync(server, files, destinationPath, cancellationToken);
            foreach (var entry in directory.Entries)
            {
                await CopyPathAsync(
                    server,
                    files,
                    entry.Path,
                    CombineVirtualPathForEndpoint(destinationPath, entry.Name),
                    cancellationToken);
            }

            return;
        }

        var fileInfo = await files.GetFileInfoAsync(server, sourcePath, cancellationToken);
        var destinationDirectory = ParentVirtualPathForEndpoint(destinationPath);
        await EnsureDirectoryPathAsync(server, files, destinationDirectory, cancellationToken);

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var tempWrite = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await files.CopyRangeAsync(server, sourcePath, tempWrite, 0, fileInfo.Length, cancellationToken);
            }

            await using var tempRead = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await files.UploadAsync(
                server,
                destinationDirectory,
                tempRead,
                FileNameFromVirtualPath(destinationPath),
                cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Temporary cleanup is best effort.
            }
        }
    }

    private static async Task ExtractArchiveUploadAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        string? destinationPath,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        await ExtractArchiveToRemoteAsync(server, files, destinationPath, archiveStream, cancellationToken);
    }

    private static async Task ExtractArchiveFromServerAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        string sourcePath,
        string? destinationPath,
        CancellationToken cancellationToken)
    {
        var fileInfo = await files.GetFileInfoAsync(server, sourcePath, cancellationToken);
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var tempWrite = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await files.CopyRangeAsync(server, sourcePath, tempWrite, 0, fileInfo.Length, cancellationToken);
            }

            await using var archiveStream = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await ExtractArchiveToRemoteAsync(server, files, destinationPath, archiveStream, cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Temporary cleanup is best effort.
            }
        }
    }

    private static async Task ExtractArchiveToRemoteAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        string? destinationPath,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"matgate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var extractionDirectory = tempDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? tempDirectory
            : tempDirectory + Path.DirectorySeparatorChar;

        try
        {
            if (archiveStream.CanSeek)
            {
                archiveStream.Position = 0;
            }

            await using (var reader = await ReaderFactory.OpenAsyncReader(
                archiveStream,
                ReaderOptions.ForExternalStream,
                cancellationToken))
            {
                await reader.WriteAllToDirectoryAsync(
                    extractionDirectory,
                    new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    },
                    cancellationToken);
            }

            await UploadDirectoryTreeAsync(server, files, tempDirectory, destinationPath, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Temporary cleanup is best effort.
            }
        }
    }

    private static async Task UploadDirectoryTreeAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        string sourceRoot,
        string? destinationPath,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(sourceRoot);
        var rootDestination = NormalizeVirtualPathForEndpoint(destinationPath);

        foreach (var directoryPath in Directory.EnumerateDirectories(normalizedRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeDirectory = Path.GetRelativePath(normalizedRoot, directoryPath);
            var remoteDirectory = CombineVirtualPathSegments(rootDestination, SafeZipEntryParts(relativeDirectory));
            await EnsureDirectoryPathAsync(server, files, remoteDirectory, cancellationToken);
        }

        foreach (var filePath in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeFile = Path.GetRelativePath(normalizedRoot, filePath);
            var parts = SafeZipEntryParts(relativeFile);
            if (parts.Count == 0)
            {
                continue;
            }

            var remoteDirectory = parts.Count == 1
                ? rootDestination
                : CombineVirtualPathSegments(rootDestination, parts.Take(parts.Count - 1).ToList());
            await EnsureDirectoryPathAsync(server, files, remoteDirectory, cancellationToken);

            await using var input = File.OpenRead(filePath);
            await files.UploadAsync(server, remoteDirectory, input, parts[^1], cancellationToken);
        }
    }

    private static async Task<FileGatewayListResult?> TryListFilesAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await files.ListAsync(server, path, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static async Task EnsureDirectoryPathAsync(
        ServerEndpoint server,
        IFileGatewayService files,
        string? path,
        CancellationToken cancellationToken)
    {
        var parts = NormalizeVirtualPathForEndpoint(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var currentPath = "/";
        foreach (var part in parts)
        {
            try
            {
                await files.CreateDirectoryAsync(server, currentPath, part, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // Existing directories are fine; later file operations will surface real failures.
            }

            currentPath = CombineVirtualPathForEndpoint(currentPath, part);
        }
    }

    private static IReadOnlyList<string> CleanPathList(IEnumerable<string?>? paths)
    {
        return paths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeVirtualPathForEndpoint)
            .Where(path => path != "/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList() ?? [];
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVirtualPathForEndpoint(string? path)
    {
        var value = (path ?? "/").Replace('\\', '/');
        var parts = new List<string>();
        foreach (var rawPart in value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart == ".")
            {
                continue;
            }

            if (rawPart == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(rawPart);
        }

        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    private static string ParentVirtualPathForEndpoint(string path)
    {
        path = NormalizeVirtualPathForEndpoint(path);
        if (path == "/")
        {
            return "/";
        }

        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private static string CombineVirtualPathForEndpoint(string? basePath, string name)
    {
        var normalizedBase = NormalizeVirtualPathForEndpoint(basePath);
        var cleanName = CleanLeafNameForEndpoint(name);
        return normalizedBase == "/" ? "/" + cleanName : normalizedBase + "/" + cleanName;
    }

    private static string CombineVirtualPathSegments(string? basePath, IReadOnlyList<string> parts)
    {
        var path = NormalizeVirtualPathForEndpoint(basePath);
        foreach (var part in parts)
        {
            path = CombineVirtualPathForEndpoint(path, part);
        }

        return path;
    }

    private static string CleanLeafNameForEndpoint(string? name)
    {
        var cleaned = (name ?? "").Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
        {
            throw new InvalidOperationException("Ungueltiger Datei- oder Ordnername.");
        }

        return cleaned;
    }

    private static string FileNameFromVirtualPath(string path)
    {
        return NormalizeVirtualPathForEndpoint(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? "matgate-file";
    }

    private static string NormalizeZipArchiveFileName(string? archiveName)
    {
        var cleaned = CleanLeafNameForEndpoint(archiveName);
        return cleaned.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : cleaned + ".zip";
    }

    private static IReadOnlyList<string> SafeZipEntryParts(string fullName)
    {
        return fullName
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part is not "." and not "..")
            .Select(CleanLeafNameForEndpoint)
            .ToList();
    }

    private static string CombineZipEntryPath(string basePath, string name)
    {
        var cleanName = CleanZipEntryName(name);
        return string.IsNullOrWhiteSpace(basePath)
            ? cleanName
            : $"{basePath.TrimEnd('/')}/{cleanName}";
    }

    private static string NormalizeZipEntryPath(string path, bool isDirectory)
    {
        var parts = SafeZipEntryParts(path);
        var normalized = string.Join('/', parts.Select(CleanZipEntryName));
        return isDirectory && !string.IsNullOrWhiteSpace(normalized) ? normalized + "/" : normalized;
    }

    private static string CleanZipEntryName(string name)
    {
        return string.Join("_", CleanLeafNameForEndpoint(name).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }

    private static ByteRange ResolveByteRange(string? rangeHeader, long fileLength)
    {
        if (fileLength < 0)
        {
            return ByteRange.NotSatisfiable;
        }

        if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return fileLength == 0
                ? new ByteRange(0, -1, false, true)
                : new ByteRange(0, fileLength - 1, false, true);
        }

        if (fileLength == 0)
        {
            return ByteRange.NotSatisfiable;
        }

        var requestedRange = rangeHeader["bytes=".Length..].Split(',', 2)[0].Trim();
        var separator = requestedRange.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
        {
            return ByteRange.NotSatisfiable;
        }

        var startText = requestedRange[..separator].Trim();
        var endText = requestedRange[(separator + 1)..].Trim();
        long start;
        long end;

        if (string.IsNullOrWhiteSpace(startText))
        {
            if (!long.TryParse(endText, out var suffixLength) || suffixLength <= 0)
            {
                return ByteRange.NotSatisfiable;
            }

            start = Math.Max(0, fileLength - suffixLength);
            end = fileLength - 1;
        }
        else
        {
            if (!long.TryParse(startText, out start) || start < 0)
            {
                return ByteRange.NotSatisfiable;
            }

            if (string.IsNullOrWhiteSpace(endText))
            {
                end = fileLength - 1;
            }
            else if (!long.TryParse(endText, out end) || end < start)
            {
                return ByteRange.NotSatisfiable;
            }
            else
            {
                end = Math.Min(end, fileLength - 1);
            }
        }

        return start >= fileLength
            ? ByteRange.NotSatisfiable
            : new ByteRange(start, end, true, true);
    }

    private static string InlineContentDisposition(string fileName)
    {
        return ContentDisposition("inline", fileName);
    }

    private static bool NeedsViewerSandbox(string contentType)
    {
        return contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/xml", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthyQuery(StringValues value)
    {
        return value.Count > 0 && value.Any(entry =>
            string.Equals(entry, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry, "yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string AttachmentContentDisposition(string fileName)
    {
        return ContentDisposition("attachment", fileName);
    }

    private static string ContentDisposition(string disposition, string fileName)
    {
        var safeAsciiName = new string(fileName.Select(character =>
            character is '"' or '\\' or '\r' or '\n'
                ? '_'
                : character <= 0x7f ? character : '_').ToArray());

        return $"{disposition}; filename=\"{safeAsciiName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private readonly record struct ByteRange(long Start, long End, bool IsPartial, bool IsSatisfiable)
    {
        public static ByteRange NotSatisfiable => new(0, -1, false, false);

        public long Length => IsSatisfiable ? Math.Max(0, End - Start + 1) : 0;
    }

    private sealed record FileServerAccess(ServerEndpoint? Server, IResult? Result);

    private sealed record FileBatchRequest(IReadOnlyList<string>? Paths);

    private sealed record FileZipCreateRequest(string? DestinationPath, string? ArchiveName, IReadOnlyList<string>? Paths);

    private sealed record FileBatchTransferRequest(IReadOnlyList<string>? Paths, string? DestinationPath);

    private sealed record FileArchiveExtractRequest(string? Path, string? DestinationPath);

    private sealed record FileCreateRequest(string? Path, string Name);

    private sealed record FileDirectoryRequest(string? Path, string Name);
}
