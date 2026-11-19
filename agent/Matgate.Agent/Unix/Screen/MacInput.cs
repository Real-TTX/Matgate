using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// Mouse and keyboard from the browser, as Quartz events: to macOS they come from the machine's own devices
// (it asks for the permission "Accessibility" first). Keys travel as the PC's scan codes; a Mac numbers its
// keys differently, so each is looked up - by its place on the keyboard, so the Mac's layout applies. Ctrl is
// Control, Alt is Option, the Windows key is Command.
internal sealed unsafe class MacInput : IInputSink
{
    private const ulong Shift = 0x20000;
    private const ulong Control = 0x40000;
    private const ulong Option = 0x80000;
    private const ulong Command = 0x100000;

    // Scan code -> the Mac's virtual key.
    private static readonly Dictionary<int, ushort> Keys = new()
    {
        [0x1E] = 0x00, [0x1F] = 0x01, [0x20] = 0x02, [0x21] = 0x03, [0x23] = 0x04, [0x22] = 0x05, [0x2C] = 0x06, [0x2D] = 0x07,
        [0x2E] = 0x08, [0x2F] = 0x09, [0x30] = 0x0B, [0x10] = 0x0C, [0x11] = 0x0D, [0x12] = 0x0E, [0x13] = 0x0F, [0x15] = 0x10,
        [0x14] = 0x11, [0x02] = 0x12, [0x03] = 0x13, [0x04] = 0x14, [0x05] = 0x15, [0x07] = 0x16, [0x06] = 0x17, [0x0D] = 0x18,
        [0x0A] = 0x19, [0x08] = 0x1A, [0x0C] = 0x1B, [0x09] = 0x1C, [0x0B] = 0x1D, [0x1B] = 0x1E, [0x18] = 0x1F, [0x16] = 0x20,
        [0x1A] = 0x21, [0x17] = 0x22, [0x19] = 0x23, [0x1C] = 0x24, [0x26] = 0x25, [0x24] = 0x26, [0x28] = 0x27, [0x25] = 0x28,
        [0x27] = 0x29, [0x2B] = 0x2A, [0x33] = 0x2B, [0x35] = 0x2C, [0x31] = 0x2D, [0x32] = 0x2E, [0x34] = 0x2F, [0x0F] = 0x30,
        [0x39] = 0x31, [0x29] = 0x32, [0x0E] = 0x33, [0x01] = 0x35, [0x56] = 0x0A,
        [0x2A] = 0x38, [0x36] = 0x3C, [0x1D] = 0x3B, [0x38] = 0x3A, [0x3A] = 0x39,
        [0x3B] = 0x7A, [0x3C] = 0x78, [0x3D] = 0x63, [0x3E] = 0x76, [0x3F] = 0x60, [0x40] = 0x61, [0x41] = 0x62, [0x42] = 0x64,
        [0x43] = 0x65, [0x44] = 0x6D, [0x57] = 0x67, [0x58] = 0x6F,
        [0x52] = 0x52, [0x4F] = 0x53, [0x50] = 0x54, [0x51] = 0x55, [0x4B] = 0x56, [0x4C] = 0x57, [0x4D] = 0x58, [0x47] = 0x59,
        [0x48] = 0x5B, [0x49] = 0x5C, [0x53] = 0x41, [0x37] = 0x43, [0x4E] = 0x45, [0x4A] = 0x4E, [0x45] = 0x47,
    };

    private static readonly Dictionary<int, ushort> ExtendedKeys = new()
    {
        [0x1D] = 0x3E, [0x38] = 0x3D, [0x5B] = 0x37, [0x5C] = 0x36, [0x47] = 0x73, [0x4F] = 0x77, [0x49] = 0x74, [0x51] = 0x79,
        [0x53] = 0x75, [0x52] = 0x72, [0x4B] = 0x7B, [0x4D] = 0x7C, [0x50] = 0x7D, [0x48] = 0x7E, [0x1C] = 0x4C, [0x35] = 0x4B,
    };

    private static readonly Dictionary<ushort, ulong> Modifiers = new()
    {
        [0x38] = Shift, [0x3C] = Shift, [0x3B] = Control, [0x3E] = Control, [0x3A] = Option, [0x3D] = Option, [0x37] = Command, [0x36] = Command,
    };

    private readonly HashSet<ushort> _held = [];
    private readonly HashSet<int> _buttons = [];
    private IReadOnlyList<MacMonitor> _monitors = [];
    private long _monitorsAt;
    private CGPoint _place;
    private long _lastClickAt;
    private CGPoint _lastClickPlace;
    private int _lastClickButton = -1;
    private int _clicks = 1;
    private int _wheelY;
    private int _wheelX;

    // False until somebody allowed it: then macOS drops what the agent sends.
    public static bool Allowed => Mac.AXIsProcessTrusted() != 0;

    public void MoveTo(int x, int y)
    {
        if (Environment.TickCount64 - _monitorsAt > 3000)
        {
            _monitors = MacMonitors.List();
            _monitorsAt = Environment.TickCount64;
        }

        // From the picture's pixels to the points macOS places its monitors in.
        var monitor = _monitors.FirstOrDefault(each => x >= each.Info.Left && y >= each.Info.Top && x < each.Info.Left + each.Info.Width && y < each.Info.Top + each.Info.Height) ?? _monitors[0];
        _place = new CGPoint { X = monitor.Points.X + (x - monitor.Info.Left) / monitor.Scale, Y = monitor.Points.Y + (y - monitor.Info.Top) / monitor.Scale };
        // With a button held, a move is a drag - of that button.
        var (type, button) = _buttons.Contains(0) ? (6u, 0u) : _buttons.Contains(2) ? (7u, 1u) : _buttons.Count > 0 ? (27u, 2u) : (5u, 0u);
        Post(Mac.CGEventCreateMouseEvent(0, type, _place, button));
    }

