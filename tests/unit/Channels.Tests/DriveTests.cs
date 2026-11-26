using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace Matgate.Display.Rdp.Channels.Tests;

// A folder of this computer as a drive of the remote desktop (MS-RDPEFS file system device): the device list and the
// drive capability, then the I/O requests a Windows server sends - built by hand here, as 2.2.1.4 and 2.2.3.3 lay them
// out - against a temporary folder. Nothing may leave the folder.
public sealed class DriveTests : IDisposable
{
    private const string ServerAnnounce = "72 44 6e 49 01 00 0c 00 01 00 00 00";
    private const string ServerCapabilities = """
        72 44 50 53 05 00 00 00 01 00 2c 00 02 00 00 00
        02 00 00 00 00 00 00 00 01 00 0c 00 ff ff 00 00
        00 00 00 00 07 00 00 00 00 00 00 00 00 00 00 00
        02 00 00 00 02 00 08 00 01 00 00 00 03 00 08 00
        01 00 00 00 04 00 08 00 02 00 00 00 05 00 08 00
        01 00 00 00
        """;

    private const string ClientIdConfirm = "72 44 43 43 01 00 0c 00 01 00 00 00";
    private const string UserLoggedOn = "72 44 4c 55";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matgate-drive-" + Guid.NewGuid().ToString("N"));
    private readonly BlockingCollection<byte[]> _sent = [];
    private readonly RdpdrChannel _channel;
    private uint _completion;

