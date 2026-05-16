using Matgate.Services;
using Matgate.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
var configuredDataDirectory = Environment.GetEnvironmentVariable("MATGATE_DATA_DIR")
    ?? builder.Configuration["Matgate:DataDirectory"];
var dataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDataDirectory)
    ? Path.Combine(builder.Environment.ContentRootPath, "data")
    : configuredDataDirectory);
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);

builder.Services
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
builder.Services.AddSingleton<IFileGatewayService, FileGatewayService>();

var app = builder.Build();

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
