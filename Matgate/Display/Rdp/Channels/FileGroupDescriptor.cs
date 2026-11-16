using System.Buffers.Binary;
using System.Text;

namespace Matgate.Display.Rdp.Channels;

// One file or folder of a copied selection. Name is a relative path with backslashes ("folder\file.txt" for a
// file inside a copied folder); a folder comes before what is in it. Size is -1 when the other side did not
// say (ask with RequestFileSize then). A decoded Name is the server's word: see FileGroupDescriptor.PathParts
// before it goes anywhere near a file system.
public sealed record ClipFile(string Name, long Size, bool IsDirectory = false, DateTime? LastWriteTimeUtc = null)
{
    // FILE_ATTRIBUTE_* - what the other side sent, or what goes out next to the directory bit.
    public uint Attributes { get; init; } = IsDirectory ? FileGroupDescriptor.AttributeDirectory : FileGroupDescriptor.AttributeNormal;
}

// The list of files on a clipboard: FILEGROUPDESCRIPTORW, a count followed by one FILEDESCRIPTORW of 592 bytes
// per entry (MS-RDPECLIP 2.2.5.2.3). It travels as the data of the registered format "FileGroupDescriptorW";
// the files themselves are read with file contents requests that name an entry by its position in this list.
public static class FileGroupDescriptor
{
    public const string FormatName = ClipboardFormats.FileGroupDescriptor;

    // Registered formats have no fixed id: the side that announces picks one for the session. This is the one
    // Matgate has always announced its file lists under.
    public const uint DefaultFormatId = 0xC0D0;

    public const int DescriptorLength = 592;

    // cFileName has 260 UTF-16 units, the last one for the terminator.
    public const int MaxNameLength = 259;

    public const uint AttributeReadOnly = 0x00000001;
    public const uint AttributeHidden = 0x00000002;
    public const uint AttributeSystem = 0x00000004;
    public const uint AttributeDirectory = 0x00000010;
    public const uint AttributeArchive = 0x00000020;
    public const uint AttributeNormal = 0x00000080;

    private const uint HasAttributes = 0x00000004;
    private const uint HasWriteTime = 0x00000020;
    private const uint HasFileSize = 0x00000040;
    private const uint ShowProgress = 0x00004000;

    private const int AttributesAt = 36;
    private const int WriteTimeAt = 56;
    private const int SizeHighAt = 64;
    private const int SizeLowAt = 68;
    private const int NameAt = 72;

    private static readonly long LastFileTime = DateTime.MaxValue.ToFileTimeUtc();
    private static readonly DateTime FirstFileTime = DateTime.FromFileTimeUtc(0);

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    // The id under which a format list offers a file list, or null when it has none.
    public static uint? FindFormat(IReadOnlyList<ClipFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        foreach (var format in formats)
        {
            if (string.Equals(format.Name, FormatName, StringComparison.OrdinalIgnoreCase))
            {
                return format.Id;
            }
        }

        return null;
    }

    // The data to answer a request for the file list with. Throws ArgumentException for a name that cannot be
    // sent (empty, a NUL in it, more than 259 UTF-16 units): a name cut short would be another file.
    public static byte[] Encode(IReadOnlyList<ClipFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var data = new byte[4 + files.Count * (long)DescriptorLength];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var entry = data.AsSpan(4 + i * DescriptorLength, DescriptorLength);
            var name = (file.Name ?? "").Replace('/', '\\').TrimStart('\\');
            if (name.Length is 0 or > MaxNameLength || name.Contains('\0'))
            {
                throw new ArgumentException($"The name of file {i} cannot go into a file list: 1 to {MaxNameLength} characters, no NUL.", nameof(files));
            }

            var flags = HasAttributes | ShowProgress;
            var attributes = file.Attributes & ~AttributeDirectory;
            if (file.IsDirectory)
            {
                attributes |= AttributeDirectory;
            }
            else if (attributes == 0)
            {
                attributes = AttributeNormal;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entry[AttributesAt..], attributes);
            if (file.Size >= 0)
            {
                flags |= HasFileSize;
                var size = file.IsDirectory ? 0 : (ulong)file.Size;
                BinaryPrimitives.WriteUInt32LittleEndian(entry[SizeHighAt..], (uint)(size >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(entry[SizeLowAt..], (uint)size);
            }

            if (file.LastWriteTimeUtc is { } written)
            {
                // A time without a kind is taken as UTC, as the name of the property says.
                var utc = written.Kind == DateTimeKind.Local ? written.ToUniversalTime() : written;
                if (utc > FirstFileTime)
                {
                    flags |= HasWriteTime;
                    BinaryPrimitives.WriteInt64LittleEndian(entry[WriteTimeAt..], utc.ToFileTimeUtc());
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entry, flags);
            Encoding.Unicode.GetBytes(name, entry[NameAt..]);
        }

        return data;
    }

    // The file list out of the data the server answered with, or null when it is not one. Entries keep their
    // place - file contents requests go by it - so an entry with an odd name is still an entry.
    public static IReadOnlyList<ClipFile>? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > (uint)((data.Length - 4) / DescriptorLength))
        {
            return null;
        }

        var files = new List<ClipFile>((int)count);
        for (var i = 0; i < (int)count; i++)
        {
            var entry = data.Slice(4 + i * DescriptorLength, DescriptorLength);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var attributes = (flags & HasAttributes) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(entry[AttributesAt..]) : 0;

            long size = -1;
            if ((flags & HasFileSize) != 0)
            {
                var wide = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(entry[SizeHighAt..]) << 32)
                    | BinaryPrimitives.ReadUInt32LittleEndian(entry[SizeLowAt..]);
                size = wide <= long.MaxValue ? (long)wide : -1;
            }

            DateTime? written = null;
            if ((flags & HasWriteTime) != 0)
            {
                var time = BinaryPrimitives.ReadInt64LittleEndian(entry[WriteTimeAt..]);
                if (time > 0 && time <= LastFileTime)
                {
                    written = DateTime.FromFileTimeUtc(time);
                }
            }

            var name = entry.Slice(NameAt, (MaxNameLength + 1) * 2);
            var length = name.Length;
            for (var at = 0; at < name.Length; at += 2)
            {
                if (name[at] == 0 && name[at + 1] == 0)
                {
                    length = at;
                    break;
                }
            }

            files.Add(new ClipFile(Encoding.Unicode.GetString(name[..length]), size, (attributes & AttributeDirectory) != 0, written)
            {
                Attributes = attributes,
            });
        }

        return files;
    }

    // The parts of a name from a file list when it is a plain relative path, null when it is anything else: a
    // drive or a share, a way up ("..") or nowhere ("."), an empty part, a character or a device name Windows
    // does not allow in file names. The name comes from the remote side; this is the check to pass before it
    // is used as a path or as a name in an archive.
    public static IReadOnlyList<string>? PathParts(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return null;
        }

        var parts = name.Split('\\', '/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part[^1] is ' ' or '.')
            {
                return null;
            }

            foreach (var character in part)
            {
                if (character < ' ' || character is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\u007F')
                {
                    return null;
                }
            }

            var stem = part.Split('.')[0].TrimEnd(' ');
            foreach (var device in DeviceNames)
            {
                if (string.Equals(stem, device, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
        }

        return parts;
    }
}
