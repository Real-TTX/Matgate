using Matgate.Services;
using Matgate.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Text;
using System.Threading.RateLimiting;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Auto-generate persistent secrets into their "*_FILE" path when nothing else provides them, so a
// bare `docker compose up` works with no init container and no manual .env. An explicit env value
// always wins; an existing non-empty file is kept.
static void EnsureSecretFile(string envName, string fileEnvName, int byteCount)
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName)))
    {
        return;
    }

    var path = Environment.GetEnvironmentVariable(fileEnvName);
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    try
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
        File.WriteAllText(path, key);
    }
    catch
    {
        // Read-only mount or similar - fall back to whatever the *_FILE / env eventually provides.
    }
}

EnsureSecretFile("MATGATE_GUACAMOLE_JSON_SECRET_KEY", "MATGATE_GUACAMOLE_JSON_SECRET_KEY_FILE", 16);
EnsureSecretFile("MATGATE_SECRET_KEY", "MATGATE_SECRET_KEY_FILE", 32);
// Shared control token for the optional browser-farm sidecar (written like guac.key; the farm reads
// the same file from the shared secrets volume). Harmless if the farm is not deployed.
EnsureSecretFile("MATGATE_BROWSER_FARM_TOKEN", "MATGATE_BROWSER_FARM_TOKEN_FILE", 24);

var builder = WebApplication.CreateBuilder(args);
var configuredDataDirectory = Environment.GetEnvironmentVariable("MATGATE_DATA_DIR")
    ?? builder.Configuration["Matgate:DataDirectory"];
var dataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDataDirectory)
    ? Path.Combine(builder.Environment.ContentRootPath, "data")
    : configuredDataDirectory);
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);

builder.WebHost.ConfigureKestrel(options =>
{
    // Uploads/file transfers may be large, so the body size stays uncapped; but keep a lenient
    // minimum data rate so a trickle of bytes can't hold a connection open indefinitely (slowloris).
    options.Limits.MaxRequestBodySize = null;
    options.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 80, gracePeriod: TimeSpan.FromSeconds(20));
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(15);
});

// Matgate is only reachable through the edge reverse proxy (caddy), so honor its forwarded
// scheme/client-IP: this makes Request.IsHttps correct behind TLS termination (so the auth cookie
// becomes Secure automatically) and lets rate limiting key on the real client address.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var requireHttps = string.Equals(
    Environment.GetEnvironmentVariable("MATGATE_REQUIRE_HTTPS"), "true", StringComparison.OrdinalIgnoreCase);

builder.Services
    .Configure<FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = long.MaxValue;
        options.BufferBody = false;
    })
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Matgate.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Behind TLS the forwarded scheme makes this Secure automatically; set MATGATE_REQUIRE_HTTPS=true
        // to force Secure-only (recommended once TLS is terminated at the edge).
        options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/forbidden";
    });

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("Matgate");
builder.Services.AddAuthorization();

// Throttle login attempts per client IP to blunt password brute-forcing.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            }));
});

builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<JsonDataStore>();
builder.Services.AddSingleton<GuacamoleConfigWriter>();
builder.Services.AddSingleton<HtmlViews>();
builder.Services.AddSingleton<GuacamoleLauncher>();
builder.Services.AddSingleton<EphemeralServerStore>();
builder.Services.AddSingleton<BrowserFarmClient>();
builder.Services.AddSingleton<BrowserFarmSessionManager>();
builder.Services.AddHostedService<BrowserFarmReaper>();
builder.Services.AddSingleton<NetworkToolsService>();
builder.Services.AddSingleton<IFileGatewayService, FileGatewayService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<WebsiteProxyService>();

var app = builder.Build();

app.UseForwardedHeaders();

// Never let a browser / installed PWA serve a stale app shell: HTML pages carry the whole app
// (CSS + JS are inlined), so mark every HTML response no-store. Assets served with their own
// cache headers (manifest, icons, proxied content) are text/* or binary and unaffected.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var contentType = context.Response.ContentType;
        if (contentType is not null && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
        }

        return Task.CompletedTask;
    });

    await next();
});

if (requireHttps)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseWebSockets();
app.UseRateLimiter();

// First-run setup gate: while NO user exists at all, every page routes to the setup wizard,
// where the admin account (username + email + password) is created. Runs BEFORE the auth
// middlewares so protected pages redirect straight to /setup instead of bouncing via /login.
// Asset-ish paths (contain a dot: manifest, icons) and internal auth checks stay untouched;
// once a user exists the check is a cheap in-memory count.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (!path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/internal/", StringComparison.OrdinalIgnoreCase)
        && !path.Contains('.'))
    {
        var dataStore = context.RequestServices.GetRequiredService<JsonDataStore>();
        if (!await dataStore.HasUsersAsync(context.RequestAborted))
        {
            context.Response.Redirect("/setup");
            return;
        }
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

var hasher = app.Services.GetRequiredService<PasswordHasher>();
var store = app.Services.GetRequiredService<JsonDataStore>();
await store.EnsureSeedAdminAsync(hasher, app.Logger, app.Lifetime.ApplicationStopping);
await store.EnsureGuacamoleSecretsAsync(hasher, app.Lifetime.ApplicationStopping);
await store.MigrateServerSecretsAsync(app.Lifetime.ApplicationStopping);
await store.EnsureWorkspacePublicAccessDefaultsAsync(TimeSpan.FromHours(24), app.Lifetime.ApplicationStopping);
await store.RemoveLegacyGatewayServersAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<GuacamoleConfigWriter>()
    .SynchronizeAsync(app.Lifetime.ApplicationStopping);

app.MapMatgateEndpoints();

await app.RunAsync();
