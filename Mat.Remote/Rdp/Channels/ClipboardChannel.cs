using System.Buffers.Binary;
using System.Text;

namespace Matgate.Display.Rdp.Channels;

// The clipboard of an RDP session (MS-RDPECLIP), the client's end.
//
// Neither side sends what is on its clipboard. It announces the formats it could deliver (a format list), and
// only when somebody pastes does the other side ask for one of them (a format data request). Files go the same
// way in two steps: the list of files is the data of the registered format "FileGroupDescriptorW", and their
// contents are read in ranges with file contents requests.
//
// Events say what the server does, methods what the application wants. The methods may be called from any
// thread; no lock is held while an event is raised, a line is logged or a message is handed to the core.
// Events are raised on the thread that calls Receive or Closed - except the answer to a request that could
// not be sent at all, which is raised (with null) from inside the call that asked.
public sealed class ClipboardChannel : StaticChannel
{
    // What a format list may come to; a longer one is answered with a failure.
    public const int MaxFormatListLength = 1024 * 1024;

    // Data requests carry no id, so only one is on the wire at a time. This many more may wait for their turn.
    public const int MaxWaitingRequests = 256;

    // File contents requests under way at the same time, for each direction.
    public const int MaxFileRequests = 64;

    // The most this client asks for in one range request: what it has to hold when the answer arrives.
    public const int MaxRangeRequest = 16 * 1024 * 1024;

    // The longest range a server may ask for in one request; more is refused before the application sees it.
    public const int MaxRangeServed = 64 * 1024 * 1024;

    // Lock ids kept at the same time, for each direction.
    public const int MaxLocks = 256;

    // The general capability flags (MS-RDPECLIP 2.2.2.1.1.1).
    public const uint LongFormatNames = 0x00000002;
    public const uint StreamFileClip = 0x00000004;
    public const uint NoFilePaths = 0x00000008;
    public const uint CanLockData = 0x00000010;
    public const uint HugeFiles = 0x00000020;

    private const uint Supported = LongFormatNames | StreamFileClip | NoFilePaths | CanLockData | HugeFiles;

    private const ushort TypeMonitorReady = 0x0001;
    private const ushort TypeFormatList = 0x0002;
    private const ushort TypeFormatListResponse = 0x0003;
    private const ushort TypeFormatDataRequest = 0x0004;
    private const ushort TypeFormatDataResponse = 0x0005;
    private const ushort TypeTemporaryDirectory = 0x0006;
    private const ushort TypeCapabilities = 0x0007;
    private const ushort TypeFileContentsRequest = 0x0008;
    private const ushort TypeFileContentsResponse = 0x0009;
    private const ushort TypeLock = 0x000A;
    private const ushort TypeUnlock = 0x000B;

    private const ushort ResponseOk = 0x0001;
    private const ushort ResponseFail = 0x0002;
    private const ushort AsciiNames = 0x0004;

    private const uint FileContentsSize = 0x00000001;
    private const uint FileContentsRange = 0x00000002;

    private const int HeaderLength = 8;
    private const int ShortNameLength = 32;
    private const int MaxServerDataRequests = 16;

    // Spelled out: inside this class "Lock" is the method that locks the server's file list.
    private readonly System.Threading.Lock _gate = new();
    private readonly Outbox _outbox = new();
    private readonly Queue<WaitingRequest> _waiting = new();
    private readonly Dictionary<uint, OwnFileRequest> _fileRequests = [];
    private readonly Dictionary<uint, Queue<ServedRequest>> _served = [];
    private readonly HashSet<uint> _ownLocks = [];
    private readonly HashSet<uint> _serverLocks = [];

    // What the server said it can do, and what both sides agreed on when it became ready.
    private bool _serverCapabilities;
    private uint _serverFlags;
    private bool _ready;
    private uint _flags;

    // What the application announced last, and whether that is still what the clipboards agree on: once the
    // remote side copied something, this side's formats are history.
    private IReadOnlyList<ClipFormat> _local = [];
    private bool _localCurrent;
    private bool _firstListDue;
    private int _listsUnanswered;
    private bool _listRefused;

