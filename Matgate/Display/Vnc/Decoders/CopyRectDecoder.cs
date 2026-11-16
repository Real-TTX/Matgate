namespace Matgate.Display.Vnc;

// CopyRect (1): the rectangle is a part of the picture the client already has, moved - a window that is
// dragged, a text that scrolls.
internal static class CopyRectDecoder
{
    public static void Decode(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
    {
        int sourceX = reader.ReadUInt16();
        int sourceY = reader.ReadUInt16();
        if (!target.Contains(sourceX, sourceY, width, height))
        {
            throw new VncException("The VNC server asked to copy a part of the picture that lies outside the desktop.");
        }

        target.Copy(sourceX, sourceY, width, height, x, y);
    }
}
