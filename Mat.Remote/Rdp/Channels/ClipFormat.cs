namespace Matgate.Display.Rdp.Channels;

// One format on a clipboard: its id on the side that announced it and, for a format an application registered
// by name, that name. The ids of registered formats differ from machine to machine - only the name says what
// the format is - so a request always carries the id of the side that offers the data.
public sealed record ClipFormat(uint Id, string? Name);

// The clipboard formats with a fixed id (winuser.h) and the registered names this client deals in.
public static class ClipboardFormats
{
    public const uint Text = 1;
    public const uint Bitmap = 2;
    public const uint MetafilePicture = 3;
    public const uint OemText = 7;
    public const uint Dib = 8;
    public const uint Palette = 9;
    public const uint UnicodeText = 13;
    public const uint FileDrop = 15;
    public const uint Locale = 16;
    public const uint DibV5 = 17;

    // A list of files (MS-RDPECLIP 1.3.1.2); see FileGroupDescriptor.
    public const string FileGroupDescriptor = "FileGroupDescriptorW";

    // What Windows registers next to a file list for the files' contents; they travel as file contents
    // requests, never as format data.
    public const string FileContents = "FileContents";
}

// The server reads a file this side offered (MS-RDPECLIP 2.2.5.3): its size, or a range of its bytes.
//
// Index counts through the file list that was sent as FileGroupDescriptorW. For a range, Count is the most the
// server takes; an answer with fewer bytes reads as the end of the file over there, so it must only be shorter
// where the file ends. ClipDataId names the file list the server locked earlier, when it did.
public sealed record FileContentsRequest(uint StreamId, int Index, bool IsSizeRequest, long Offset, int Count, uint? ClipDataId);
