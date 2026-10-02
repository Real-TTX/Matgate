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

        // Die Farbtupfer der Protokolle: erst die der Grundpalette, dann die des Themas. Getrennt
        // nach Modus, weil ein Ton, der im Dunkeln leuchtet, im Hellen auf seiner eigenen Toenung
        // verschwindet.
        foreach (var (token, value) in ProtocolValues(baseTheme, dark))
        {
            values["proto-" + token] = value;
        }

        foreach (var (token, value) in ProtocolValues(theme, dark))
        {
            values["proto-" + token] = value;
        }

        var stroke = (theme.IconStroke ?? "").Trim();
        if (stroke.Length > 0)
        {
            values["icon-stroke"] = stroke;
        }

        return values;
    }

    private static IEnumerable<KeyValuePair<string, string>> ProtocolValues(ThemeDefinition theme, bool dark)
    {
        var map = dark && theme.ProtocolsDark.Count > 0 ? theme.ProtocolsDark : theme.Protocols;
        return map.Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => new KeyValuePair<string, string>(entry.Key.Trim().ToLowerInvariant(), entry.Value.Trim()));
    }

    // Was in ein Stylesheet geschrieben werden darf. Ohne diese Pruefung koennte ein Wert wie
    // "#fff; } html { display: none" die Regel schliessen und eine eigene aufmachen - die Datei
    // liegt zwar im Datenverzeichnis, aber eine Konfiguration soll keine Oberflaeche kapern.
    public static bool Accept(string token, string value)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        // Erlaubt ist, was Farben und Schatten brauchen: Ziffern, Buchstaben, Raute, Prozent,
        // Klammern, Punkt, Komma, Schraegstrich, Leerzeichen, Minus.
        foreach (var c in value)
        {
            var erlaubt = char.IsAsciiLetterOrDigit(c)
                || c is '#' or '%' or '(' or ')' or '.' or ',' or '/' or ' ' or '-' or '+';
            if (!erlaubt)
            {
                return false;
            }
        }

        return value.Length <= 120;
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
                ["active-bg"] = "#e7f3ec",
                ["text"] = "#1f2725",
                ["muted"] = "#5d6763",
                ["line"] = "#dce2de",
                ["accent"] = "#176b5b",
                ["accent-2"] = "#2b5876",
                ["danger"] = "#a63a3a",
                ["primary-hover"] = "#115347",
                ["danger-hover"] = "#8a2f2f",
                ["shadow"] = "0 10px 24px rgb(31 39 37 / 8%)",
                ["shadow-strong"] = "0 12px 28px rgb(31 39 37 / 14%)",
                ["radius"] = "8px",
            },
            Protocols = new(StringComparer.OrdinalIgnoreCase)
            {
                ["rdp"] = "#2f6fd0", ["vnc"] = "#7a3fb5", ["ssh"] = "#5b4bc4",
                ["sftp"] = "#8a6410", ["ftp"] = "#a3521f", ["smb"] = "#1a7a4e",
                ["website"] = "#1b6fa8", ["webdav"] = "#15707c", ["local"] = "#176b5b",
            },
            ProtocolsDark = new(StringComparer.OrdinalIgnoreCase)
            {
                ["rdp"] = "#4c8dff", ["vnc"] = "#b06cff", ["ssh"] = "#8a7cff",
                ["sftp"] = "#f0a92b", ["ftp"] = "#e0863a", ["smb"] = "#35c07f",
                ["website"] = "#3aa0ff", ["webdav"] = "#2bb3c0", ["local"] = "#5bc2a8",
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
                ["primary-hover"] = "#7ad3bc",
                ["danger-hover"] = "#e28a8a",
                ["shadow"] = "0 10px 24px rgb(0 0 0 / 32%)",
                ["shadow-strong"] = "0 12px 28px rgb(0 0 0 / 42%)",
                ["radius"] = "8px",
            },
        };

        // Kuehl und neutral: Grafit mit einem blauen Akzent, etwas kantiger (Radius 6).
        yield return new ThemeDefinition
        {
            Key = "graphit",
            Name = "Graphit",
            Light = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bg"] = "#f3f4f6",
                ["panel"] = "#ffffff",
                ["surface"] = "#ffffff",
                ["surface-2"] = "#eceef1",
                ["surface-3"] = "#dcdfe5",
                ["hover-bg"] = "#f6f7f9",
                ["hover-strong-bg"] = "#eef0f3",
                ["active-bg"] = "#e8eef5",
                ["text"] = "#1b1f25",
                ["muted"] = "#5c636d",
                ["line"] = "#dadde3",
                ["accent"] = "#15609e",
                ["accent-2"] = "#5a4fb5",
                ["danger"] = "#b22a28",
                ["primary-hover"] = "#114d7e",
                ["danger-hover"] = "#92201f",
                ["shadow"] = "0 10px 24px rgb(27 31 37 / 8%)",
                ["shadow-strong"] = "0 12px 28px rgb(27 31 37 / 14%)",
                ["radius"] = "6px",
            },
            Dark = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bg"] = "#0d0f12",
                ["panel"] = "#15181d",
                ["surface"] = "#16191e",
                ["surface-2"] = "#1c2026",
                ["surface-3"] = "#252a32",
                ["hover-bg"] = "#1f242b",
                ["hover-strong-bg"] = "#262c34",
                ["active-bg"] = "#1d2a38",
                ["text"] = "#e8eaef",
                ["muted"] = "#9aa2ae",
                ["line"] = "#2d333c",
                ["accent"] = "#5aa9f0",
                ["accent-2"] = "#a79bf0",
                ["danger"] = "#e07b74",
                ["primary-hover"] = "#80bdf5",
                ["danger-hover"] = "#ea958f",
                ["shadow"] = "0 10px 24px rgb(0 0 0 / 34%)",
                ["shadow-strong"] = "0 12px 28px rgb(0 0 0 / 44%)",
                ["radius"] = "6px",
            },
        };

        // Warm: im Hellen gebranntes Kupfer, im Dunklen Amber. Umgekehrt ginge es nicht - Amber ist
        // selbst hell, und die Schrift auf einer Akzentfuellung ist --bg.
        yield return new ThemeDefinition
        {
            Key = "bernstein",
            Name = "Bernstein",
            IconStroke = "2.25",
            Light = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bg"] = "#faf6f0",
                ["panel"] = "#ffffff",
                ["surface"] = "#ffffff",
                ["surface-2"] = "#f3ece1",
                ["surface-3"] = "#e7dccc",
                ["hover-bg"] = "#fbf7f2",
                ["hover-strong-bg"] = "#f5efe6",
                ["active-bg"] = "#fbeeda",
                ["text"] = "#2a2320",
                ["muted"] = "#6b6056",
                ["line"] = "#e6ddd0",
                ["accent"] = "#9a4f16",
                ["accent-2"] = "#2f6563",
                ["danger"] = "#a62f2f",
                ["primary-hover"] = "#7f3f0f",
                ["danger-hover"] = "#8a2626",
                ["shadow"] = "0 10px 24px rgb(42 35 32 / 9%)",
                ["shadow-strong"] = "0 12px 28px rgb(42 35 32 / 16%)",
                ["radius"] = "12px",
            },
            Dark = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bg"] = "#14110d",
                ["panel"] = "#1b1713",
                ["surface"] = "#1d1915",
                ["surface-2"] = "#251f1a",
                ["surface-3"] = "#2e2720",
                ["hover-bg"] = "#292219",
                ["hover-strong-bg"] = "#322a21",
                ["active-bg"] = "#35291a",
                ["text"] = "#f4ece1",
                ["muted"] = "#b0a394",
                ["line"] = "#3a3128",
                ["accent"] = "#e0a24a",
                ["accent-2"] = "#6fc3bd",
                ["danger"] = "#e07a6a",
                ["primary-hover"] = "#edb66a",
                ["danger-hover"] = "#e89588",
                ["shadow"] = "0 10px 24px rgb(0 0 0 / 38%)",
                ["shadow-strong"] = "0 12px 28px rgb(0 0 0 / 50%)",
                ["radius"] = "12px",
            },
            Protocols = new(StringComparer.OrdinalIgnoreCase)
            {
                ["rdp"] = "#3c69be",
                ["vnc"] = "#964baf",
                ["ssh"] = "#7757c1",
                ["sftp"] = "#896510",
                ["ftp"] = "#ab5226",
                ["smb"] = "#1c784d",
                ["website"] = "#1b7298",
                ["webdav"] = "#15707c",
                ["local"] = "#9a4f16",
            },
            ProtocolsDark = new(StringComparer.OrdinalIgnoreCase)
            {
                ["rdp"] = "#7fa8ee",
                ["vnc"] = "#c48bd9",
                ["ssh"] = "#a894e3",
                ["sftp"] = "#d8a33c",
                ["ftp"] = "#e58a5a",
                ["smb"] = "#4cc48c",
                ["website"] = "#5bbbe4",
                ["webdav"] = "#52c4cf",
                ["local"] = "#e0a24a",
            },
        };
    }
}
