using System.Text.Json;
using Matgate.Models;

namespace Matgate.Services;

// The palettes: the built-in ones, and on top of them whatever a file in the data directory adds
//
// or replaces. A palette only defines what it wants to change. Everything else falls back to
// "matgate" - which is why a file can be three lines long ("different accent") and still yield a
// complete theme. If the file is missing, broken or nonsense, the built-in ones stay and it goes
// into the log; an unreadable file must not take the UI down with it.
public sealed class ThemeService
{
    public const string DefaultKey = "matgate";

    // The colour tokens the stylesheet reads. A theme may set any of them; what it leaves out comes
    // from the base palette.
    public static readonly string[] Tokens =
    [
        "bg", "panel", "surface", "surface-2", "surface-3",
        "hover-bg", "hover-strong-bg", "active-bg",
        "text", "muted", "line",
        "accent", "accent-2", "danger", "primary-hover", "danger-hover",
        "shadow", "shadow-strong", "radius", "radius-lg", "control-height",
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
            // The file may be edited while running - it is re-read when it has changed, but at most every few
            // seconds, so that not every page hits the disk.
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

    // The finished values of a theme for one mode: the base palette first, then the theme on top.
    public IReadOnlyDictionary<string, string> Values(
        ThemeDefinition theme,
        bool dark,
        string? accentOverride = null,
        string? accent2Override = null,
        string? backgroundOverride = null)
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

        // The protocol colour dots: the base palette's first, then the theme's. Kept separate per mode,
        // because a shade that glows in the dark disappears into its own tint in the light.
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

        // The user's own background first: everything after it - including how readable the accents are -
        // depends on what it ends up sitting on.
        if (IsColour(backgroundOverride))
        {
            ApplyBackground(values, backgroundOverride!, dark ? baseTheme.Dark : baseTheme.Light,
                dark ? baseTheme.Light : baseTheme.Dark);
        }

        // The user's own accent colours last, so they also override a theme loaded from file. What was
        // picked is not necessarily what gets set: the text on top of it is --bg.
        var background = values.GetValueOrDefault("bg", dark ? "#0f1412" : "#ffffff");
        if (IsColour(accentOverride))
        {
            var safe = SafeAccent(accentOverride!, background);
            values["accent"] = safe;
            values["primary-hover"] = HoverAccent(safe, background);
            values["proto-local"] = safe;
        }

        // The second accent carries no text - it colours the logo, gradients and secondary highlights. So
        // the threshold for surfaces (3.0) is enough for it instead of the one for text (4.5); measured
        // more strictly, every other shade would be lightened for no reason.
        if (IsColour(accent2Override))
        {
            values["accent-2"] = SafeAccent(accent2Override!, background, 3.0);
        }

        return values;
    }

    // A freely chosen background is only as good as what sits on it. The surfaces are therefore
    // derived from it - fields, lines, hover - and text and shadow come from whichever side matches
    // its brightness: picking black in a light theme gets the light text of the dark one, otherwise
    // it would be dark on dark.
    private static void ApplyBackground(
        Dictionary<string, string> values,
        string background,
        IReadOnlyDictionary<string, string> sameSide,
        IReadOnlyDictionary<string, string> otherSide)
    {
        if (!TryParse(background, out var r, out var g, out var b))
        {
            return;
        }

        var isDark = Luminance(r, g, b) < 0.4;
        var matching = isDark == IsDark(sameSide) ? sameSide : otherSide;

        foreach (var token in new[] { "text", "muted", "shadow", "shadow-strong" })
        {
            if (matching.TryGetValue(token, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                values[token] = value.Trim();
            }
        }

        values["bg"] = Normalise(background);

        // A dark ground carries lighter surfaces, a light one carries white fields and recessed wells.
        // The numbers are read off the built-in palettes.
        (string Token, int Percent)[] steps = isDark
            ?
            [
                ("panel", 6), ("surface", 7), ("surface-2", 12), ("surface-3", 18),
                ("hover-bg", 14), ("hover-strong-bg", 20), ("active-bg", 24), ("line", 28),
            ]
            :
            [
                ("panel", 45), ("surface", 45), ("surface-2", -4), ("surface-3", -12),
                ("hover-bg", 25), ("hover-strong-bg", -3), ("active-bg", -8), ("line", -14),
            ];

        foreach (var (token, percent) in steps)
        {
            var (sr, sg, sb) = Shift(r, g, b, percent);
            values[token] = $"#{sr:x2}{sg:x2}{sb:x2}";
        }
    }

    private static bool IsDark(IReadOnlyDictionary<string, string> side)
    {
        return TryParse(side.GetValueOrDefault("bg", "#ffffff"), out var r, out var g, out var b)
            && Luminance(r, g, b) < 0.4;
    }

    private static string Normalise(string hex)
    {
        return TryParse(hex, out var r, out var g, out var b) ? $"#{r:x2}{g:x2}{b:x2}" : hex.Trim();
    }

    private static IEnumerable<KeyValuePair<string, string>> ProtocolValues(ThemeDefinition theme, bool dark)
    {
        var map = dark && theme.ProtocolsDark.Count > 0 ? theme.ProtocolsDark : theme.Protocols;
        return map.Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => new KeyValuePair<string, string>(entry.Key.Trim().ToLowerInvariant(), entry.Value.Trim()));
    }

