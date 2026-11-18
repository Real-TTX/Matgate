using System.Text;
using System.Text.Json;

namespace Matgate.Models;

// One button of the key bar: the row of keys a phone's keyboard does not have (Esc, Ctrl, the arrows) and
// the shortcuts someone wants within reach (Ctrl+C, Ctrl+V). Every user has their own list, configured
// under Account -> Key bar.
public sealed class KeyBarButton
{
    // Names the button in the addresses of the account pages. Lower-case letters, digits and dashes.
    public string Id { get; set; } = "";

    // What is written on the button. Empty means: derived from the keys, in the user's language.
    public string Label { get; set; } = "";

    // The modifiers (Ctrl, Alt, Shift, Win) first, then at most one key: a name out of KeyBar.Named or a
    // single character. Letters are stored upper-case, as they are printed on a keyboard.
    public List<string> Keys { get; set; } = [];

    // For modifiers alone: the button stays down until the next key instead of being tapped.
    public bool Sticky { get; set; }

    // Where the button shows: everywhere, on desktops only (RDP, VNC, an agent's screen) or in terminals
    // only (SSH, an agent's programs).
    public string Scope { get; set; } = KeyBar.ScopeAll;
}

// The key bar's one translation: what a button's keys are on each of the three kinds of session.
//   - the screen page (RDP and VNC through Matgate's own engine, an agent's screen): scan codes, with the
//     keysym beside them for VNC; a character goes as text, which every engine turns into the right key
//   - the terminal page (SSH, an agent's programs): the bytes a terminal sends for the key
//   - Guacamole sessions: keysyms
public static class KeyBar
{
    public const string ScopeAll = "all";
    public const string ScopeScreen = "screen";
    public const string ScopeTerminal = "terminal";

    public const int MaxButtons = 40;
    public const int MaxLabelLength = 12;

    public const string ActionSecureAttention = "cad";
    public const string ActionPaste = "paste";

    // The bits of a set of modifiers, as xterm counts them (its parameter is this number plus one).
    public const int MaskShift = 1;
    public const int MaskAlt = 2;
    public const int MaskCtrl = 4;

    public sealed record Key(string Name, int Scan, bool Extended, uint Keysym, string De, string En, string LongDe, string LongEn);

    // In the order they are stored and pressed.
    public static readonly IReadOnlyList<Key> Modifiers =
    [
        new("Ctrl", 0x1D, false, 0xFFE3, "Strg", "Ctrl", "Strg", "Ctrl"),
        new("Alt", 0x38, false, 0xFFE9, "Alt", "Alt", "Alt", "Alt"),
        new("Shift", 0x2A, false, 0xFFE1, "⇧", "⇧", "Umschalt", "Shift"),
        new("Win", 0x5B, true, 0xFFEB, "⊞", "⊞", "Windows", "Windows"),
    ];

    public static readonly IReadOnlyList<Key> Named = BuildNamed();

    private static List<Key> BuildNamed()
    {
        var keys = new List<Key>
        {
            new("Esc", 0x01, false, 0xFF1B, "Esc", "Esc", "Esc", "Esc"),
            new("Tab", 0x0F, false, 0xFF09, "Tab", "Tab", "Tab", "Tab"),
            new("Enter", 0x1C, false, 0xFF0D, "⏎", "⏎", "Eingabe", "Enter"),
            new("Backspace", 0x0E, false, 0xFF08, "⌫", "⌫", "Rücktaste", "Backspace"),
            new("Space", 0x39, false, 0x20, "Leer", "Space", "Leertaste", "Space"),
            new("Left", 0x4B, true, 0xFF51, "←", "←", "Pfeil links", "Left arrow"),
            new("Up", 0x48, true, 0xFF52, "↑", "↑", "Pfeil hoch", "Up arrow"),
            new("Down", 0x50, true, 0xFF54, "↓", "↓", "Pfeil runter", "Down arrow"),
            new("Right", 0x4D, true, 0xFF53, "→", "→", "Pfeil rechts", "Right arrow"),
            new("Delete", 0x53, true, 0xFFFF, "Entf", "Del", "Entf", "Delete"),
            new("Insert", 0x52, true, 0xFF63, "Einfg", "Ins", "Einfg", "Insert"),
            new("Home", 0x47, true, 0xFF50, "Pos1", "Home", "Pos1", "Home"),
            new("End", 0x4F, true, 0xFF57, "Ende", "End", "Ende", "End"),
            new("PageUp", 0x49, true, 0xFF55, "Bild↑", "PgUp", "Bild auf", "Page Up"),
            new("PageDown", 0x51, true, 0xFF56, "Bild↓", "PgDn", "Bild ab", "Page Down"),
        };
        int[] functionScans = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (var number = 1; number <= 12; number++)
        {
            var name = "F" + number;
            keys.Add(new(name, functionScans[number - 1], false, (uint)(0xFFBD + number), name, name, name, name));
        }

        keys.Add(new("PrintScreen", 0x37, true, 0xFF61, "Druck", "PrtSc", "Druck", "Print Screen"));
        keys.Add(new("Menu", 0x5D, true, 0xFF67, "Menü", "Menu", "Menü-Taste", "Menu key"));
        return keys;
    }

