using Matgate.Services;
using Matgate.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.DataProtection;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
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
    options.Limits.MaxRequestBodySize = null;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(15);
});

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
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/forbidden";
    });

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("Matgate");
builder.Services.AddAuthorization();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<JsonDataStore>();
builder.Services.AddSingleton<GuacamoleConfigWriter>();
builder.Services.AddSingleton<HtmlViews>();
builder.Services.AddSingleton<GuacamoleLauncher>();
builder.Services.AddSingleton<NetworkToolsService>();
builder.Services.AddSingleton<IFileGatewayService, FileGatewayService>();
builder.Services.AddSingleton<WebsiteProxyService>();

var app = builder.Build();

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

var hasher = app.Services.GetRequiredService<PasswordHasher>();
var store = app.Services.GetRequiredService<JsonDataStore>();
await store.EnsureSeedAdminAsync(hasher, app.Logger, app.Lifetime.ApplicationStopping);
await store.EnsureGuacamoleSecretsAsync(hasher, app.Lifetime.ApplicationStopping);
await store.RemoveLegacyGatewayServersAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<GuacamoleConfigWriter>()
    .SynchronizeAsync(app.Lifetime.ApplicationStopping);

app.MapMatgateEndpoints();

await app.RunAsync();
