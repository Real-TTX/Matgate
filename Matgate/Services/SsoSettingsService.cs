using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Matgate.Services;

// The sign-in services as the administration sets them up (Administration -> Sign-in), kept in sso.json - the client
// secrets encrypted like the connections' passwords. Until that file exists, the environment decides
// (MATIDENTITY_*, MATGATE_ENTRA_*, MATGATE_SSO_ONLY), so a stack configured that way keeps working; saving in the
// administration takes over from then on. A change applies at once: the services' options are read anew.
public sealed class SsoConfig
{
    public bool MatIdentityEnabled { get; set; }

    public string MatIdentityAuthority { get; set; } = "";

    public string MatIdentityClientId { get; set; } = "";

    public string MatIdentityClientSecret { get; set; } = "";

    public string MatIdentityInternalUrl { get; set; } = "";

    public bool EntraEnabled { get; set; }

    public string EntraTenantId { get; set; } = "";

    public string EntraClientId { get; set; } = "";

    public string EntraClientSecret { get; set; } = "";

    public bool SsoOnly { get; set; }

    // Names for what the services send: Entra sends a group as its object id ("entra:3f2a...") - here it gets a
    // name ("Technik") to be shown wherever it appears. Key "<provider>:<value>".
    public Dictionary<string, string> GroupNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SsoSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly IConfiguration _configuration;
    private readonly SecretProtector _protector;
    private readonly IServiceProvider _services;
    private readonly object _gate = new();
    private SsoSettings _current;

    public SsoSettingsService(JsonDataStore store, IConfiguration configuration, SecretProtector protector, IServiceProvider services)
    {
        _path = Path.Combine(store.DataDirectory, "sso.json");
        _configuration = configuration;
        _protector = protector;
        _services = services;
        _current = Build(Load());
    }

    public SsoSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    // Set up in the administration (true) or still from the environment.
    public bool FromFile => File.Exists(_path);

    // What the administration shows: the file, or what the environment says (secrets as they are).
    public SsoConfig Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<SsoConfig>(File.ReadAllText(_path), JsonOptions) ?? new SsoConfig();
                stored.GroupNames = new(stored.GroupNames ?? [], StringComparer.OrdinalIgnoreCase);
                stored.MatIdentityClientSecret = _protector.Unprotect(stored.MatIdentityClientSecret);
                stored.EntraClientSecret = _protector.Unprotect(stored.EntraClientSecret);
                return stored;
            }
            catch (JsonException)
            {
                return new SsoConfig();
            }
        }

        string Value(string name) => (_configuration[name] ?? "").Trim();
        return new SsoConfig
        {
            MatIdentityEnabled = Value("MATIDENTITY_AUTHORITY").Length > 0 && Value("MATIDENTITY_CLIENT_ID").Length > 0,
            MatIdentityAuthority = Value("MATIDENTITY_AUTHORITY"),
            MatIdentityClientId = Value("MATIDENTITY_CLIENT_ID"),
            MatIdentityClientSecret = Value("MATIDENTITY_CLIENT_SECRET"),
            MatIdentityInternalUrl = Value("MATIDENTITY_INTERNAL_URL"),
            EntraEnabled = Value("MATGATE_ENTRA_TENANT_ID").Length > 0 && Value("MATGATE_ENTRA_CLIENT_ID").Length > 0,
            EntraTenantId = Value("MATGATE_ENTRA_TENANT_ID"),
            EntraClientId = Value("MATGATE_ENTRA_CLIENT_ID"),
            EntraClientSecret = Value("MATGATE_ENTRA_CLIENT_SECRET"),
            SsoOnly = string.Equals(Value("MATGATE_SSO_ONLY"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    public void Save(SsoConfig config)
    {
        var stored = new SsoConfig
        {
            MatIdentityEnabled = config.MatIdentityEnabled,
            MatIdentityAuthority = config.MatIdentityAuthority.Trim(),
            MatIdentityClientId = config.MatIdentityClientId.Trim(),
            MatIdentityClientSecret = _protector.Protect(config.MatIdentityClientSecret),
            MatIdentityInternalUrl = config.MatIdentityInternalUrl.Trim(),
            EntraEnabled = config.EntraEnabled,
            EntraTenantId = config.EntraTenantId.Trim(),
            EntraClientId = config.EntraClientId.Trim(),
            EntraClientSecret = _protector.Protect(config.EntraClientSecret),
            SsoOnly = config.SsoOnly,
            GroupNames = config.GroupNames.Where(pair => pair.Value.Trim().Length > 0).ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase),
        };
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
        File.Move(temp, _path, overwrite: true);
        lock (_gate)
        {
            _current = Build(config);
        }

        // The services read their options anew at the next sign-in.
        var cache = _services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
        cache.TryRemove(SsoProvider.MatIdentity);
        cache.TryRemove(SsoProvider.Entra);
    }

    private static SsoSettings Build(SsoConfig config)
    {
        var values = new Dictionary<string, string?>();
        if (config.MatIdentityEnabled)
        {
            values["MATIDENTITY_AUTHORITY"] = config.MatIdentityAuthority;
            values["MATIDENTITY_CLIENT_ID"] = config.MatIdentityClientId;
            values["MATIDENTITY_CLIENT_SECRET"] = config.MatIdentityClientSecret;
            values["MATIDENTITY_INTERNAL_URL"] = config.MatIdentityInternalUrl;
        }

        if (config.EntraEnabled)
        {
            values["MATGATE_ENTRA_TENANT_ID"] = config.EntraTenantId;
            values["MATGATE_ENTRA_CLIENT_ID"] = config.EntraClientId;
            values["MATGATE_ENTRA_CLIENT_SECRET"] = config.EntraClientSecret;
        }

        values["MATGATE_SSO_ONLY"] = config.SsoOnly ? "true" : "false";
        return SsoSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
}