    public DriveTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Ordner"));
        File.WriteAllText(Path.Combine(_folder, "Ordner", "alt.txt"), "alter Inhalt");
        _channel = new RdpdrChannel(drives: [new RdpDrive("Büro-Dateien", _folder)]);
        _channel.Send = message => _sent.Add(message.ToArray());
    }

    public void Dispose()
    {
        _channel.Closed();
        Thread.Sleep(50);
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void The_drive_is_announced_with_the_drive_capability()
    {
        var list = Handshake();

        // DR_DEVICELIST_ANNOUNCE: one device, a file system (8), id 1, DOS name of seven ASCII characters, and the
        // display name - umlauts spelled out - with its terminator.
        Assert.Equal("72 44 41 44 01 00 00 00 08 00 00 00 01 00 00 00", Hex.Text(list[..16]));
        Assert.Equal("Buero-D", Encoding.ASCII.GetString(list, 16, 7));
        var length = BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(24));
        Assert.Equal("Buero-Dateien\0", Encoding.ASCII.GetString(list, 28, length));
    }

    [Fact]
    public void Create_write_read_list_rename_and_delete()
    {
        Handshake();

        // A new file: FILE_CREATE, not a directory, with write access.
        var (status, created) = Request(0x00, 0, Create("\\Ordner\\neu.txt", 0x40000000, 2, 0x40));
        Assert.Equal(0u, status);
        var file = BinaryPrimitives.ReadUInt32LittleEndian(created);
        Assert.Equal(2, created[4]);

        var content = Encoding.UTF8.GetBytes("Hallo aus der Sitzung");
        (status, var written) = Request(0x04, file, new Body().U32((uint)content.Length).U64(0).Zeros(20).Bytes(content).ToArray());
        Assert.Equal(0u, status);
        Assert.Equal((uint)content.Length, BinaryPrimitives.ReadUInt32LittleEndian(written));

        (status, var read) = Request(0x03, file, new Body().U32(100).U64(6).Zeros(20).ToArray());
        Assert.Equal(0u, status);
        Assert.Equal("aus der Sitzung", Encoding.UTF8.GetString(read.AsSpan(4, (int)BinaryPrimitives.ReadUInt32LittleEndian(read))));

        // FileStandardInformation: size 21, not a directory.
        (status, var standard) = Request(0x05, file, new Body().U32(5).U32(0).Zeros(24).ToArray());
        Assert.Equal(0u, status);
        Assert.Equal(21L, BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(12)));
        Request(0x02, file, new Body().Zeros(32).ToArray());
        Assert.Equal("Hallo aus der Sitzung", File.ReadAllText(Path.Combine(_folder, "Ordner", "neu.txt")));

        // The folder's listing, one entry a request (FileBothDirectoryInformation), then "no more files".
        (_, var opened) = Request(0x00, 0, Create("\\Ordner", 0x80, 1, 0x01));
        var folder = BinaryPrimitives.ReadUInt32LittleEndian(opened);
        var names = new List<string>();
        for (var first = true; ; first = false)
        {
            (status, var entry) = Request(0x0C, folder, QueryDirectory(3, first, "\\Ordner\\*"), minor: 1);
            if (status == 0x80000006)
            {
                break;
            }

            Assert.Equal(0u, status);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(4 + 60));
            names.Add(Encoding.Unicode.GetString(entry, 4 + 93, nameLength));
        }

        Assert.Equal(["alt.txt", "neu.txt"], names.Order());
        Request(0x02, folder, new Body().Zeros(32).ToArray());

        // Rename (FileRenameInformation, the new name from the drive's top), then delete on close.
        (_, opened) = Request(0x00, 0, Create("\\Ordner\\neu.txt", 0x00010000, 1, 0x40));
        file = BinaryPrimitives.ReadUInt32LittleEndian(opened);
        var newName = Encoding.Unicode.GetBytes("\\Ordner\\umbenannt.txt");
        (status, _) = Request(0x06, file, new Body().U32(10).U32((uint)(6 + newName.Length)).Zeros(24).U8(0).U8(0).U32((uint)newName.Length).Bytes(newName).ToArray());
        Assert.Equal(0u, status);
        Assert.True(File.Exists(Path.Combine(_folder, "Ordner", "umbenannt.txt")));

        (status, _) = Request(0x06, file, new Body().U32(13).U32(1).Zeros(24).U8(1).ToArray());
        Assert.Equal(0u, status);
        Request(0x02, file, new Body().Zeros(32).ToArray());
        Assert.False(File.Exists(Path.Combine(_folder, "Ordner", "umbenannt.txt")));
    }

    [Fact]
    public void Nothing_outside_the_folder_can_be_reached()
    {
        Handshake();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_folder)!, "geheim-" + Path.GetFileName(_folder) + ".txt"), "geheim");
        try
        {
            var (status, _) = Request(0x00, 0, Create("\\..\\geheim-" + Path.GetFileName(_folder) + ".txt", 0x80000000, 1, 0x40));
            Assert.Equal(0xC0000022u, status);
            (status, _) = Request(0x00, 0, Create("\\C:\\Windows\\win.ini", 0x80000000, 1, 0x40));
            Assert.Equal(0xC0000022u, status);
            (status, _) = Request(0x00, 0, Create("\\fehlt.txt", 0x80000000, 1, 0x40));
            Assert.Equal(0xC0000034u, status);
        }
        finally
        {
            File.Delete(Path.Combine(Path.GetDirectoryName(_folder)!, "geheim-" + Path.GetFileName(_folder) + ".txt"));
        }
    }

    [Fact]
    public void The_volume_is_described()
    {
        Handshake();
        var (status, size) = Request(0x0A, 0, new Body().U32(7).U32(0).Zeros(24).ToArray());
        Assert.Equal(0u, status);
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(size));
        (status, var attributes) = Request(0x0A, 0, new Body().U32(5).U32(0).Zeros(24).ToArray());
        Assert.Equal(0u, status);
        Assert.Equal("NTFS", Encoding.Unicode.GetString(attributes, 16, 8));
    }

    // The handshake up to the device list, which it returns.
    private byte[] Handshake()
    {
        _channel.Receive(Hex.Bytes(ServerAnnounce));
        _channel.Receive(Hex.Bytes(ServerCapabilities));
        _channel.Receive(Hex.Bytes(ClientIdConfirm));
        _channel.Receive(Hex.Bytes(UserLoggedOn));
        var sent = Drain();
        var capabilities = sent.Single(message => message[2] == 0x50 && message[3] == 0x43);
        // Two sets now: the general one and the drive set, version 2.
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(capabilities.AsSpan(4)));
        Assert.Equal("04 00 08 00 02 00 00 00", Hex.Text(capabilities[^8..]));
        return sent.Single(message => message[2] == 0x41 && message[3] == 0x44);
    }

    private List<byte[]> Drain()
    {
        var list = new List<byte[]>();
        while (_sent.TryTake(out var message, 200))
        {
            list.Add(message);
        }

        return list;
    }

    // Sends one Device I/O Request to device 1 and waits for its response: the status and what follows it.
    private (uint Status, byte[] Body) Request(uint major, uint file, byte[] body, uint minor = 0)
    {
        var completion = ++_completion;
        var request = new Body().U16(0x4472).U16(0x4952).U32(1).U32(file).U32(completion).U32(major).U32(minor).Bytes(body).ToArray();
        _channel.Receive(request);
        Assert.True(_sent.TryTake(out var response, 5000), "no answer");
        Assert.Equal("72 44 43 49 01 00 00 00", Hex.Text(response[..8]));
        Assert.Equal(completion, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)));
        return (BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(12)), response[16..]);
    }

    private static byte[] Create(string path, uint access, uint disposition, uint options)
    {
        var name = Encoding.Unicode.GetBytes(path + "\0");
        return new Body().U32(access).U64(0).U32(0).U32(7).U32(disposition).U32(options).U32((uint)name.Length).Bytes(name).ToArray();
    }

    private static byte[] QueryDirectory(uint kind, bool initial, string path)
    {
        var name = Encoding.Unicode.GetBytes(path + "\0");
        return new Body().U32(kind).U8(initial ? (byte)1 : (byte)0).U32((uint)name.Length).Zeros(23).Bytes(name).ToArray();
    }

    private sealed class Body
    {
        private readonly MemoryStream _stream = new();

        public Body U8(byte value)
        {
            _stream.WriteByte(value);
            return this;
        }

        public Body U16(ushort value) => Bytes(BitConverter.GetBytes(value));

        public Body U32(uint value) => Bytes(BitConverter.GetBytes(value));

        public Body U64(ulong value) => Bytes(BitConverter.GetBytes(value));

        public Body Zeros(int count) => Bytes(new byte[count]);

        public Body Bytes(ReadOnlySpan<byte> value)
        {
            _stream.Write(value);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
