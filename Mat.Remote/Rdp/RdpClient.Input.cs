namespace Matgate.Display.Rdp;

// Mouse and keyboard, to the server. Each call is one small message in the queue; nothing here waits.
public sealed partial class RdpClient
{
    private const int EventScancode = 0;
    private const int EventMouse = 1;
    private const int EventMouseExtra = 2;
    private const int EventSync = 3;
    private const int EventUnicode = 4;

    private int _pointerX;
    private int _pointerY;

    public void MouseMove(int x, int y)
    {
        _pointerX = Math.Clamp(x, 0, Math.Max(Width - 1, 0));
        _pointerY = Math.Clamp(y, 0, Math.Max(Height - 1, 0));
        Input(EventMouse, 0, 0x0800, _pointerX, _pointerY);
    }

    // Buttons as browsers number them: 0 left, 1 middle, 2 right, 3 back, 4 forward.
    public void MouseButton(int button, bool down)
    {
        var pressed = down ? 0x8000 : 0;
        if (button is 3 or 4)
        {
            Input(EventMouseExtra, 0, (button == 3 ? 0x0001 : 0x0002) | pressed, _pointerX, _pointerY);
            return;
        }

        var flag = button switch
        {
            0 => 0x1000,
            1 => 0x4000,
            _ => 0x2000,
        };
        Input(EventMouse, 0, flag | pressed, _pointerX, _pointerY);
    }

    // A turn of the wheel in the units Windows uses (120 a notch); more than one message can carry is split.
    public void MouseWheel(int delta, bool horizontal)
    {
        var axis = horizontal ? 0x0400 : 0x0200;
        for (var i = 0; delta != 0 && i < 20; i++)
        {
            var step = Math.Clamp(delta, -255, 255);
            var flags = step < 0 ? axis | 0x0100 | ((0x100 + step) & 0xFF) : axis | (step & 0xFF);
            Input(EventMouse, 0, flags, _pointerX, _pointerY);
            delta -= step;
        }
    }

    // A key by its scancode (set 1), as the keyboard itself would report it.
    public void Key(int scancode, bool extended, bool down)
    {
        Input(EventScancode, (down ? 0 : 0x01) | (extended ? 0x02 : 0), scancode & 0xFF, 0, 0);
    }

    // A character that no key of the server's layout has to exist for: pressed and released.
    public void Unicode(char unit)
    {
        Input(EventUnicode, 0, unit, 0, 0);
        Input(EventUnicode, 0x01, unit, 0, 0);
    }

    // Tells the server how the lock keys stand, so its idea of Caps Lock and Num Lock matches the viewer's.
    public void SyncLocks(bool scrollLock, bool numLock, bool capsLock)
    {
        Input(EventSync, (scrollLock ? 1 : 0) | (numLock ? 2 : 0) | (capsLock ? 4 : 0), 0, 0, 0);
    }

    private void Input(int code, int flags, int a, int x, int y)
    {
        if (!_active)
        {
            return;
        }

        if (_serverFastPathInput)
        {
            // Fast-path: one byte with the number of events, the length, then the event.
            var size = code switch
            {
                EventScancode => 2,
                EventSync => 1,
                EventUnicode => 3,
                _ => 7,
            };
            var message = new WireWriter(2 + size);
            message.U8(1 << 2).U8(2 + size).U8((code << 5) | flags);
            switch (code)
            {
                case EventScancode:
                    message.U8(a);
                    break;
                case EventUnicode:
                    message.U16(a);
                    break;
                case EventMouse or EventMouseExtra:
                    message.U16(a).U16(x).U16(y);
                    break;
            }

            Send(message.ToArray());
            return;
        }

        // The classic form, for a server that does not take fast-path input: a time stamp, the kind, six bytes.
        var input = new WireWriter(16);
        input.U16(1).U16(0).U32(0);
        switch (code)
        {
            case EventScancode:
                input.U16(0x0004).U16(((flags & 0x01) != 0 ? 0x8000 : 0) | ((flags & 0x02) != 0 ? 0x0100 : 0)).U16(a).U16(0);
                break;
            case EventUnicode:
                input.U16(0x0005).U16((flags & 0x01) != 0 ? 0x8000 : 0).U16(a).U16(0);
                break;
            case EventSync:
                input.U16(0x0000).U16(0).U32((uint)flags);
                break;
            default:
                input.U16(code == EventMouse ? 0x8001 : 0x8002).U16(a).U16(x).U16(y);
                break;
        }

        SendData(0x1C, input.Span);
    }
}
