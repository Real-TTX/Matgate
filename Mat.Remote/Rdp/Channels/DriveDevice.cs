using System.Buffers.Binary;
using System.IO.Enumeration;
using System.Text;

namespace Matgate.Display.Rdp.Channels;

// A folder of this computer, shown on the remote desktop as a drive with this name.
public sealed record RdpDrive(string Name, string Path);

// A folder of this computer as a drive of the remote desktop (MS-RDPEFS, file system devices): the server sends I/O
// requests (open, read, write, list, rename, delete, ask about the volume) and this answers each one from the folder.
// Nothing outside the folder can be reached - every path is resolved and checked against it.
//
// Handle runs on one worker of the device channel, never on the session's read loop; a request whose answer has to
// wait (a watch for changes) gets none, as FreeRDP does it.
internal sealed class DriveDevice
{
    public const uint DeviceTypeFileSystem = 0x00000008;

    private const uint StatusSuccess = 0x00000000;
    private const uint StatusNoMoreFiles = 0x80000006;
    private const uint StatusUnsuccessful = 0xC0000001;
    private const uint StatusInvalidHandle = 0xC0000008;
    private const uint StatusInvalidParameter = 0xC000000D;
    private const uint StatusAccessDenied = 0xC0000022;
    private const uint StatusObjectNameNotFound = 0xC0000034;
    private const uint StatusObjectNameCollision = 0xC0000035;
    private const uint StatusObjectPathNotFound = 0xC000003A;
    private const uint StatusSharingViolation = 0xC0000043;
    private const uint StatusDiskFull = 0xC000007F;
    private const uint StatusFileIsADirectory = 0xC00000BA;
    private const uint StatusNotSupported = 0xC00000BB;
    private const uint StatusDirectoryNotEmpty = 0xC0000101;
    private const uint StatusNotADirectory = 0xC0000103;

    private const uint MajorCreate = 0x00;
    private const uint MajorClose = 0x02;
    private const uint MajorRead = 0x03;
    private const uint MajorWrite = 0x04;
    private const uint MajorQueryInformation = 0x05;
    private const uint MajorSetInformation = 0x06;
    private const uint MajorQueryVolumeInformation = 0x0A;
    private const uint MajorSetVolumeInformation = 0x0B;
    private const uint MajorDirectoryControl = 0x0C;
    private const uint MajorDeviceControl = 0x0E;
    private const uint MajorLockControl = 0x11;

    private const uint MinorQueryDirectory = 0x01;
    private const uint MinorNotifyChangeDirectory = 0x02;

    private const uint FileSupersede = 0;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileOverwrite = 4;
    private const uint FileOverwriteIf = 5;

    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileDeleteOnClose = 0x00001000;

    private const byte InformationSuperseded = 0;
    private const byte InformationOpened = 1;
    private const byte InformationCreated = 2;
    private const byte InformationOverwritten = 3;

    private const uint AttributeReadOnly = 0x01;
    private const uint AttributeHidden = 0x02;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeArchive = 0x20;
    private const uint AttributeNormal = 0x80;

    private const int MaxRead = 1024 * 1024;

    private readonly string _root;
    private readonly Dictionary<uint, OpenFile> _files = [];
    private uint _nextFile;