    public static bool IsScope(string? value) => value is ScopeAll or ScopeScreen or ScopeTerminal;

    public static bool IsId(string? value) =>
        value is { Length: > 0 and <= 32 } && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private static Key? Modifier(string name) =>
        Modifiers.FirstOrDefault(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Key? NamedKey(string name) =>
        Named.FirstOrDefault(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase));

    // A single character that a key can stand for: one code point, visible, not a space (that one has a name).
    private static string? Character(string value)
    {
        if (string.IsNullOrEmpty(value) || !Rune.TryGetRuneAt(value, 0, out var rune) || rune.Utf16SequenceLength != value.Length)
        {
            return null;
        }

        if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Surrogate
            or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.Format)
        {
            return null;
        }

        return Rune.ToUpperInvariant(rune).ToString();
    }

    public static bool IsModifier(string name) => Modifier(name) is not null;

    public static bool IsNamed(string name) => NamedKey(name) is not null;

    public static bool IsCharacter(string value) => Character(value) is not null;

    // The keys as they are stored: every modifier once and in the fixed order, then the one key. Null when
    // the list names something that is no key, more than one key, or nothing at all.
    public static List<string>? Normalize(IEnumerable<string>? keys)
    {
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        string? main = null;
        foreach (var raw in keys ?? [])
        {
            var value = raw ?? "";
            if (value.Length == 0)
            {
                continue;
            }

            // A space is a key of its own; anything else is a name with nothing around it.
            var name = value == " " ? "Space" : value.Trim();
            if (name.Length == 0)
            {
                return null;
            }

            if (Modifier(name) is { } modifier)
            {
                modifiers.Add(modifier.Name);
                continue;
            }

            var key = NamedKey(name)?.Name ?? Character(name);
            if (key is null || main is not null)
            {
                return null;
            }

            main = key;
        }

        var result = Modifiers.Where(key => modifiers.Contains(key.Name)).Select(key => key.Name).ToList();
        if (main is not null)
        {
            result.Add(main);
        }

        return result.Count == 0 ? null : result;
    }

    private static (List<Key> Modifiers, string? Main) Split(KeyBarButton button)
    {
        var modifiers = new List<Key>();
        string? main = null;
        foreach (var name in button.Keys)
        {
            if (Modifier(name) is { } modifier)
            {
                modifiers.Add(modifier);
            }
            else
            {
                main = name;
            }
        }

        return (modifiers, main);
    }

    private static int MaskOf(IEnumerable<Key> modifiers)
    {
        var mask = 0;
        foreach (var modifier in modifiers)
        {
            mask |= modifier.Name switch
            {
                "Shift" => MaskShift,
                "Alt" => MaskAlt,
                "Ctrl" => MaskCtrl,
                _ => 0,
            };
        }

        return mask;
    }

    private static bool Is(KeyBarButton button, params string[] keys) => button.Keys.SequenceEqual(keys, StringComparer.Ordinal);

    // A button as it may be stored, or null when its keys are none. Used for what a form sent and for what
    // a data file holds alike - a file edited by hand must not put anything on the bar that is no key.
    public static KeyBarButton? Clean(KeyBarButton? button)
    {
        if (button is null || Normalize(button.Keys) is not { } keys)
        {
            return null;
        }

        var label = new string((button.Label ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (label.Length > MaxLabelLength)
        {
            label = label[..MaxLabelLength].Trim();
        }

        return new KeyBarButton
        {
            Id = IsId(button.Id) ? button.Id : NewId(),
            Label = label,
            Keys = keys,
            // Only modifiers can wait for a key.
            Sticky = button.Sticky && keys.All(name => Modifier(name) is not null),
            Scope = IsScope(button.Scope) ? button.Scope : ScopeAll,
        };
    }

    public static List<KeyBarButton> Clean(IEnumerable<KeyBarButton>? buttons)
    {
        var result = new List<KeyBarButton>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in buttons ?? [])
        {
            if (result.Count >= MaxButtons || Clean(button) is not { } clean)
            {
                continue;
            }

            if (!ids.Add(clean.Id))
            {
                clean.Id = NewId();
                ids.Add(clean.Id);
            }

            result.Add(clean);
        }

        return result;
    }

    // What everyone has who never configured anything: the keys of the two bars there were before, in one.
    public static List<KeyBarButton> Defaults()
    {
        static KeyBarButton Button(string id, string scope, bool sticky, params string[] keys) =>
            new() { Id = id, Scope = scope, Sticky = sticky, Keys = [.. keys] };

        return
        [
            Button("esc", ScopeAll, false, "Esc"),
            Button("tab", ScopeAll, false, "Tab"),
            Button("shift-tab", ScopeTerminal, false, "Shift", "Tab"),
            Button("ctrl", ScopeAll, true, "Ctrl"),
            Button("alt", ScopeAll, true, "Alt"),
            Button("win", ScopeScreen, false, "Win"),
            Button("delete", ScopeScreen, false, "Delete"),
            Button("left", ScopeAll, false, "Left"),
            Button("up", ScopeAll, false, "Up"),
            Button("down", ScopeAll, false, "Down"),
            Button("right", ScopeAll, false, "Right"),
            Button("ctrl-c", ScopeAll, false, "Ctrl", "C"),
            Button("ctrl-v", ScopeAll, false, "Ctrl", "V"),
            Button("enter", ScopeTerminal, false, "Enter"),
            Button("ctrl-alt-delete", ScopeScreen, false, "Ctrl", "Alt", "Delete"),
        ];
    }

    // The user's bar: their own list, or the default as long as they have none. An own list may be empty -
    // someone who removed every button wants no bar.
    public static List<KeyBarButton> For(MatgateUser user) => user.KeyBar is { } own ? Clean(own) : Defaults();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The user's bar as each kind of page takes it.
    public static string ScreenJson(MatgateUser user, bool de) => JsonSerializer.Serialize(ForScreen(For(user), de), Json);

    public static string TerminalJson(MatgateUser user, bool de) => JsonSerializer.Serialize(ForTerminal(For(user), de), Json);

    public static string GuacamoleJson(MatgateUser user, bool de) => JsonSerializer.Serialize(ForGuacamole(For(user), de), Json);

    public static bool ShowsIn(KeyBarButton button, string scope) => button.Scope == ScopeAll || button.Scope == scope;

    // What is written on the button: its own label, or the keys in short ("Strg+C", an arrow).
    public static string Label(KeyBarButton button, bool de)
    {
        if (!string.IsNullOrWhiteSpace(button.Label))
        {
            return button.Label;
        }

        var (modifiers, main) = Split(button);
        var text = new StringBuilder();
        foreach (var modifier in modifiers)
        {
            text.Append(de ? modifier.De : modifier.En);
            // The arrow of Shift stands right in front of its key, as on the keyboard itself.
            if (!(modifier.Name == "Shift" && modifier == modifiers[^1] && main is not null))
            {
                text.Append('+');
            }
        }

        if (main is not null)
        {
            var named = NamedKey(main);
            text.Append(named is null ? main : de ? named.De : named.En);
        }
        else if (text.Length > 0 && text[^1] == '+')
        {
            text.Length--;
        }

        return text.ToString();
    }

    // The keys in words, for the list in the account and as the button's tooltip ("Strg + Alt + Entf").
    public static string Describe(KeyBarButton button, bool de)
    {
        var (modifiers, main) = Split(button);
        var parts = modifiers.Select(modifier => de ? modifier.LongDe : modifier.LongEn).ToList();
        if (main is not null)
        {
            var named = NamedKey(main);
            parts.Add(named is null ? main : de ? named.LongDe : named.LongEn);
        }

        return string.Join(" + ", parts);
    }

    // What a button does beyond pressing its keys, where a session has a better way than the keys:
    // Ctrl+Alt+Del is Windows' own, and Ctrl+V should bring what is on this device's clipboard along.
    private static string? ActionOf(KeyBarButton button) =>
        Is(button, "Ctrl", "Alt", "Delete") ? ActionSecureAttention
        : Is(button, "Ctrl", "V") ? ActionPaste
        : null;

    // ---- the screen page -------------------------------------------------------------------------------------

    public sealed record ScanKey(int S, bool E, uint Ks);

    // Mods are pressed in this order and let go in the reverse one; between them either Key is tapped or Text
    // is typed. A sticky button has only Mods: they stay down until the next key.
    public sealed record ScreenButton(string Id, string Label, string Title, bool Sticky, IReadOnlyList<ScanKey> Mods, ScanKey? Key, string? Text, string? Action);

    private static ScanKey Scan(Key key) => new(key.Scan, key.Extended, key.Keysym);

    public static ScreenButton? ForScreen(KeyBarButton button, bool de)
    {
        if (!ShowsIn(button, ScopeScreen))
        {
            return null;
        }

        var (modifiers, main) = Split(button);
        ScanKey? key = null;
        string? text = null;
        if (main is not null)
        {
            if (NamedKey(main) is { } named)
            {
                key = Scan(named);
            }
            else
            {
                // A character travels as text: where it sits depends on the keyboard layout of the machine,
                // and only the engine knows that. With Shift it is the upper-case one - and when Shift is all
                // there is, the character says it already and no key needs holding.
                var shifted = modifiers.Any(modifier => modifier.Name == "Shift");
                text = shifted ? main : main.ToLowerInvariant();
                if (shifted && modifiers.Count == 1)
                {
                    modifiers = [];
                }
            }
        }

        return new ScreenButton(button.Id, Label(button, de), Describe(button, de), button.Sticky, [.. modifiers.Select(Scan)], key, text, ActionOf(button));
    }

    public static List<ScreenButton> ForScreen(IEnumerable<KeyBarButton> buttons, bool de) =>
        [.. buttons.Select(button => ForScreen(button, de)).OfType<ScreenButton>()];

    // ---- the terminal page -----------------------------------------------------------------------------------

    // Send holds the bytes (hex) for the button's own modifiers together with each set of modifiers a sticky
    // button may hold at that moment: Send[mask] is the key with (Mods | mask). App is what an unmodified
    // cursor key sends while the program has switched the cursor keys to application mode. A sticky button has
    // no bytes of its own, only Mods.
    public sealed record TerminalButton(string Id, string Label, string Title, bool Sticky, int Mods, IReadOnlyList<string>? Send, string? App, string? Action);

    public static TerminalButton? ForTerminal(KeyBarButton button, bool de)
    {
        if (!ShowsIn(button, ScopeTerminal))
        {
            return null;
        }

        var (modifiers, main) = Split(button);
        // A terminal knows no Windows key.
        if (modifiers.Any(modifier => modifier.Name == "Win"))
        {
            return null;
        }

        var mask = MaskOf(modifiers);
        if (main is null)
        {
            // A modifier alone sends nothing: it only means something held for the next key.
            return button.Sticky ? new TerminalButton(button.Id, Label(button, de), Describe(button, de), true, mask, null, null, null) : null;
        }

        if (TerminalBytes(main, mask) is null)
        {
            return null;
        }

        var send = new string[8];
        for (var extra = 0; extra < send.Length; extra++)
        {
            send[extra] = Convert.ToHexStringLower(TerminalBytes(main, mask | extra)!);
        }

        var app = mask == 0 && CursorLetter(main) is { } letter ? Convert.ToHexStringLower([0x1B, (byte)'O', (byte)letter]) : null;
        return new TerminalButton(button.Id, Label(button, de), Describe(button, de), false, mask, send, app, ActionOf(button) == ActionPaste ? ActionPaste : null);
    }

    public static List<TerminalButton> ForTerminal(IEnumerable<KeyBarButton> buttons, bool de) =>
        [.. buttons.Select(button => ForTerminal(button, de)).OfType<TerminalButton>()];

    private static char? CursorLetter(string key) => key switch
    {
        "Up" => 'A',
        "Down" => 'B',
        "Right" => 'C',
        "Left" => 'D',
        "Home" => 'H',
        "End" => 'F',
        _ => null,
    };

    private static int? TildeNumber(string key) => key switch
    {
        "Insert" => 2,
        "Delete" => 3,
        "PageUp" => 5,
        "PageDown" => 6,
        "F5" => 15,
        "F6" => 17,
        "F7" => 18,
        "F8" => 19,
        "F9" => 20,
        "F10" => 21,
        "F11" => 23,
        "F12" => 24,
        _ => null,
    };

    // What Ctrl makes of a character: the control characters sit under @ A-Z [ \ ] ^ _, and a few keys stand
    // in for those a keyboard has no key for (as xterm does it).
    private static string Control(string character)
    {
        if (character.Length != 1)
        {
            return character;
        }

        var code = char.ToUpperInvariant(character[0]);
        return code switch
        {
            >= '@' and <= '_' => ((char)(code & 0x1F)).ToString(),
            ' ' or '2' => "\0",
            '3' => "\u001B",
            '4' => "\u001C",
            '5' => "\u001D",
            '6' => "\u001E",
            '7' or '/' => "\u001F",
            '8' or '?' => "\u007F",
            _ => character,
        };
    }

    // The bytes a terminal (xterm) sends for a key with these modifiers; null for a key a terminal has none for.
    public static byte[]? TerminalBytes(string key, int mask)
    {
        var shift = (mask & MaskShift) != 0;
        var alt = (mask & MaskAlt) != 0;
        var ctrl = (mask & MaskCtrl) != 0;
        var escape = "\u001B";
        string? text;
        if (CursorLetter(key) is { } letter)
        {
            text = mask == 0 ? $"{escape}[{letter}" : $"{escape}[1;{mask + 1}{letter}";
            return Encoding.UTF8.GetBytes(text);
        }

        if (TildeNumber(key) is { } number)
        {
            text = mask == 0 ? $"{escape}[{number}~" : $"{escape}[{number};{mask + 1}~";
            return Encoding.UTF8.GetBytes(text);
        }

        if (key is "F1" or "F2" or "F3" or "F4")
        {
            var final = (char)('P' + (key[1] - '1'));
            text = mask == 0 ? $"{escape}O{final}" : $"{escape}[1;{mask + 1}{final}";
            return Encoding.UTF8.GetBytes(text);
        }

        if (NamedKey(key) is not null)
        {
            text = key switch
            {
                "Esc" => escape,
                "Tab" => shift ? escape + "[Z" : "\t",
                "Enter" => "\r",
                "Backspace" => ctrl ? "\b" : "\u007F",
                "Space" => ctrl ? "\0" : " ",
                _ => null,
            };
        }
        else
        {
            text = shift ? key : key.ToLowerInvariant();
            if (ctrl)
            {
                text = Control(text);
            }
        }

        if (text is null)
        {
            return null;
        }

        // Alt is "Meta sends Escape": the key, with an Escape in front.
        return Encoding.UTF8.GetBytes(alt ? escape + text : text);
    }

    // ---- Guacamole sessions ----------------------------------------------------------------------------------

    // Syms are pressed in this order and let go in the reverse one. A sticky button names the modifiers of the
    // on-screen keyboard it switches (Mods) instead. Scope is carried along: one page holds sessions of both kinds.
    public sealed record GuacamoleButton(string Id, string Label, string Title, bool Sticky, string Scope, IReadOnlyList<string> Mods, IReadOnlyList<uint> Syms, string? Action);

    public static GuacamoleButton ForGuacamole(KeyBarButton button, bool de)
    {
        var (modifiers, main) = Split(button);
        var syms = modifiers.Select(modifier => modifier.Keysym).ToList();
        if (main is not null)
        {
            if (NamedKey(main) is { } named)
            {
                syms.Add(named.Keysym);
            }
            else
            {
                var shifted = modifiers.Any(modifier => modifier.Name == "Shift");
                var point = (uint)Rune.GetRuneAt(shifted ? main : main.ToLowerInvariant(), 0).Value;
                // Latin-1 has keysyms of its own; everything else is a "Unicode keysym".
                syms.Add(point < 0x100 ? point : 0x01000000 + point);
            }
        }

        return new GuacamoleButton(button.Id, Label(button, de), Describe(button, de), button.Sticky, button.Scope,
            [.. modifiers.Select(modifier => modifier.Name.ToLowerInvariant())], syms, ActionOf(button) == ActionPaste ? ActionPaste : null);
    }

    public static List<GuacamoleButton> ForGuacamole(IEnumerable<KeyBarButton> buttons, bool de) =>
        [.. buttons.Select(button => ForGuacamole(button, de))];
}
