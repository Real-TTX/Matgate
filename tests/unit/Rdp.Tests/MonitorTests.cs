using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Tests;

// A desktop over several monitors (MS-RDPBCGR 2.2.1.3.6): the core block asks for the rectangle around them, the
// monitor block lists each with its edges, relative to the primary one at 0,0.
public sealed class MonitorTests
{
    [Fact]
    public void Two_monitors_side_by_side_with_the_primary_on_the_right()
    {
        var options = new RdpOptions
        {
            Host = "server",
            Width = 1280,
            Height = 800,
            Monitors = [new RdpMonitor(0, 0, 1920, 1080, false), new RdpMonitor(1920, 0, 2560, 1440, true)],
        };
        var data = new RdpClient(options, new Handler()).ClientData();

        // Core block: 1920 + 2560 wide, 1440 high.
        Assert.Equal(0xC001, BinaryPrimitives.ReadUInt16LittleEndian(data));
        Assert.Equal(4480, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)));
        Assert.Equal(1440, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)));

        var at = Block(data, 0xC005);
        Assert.Equal(12 + 2 * 20, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8)));
        int[] Monitor(int index) => Enumerable.Range(0, 5).Select(field => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 12 + index * 20 + field * 4))).ToArray();
        Assert.Equal([-1920, 0, -1, 1079, 0], Monitor(0));
        Assert.Equal([0, 0, 2559, 1439, 1], Monitor(1));
    }

    [Fact]
    public void One_monitor_is_no_monitor_block()
    {
        var options = new RdpOptions { Host = "server", Width = 1280, Height = 800, Monitors = [new RdpMonitor(0, 0, 1920, 1080, true)] };
        var data = new RdpClient(options, new Handler()).ClientData();
        Assert.Equal(1280, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)));
        Assert.Equal(-1, Block(data, 0xC005));
    }

    private static int Block(byte[] data, ushort type)
    {
        for (var at = 0; at + 4 <= data.Length;)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) == type)
            {
                return at;
            }

            at += BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
        }

        return -1;
    }

    private sealed class Handler : IRdpHandler
    {
        public void Updated(int x, int y, int width, int height)
        {
        }

        public void Resized(int width, int height)
        {
        }

        public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
        {
        }

        public void CursorHidden()
        {
        }

        public void CursorDefault()
        {
        }
    }
}
