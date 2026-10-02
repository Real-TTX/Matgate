using System.Text.Json;
using Matgate.Models;

namespace Matgate.Services;

// Die Paletten: eingebaut, und darüber das, was eine Datei im Datenverzeichnis ergänzt oder ersetzt.
//
// Eine Palette definiert nur, was sie ändern will. Alles Übrige fällt auf "matgate" zurück - deshalb
// kann eine Datei aus drei Zeilen bestehen ("Akzent anders") und trotzdem ein vollständiges Thema
// ergeben. Fehlt die Datei, ist sie kaputt oder nennt sie Unsinn, bleiben die eingebauten stehen und
// es steht im Protokoll; eine unleserliche Datei darf die Oberfläche nicht mitnehmen.
public sealed class ThemeService
{
    public const string DefaultKey = "matgate";

    // Die Farbtoken, die das Stylesheet liest. Ein Thema darf jedes davon setzen; was es auslässt,
    // kommt aus der Grundpalette.
    public static readonly string[] Tokens =
    [
        "bg", "panel", "surface", "surface-2", "surface-3",
        "hover-bg", "hover-strong-bg", "active-bg",
        "text", "muted", "line",
        "accent", "accent-2", "danger", "primary-hover", "danger-hover",
        "shadow", "shadow-strong", "radius",
    ];

    private readonly ILogger<ThemeService> _logger;
    private readonly string _filePath;
    private readonly object _gate = new();
    private IReadOnlyList<ThemeDefinition> _themes = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private DateTime _fileStamp = DateTime.MinValue;

    public ThemeService(IConfiguration configuration, IHostEnvironment environment, ILogger<ThemeService> logger)
    {
        _logger = logger;

        var dataDirectory = Environment.GetEnvironmentVariable("MATGATE_DATA_DIR")
            ?? configuration["Matgate:DataDirectory"]
            ?? Path.Combine(environment.ContentRootPath, "data");

        _filePath = Path.Combine(Path.GetFullPath(dataDirectory), "themes.json");
        Reload();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<ThemeDefinition> All
    {
        get
        {
            // Die Datei darf im Betrieb geändert werden - gelesen wird sie, wenn sie sich gerührt
            // hat, höchstens aber alle paar Sekunden, damit jede Seite nicht auf die Platte geht.
            if (DateTimeOffset.UtcNow - _loadedAt > TimeSpan.FromSeconds(5))
            {
                var stamp = File.Exists(_filePath) ? File.GetLastWriteTimeUtc(_filePath) : DateTime.MinValue;
                if (stamp != _fileStamp)
                {
                    Reload();
                }
                else
                {
                    _loadedAt = DateTimeOffset.UtcNow;
                }
            }

            return _themes;
        }
    }

    public ThemeDefinition Resolve(string? key)
    {
        var all = All;
        return all.FirstOrDefault(theme => string.Equals(theme.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            ?? all.First(theme => theme.Key == DefaultKey);
    }

    // Die fertigen Werte eines Themas für einen Modus: erst die Grundpalette, dann das Thema darüber.
    public IReadOnlyDictionary<string, string> Values(ThemeDefinition theme, bool dark)
    {
        var baseTheme = All.First(entry => entry.Key == DefaultKey);
        var values = new Dictionary<string, string>(dark ? baseTheme.Dark : baseTheme.Light, StringComparer.OrdinalIgnoreCase);

        foreach (var (token, value) in dark ? theme.Dark : theme.Light)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[token] = value.Trim();
            }
        }

        return values;
    }

    private void Reload()
    {
        lock (_gate)
        {
            var themes = BuiltIn().ToList();

            try
            {
                if (File.Exists(_filePath))
                {
                    _fileStamp = File.GetLastWriteTimeUtc(_filePath);
                    var fromFile = JsonSerializer.Deserialize<List<ThemeDefinition>>(
                        File.ReadAllText(_filePath),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));

                    foreach (var theme in fromFile ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(theme.Key))
                        {
                            _logger.LogWarning("Ein Thema in {Path} hat keinen Schlüssel und wird übergangen.", _filePath);
                            continue;
                        }

                        theme.Key = theme.Key.Trim().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(theme.Name))
                        {
                            theme.Name = theme.Key;
                        }

                        // Gleicher Schlüssel heißt ersetzen: so lässt sich auch ein eingebautes Thema
                        // anpassen, ohne dass es zweimal in der Liste steht.
                        var existing = themes.FindIndex(entry => entry.Key == theme.Key);
                        if (existing >= 0)
                        {
                            themes[existing] = theme;
                        }
                        else
                        {
                            themes.Add(theme);
                        }
                    }

                    _logger.LogInformation("{Count} Themen geladen, davon {Extra} aus {Path}.",
                        themes.Count, (fromFile ?? []).Count, _filePath);
                }
                else
                {
                    _fileStamp = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                // Lieber die eingebauten Themen als gar keine Oberfläche.
                _logger.LogWarning(ex, "{Path} konnte nicht gelesen werden; es gelten die eingebauten Themen.", _filePath);
                _fileStamp = DateTime.MinValue;
            }

            _themes = themes;
            _loadedAt = DateTimeOffset.UtcNow;
        }
    }

    // Ohne Dienst: die eingebaute Grundpalette. Wird nur gebraucht, wenn die Aufloesung aus dem
    // Anfragekontext fehlschlaegt.
    public static IReadOnlyDictionary<string, string> FallbackValues(bool dark)
    {
        var basis = BuiltIn().First();
        return dark ? basis.Dark : basis.Light;
    }

    private static IEnumerable<ThemeDefinition> BuiltIn()
    {
        yield return new ThemeDefinition
        {
            Key = DefaultKey,
            Name = "Matgate",
            Light = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bg"] = "#f4f6f4",
                ["panel"] = "#ffffff",
                ["surface"] = "#ffffff",
                ["surface-2"] = "#eef2ef",
                ["surface-3"] = "#dfe7e3",
                ["hover-bg"] = "#f5f8f6",
                ["hover-strong-bg"] = "#eef4f1",
                ["active-bg"] = "#eef7f1",
                ["text"] = "#1f2725",
                ["muted"] = "#67706c",
                ["line"] = "#dce2de",
                ["accent"] = "#176b5b",
                ["accent-2"] = "#2b5876",
                ["danger"] = "#a63a3a",
                ["primary-hover"] = "#145d4f",
                ["danger-hover"] = "#923232",
                ["shadow"] = "0 10px 24px rgb(31 39 37 / 8%)",
                ["shadow-strong"] = "0 12px 28px rgb(31 39 37 / 14%)",
                ["radius"] = "8px",
            },
            Dark = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bg"] = "#0f1412",
                ["panel"] = "#161c19",
                ["surface"] = "#171d1a",
                ["surface-2"] = "#1d2421",
                ["surface-3"] = "#232c28",
                ["hover-bg"] = "#202823",
                ["hover-strong-bg"] = "#26312c",
                ["active-bg"] = "#1f352f",
                ["text"] = "#edf2ef",
                ["muted"] = "#a0aca6",
                ["line"] = "#2f3d37",
                ["accent"] = "#5bc2a8",
                ["accent-2"] = "#8cb8e0",
                ["danger"] = "#d46f6f",
                ["primary-hover"] = "#4aa78f",
                ["danger-hover"] = "#bd5f5f",
                ["shadow"] = "0 10px 24px rgb(0 0 0 / 32%)",
                ["shadow-strong"] = "0 12px 28px rgb(0 0 0 / 42%)",
                ["radius"] = "8px",
            },
        };
    }
}