    // What may be written into a stylesheet. Without this check a value like "#fff; } html { display:
    // none" could close the rule and open one of its own - the file does live in the data directory,
    // but a configuration should not be able to hijack a UI.
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

        // Allowed is what colours and shadows need: digits, letters, hash, percent, parentheses, dot,
        // comma, slash, space, minus.
        foreach (var c in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(c)
                || c is '#' or '%' or '(' or ')' or '.' or ',' or '/' or ' ' or '-' or '+';
            if (!allowed)
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
                            _logger.LogWarning("A theme in {Path} has no key and is skipped.", _filePath);
                            continue;
                        }

                        theme.Key = theme.Key.Trim().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(theme.Name))
                        {
                            theme.Name = theme.Key;
                        }

                        // The same key means replace: that way a built-in theme can be adjusted without ending up in the
                        // list twice.
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

                    _logger.LogInformation("{Count} themes loaded, {Extra} of them from {Path}.",
                        themes.Count, (fromFile ?? []).Count, _filePath);
                }
                else
                {
                    _fileStamp = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                // Better the built-in themes than no UI at all.
                _logger.LogWarning(ex, "{Path} could not be read; the built-in themes apply.", _filePath);
                _fileStamp = DateTime.MinValue;
            }

            _themes = themes;
            _loadedAt = DateTimeOffset.UtcNow;
        }
    }

    // Text on an accent surface is --bg, so a freely chosen colour can become unreadable there - light
    // green on near-white is 1.4 instead of the required 4.5. Rather than rejecting the choice, the
    // hue is kept and the lightness pushed in the opposite direction until it is enough: the user gets
    // their colour, just in a shade that can be read.
    public static string SafeAccent(string hex, string background, double minimum = 4.5)
    {
        if (!TryParse(hex, out var r, out var g, out var b) || !TryParse(background, out var br, out var bg2, out var bb))
        {
            return hex;
        }

        var backgroundLum = Luminance(br, bg2, bb);
        var darkBackground = backgroundLum < 0.5;

        for (var step = 0; step <= 100; step++)
        {
            var (rr, gg, bbb) = Shift(r, g, b, darkBackground ? step : -step);
            if (Contrast(Luminance(rr, gg, bbb), backgroundLum) >= minimum)
            {
                return $"#{rr:x2}{gg:x2}{bbb:x2}";
            }
        }

        return darkBackground ? "#ffffff" : "#000000";
    }

    // The same colour, a touch stronger - for the hover state.
    public static string HoverAccent(string hex, string background)
    {
        if (!TryParse(hex, out var r, out var g, out var b) || !TryParse(background, out _, out _, out _))
        {
            return hex;
        }

        var isDark = Luminance(r, g, b) < 0.4;
        var (rr, gg, bb) = Shift(r, g, b, isDark ? 8 : -8);
        return SafeAccent($"#{rr:x2}{gg:x2}{bb:x2}", background);
    }

    public static bool IsColour(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && TryParse(value, out _, out _, out _);
    }

    private static (int R, int G, int B) Shift(int r, int g, int b, int percent)
    {
        int Scaled(int value) => percent >= 0
            ? (int)Math.Round(value + (255 - value) * (percent / 100.0))
            : (int)Math.Round(value * (1 + percent / 100.0));
        return (Math.Clamp(Scaled(r), 0, 255), Math.Clamp(Scaled(g), 0, 255), Math.Clamp(Scaled(b), 0, 255));
    }

    private static bool TryParse(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(c => new string(c, 2)));
        }

        if (value.Length != 6 || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        r = Convert.ToInt32(value[..2], 16);
        g = Convert.ToInt32(value.Substring(2, 2), 16);
        b = Convert.ToInt32(value.Substring(4, 2), 16);
        return true;
    }

    private static double Luminance(int r, int g, int b)
    {
        double Channel(int value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static double Contrast(double a, double b)
    {
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // Without the service: the built-in base palette. Only needed when resolving it from the request
    // context fails.
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
                ["radius-lg"] = "12px",
                ["control-height"] = "32px",
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
                ["radius-lg"] = "12px",
                ["control-height"] = "32px",
            },
        };

        // Cool and neutral: graphite with a blue accent, a little more angular (radius 6).
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
                ["radius-lg"] = "8px",
                ["control-height"] = "32px",
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
                ["radius-lg"] = "8px",
                ["control-height"] = "32px",
            },
        };

        // Warm: burnt copper in light mode, amber in dark. The other way round would not work - amber is
        // light itself, and the text on an accent fill is --bg.
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
                ["radius-lg"] = "18px",
                ["control-height"] = "38px",
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
                ["radius-lg"] = "18px",
                ["control-height"] = "38px",
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