    // What the server announced last. A request made for an older announcement is not sent any more.
    private IReadOnlyList<ClipFormat> _remote = [];
    private int _remoteGeneration;
    private bool _requestOutstanding;
    private int _serverDataRequests;
    private int _servedCount;

    private readonly record struct WaitingRequest(uint FormatId, int Generation, bool Refused);

    private readonly record struct OwnFileRequest(bool IsSize, int Count);

    private readonly record struct ServedRequest(bool IsSize, int Count);

    public override string Name => "cliprdr";

    // The server's clipboard endpoint reads the chunk headers itself: the core sets CHANNEL_FLAG_SHOW_PROTOCOL
    // on every chunk of this channel.
    public override uint Options => StaticChannelOptions.Initialized | StaticChannelOptions.EncryptRdp | StaticChannelOptions.ShowProtocol;

    // The server is ready, formats may be announced. Also raised when the server's clipboard starts over in
    // the middle of a session; whatever was under way then has been answered with null before.
    public event Action? Ready;

    // Something was copied on the remote desktop.
    public event Action<IReadOnlyList<ClipFormat>>? RemoteFormats;

    // The server's answer to a format list this side sent - one event per list, in their order. That is every
    // AnnounceFormats made while ready, and the list the channel sends by itself when the server becomes ready
    // and the application did not announce anything from its Ready handler.
    public event Action<bool>? FormatsAcknowledged;

    // The server pastes: answer with SendData.
    public event Action<uint>? DataRequested;

    // The answer to RequestData, in the order of the requests; null = refused.
    public event Action<byte[]?>? DataReceived;

    // The server reads a file this side offered: answer with SendFileContents or SendFileSize.
    public event Action<FileContentsRequest>? FileContentsRequested;

    // The answer to RequestFileSize (eight bytes, see FileSize) or RequestFileRange: stream id, data;
    // null = refused.
    public event Action<uint, byte[]?>? FileContentsReceived;

    // The server wants the file list this side offers right now kept readable under this id, also after the
    // clipboard here has changed - until it unlocks the id. Its file contents requests then name the id.
    public event Action<uint>? Locked;

    public event Action<uint>? Unlocked;

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    // Both sides can read files in ranges: a file list is worth announcing and worth asking for.
    public bool CanTransferFiles => Agreed(StreamFileClip);

    // Both sides lock and unlock file lists.
    public bool CanLock => Agreed(CanLockData);

    // Positions beyond 2 GB may be asked of the server.
    public bool SupportsHugeFiles => Agreed(HugeFiles);

    // The file size out of the answer to RequestFileSize, or null when the data is not one.
    public static long? FileSize(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt64LittleEndian(data);
        return size <= long.MaxValue ? (long)size : null;
    }

    // Tells the server what this side's clipboard offers now (an empty list: nothing). Before the server is
    // ready the list is kept and sent when it is. Throws ArgumentException for a list that cannot be sent.
    public void AnnounceFormats(IReadOnlyList<ClipFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var list = formats.ToArray();
        long length = 0;
        foreach (var format in list)
        {
            if (format is null || (format.Name is { } name && name.Contains('\0')))
            {
                throw new ArgumentException("A clipboard format is missing, or its name contains a NUL.", nameof(formats));
            }

            length += 4 + ((format.Name?.Length ?? 0) + 1) * 2L;
        }

        if (length > MaxFormatListLength)
        {
            throw new ArgumentException($"The format list is longer than {MaxFormatListLength} bytes.", nameof(formats));
        }

        lock (_gate)
        {
            _local = list;
            _localCurrent = true;
            if (_ready)
            {
                QueueFormatList(list);
            }
        }

        Flush();
    }

    // Asks the server for the data of one of the formats it announced. The answer comes as DataReceived.
    public void RequestData(uint formatId)
    {
        List<Action>? after = null;
        lock (_gate)
        {
            var refused = !_ready || !Listed(_remote, formatId);
            if (!_requestOutstanding && _waiting.Count == 0)
            {
                if (refused)
                {
                    Later(ref after, () => Guarded.Raise(DataReceived, null, Log, "cliprdr: the handler of DataReceived"));
                }
                else
                {
                    QueueDataRequest(formatId);
                }
            }
            else if (_waiting.Count >= MaxWaitingRequests)
            {
                Note(ref after, "too many data requests are waiting; one is refused");
                Later(ref after, () => Guarded.Raise(DataReceived, null, Log, "cliprdr: the handler of DataReceived"));
            }
            else
            {
                // Refused or not, it waits for its turn: the answers come in the order of the requests.
                _waiting.Enqueue(new WaitingRequest(formatId, _remoteGeneration, refused));
            }
        }

        Flush();
        Run(after);
    }