    public DriveDevice(uint id, RdpDrive drive)
    {
        Id = id;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(drive.Path));
        DisplayName = Ascii(drive.Name, 64, "Laufwerk");
        DosName = Ascii(drive.Name, 7, "Drive");
    }

    public uint Id { get; }

    // What the remote desktop shows ("<name> auf <computer>"): ASCII, as FreeRDP sends it.
    public string DisplayName { get; }

    public string DosName { get; }

    private sealed class OpenFile(string path, bool directory)
    {
        public string Path { get; set; } = path;

        public bool IsDirectory { get; } = directory;

        public FileStream? Stream { get; set; }

        public bool Writable { get; set; }

        public bool DeleteOnClose { get; set; }

        public List<FileSystemInfo>? Listing { get; set; }

        public int Next { get; set; }
    }

    // The DEVICE_ANNOUNCE of this drive.
    public byte[] Announcement()
    {
        var data = Encoding.ASCII.GetBytes(DisplayName + "\0");
        var dos = new byte[8];
        Encoding.ASCII.GetBytes(DosName, dos);
        var writer = new ByteWriter(20 + data.Length).U32(DeviceTypeFileSystem).U32(Id).Bytes(dos).U32((uint)data.Length).Bytes(data);
        return writer.ToArray();
    }

    // One Device I/O Request (from DeviceId on, the header is gone); the Device I/O Response, or null when none is due yet.
    public byte[]? Handle(ReadOnlySpan<byte> request)
    {
        var reader = new ByteReader(request);
        if (!reader.TryU32(out _) || !reader.TryU32(out var fileId) || !reader.TryU32(out var completion)
            || !reader.TryU32(out var major) || !reader.TryU32(out var minor))
        {
            return null;
        }

        var body = request[20..];
        var reply = new Reply(Id, completion);
        try
        {
            switch (major)
            {
                case MajorCreate:
                    Create(body, reply);
                    break;
                case MajorClose:
                    Close(fileId, reply);
                    break;
                case MajorRead:
                    Read(fileId, body, reply);
                    break;
                case MajorWrite:
                    Write(fileId, body, reply);
                    break;
                case MajorQueryInformation:
                    QueryInformation(fileId, body, reply);
                    break;
                case MajorSetInformation:
                    SetInformation(fileId, body, reply);
                    break;
                case MajorQueryVolumeInformation:
                    QueryVolume(body, reply);
                    break;
                case MajorSetVolumeInformation:
                    reply.Status = StatusAccessDenied;
                    reply.U32(0);
                    break;
                case MajorDirectoryControl when minor == MinorQueryDirectory:
                    QueryDirectory(fileId, body, reply);
                    break;
                case MajorDirectoryControl when minor == MinorNotifyChangeDirectory:
                    // Answered when something changes - which is never watched here; the server waits quietly.
                    return null;
                case MajorDeviceControl:
                    reply.U32(0);
                    break;
                case MajorLockControl:
                    reply.Zeros(5);
                    break;
                default:
                    reply.Status = StatusNotSupported;
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reply.Fail(StatusOf(exception), major);
        }

        return reply.ToArray();
    }

    // Closes every file the server left open (the session ended).
    public void CloseAll()
    {
        foreach (var file in _files.Values)
        {
            file.Stream?.Dispose();
        }

        _files.Clear();
    }

    // ---- the requests -------------------------------------------------------------------------------------------------

    private void Create(ReadOnlySpan<byte> body, Reply reply)
    {
        var reader = new ByteReader(body);
        if (!reader.TryU32(out var access) || !reader.TrySkip(8) || !reader.TryU32(out _) || !reader.TryU32(out _)
            || !reader.TryU32(out var disposition) || !reader.TryU32(out var options) || !reader.TryU32(out var pathLength)
            || !reader.TryBytes(pathLength, out var pathBytes))
        {
            reply.Fail(StatusInvalidParameter, MajorCreate);
            return;
        }

        var path = Resolve(Utf16(pathBytes));
        if (path is null)
        {
            reply.Fail(StatusAccessDenied, MajorCreate);
            return;
        }

        var wantsDirectory = (options & FileDirectoryFile) != 0;
        var wantsFile = (options & FileNonDirectoryFile) != 0;
        byte information;
        bool directory;
        if (Directory.Exists(path))
        {
            if (wantsFile)
            {
                reply.Fail(StatusFileIsADirectory, MajorCreate);
                return;
            }

            if (disposition == FileCreate)
            {
                reply.Fail(StatusObjectNameCollision, MajorCreate);
                return;
            }

            directory = true;
            information = InformationOpened;
        }
        else if (File.Exists(path))
        {
            if (wantsDirectory)
            {
                reply.Fail(StatusNotADirectory, MajorCreate);
                return;
            }

            directory = false;
            switch (disposition)
            {
                case FileCreate:
                    reply.Fail(StatusObjectNameCollision, MajorCreate);
                    return;
                case FileSupersede or FileOverwrite or FileOverwriteIf:
                    using (new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                    }

                    information = disposition == FileSupersede ? InformationSuperseded : InformationOverwritten;
                    break;
                default:
                    information = InformationOpened;
                    break;
            }
        }
        else
        {
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                reply.Fail(StatusObjectPathNotFound, MajorCreate);
                return;
            }

            if (disposition is FileOpen or FileOverwrite)
            {
                reply.Fail(StatusObjectNameNotFound, MajorCreate);
                return;
            }

            directory = wantsDirectory;
            if (directory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                }
            }

            information = InformationCreated;
        }

        var file = new OpenFile(path, directory)
        {
            DeleteOnClose = (options & FileDeleteOnClose) != 0,
            Writable = (access & 0x52000006) != 0,
        };
        var id = ++_nextFile;
        _files[id] = file;
        reply.U32(id).U8(information);
    }

    private void Close(uint fileId, Reply reply)
    {
        if (_files.Remove(fileId, out var file))
        {
            file.Stream?.Dispose();
            if (file.DeleteOnClose)
            {
                Delete(file);
            }
        }

        reply.Zeros(4);
    }

    private void Read(uint fileId, ReadOnlySpan<byte> body, Reply reply)
    {
        var reader = new ByteReader(body);
        if (!_files.TryGetValue(fileId, out var file) || file.IsDirectory || !reader.TryU32(out var length) || !reader.TryU64(out var offset))
        {
            reply.Fail(StatusInvalidHandle, MajorRead);
            return;
        }

        var stream = Opened(file, false);
        var buffer = new byte[Math.Min((int)Math.Min(length, MaxRead), (int)Math.Max(0, Math.Min(stream.Length - (long)offset, MaxRead)))];
        stream.Seek((long)offset, SeekOrigin.Begin);
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        reply.U32((uint)read).Bytes(buffer.AsSpan(0, read));
    }

    private void Write(uint fileId, ReadOnlySpan<byte> body, Reply reply)
    {
        var reader = new ByteReader(body);
        if (!_files.TryGetValue(fileId, out var file) || file.IsDirectory || !reader.TryU32(out var length) || !reader.TryU64(out var offset)
            || !reader.TrySkip(20) || !reader.TryBytes(length, out var data))
        {
            reply.Fail(StatusInvalidHandle, MajorWrite);
            return;
        }

        var stream = Opened(file, true);
        stream.Seek((long)offset, SeekOrigin.Begin);
        stream.Write(data);
        reply.U32((uint)data.Length).U8(0);
    }

    private void QueryInformation(uint fileId, ReadOnlySpan<byte> body, Reply reply)
    {
        var reader = new ByteReader(body);
        if (!_files.TryGetValue(fileId, out var file) || !reader.TryU32(out var kind))
        {
            reply.Fail(StatusInvalidHandle, MajorQueryInformation);
            return;
        }

        file.Stream?.Flush();
        FileSystemInfo info = file.IsDirectory ? new DirectoryInfo(file.Path) : new FileInfo(file.Path);
        if (!info.Exists)
        {
            reply.Fail(StatusObjectNameNotFound, MajorQueryInformation);
            return;
        }

        var size = info is FileInfo plain ? plain.Length : 0;
        switch (kind)
        {
            case 4:
                // FileBasicInformation.
                reply.U32(36).U64(Time(info.CreationTimeUtc)).U64(Time(info.LastAccessTimeUtc)).U64(Time(info.LastWriteTimeUtc))
                    .U64(Time(info.LastWriteTimeUtc)).U32(Attributes(info));
                break;
            case 5:
                // FileStandardInformation.
                reply.U32(22).U64((ulong)Allocation(size)).U64((ulong)size).U32(1).U8(file.DeleteOnClose ? (byte)1 : (byte)0)
                    .U8(file.IsDirectory ? (byte)1 : (byte)0);
                break;
            case 35:
                // FileAttributeTagInformation.
                reply.U32(8).U32(Attributes(info)).U32(0);
                break;
            default:
                reply.Fail(StatusNotSupported, MajorQueryInformation);
                break;
        }
    }

    private void SetInformation(uint fileId, ReadOnlySpan<byte> body, Reply reply)
    {
        var reader = new ByteReader(body);
        if (!_files.TryGetValue(fileId, out var file) || !reader.TryU32(out var kind) || !reader.TryU32(out var length)
            || !reader.TrySkip(24) || !reader.TryBytes(Math.Min((long)length, body.Length - 32L), out var data))
        {
            reply.Fail(StatusInvalidHandle, MajorSetInformation);
            return;
        }

        switch (kind)
        {
            case 4 when data.Length >= 36:
                // FileBasicInformation: the times that are set (0 and -1 leave one as it is), and read-only.
                SetTime(file, BinaryPrimitives.ReadInt64LittleEndian(data[16..]));
                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
                if (attributes != 0 && !file.IsDirectory)
                {
                    var info = new FileInfo(file.Path);
                    info.IsReadOnly = (attributes & AttributeReadOnly) != 0;
                }

                break;
            case 19:
                // FileAllocationInformation: room to come, nothing to do.
                break;
            case 20 when data.Length >= 8 && !file.IsDirectory:
                // FileEndOfFileInformation.
                Opened(file, true).SetLength(BinaryPrimitives.ReadInt64LittleEndian(data));
                break;
            case 13:
                // FileDispositionInformation: delete when closed (an empty buffer means yes).
                var delete = data.Length == 0 || data[0] != 0;
                if (delete && file.IsDirectory && Directory.EnumerateFileSystemEntries(file.Path).Any())
                {
                    reply.Fail(StatusDirectoryNotEmpty, MajorSetInformation);
                    return;
                }

                file.DeleteOnClose = delete;
                break;
            case 10 when data.Length >= 6:
                // FileRenameInformation: replace?, a root handle (none), the new name from the drive's top.
                var replace = data[0] != 0;
                var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
                if (nameLength > data.Length - 6)
                {
                    reply.Fail(StatusInvalidParameter, MajorSetInformation);
                    return;
                }

                var target = Resolve(Utf16(data.Slice(6, nameLength)));
                if (target is null)
                {
                    reply.Fail(StatusAccessDenied, MajorSetInformation);
                    return;
                }

                if (!Rename(file, target, replace))
                {
                    reply.Fail(StatusObjectNameCollision, MajorSetInformation);
                    return;
                }

                break;
            default:
                reply.Fail(StatusNotSupported, MajorSetInformation);
                return;
        }

        reply.U32(length).U8(0);
    }

    private void QueryVolume(ReadOnlySpan<byte> body, Reply reply)
    {
        var reader = new ByteReader(body);
        if (!reader.TryU32(out var kind))
        {
            reply.Fail(StatusInvalidParameter, MajorQueryVolumeInformation);
            return;
        }

        var (total, free) = Space();
        const uint SectorsPerUnit = 8, BytesPerSector = 512;
        const long Unit = SectorsPerUnit * BytesPerSector;
        switch (kind)
        {
            case 1:
                // FileFsVolumeInformation.
                var label = Encoding.Unicode.GetBytes(DosName);
                reply.U32((uint)(17 + label.Length)).U64(Time(Directory.GetCreationTimeUtc(_root))).U32((uint)_root.GetHashCode())
                    .U32((uint)label.Length).U8(0).Bytes(label);
                break;
            case 3:
                // FileFsSizeInformation.
                reply.U32(24).U64((ulong)(total / Unit)).U64((ulong)(free / Unit)).U32(SectorsPerUnit).U32(BytesPerSector);
                break;
            case 4:
                // FileFsDeviceInformation: a disk.
                reply.U32(8).U32(0x00000007).U32(0);
                break;
            case 5:
                // FileFsAttributeInformation: case preserved, Unicode names.
                var name = Encoding.Unicode.GetBytes("NTFS");
                reply.U32((uint)(12 + name.Length)).U32(0x00000007).U32(255).U32((uint)name.Length).Bytes(name);
                break;
            case 7:
                // FileFsFullSizeInformation.
                reply.U32(32).U64((ulong)(total / Unit)).U64((ulong)(free / Unit)).U64((ulong)(free / Unit)).U32(SectorsPerUnit).U32(BytesPerSector);
                break;
            default:
                reply.Fail(StatusNotSupported, MajorQueryVolumeInformation);
                break;
        }
    }

    private void QueryDirectory(uint fileId, ReadOnlySpan<byte> body, Reply reply)
    {
        var reader = new ByteReader(body);
        if (!_files.TryGetValue(fileId, out var file) || !reader.TryU32(out var kind) || !reader.TryU8(out var initial)
            || !reader.TryU32(out var pathLength) || !reader.TrySkip(23) || !reader.TryBytes(pathLength, out var pathBytes))
        {
            reply.Fail(StatusInvalidHandle, MajorDirectoryControl);
            return;
        }

        if (initial != 0 || file.Listing is null)
        {
            // "\folder\*.txt": the folder is the open one, the last part says what to list.
            var pattern = Utf16(pathBytes).Replace('/', '\\');
            var cut = pattern.LastIndexOf('\\');
            pattern = cut >= 0 ? pattern[(cut + 1)..] : pattern;
            pattern = pattern.Length == 0 ? "*" : pattern;
            var folder = file.IsDirectory ? file.Path : Path.GetDirectoryName(file.Path) ?? _root;
            file.Listing = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(entry => FileSystemName.MatchesWin32Expression(pattern, entry.Name, ignoreCase: true))
                .ToList();
            file.Next = 0;
        }

        if (file.Next >= file.Listing.Count)
        {
            reply.Status = StatusNoMoreFiles;
            reply.U32(0).U8(0);
            return;
        }

        var entry = file.Listing[file.Next++];
        var entryName = Encoding.Unicode.GetBytes(entry.Name);
        var size = entry is FileInfo plain ? plain.Length : 0;
        var created = Time(entry.CreationTimeUtc);
        var accessed = Time(entry.LastAccessTimeUtc);
        var written = Time(entry.LastWriteTimeUtc);
        var attributes = Attributes(entry);
        switch (kind)
        {
            case 1 or 2 or 3:
                // FileDirectoryInformation, FileFullDirectoryInformation (+ EA size), FileBothDirectoryInformation (+ short name).
                var fixedLength = kind switch { 1 => 64, 2 => 68, _ => 93 };
                reply.U32((uint)(fixedLength + entryName.Length)).U32(0).U32(0).U64(created).U64(accessed).U64(written).U64(written)
                    .U64((ulong)size).U64((ulong)Allocation(size)).U32(attributes).U32((uint)entryName.Length);
                if (kind >= 2)
                {
                    reply.U32(0);
                }

                if (kind == 3)
                {
                    // ShortNameLength and ShortName - without the Reserved byte between them (as FreeRDP and Windows have it).
                    reply.U8(0).Zeros(24);
                }

                reply.Bytes(entryName);
                break;
            case 12:
                // FileNamesInformation.
                reply.U32((uint)(12 + entryName.Length)).U32(0).U32(0).U32((uint)entryName.Length).Bytes(entryName);
                break;
            default:
                reply.Fail(StatusNotSupported, MajorDirectoryControl);
                break;
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    // A path of the remote side ("\folder\file") as a path in the folder; null when it would lead out of it.
    private string? Resolve(string remote)
    {
        var relative = remote.TrimEnd('\0').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (relative.Contains(':') || relative.Contains('\0'))
        {
            return null;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root, relative)));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return full.Equals(_root, comparison) || full.StartsWith(_root + Path.DirectorySeparatorChar, comparison) ? full : null;
    }

    private static FileStream Opened(OpenFile file, bool write)
    {
        if (file.Stream is { } open && (!write || open.CanWrite))
        {
            return open;
        }

        file.Stream?.Dispose();
        file.Stream = new FileStream(file.Path, FileMode.Open, write || file.Writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return file.Stream;
    }

    private static void Delete(OpenFile file)
    {
        try
        {
            if (file.IsDirectory)
            {
                Directory.Delete(file.Path, false);
            }
            else
            {
                File.Delete(file.Path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Rename(OpenFile file, string target, bool replace)
    {
        file.Stream?.Dispose();
        file.Stream = null;
        if (string.Equals(file.Path, target, StringComparison.Ordinal))
        {
            return true;
        }

        var caseOnly = string.Equals(file.Path, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
        {
            if (!replace || Directory.Exists(target))
            {
                return false;
            }

            File.Delete(target);
        }

        if (file.IsDirectory)
        {
            Directory.Move(file.Path, target);
        }
        else
        {
            File.Move(file.Path, target);
        }

        file.Path = target;
        return true;
    }

    private static void SetTime(OpenFile file, long written)
    {
        if (written is 0 or -1)
        {
            return;
        }

        var time = DateTime.FromFileTimeUtc(written);
        if (file.IsDirectory)
        {
            Directory.SetLastWriteTimeUtc(file.Path, time);
        }
        else
        {
            file.Stream?.Flush();
            File.SetLastWriteTimeUtc(file.Path, time);
        }
    }

    private (long Total, long Free) Space()
    {
        try
        {
            var drive = new DriveInfo(_root);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (1L << 40, 1L << 39);
        }
    }

    private static ulong Time(DateTime utc) => utc.Year < 1601 ? 0 : (ulong)utc.ToFileTimeUtc();

    private static long Allocation(long size) => (size + 4095) / 4096 * 4096;

    private static uint Attributes(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        uint result = 0;
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            result |= AttributeDirectory;
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly) && !attributes.HasFlag(FileAttributes.Directory))
        {
            result |= AttributeReadOnly;
        }

        if (attributes.HasFlag(FileAttributes.Hidden) || info.Name.StartsWith('.'))
        {
            result |= AttributeHidden;
        }

        if (attributes.HasFlag(FileAttributes.Archive))
        {
            result |= AttributeArchive;
        }

        return result == 0 ? AttributeNormal : result;
    }

    private static uint StatusOf(Exception exception) => exception switch
    {
        UnauthorizedAccessException => StatusAccessDenied,
        FileNotFoundException => StatusObjectNameNotFound,
        DirectoryNotFoundException => StatusObjectPathNotFound,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => StatusSharingViolation,
        IOException io when (io.HResult & 0xFFFF) is 39 or 112 => StatusDiskFull,
        IOException io when (io.HResult & 0xFFFF) is 145 => StatusDirectoryNotEmpty,
        _ => StatusUnsuccessful,
    };

    private static string Utf16(ReadOnlySpan<byte> bytes) => Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]).TrimEnd('\0');

    // ASCII for the remote desktop: umlauts spelled out, the rest that is not a plain character as "_".
    private static string Ascii(string name, int max, string fallback)
    {
        var spelled = name.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue").Replace("ß", "ss");
        var text = new string(spelled.Select(c => c is >= ' ' and < (char)127 && !":<>\"/\\|?*".Contains(c) ? c : '_').ToArray()).Trim();
        text = text.Length > max ? text[..max] : text;
        return text.Length == 0 ? fallback : text;
    }

    // A Device I/O Response: the core header, device, completion, status, then what the request returns.
    private sealed class Reply(uint device, uint completion)
    {
        private readonly MemoryStream _body = new();

        public uint Status { get; set; } = StatusSuccess;

        public Reply U8(byte value)
        {
            _body.WriteByte(value);
            return this;
        }

        public Reply U32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _body.Write(bytes);
            return this;
        }

        public Reply U64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            _body.Write(bytes);
            return this;
        }

        public Reply Bytes(ReadOnlySpan<byte> value)
        {
            _body.Write(value);
            return this;
        }

        public Reply Zeros(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _body.WriteByte(0);
            }

            return this;
        }

        // A failed request: the status, and the fields its answer always carries, empty.
        public void Fail(uint status, uint major)
        {
            Status = status;
            _body.SetLength(0);
            switch (major)
            {
                case MajorCreate:
                    U32(0).U8(0);
                    break;
                case MajorRead or MajorQueryInformation or MajorQueryVolumeInformation or MajorDeviceControl:
                    U32(0);
                    break;
                case MajorWrite or MajorSetInformation or MajorDirectoryControl:
                    U32(0).U8(0);
                    break;
                case MajorClose:
                    Zeros(4);
                    break;
            }
        }

        public byte[] ToArray()
        {
            var message = new byte[16 + _body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(message, 0x4472);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), 0x4943);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), device);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), completion);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), Status);
            _body.ToArray().CopyTo(message, 16);
            return message;
        }
    }
}