    // The browser counts left, middle, right, back, forward; Quartz counts left, right, middle, then the rest.
    public void Button(int button, bool down)
    {
        var (type, number) = button switch
        {
            0 => (down ? 1u : 2u, 0u),
            2 => (down ? 3u : 4u, 1u),
            1 => (down ? 25u : 26u, 2u),
            3 => (down ? 25u : 26u, 3u),
            4 => (down ? 25u : 26u, 4u),
            _ => (0u, 0u),
        };
        if (type == 0)
        {
            return;
        }

        if (down)
        {
            // A double click is two clicks that say they are the second one.
            var now = Environment.TickCount64;
            var near = Math.Abs(_place.X - _lastClickPlace.X) < 5 && Math.Abs(_place.Y - _lastClickPlace.Y) < 5;
            _clicks = button == _lastClickButton && near && now - _lastClickAt < 500 ? Math.Min(_clicks + 1, 3) : 1;
            (_lastClickAt, _lastClickPlace, _lastClickButton) = (now, _place, button);
            _buttons.Add(button);
        }
        else
        {
            _buttons.Remove(button);
        }

        var sent = Mac.CGEventCreateMouseEvent(0, type, _place, number);
        if (sent != 0)
        {
            // kCGMouseEventClickState
            Mac.CGEventSetIntegerValueField(sent, 1, _clicks);
        }

        Post(sent);
    }

    // 120 is one notch, as on Windows; here in pixels, a notch being forty of them.
    public void Wheel(int vertical, int horizontal)
    {
        _wheelY += vertical;
        _wheelX += horizontal;
        var (down, across) = (_wheelY / 3, _wheelX / 3);
        _wheelY -= down * 3;
        _wheelX -= across * 3;
        if (down != 0 || across != 0)
        {
            // kCGScrollEventUnitPixel; to the right is negative here.
            Post(Mac.CGEventCreateScrollWheelEvent2(0, 0, 2, down, -across, 0));
        }
    }

    public void Key(int scanCode, bool extended, bool down)
    {
        if (!(extended ? ExtendedKeys : Keys).TryGetValue(scanCode, out var key))
        {
            return;
        }

        if (Modifiers.ContainsKey(key))
        {
            if (down)
            {
                _held.Add(key);
            }
            else
            {
                _held.Remove(key);
            }
        }

        var sent = Mac.CGEventCreateKeyboardEvent(0, key, (byte)(down ? 1 : 0));
        if (sent != 0)
        {
            Mac.CGEventSetFlags(sent, Flags());
        }

        Post(sent);
    }

    // Text as it is: each run of characters as itself, whatever the layout.
    public void Text(string text)
    {
        for (var at = 0; at < text.Length; at += 16)
        {
            var part = text.Substring(at, Math.Min(16, text.Length - at));
            // Never half of a pair.
            if (char.IsHighSurrogate(part[^1]) && at + part.Length < text.Length)
            {
                part += text[at + part.Length];
                at++;
            }

            foreach (var down in new[] { true, false })
            {
                var sent = Mac.CGEventCreateKeyboardEvent(0, 0, (byte)(down ? 1 : 0));
                if (sent == 0)
                {
                    continue;
                }

                fixed (char* characters = part)
                {
                    Mac.CGEventKeyboardSetUnicodeString(sent, (nuint)part.Length, characters);
                }

                Mac.CGEventSetFlags(sent, 0);
                Post(sent);
            }
        }
    }

    // A character typed while Control, Option or Command is held: the key with that letter or digit on an
    // American keyboard, and the character itself beside it - programs look at either.
    public void Shortcut(char character)
    {
        var lower = char.ToLowerInvariant(character);
        var scan = lower is >= 'a' and <= 'z' ? "\x1E\x30\x2E\x20\x12\x21\x22\x23\x17\x24\x25\x26\x32\x31\x18\x19\x10\x13\x1F\x14\x16\x2F\x11\x2D\x15\x2C"[lower - 'a']
            : lower is >= '1' and <= '9' ? (char)(0x02 + lower - '1')
            : lower == '0' ? (char)0x0B
            : (char)0;
        if (scan == 0 || !Keys.TryGetValue(scan, out var key))
        {
            Text(character.ToString());
            return;
        }

        foreach (var down in new[] { true, false })
        {
            var sent = Mac.CGEventCreateKeyboardEvent(0, key, (byte)(down ? 1 : 0));
            if (sent == 0)
            {
                continue;
            }

            Mac.CGEventKeyboardSetUnicodeString(sent, 1, &lower);
            Mac.CGEventSetFlags(sent, Flags());
            Post(sent);
        }
    }

    private ulong Flags() => _held.Aggregate(0UL, (flags, key) => flags | Modifiers[key]);

    private static void Post(nint sent)
    {
        if (sent != 0)
        {
            // kCGHIDEventTap: where the devices' own events come in.
            Mac.CGEventPost(0, sent);
            Mac.CFRelease(sent);
        }
    }
}
