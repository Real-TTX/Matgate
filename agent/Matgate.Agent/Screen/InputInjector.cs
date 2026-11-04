using System.Runtime.InteropServices;

namespace Matgate.Agent.Screen;

// Mouse and keyboard from the browser, as if they came from this machine's own devices (SendInput). Keys
// travel as scan codes: the machine's keyboard layout applies, so a German keyboard in front of a
// German Windows types exactly what it says on the keys.
public interface IInputSink
{
    void MoveTo(int x, int y);

    void Button(int button, bool down);

    void Wheel(int vertical, int horizontal);

    void Key(int scanCode, bool extended, bool down);

    void Text(string text);
}

public sealed class InputInjector : IInputSink
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHorizontalWheel = 0x1000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const uint KeyScanCode = 0x0008;

    public void MoveTo(int x, int y)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = Math.Max(2, GetSystemMetrics(78));
        var height = Math.Max(2, GetSystemMetrics(79));
        var dx = (int)((long)(x - left) * 65535 / (width - 1));
        var dy = (int)((long)(y - top) * 65535 / (height - 1));
        SendMouse(dx, dy, 0, MouseMove | MouseAbsolute | MouseVirtualDesk);
    }

    // 0 left, 1 middle, 2 right, 3 back, 4 forward - the numbering of the browser's MouseEvent.button.
    public void Button(int button, bool down)
    {
        var (flags, data) = button switch
        {
            0 => (down ? MouseLeftDown : MouseLeftUp, 0u),
            1 => (down ? MouseMiddleDown : MouseMiddleUp, 0u),
            2 => (down ? MouseRightDown : MouseRightUp, 0u),
            3 => (down ? MouseXDown : MouseXUp, 1u),
            4 => (down ? MouseXDown : MouseXUp, 2u),
            _ => (0u, 0u),
        };
        if (flags != 0)
        {
            SendMouse(0, 0, data, flags);
        }
    }

    // In Windows' own units: 120 per notch, positive = away from the user / to the right.
    public void Wheel(int vertical, int horizontal)
    {
        if (vertical != 0)
        {
            SendMouse(0, 0, (uint)vertical, MouseWheel);
        }

        if (horizontal != 0)
        {
            SendMouse(0, 0, (uint)horizontal, MouseHorizontalWheel);
        }
    }

    public void Key(int scanCode, bool extended, bool down)
    {
        var flags = KeyScanCode | (extended ? KeyExtended : 0) | (down ? 0 : KeyUp);
        SendKeyboard(0, (ushort)scanCode, flags);
    }

    // Text as it is (a phone's keyboard, an input method): each character as itself, whatever the layout.
    public void Text(string text)
    {
        foreach (var character in text)
        {
            SendKeyboard(0, character, KeyUnicode);
            SendKeyboard(0, character, KeyUnicode | KeyUp);
        }
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new Input { Type = InputMouse };
        input.Union.Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendKeyboard(ushort virtualKey, ushort scan, uint flags)
    {
        var input = new Input { Type = InputKeyboard };
        input.Union.Keyboard = new KeyboardInput { VirtualKey = virtualKey, Scan = scan, Flags = flags };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

// For the tests: nothing reaches the real mouse or keyboard, every input is reported back instead.
public sealed class RecordingInput(Action<string> report) : IInputSink
{
    public void MoveTo(int x, int y) => report($"move {x},{y}");

    public void Button(int button, bool down) => report($"button {button} {(down ? "down" : "up")}");

    public void Wheel(int vertical, int horizontal) => report($"wheel {vertical},{horizontal}");

    public void Key(int scanCode, bool extended, bool down) => report($"key {scanCode:x2}{(extended ? "e" : "")} {(down ? "down" : "up")}");

    public void Text(string text) => report($"text {text}");
}
