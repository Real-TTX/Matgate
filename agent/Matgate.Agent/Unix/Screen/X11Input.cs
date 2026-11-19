using System.Text;
using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// Mouse and keyboard from the browser, through XTest: to the X server they are its own devices. Keys travel
// as the PC's scan codes, as on Windows; X numbers its keys by Linux's own codes plus eight, and those are
// the scan codes for everything but the "extended" keys - so the machine's keyboard layout applies here too.
internal sealed unsafe class X11Input : IInputSink, IDisposable
{
    private const uint ShiftKey = 42 + 8;

    // The extended (E0) scan codes and Linux's key codes for them.
    private static readonly Dictionary<int, int> Extended = new()
    {
        [0x1C] = 96, [0x1D] = 97, [0x35] = 98, [0x37] = 99, [0x38] = 100, [0x47] = 102, [0x48] = 103, [0x49] = 104,
        [0x4B] = 105, [0x4D] = 106, [0x4F] = 107, [0x50] = 108, [0x51] = 109, [0x52] = 110, [0x53] = 111,
        [0x5B] = 125, [0x5C] = 126, [0x5D] = 127, [0x20] = 113, [0x2E] = 114, [0x30] = 115, [0x22] = 164,
        [0x24] = 166, [0x10] = 165, [0x19] = 163, [0x45] = 119, [0x46] = 119, [0x5E] = 116, [0x5F] = 142,
        [0x32] = 172, [0x6C] = 155, [0x21] = 140, [0x65] = 217, [0x6A] = 158, [0x69] = 159, [0x67] = 173,
    };

    private readonly nint _display;
    private readonly int _lowest;
    private readonly int _highest;
    private Dictionary<nuint, (uint Key, bool Shift)>? _symbols;
    private int _symbolsGroup = -1;
    private uint _spare;
    private int _wheelY;
    private int _wheelX;

    public X11Input()
    {
        _display = X11.Open();
        if (X11.XTestQueryExtension(_display, out _, out _, out _, out _) == 0)
        {
            X11.XCloseDisplay(_display);
            throw new InvalidOperationException("The X display takes no input from programs (no XTEST).");
        }

        X11.XDisplayKeycodes(_display, out _lowest, out _highest);
    }

    public void MoveTo(int x, int y)
    {
        X11.XTestFakeMotionEvent(_display, -1, x, y, 0);
        X11.XFlush(_display);
    }

    // The browser counts left, middle, right, back, forward; X counts left, middle, right, then the wheel's
    // four directions, then back and forward.
    public void Button(int button, bool down)
    {
        var number = button switch
        {
            0 => 1u,
            1 => 2u,
            2 => 3u,
            3 => 8u,
            4 => 9u,
            _ => 0u,
        };
        if (number != 0)
        {
            X11.XTestFakeButtonEvent(_display, number, down ? 1 : 0, 0);
            X11.XFlush(_display);
        }
    }

    // 120 is one notch, as on Windows; to X a notch is a click of a button.
    public void Wheel(int vertical, int horizontal)
    {
        _wheelY += vertical;
        _wheelX += horizontal;
        for (; _wheelY >= 120; _wheelY -= 120)
        {
            Click(4);
        }

        for (; _wheelY <= -120; _wheelY += 120)
        {
            Click(5);
        }

        for (; _wheelX >= 120; _wheelX -= 120)
        {
            Click(7);
        }

        for (; _wheelX <= -120; _wheelX += 120)
        {
            Click(6);
        }

        X11.XFlush(_display);
    }

    public void Key(int scanCode, bool extended, bool down)
    {
        var code = extended ? Extended.GetValueOrDefault(scanCode) : scanCode is > 0 and <= 0x58 ? scanCode : 0;
        if (code == 0 || code + 8 > _highest)
        {
            return;
        }

        X11.XTestFakeKeyEvent(_display, (uint)code + 8, down ? 1 : 0, 0);
        X11.XFlush(_display);
    }