    // Answers the oldest DataRequested that has no answer yet; null refuses it.
    public void SendData(byte[]? data)
    {
        List<Action>? after = null;
        lock (_gate)
        {
            if (_serverDataRequests == 0)
            {
                Note(ref after, "there is no data request of the server to answer");
            }
            else
            {
                _serverDataRequests--;
                QueueDataResponse(data);
            }
        }

        Flush();
        Run(after);
    }

    // Asks for the size of a file from the server's file list. The answer comes as FileContentsReceived with
    // this stream id, which must not be in use by another request (ArgumentException).
    public void RequestFileSize(uint streamId, int index, uint? clipDataId = null) =>
        RequestFileContents(streamId, index, true, 0, 8, clipDataId);

    // Asks for count bytes of a file from the server's file list, starting at offset. The answer is shorter
    // only where the file ends.
    public void RequestFileRange(uint streamId, int index, long offset, int count, uint? clipDataId = null) =>
        RequestFileContents(streamId, index, false, offset, count, clipDataId);

    // Answers a FileContentsRequested for a range; null refuses the request (of either kind).
    public void SendFileContents(uint streamId, byte[]? data) => AnswerFileContents(streamId, data, false);

    // Answers a FileContentsRequested for a size.
    public void SendFileSize(uint streamId, long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var data = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(data, (ulong)size);
        AnswerFileContents(streamId, data, true);
    }

    // Asks the server to keep the file list it offers right now readable under this id, also after its
    // clipboard has changed. Pass the id to the file requests that mean that list, and unlock it when done.
    // False when the server does not do locks, or the id is locked already.
    public bool Lock(uint clipDataId)
    {
        lock (_gate)
        {
            if (!_ready || (_flags & CanLockData) == 0 || _ownLocks.Count >= MaxLocks || !_ownLocks.Add(clipDataId))
            {
                return false;
            }

            _outbox.Add(Message(TypeLock, 0, 4).U32(clipDataId).ToArray());
        }

        Flush();
        return true;
    }

    // False when the id is not locked.
    public bool Unlock(uint clipDataId)
    {
        lock (_gate)
        {
            if (!_ownLocks.Remove(clipDataId))
            {
                return false;
            }

            _outbox.Add(Message(TypeUnlock, 0, 4).U32(clipDataId).ToArray());
        }

        Flush();
        return true;
    }

    public override void Receive(ReadOnlySpan<byte> message)
    {
        var reader = new ByteReader(message);
        if (!reader.TryU16(out var type) || !reader.TryU16(out var flags) || !reader.TryU32(out var length))
        {
            Report("dropped a message shorter than its header");
            return;
        }

        // More than the header counts is fine - older Windows versions append four bytes (MS-RDPECLIP <1>) -
        // less is not.
        if (!reader.TryBytes(length, out var body))
        {
            Report($"dropped message {type}: it says {length} bytes and has {reader.Remaining}");
            return;
        }

        List<Action>? after = null;
        var ready = false;
        lock (_gate)
        {
            switch (type)
            {
                case TypeCapabilities:
                    ServerCapabilities(body, ref after);
                    break;
                case TypeMonitorReady:
                    MonitorReady(ref after);
                    ready = true;
                    break;
                case TypeFormatList:
                    ServerFormatList(flags, body, ref after);
                    break;
                case TypeFormatListResponse:
                    ServerFormatListResponse(flags, ref after);
                    break;
                case TypeFormatDataRequest:
                    ServerDataRequest(body, ref after);
                    break;
                case TypeFormatDataResponse:
                    ServerDataResponse(flags, body, ref after);
                    break;
                case TypeFileContentsRequest:
                    ServerFileContentsRequest(body, ref after);
                    break;
                case TypeFileContentsResponse:
                    ServerFileContentsResponse(flags, body, ref after);
                    break;
                case TypeLock:
                    ServerLock(body, true, ref after);
                    break;
                case TypeUnlock:
                    ServerLock(body, false, ref after);
                    break;
                case TypeTemporaryDirectory:
                    Note(ref after, "ignored a temporary directory, which only a client sends");
                    break;
                default:
                    Note(ref after, $"ignored message {type}, which this client does not know");
                    break;
            }
        }

        Flush();
        Run(after);
        if (!ready)
        {
            return;
        }

        // The specification wants a format list from the client now. The application gets the first word; the
        // channel sends the list itself when the application had nothing to say.
        Guarded.Raise(Ready, Log, "cliprdr: the handler of Ready");
        lock (_gate)
        {
            if (_ready && _firstListDue)
            {
                QueueFormatList(_localCurrent ? _local : []);
            }
        }

        Flush();
    }

