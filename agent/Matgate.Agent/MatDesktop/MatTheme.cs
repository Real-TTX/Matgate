// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MatDesktop;

/// <summary>The appearance a user chooses (DESK-02): follow the system, or always light, or always dark.</summary>
public enum MatAppearance
{
    /// <summary>Follow the light or dark mode of the operating system, and change with it.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// What <see cref="MatTheme.Install"/> needs: the app's accent (IDENT-01, the value of <c>--mat-app-accent</c> in <c>matui.apps.css</c>), the colour of text on it and the app's own accent text
/// colour where the app sets them (<c>--mat-app-on-accent</c>, <c>--mat-app-accent-text</c>), and the appearance.
/// </summary>
public sealed record MatThemeOptions(string Accent, string? OnAccent = null, string? AccentText = null, MatAppearance Appearance = MatAppearance.System)
{
    /// <summary>The icon of every window of the program (<see cref="MatWindow.DefaultIcon"/>), or null.</summary>
    public WindowIcon? Icon { get; init; }
}

/// <summary>
/// The theme of a desktop program: Fluent's control templates in the colours and sizes of MatUi, the control themes of the flat look and the resources named after the tokens.
/// <list type="bullet">
/// <item>A brush per colour token in a light and a dark theme dictionary: <c>--mat-surface-2</c> is <c>Mat.Surface2</c> (a <see cref="SolidColorBrush"/>) and <c>Mat.Surface2.Color</c> (its <see cref="Color"/>), see <see cref="KeyOf"/>.</item>
/// <item>Fluent's own keys (<c>SystemAccentColor</c>, the text fields, combo boxes, switches) set to those colours, so that Fluent's controls look like MatUi's.</item>
/// <item>Sizes: <c>Mat.Radius</c> (and <c>Mat.RadiusXs</c> ... <c>Mat.RadiusPill</c>) as <see cref="CornerRadius"/>, <c>Mat.Space1</c> ... <c>Mat.Space12</c> as numbers and <c>Mat.Space4.All</c> as a
/// <see cref="Thickness"/>, <c>Mat.ControlHeight</c> (34, the compact density with a mouse), <c>Mat.ControlHeightSm</c>, <c>Mat.ControlHeightLg</c>, <c>Mat.FontSize</c> (14) and <c>Mat.FontSizeXs</c> ...
/// <c>Mat.FontSize3xl</c>, <c>Mat.Font</c> and <c>Mat.Mono</c> as a <see cref="FontFamily"/> chosen per system.</item>
/// <item>The control themes of <c>Themes/Controls.axaml</c> and the icons of <c>Themes/MatIcons.axaml</c>.</item>
/// </list>
/// A window uses <c>{DynamicResource Mat.Surface}</c> and never a literal colour (DESK-02).
/// </summary>
public static class MatTheme
{
    /// <summary>The options the theme was installed with, or null before <see cref="Install"/>.</summary>
    public static MatThemeOptions? Options { get; private set; }

    /// <summary>The appearance in force.</summary>
    public static MatAppearance Appearance { get; private set; } = MatAppearance.System;

