using System.Buffers.Binary;

namespace Matgate.Display.Vnc;

// RRE (2): a background colour and, on top of it, rectangles of one colour each.
internal static class RreDecoder
{
    // A pixel and four 16-bit numbers.
    private const int SubrectangleSize = 12;

    public static void Decode(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
    {
        var count = reader.ReadUInt32();
        target.Fill(x, y, width, height, VncPixel.FromWire(reader.Take(4)));

        // The count is the server's word and can be anything; nothing is set aside for it - the rectangles are
        // drawn as they come, some hundred at a time.
        while (count > 0)
        {
            var batch = (int)Math.Min(count, 1024);
            var bytes = reader.Take(batch * SubrectangleSize);
            for (var index = 0; index < batch; index++)
            {
                var entry = bytes.Slice(index * SubrectangleSize, SubrectangleSize);
                int left = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);
                int top = BinaryPrimitives.ReadUInt16BigEndian(entry[6..]);
                int subWidth = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
                int subHeight = BinaryPrimitives.ReadUInt16BigEndian(entry[10..]);
                if (left + subWidth > width || top + subHeight > height)
                {
                    throw new VncException("The VNC server sent a picture that does not fit its own rectangle (RRE).");
                }

                target.Fill(x + left, y + top, subWidth, subHeight, VncPixel.FromWire(entry));
            }

            count -= (uint)batch;
        }
    }
}