    public override void Closed()
    {
        List<Action>? after = null;
        lock (_gate)
        {
            StartOver(ref after);
            _outbox.Clear();
            _ready = false;
            _flags = 0;
            _serverCapabilities = false;
            _serverFlags = 0;
        }

        Run(after);
    }

    private bool Agreed(uint flag)
    {
        lock (_gate)
        {
            return _ready && (_flags & flag) != 0;
        }
    }

    // ---- the server's messages (all under the lock) -----------------------------------------------------------

    private void ServerCapabilities(ReadOnlySpan<byte> body, ref List<Action>? after)
    {
        var reader = new ByteReader(body);
        if (!reader.TryU16(out var count) || !reader.TrySkip(2))
        {
            Note(ref after, "dropped capabilities too short to count their sets");
            return;
        }

        // Without a general capability set the defaults apply: no flag at all.
        uint flags = 0;
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryU16(out var kind) || !reader.TryU16(out var length) || length < 4 || !reader.TryBytes(length - 4, out var set))
            {
                Note(ref after, "dropped capabilities with a set that does not fit");
                return;
            }

            if (kind == 1)
            {
                if (set.Length < 8)
                {
                    Note(ref after, "dropped capabilities with a general set that is too short");
                    return;
                }

                // The version before the flags is for information only.
                flags = BinaryPrimitives.ReadUInt32LittleEndian(set[4..]);
            }
        }

        // They take effect with the monitor ready that follows; until then the session goes on as agreed.
        _serverCapabilities = true;
        _serverFlags = flags;
    }

    private void MonitorReady(ref List<Action>? after)
    {
        // A server that is ready again - its clipboard process was restarted - knows nothing of what was asked
        // of the one before.
        StartOver(ref after);
        _ready = true;
        _firstListDue = true;
        if (!_serverCapabilities)
        {
            // No capabilities from the server means the defaults on both sides, and no capabilities from here.
            _flags = 0;
            return;
        }

        // What the server cannot do is not offered to it (MS-RDPECLIP 3.2.5.1.3).
        _flags = Supported & _serverFlags;
        _outbox.Add(Message(TypeCapabilities, 0, 16).U16(1).U16(0).U16(1).U16(12).U32(2).U32(_flags).ToArray());
    }

    private void ServerFormatList(ushort flags, ReadOnlySpan<byte> body, ref List<Action>? after)
    {
        if (!_ready)
        {
            Note(ref after, "ignored a format list from a server that is not ready");
            return;
        }

        var formats = body.Length > MaxFormatListLength
            ? null
            : (_flags & LongFormatNames) != 0 ? DecodeLongNames(body) : DecodeShortNames(body, (flags & AsciiNames) != 0);
        if (formats is null)
        {
            Note(ref after, $"refused a format list of {body.Length} bytes that does not hold together");
            _outbox.Add(Message(TypeFormatListResponse, ResponseFail, 0).ToArray());
            return;
        }

        _remote = formats;
        _remoteGeneration++;
        _localCurrent = false;

        // The answer goes out before the application hears of the list: the server waits for it.
        _outbox.Add(Message(TypeFormatListResponse, ResponseOk, 0).ToArray());
        Later(ref after, () => Guarded.Raise(RemoteFormats, formats, Log, "cliprdr: the handler of RemoteFormats"));
    }

    private void ServerFormatListResponse(ushort flags, ref List<Action>? after)
    {
        if (_listsUnanswered == 0)
        {
            Note(ref after, "ignored an answer to a format list that was never sent");
            return;
        }

        var accepted = Succeeded(flags);
        _listsUnanswered--;

        // After a refused list every request of the server has to be refused (MS-RDPECLIP 3.1.5.2.4), until
        // it accepts a later one.
        _listRefused = !accepted;
        Later(ref after, () => Guarded.Raise(FormatsAcknowledged, accepted, Log, "cliprdr: the handler of FormatsAcknowledged"));
    }

    private void ServerDataRequest(ReadOnlySpan<byte> body, ref List<Action>? after)
    {
        if (!_ready)
        {
            Note(ref after, "ignored a data request from a server that is not ready");
            return;
        }

        // Whatever is wrong with a request, it gets its answer: the paste on the other side waits for it.
        if (body.Length < 4)
        {
            Note(ref after, "refused a data request too short for its format");
            QueueDataResponse(null);
            return;
        }

        var formatId = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (_listRefused || !Listed(_local, formatId))
        {
            Note(ref after, $"refused a data request for format {formatId}, which is not on offer");
            QueueDataResponse(null);
            return;
        }

        if (_serverDataRequests >= MaxServerDataRequests)
        {
            Note(ref after, "refused a data request: too many are waiting for the application");
            QueueDataResponse(null);
            return;
        }

        _serverDataRequests++;
        Later(ref after, () => Guarded.Raise(DataRequested, formatId, Log, "cliprdr: the handler of DataRequested"));
    }

    private void ServerDataResponse(ushort flags, ReadOnlySpan<byte> body, ref List<Action>? after)
    {
        if (!_requestOutstanding)
        {
            Note(ref after, "ignored data nobody asked for");
            return;
        }

        _requestOutstanding = false;
        var data = Succeeded(flags) ? body.ToArray() : null;
        Later(ref after, () => Guarded.Raise(DataReceived, data, Log, "cliprdr: the handler of DataReceived"));
        NextRequest(ref after);
    }

    private void ServerFileContentsRequest(ReadOnlySpan<byte> body, ref List<Action>? after)
    {
        var reader = new ByteReader(body);
        if (!_ready || !reader.TryU32(out var streamId))
        {
            Note(ref after, "ignored a file request that cannot be answered");
            return;
        }

        if (!reader.TryU32(out var index) || !reader.TryU32(out var kind) || !reader.TryU32(out var low)
            || !reader.TryU32(out var high) || !reader.TryU32(out var count))
        {
            Note(ref after, "refused a file request that is too short");
            QueueFileRefusal(streamId);
            return;
        }

        // The lock id is there when the length in the header covers it, not when bytes happen to follow.
        uint? clipDataId = reader.TryU32(out var id) ? id : null;
        var size = kind == FileContentsSize;
        var position = ((ulong)high << 32) | low;
        string? refusal = null;
        if ((_flags & StreamFileClip) == 0)
        {
            refusal = "file transfer was not agreed on";
        }
        else if (_listRefused)
        {
            refusal = "the server refused the format list";
        }
        else if (!size && kind != FileContentsRange)
        {
            refusal = $"it wants neither a size nor a range ({kind})";
        }
        else if (index > int.MaxValue)
        {
            refusal = "the file index is negative";
        }
        else if (!size && (position > long.MaxValue || count > MaxRangeServed))
        {
            refusal = $"the range ({count} bytes at {position}) is out of bounds";
        }
        else if (_servedCount >= MaxFileRequests)
        {
            refusal = "too many are waiting for the application";
        }

        if (refusal is not null)
        {
            Note(ref after, "refused a file request: " + refusal);
            QueueFileRefusal(streamId);
            return;
        }

        // A size is asked for with a position of zero and a count of eight; whatever else the fields say
        // means nothing.
        var request = new FileContentsRequest(streamId, (int)index, size, size ? 0 : (long)position, size ? 8 : (int)count, clipDataId);
        if (!_served.TryGetValue(streamId, out var waiting))
        {
            _served[streamId] = waiting = new Queue<ServedRequest>();
        }

        waiting.Enqueue(new ServedRequest(request.IsSizeRequest, request.Count));
        _servedCount++;
        Later(ref after, () => Guarded.Raise(FileContentsRequested, request, Log, "cliprdr: the handler of FileContentsRequested"));
    }

    private void ServerFileContentsResponse(ushort flags, ReadOnlySpan<byte> body, ref List<Action>? after)
    {
        if (body.Length < 4)
        {
            Note(ref after, "dropped file contents too short for their stream id");
            return;
        }

        var streamId = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (!_fileRequests.Remove(streamId, out var request))
        {
            Note(ref after, $"ignored file contents nobody asked for (stream {streamId})");
            return;
        }

        var contents = body[4..];
        byte[]? data = null;
        if (Succeeded(flags))
        {
            if (request.IsSize && contents.Length >= 8)
            {
                data = contents[..8].ToArray();
            }
            else if (!request.IsSize && contents.Length <= request.Count)
            {
                data = contents.ToArray();
            }
            else
            {
                Note(ref after, $"refused file contents of {contents.Length} bytes that are not what was asked for (stream {streamId})");
            }
        }

        Later(ref after, () => Guarded.Raise(FileContentsReceived, streamId, data, Log, "cliprdr: the handler of FileContentsReceived"));
    }

    private void ServerLock(ReadOnlySpan<byte> body, bool locks, ref List<Action>? after)
    {
        if (!_ready || (_flags & CanLockData) == 0 || body.Length < 4)
        {
            Note(ref after, "ignored a lock message that was not agreed on or is too short");
            return;
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (locks)
        {
            if (_serverLocks.Count >= MaxLocks && !_serverLocks.Contains(id))
            {
                Note(ref after, "ignored a lock: the server holds too many");
                return;
            }

            if (_serverLocks.Add(id))
            {
                Later(ref after, () => Guarded.Raise(Locked, id, Log, "cliprdr: the handler of Locked"));
            }
        }
        else if (_serverLocks.Remove(id))
        {
            // An unlock for an id that is not locked is ignored (MS-RDPECLIP 3.1.5.3.4).
            Later(ref after, () => Guarded.Raise(Unlocked, id, Log, "cliprdr: the handler of Unlocked"));
        }
    }

    // ---- what goes out (all under the lock) --------------------------------------------------------------------

    private void QueueFormatList(IReadOnlyList<ClipFormat> formats)
    {
        _firstListDue = false;
        _listsUnanswered++;
        _outbox.Add((_flags & LongFormatNames) != 0 ? EncodeLongNames(formats) : EncodeShortNames(formats));
    }

    private void QueueDataRequest(uint formatId)
    {
        _requestOutstanding = true;
        _outbox.Add(Message(TypeFormatDataRequest, 0, 4).U32(formatId).ToArray());
    }

    // A refusal carries no data (MS-RDPECLIP 3.1.5.4.3).
    private void QueueDataResponse(byte[]? data) =>
        _outbox.Add(data is null
            ? Message(TypeFormatDataResponse, ResponseFail, 0).ToArray()
            : Message(TypeFormatDataResponse, ResponseOk, data.Length).Bytes(data).ToArray());

    private void QueueFileResponse(uint streamId, ReadOnlySpan<byte> data) =>
        _outbox.Add(Message(TypeFileContentsResponse, ResponseOk, 4 + data.Length).U32(streamId).Bytes(data).ToArray());

    private void QueueFileRefusal(uint streamId) =>
        _outbox.Add(Message(TypeFileContentsResponse, ResponseFail, 4).U32(streamId).ToArray());

    // Sends the next request that still means something. One that was made for an announcement the server
    // has replaced since, or for a format it never offered, is answered with null instead - in its turn.
    private void NextRequest(ref List<Action>? after)
    {
        while (!_requestOutstanding && _waiting.TryDequeue(out var next))
        {
            if (next.Refused || next.Generation != _remoteGeneration)
            {
                Later(ref after, () => Guarded.Raise(DataReceived, null, Log, "cliprdr: the handler of DataReceived"));
                continue;
            }

            QueueDataRequest(next.FormatId);
        }
    }

    // The exchange with the server begins again, or ends. Everything this side asked for and will never hear
    // of again is answered with null, and what the server asked for is forgotten: an answer would go to
    // somebody who no longer waits for it.
    private void StartOver(ref List<Action>? after)
    {
        var requests = _waiting.Count + (_requestOutstanding ? 1 : 0);
        for (var i = 0; i < requests; i++)
        {
            Later(ref after, () => Guarded.Raise(DataReceived, null, Log, "cliprdr: the handler of DataReceived"));
        }

        foreach (var streamId in _fileRequests.Keys)
        {
            Later(ref after, () => Guarded.Raise(FileContentsReceived, streamId, null, Log, "cliprdr: the handler of FileContentsReceived"));
        }

        _waiting.Clear();
        _fileRequests.Clear();
        _served.Clear();
        _ownLocks.Clear();
        _serverLocks.Clear();
        _remote = [];
        _remoteGeneration++;
        _requestOutstanding = false;
        _serverDataRequests = 0;
        _servedCount = 0;
        _listsUnanswered = 0;
        _listRefused = false;
        _firstListDue = false;
    }

    private void RequestFileContents(uint streamId, int index, bool size, long offset, int count, uint? clipDataId)
    {
        List<Action>? after = null;
        lock (_gate)
        {
            if (_fileRequests.ContainsKey(streamId))
            {
                // Refusing through the event would answer the request that owns the id.
                throw new ArgumentException($"Stream id {streamId} belongs to a request that has no answer yet.", nameof(streamId));
            }

            // Without huge-file support a server takes positions below 2 GB only (MS-RDPECLIP 2.2.5.3).
            var sendable = _ready
                && (_flags & StreamFileClip) != 0
                && index >= 0
                && offset >= 0
                && count is >= 1 and <= MaxRangeRequest
                && (offset <= int.MaxValue || (_flags & HugeFiles) != 0)
                && (clipDataId is not { } locked || _ownLocks.Contains(locked))
                && _fileRequests.Count < MaxFileRequests;
            if (!sendable)
            {
                Later(ref after, () => Guarded.Raise(FileContentsReceived, streamId, null, Log, "cliprdr: the handler of FileContentsReceived"));
            }
            else
            {
                _fileRequests[streamId] = new OwnFileRequest(size, count);
                var message = Message(TypeFileContentsRequest, 0, clipDataId is null ? 24 : 28)
                    .U32(streamId)
                    .U32((uint)index)
                    .U32(size ? FileContentsSize : FileContentsRange)
                    .U32((uint)offset)
                    .U32((uint)(offset >> 32))
                    .U32((uint)count);
                if (clipDataId is { } id)
                {
                    message.U32(id);
                }

                _outbox.Add(message.ToArray());
            }
        }

        Flush();
        Run(after);
    }

    private void AnswerFileContents(uint streamId, byte[]? data, bool size)
    {
        List<Action>? after = null;
        lock (_gate)
        {
            if (!_served.TryGetValue(streamId, out var waiting))
            {
                Note(ref after, $"there is no file request of the server to answer (stream {streamId})");
            }
            else
            {
                var request = waiting.Dequeue();
                _servedCount--;
                if (waiting.Count == 0)
                {
                    _served.Remove(streamId);
                }

                if (data is null)
                {
                    QueueFileRefusal(streamId);
                }
                else if (request.IsSize != size)
                {
                    // The server would read a size as contents, or contents as a size.
                    Note(ref after, $"refused to answer a file request with the wrong kind of answer (stream {streamId})");
                    QueueFileRefusal(streamId);
                }
                else if (data.Length > request.Count)
                {
                    Note(ref after, $"cut an answer of {data.Length} bytes down to the {request.Count} the server asked for (stream {streamId})");
                    QueueFileResponse(streamId, data.AsSpan(0, request.Count));
                }
                else
                {
                    QueueFileResponse(streamId, data);
                }
            }
        }

        Flush();
        Run(after);
    }

    private void Flush() => _outbox.Flush(Send, Log, Name);

    // What has to wait until the lock is released: events and log lines, in the order they came up.
    private static void Later(ref List<Action>? after, Action action) => (after ??= []).Add(action);

    private void Note(ref List<Action>? after, string message) => Later(ref after, () => Report(message));

    private static void Run(List<Action>? after)
    {
        if (after is null)
        {
            return;
        }

        foreach (var action in after)
        {
            action();
        }
    }

    // A response succeeded when it says so and does not say the opposite as well.
    private static bool Succeeded(ushort flags) => (flags & ResponseOk) != 0 && (flags & ResponseFail) == 0;

    private static bool Listed(IReadOnlyList<ClipFormat> formats, uint formatId)
    {
        foreach (var format in formats)
        {
            if (format.Id == formatId)
            {
                return true;
            }
        }

        return false;
    }

    // ---- the wire formats ----------------------------------------------------------------------------------------

    private static ByteWriter Message(ushort type, ushort flags, int length) =>
        new ByteWriter(HeaderLength + length).U16(type).U16(flags).U32((uint)length);

    // CLIPRDR_LONG_FORMAT_NAME: the id, then the name in UTF-16 with its terminator - which is all there is of
    // a format without a name.
    private static byte[] EncodeLongNames(IReadOnlyList<ClipFormat> formats)
    {
        var length = 0;
        foreach (var format in formats)
        {
            length += 4 + ((format.Name?.Length ?? 0) + 1) * 2;
        }

        var message = Message(TypeFormatList, 0, length);
        foreach (var format in formats)
        {
            message.U32(format.Id);
            if (format.Name is { Length: > 0 } name)
            {
                message.Bytes(Encoding.Unicode.GetBytes(name));
            }

            message.U16(0);
        }

        return message.ToArray();
    }

    // CLIPRDR_SHORT_FORMAT_NAME: the id and 32 bytes of name, here always UTF-16 - 15 characters and the
    // terminator - as Windows clients send it. A longer name is cut (MS-RDPECLIP 2.2.3.1.1.1).
    private static byte[] EncodeShortNames(IReadOnlyList<ClipFormat> formats)
    {
        var message = Message(TypeFormatList, 0, formats.Count * (4 + ShortNameLength));
        Span<byte> field = stackalloc byte[ShortNameLength];
        foreach (var format in formats)
        {
            field.Clear();
            var name = format.Name ?? "";
            var units = Math.Min(name.Length, ShortNameLength / 2 - 1);
            if (units > 0 && char.IsHighSurrogate(name[units - 1]))
            {
                units--;
            }

            Encoding.Unicode.GetBytes(name.AsSpan(0, units), field);
            message.U32(format.Id).Bytes(field);
        }

        return message.ToArray();
    }

    private static List<ClipFormat>? DecodeLongNames(ReadOnlySpan<byte> data)
    {
        var formats = new List<ClipFormat>();
        var reader = new ByteReader(data);
        while (reader.Remaining > 0)
        {
            if (!reader.TryU32(out var id))
            {
                return null;
            }

            var rest = reader.Rest;
            var end = -1;
            for (var i = 0; i + 1 < rest.Length; i += 2)
            {
                if (rest[i] == 0 && rest[i + 1] == 0)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                return null;
            }

            formats.Add(new ClipFormat(id, end == 0 ? null : Encoding.Unicode.GetString(rest[..end])));
            reader.TrySkip(end + 2);
        }

        return formats;
    }

    private static List<ClipFormat>? DecodeShortNames(ReadOnlySpan<byte> data, bool ascii)
    {
        if (data.Length % (4 + ShortNameLength) != 0)
        {
            return null;
        }

        var formats = new List<ClipFormat>(data.Length / (4 + ShortNameLength));
        for (var at = 0; at < data.Length; at += 4 + ShortNameLength)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            var field = data.Slice(at + 4, ShortNameLength);
            string name;
            if (ascii)
            {
                var end = field.IndexOf((byte)0);
                name = Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
            }
            else
            {
                // A name that fills the field has no terminator.
                var end = field.Length;
                for (var i = 0; i < field.Length; i += 2)
                {
                    if (field[i] == 0 && field[i + 1] == 0)
                    {
                        end = i;
                        break;
                    }
                }

                name = Encoding.Unicode.GetString(field[..end]);
            }

            formats.Add(new ClipFormat(id, name.Length == 0 ? null : name));
        }

        return formats;
    }
}