    // Text as it is (a phone's keyboard, an input method): each character as itself, whatever the layout.
    public void Text(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            Type(rune, shortcut: false);
        }
    }

    // A character typed while Ctrl or Alt is held: the key it sits on in this machine's layout.
    public void Shortcut(char character) => Type(new Rune(char.IsSurrogate(character) ? '?' : character), shortcut: true);

    private void Type(Rune rune, bool shortcut)
    {
        var symbol = Symbol(rune);
        var known = Symbols();
        if (!known.TryGetValue(symbol, out var key) && shortcut)
        {
            known.TryGetValue(Symbol(Rune.ToLowerInvariant(rune)), out key);
        }

        if (key.Key != 0)
        {
            Press(key.Key, key.Shift && !shortcut);
            return;
        }

        // On no key of this layout: a key nothing uses types it, for this one character.
        if (_spare == 0)
        {
            _spare = FindSpare();
            if (_spare == 0)
            {
                return;
            }
        }

        var symbols = stackalloc nuint[1] { symbol };
        X11.XChangeKeyboardMapping(_display, (int)_spare, 1, symbols, 1);
        X11.XSync(_display, 0);
        // Programs hear of the new key through an event of their own; give them the time to.
        Thread.Sleep(20);
        Press(_spare, shift: false);
        Thread.Sleep(5);
    }

    private void Press(uint key, bool shift)
    {
        if (shift)
        {
            X11.XTestFakeKeyEvent(_display, ShiftKey, 1, 0);
        }

        X11.XTestFakeKeyEvent(_display, key, 1, 0);
        X11.XTestFakeKeyEvent(_display, key, 0, 0);
        if (shift)
        {
            X11.XTestFakeKeyEvent(_display, ShiftKey, 0, 0);
        }

        X11.XSync(_display, 0);
    }

    private void Click(uint button)
    {
        X11.XTestFakeButtonEvent(_display, button, 1, 0);
        X11.XTestFakeButtonEvent(_display, button, 0, 0);
    }

    private static nuint Symbol(Rune rune) => rune.Value switch
    {
        '\n' or '\r' => 0xFF0D,
        '\t' => 0xFF09,
        '\b' => 0xFF08,
        >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF => (nuint)rune.Value,
        _ => 0x01000000 | (nuint)rune.Value,
    };

    // Which key types which character, plain or with Shift, in the layout that is active right now.
    private Dictionary<nuint, (uint Key, bool Shift)> Symbols()
    {
        var state = stackalloc byte[32];
        // XkbUseCoreKbd; the active layout is the first byte of the state.
        var group = X11.XkbGetState(_display, 0x0100, state) == 0 ? state[0] : 0;
        if (_symbols is not null && group == _symbolsGroup)
        {
            return _symbols;
        }

        var symbols = new Dictionary<nuint, (uint, bool)>();
        for (var level = 1; level >= 0; level--)
        {
            for (var key = _highest; key >= _lowest; key--)
            {
                if (key == _spare)
                {
                    continue;
                }

                var symbol = X11.XkbKeycodeToKeysym(_display, (byte)key, group, level);
                if (symbol != 0)
                {
                    // The plain keys last, the low key codes last: they win.
                    symbols[symbol] = ((uint)key, level == 1);
                }
            }
        }

        _symbolsGroup = group;
        return _symbols = symbols;
    }

    private uint FindSpare()
    {
        var mapping = X11.XGetKeyboardMapping(_display, (byte)_lowest, _highest - _lowest + 1, out var perKey);
        if (mapping is null)
        {
            return 0;
        }

        try
        {
            for (var key = _highest; key >= _lowest; key--)
            {
                var free = true;
                for (var i = 0; i < perKey && free; i++)
                {
                    free = mapping[(key - _lowest) * perKey + i] == 0;
                }

                if (free)
                {
                    return (uint)key;
                }
            }

            return 0;
        }
        finally
        {
            X11.XFree((nint)mapping);
        }
    }

    public void Dispose()
    {
        if (_spare != 0)
        {
            var nothing = stackalloc nuint[1] { 0 };
            X11.XChangeKeyboardMapping(_display, (int)_spare, 1, nothing, 1);
            X11.XSync(_display, 0);
        }

        X11.XCloseDisplay(_display);
    }
}