    /// <summary>The resource key of a colour token: <c>--mat-surface-2</c> is <c>Mat.Surface2</c>, <c>--mat-nav-bg</c> is <c>Mat.NavBg</c>.</summary>
    public static string KeyOf(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var parts = token.Replace("--mat-", string.Empty, StringComparison.Ordinal).Split('-', StringSplitOptions.RemoveEmptyEntries);
        return "Mat." + string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// Installs the theme into <paramref name="app"/> (in <c>Application.Initialize</c>, before a window is made): Fluent with the palette of the accent, the icons, the colours of both modes,
    /// the sizes, the control themes, and the appearance of <paramref name="options"/>.
    /// </summary>
    public static void Install(Application app, MatThemeOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        var light = MatPalette.For(options.Accent, dark: false, options.OnAccent, options.AccentText);
        var dark = MatPalette.For(options.Accent, dark: true, options.OnAccent, options.AccentText);

        var fluent = new FluentTheme();
        fluent.Palettes[ThemeVariant.Light] = Palette(light);
        fluent.Palettes[ThemeVariant.Dark] = Palette(dark);
        app.Styles.Add(fluent);

        // the two dictionaries of XAML are classes: they load in an app that copied the files into a folder of its own, whatever its assembly is called
        app.Resources.MergedDictionaries.Add(new MatIconResources());
        app.Resources.MergedDictionaries.Add(Colors(light, dark));
        app.Resources.MergedDictionaries.Add(Metrics());
        app.Resources.MergedDictionaries.Add(new MatControlThemes());
        if (options.Icon is not null)
        {
            MatWindow.DefaultIcon = options.Icon;
        }

        SetAppearance(options.Appearance);
    }

    /// <summary>Switches between System, Light and Dark (DESK-02): System follows the operating system and changes with it.</summary>
    public static void SetAppearance(MatAppearance appearance)
    {
        Appearance = appearance;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = appearance switch
            {
                MatAppearance.Light => ThemeVariant.Light,
                MatAppearance.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    /// <summary>The colour of a token in the mode the application shows now (for drawing outside the visual tree: the tray icon, a menu icon).</summary>
    public static MatRgba ColorOf(string token, bool? dark = null)
    {
        var isDark = dark ?? Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var options = Options ?? new MatThemeOptions(MatTokens.DefaultAccent);
        return MatPalette.For(options.Accent, isDark, options.OnAccent, options.AccentText)[token];
    }

    /// <summary>A token colour as an Avalonia colour.</summary>
    public static Color ToColor(MatRgba c) => Color.FromArgb(c.A8, c.R8, c.G8, c.B8);

    /// <summary>The font of the interface: Segoe UI Variable on Windows (the system's), the system font on macOS, the stack of the web elsewhere.</summary>
    public static FontFamily UiFont() => OperatingSystem.IsWindows()
        ? new FontFamily("Segoe UI Variable Text, Segoe UI")
        : OperatingSystem.IsMacOS() ? FontFamily.Default : new FontFamily(Stack(MatTokens.Type.Font, "-apple-system", "BlinkMacSystemFont", "sans-serif"));

    /// <summary>The monospaced font for versions, ports, codes: Cascadia Mono or Consolas on Windows, the stack of the web elsewhere.</summary>
    public static FontFamily MonoFont() => OperatingSystem.IsWindows()
        ? new FontFamily("Cascadia Mono, Consolas")
        : new FontFamily(Stack(MatTokens.Type.Mono, "ui-monospace", "monospace"));

    /// <summary>A CSS font stack as an Avalonia family list, without the names only a browser knows.</summary>
    internal static string Stack(string css, params string[] browserOnly) => string.Join(", ", css.Split(',')
        .Select(f => f.Trim().Trim('"'))
        .Where(f => f.Length > 0 && !browserOnly.Contains(f, StringComparer.OrdinalIgnoreCase)));

    private static ColorPaletteResources Palette(MatPalette p) => new()
    {
        Accent = ToColor(p.Accent),
        RegionColor = ToColor(p.Surface),
        ErrorText = ToColor(p["--mat-danger-text"]),
        BaseHigh = ToColor(p.Text),
        BaseMediumHigh = ToColor(p.TextMuted),
        AltHigh = ToColor(p.Surface),
    };

    /// <summary>The light and the dark theme dictionary: a brush and a colour per token, the derived ones of MatDesktop and Fluent's keys.</summary>
    internal static ResourceDictionary Colors(MatPalette light, MatPalette dark)
    {
        var dictionary = new ResourceDictionary();
        dictionary.ThemeDictionaries[ThemeVariant.Light] = Mode(light);
        dictionary.ThemeDictionaries[ThemeVariant.Dark] = Mode(dark);
        return dictionary;
    }

    /// <summary>The keys of the colours that MatDesktop adds to the tokens (derived the way the CSS of a control derives them).</summary>
    public static IReadOnlyList<string> DerivedKeys { get; } = ["Mat.DangerLine"];

    private static ResourceDictionary Mode(MatPalette p)
    {
        var d = new ResourceDictionary();
        foreach (var (token, colour) in p.Colors)
        {
            Add(d, KeyOf(token), colour);
        }

        // the line of a danger button: color-mix(in srgb, var(--mat-danger) 45%, transparent) (matui.controls.css)
        Add(d, "Mat.DangerLine", p.DangerLine);

        // Fluent's keys: the text fields, combo boxes, switches and check boxes in the colours of MatUi's fields (matui.forms.css)
        var surface = p.Surface;
        var strong = p["--mat-border-strong"];
        var faint = p["--mat-text-faint"];
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "ComboBoxBackground", "ComboBoxDropDownBackground" })
        {
            Brush(d, key, surface);
        }

        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxDropDownBorderBrush" })
        {
            Brush(d, key, strong);
        }

        Brush(d, "TextControlBorderBrushFocused", p.Accent);
        Brush(d, "TextControlForeground", p.Text);
        Brush(d, "TextControlForegroundPointerOver", p.Text);
        Brush(d, "TextControlForegroundFocused", p.Text);
        Brush(d, "TextControlPlaceholderForeground", faint);
        Brush(d, "TextControlPlaceholderForegroundPointerOver", faint);
        Brush(d, "TextControlPlaceholderForegroundFocused", faint);
        Brush(d, "TextControlSelectionHighlightColor", p["--mat-selection"]);
        Brush(d, "ComboBoxBackgroundPointerOver", p["--mat-surface-2"]);
        Brush(d, "ComboBoxBackgroundPressed", p["--mat-surface-3"]);
        Brush(d, "ComboBoxForeground", p.Text);
        Brush(d, "ToggleSwitchFillOff", p["--mat-surface-3"]);
        Brush(d, "ToggleSwitchFillOffPointerOver", p["--mat-surface-3"]);
        Brush(d, "ToggleSwitchStrokeOff", strong);
        Brush(d, "ToggleSwitchStrokeOffPointerOver", strong);
        Brush(d, "ToggleSwitchKnobFillOff", p.TextMuted);
        Brush(d, "ToggleSwitchKnobFillOffPointerOver", p.Text);
        Brush(d, "ToggleSwitchFillOn", p.Accent);
        Brush(d, "ToggleSwitchFillOnPointerOver", p["--mat-accent-hover"]);
        Brush(d, "ToggleSwitchStrokeOn", p.Accent);
        Brush(d, "ToggleSwitchKnobFillOn", p.OnAccent);
        Brush(d, "ToggleSwitchKnobFillOnPointerOver", p.OnAccent);
        Brush(d, "CheckBoxCheckBackgroundFillChecked", p.Accent);
        Brush(d, "CheckBoxCheckBackgroundStrokeChecked", p.Accent);
        Brush(d, "CheckBoxCheckGlyphForegroundChecked", p.OnAccent);
        Brush(d, "CheckBoxCheckBackgroundStrokeUnchecked", strong);
        return d;
    }

    private static void Add(ResourceDictionary d, string key, MatRgba colour)
    {
        d[key] = new SolidColorBrush(ToColor(colour));
        d[key + ".Color"] = ToColor(colour);
    }

    private static void Brush(ResourceDictionary d, string key, MatRgba colour) => d[key] = new SolidColorBrush(ToColor(colour));

    /// <summary>The sizes, the type and Fluent's keys of shape (the same in both modes).</summary>
    internal static ResourceDictionary Metrics()
    {
        var d = new ResourceDictionary
        {
            ["Mat.RadiusXs"] = new CornerRadius(MatTokens.Size.RadiusXs),
            ["Mat.RadiusSm"] = new CornerRadius(MatTokens.Size.RadiusSm),
            ["Mat.Radius"] = new CornerRadius(MatTokens.Size.Radius),
            ["Mat.RadiusLg"] = new CornerRadius(MatTokens.Size.RadiusLg),
            ["Mat.RadiusXl"] = new CornerRadius(MatTokens.Size.RadiusXl),
            ["Mat.RadiusPill"] = new CornerRadius(MatTokens.Size.RadiusPill),
            ["Mat.ControlHeight"] = MatTokens.Size.ControlH,
            ["Mat.ControlHeightSm"] = MatTokens.Size.ControlHSm,
            ["Mat.ControlHeightLg"] = MatTokens.Size.ControlHLg,
            ["Mat.FocusRingWidth"] = MatTokens.FocusRingWidth,
            ["Mat.FontSizeXs"] = MatTokens.Type.FsXs,
            ["Mat.FontSizeSm"] = MatTokens.Type.FsSm,
            ["Mat.FontSize"] = MatTokens.Type.FsMd,
            ["Mat.FontSizeLg"] = MatTokens.Type.FsLg,
            ["Mat.FontSizeXl"] = MatTokens.Type.FsXl,
            ["Mat.FontSize2xl"] = MatTokens.Type.Fs2xl,
            ["Mat.FontSize3xl"] = MatTokens.Type.Fs3xl,
            ["Mat.Font"] = UiFont(),
            ["Mat.Mono"] = MonoFont(),

            // the close button of Windows under the pointer: the system's red in every theme (MatTitleBar says why)
            ["Mat.CaptionCloseHover"] = new SolidColorBrush(Color.Parse(MatTitleBar.WindowsCloseHover)),
            ["Mat.CaptionCloseHoverGlyph"] = new SolidColorBrush(Color.Parse(MatTitleBar.WindowsCloseHoverGlyph)),

            // Fluent: corners and the height of a field like MatUi's (34 px with a mouse)
            ["ControlCornerRadius"] = new CornerRadius(MatTokens.Size.Radius),
            ["OverlayCornerRadius"] = new CornerRadius(MatTokens.Size.RadiusLg),
            ["TextControlThemeMinHeight"] = MatTokens.Size.ControlH,
            ["TextControlThemePadding"] = new Thickness(MatTokens.Size.Space3, 6, MatTokens.Size.Space3, 6),
            ["TextControlBorderThemeThicknessFocused"] = new Thickness(1),
        };
        foreach (var (name, value) in new (string, double)[]
                 {
                     ("Space1", MatTokens.Size.Space1), ("Space2", MatTokens.Size.Space2), ("Space3", MatTokens.Size.Space3), ("Space4", MatTokens.Size.Space4),
                     ("Space5", MatTokens.Size.Space5), ("Space6", MatTokens.Size.Space6), ("Space8", MatTokens.Size.Space8), ("Space10", MatTokens.Size.Space10),
                     ("Space12", MatTokens.Size.Space12),
                 })
        {
            d["Mat." + name] = value;
            d["Mat." + name + ".All"] = new Thickness(value);
        }

        return d;
    }
}
